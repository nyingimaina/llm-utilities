using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeNavigator;

sealed class WorkspaceAnalyzer
{
    readonly CSharpCompilation? _compilation;
    readonly string[] _allCsFiles;
    readonly string _workspaceRoot;
    WorkspaceAnalyzer(string workspaceRoot, CSharpCompilation compilation, string[] allCsFiles)
    {
        _workspaceRoot = workspaceRoot;
        _compilation = compilation;
        _allCsFiles = allCsFiles;
    }

    public static WorkspaceAnalyzer? Create(string? filePath)
    {
        var root = FindWorkspaceRoot(filePath);
        if (root is null) return null;

        var csFiles = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => f.IndexOf($"obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) < 0
                     && f.IndexOf($"bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) < 0)
            .ToArray();

        if (csFiles.Length == 0) return null;

        var trees = new List<SyntaxTree>();
        foreach (var f in csFiles)
        {
            try { trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f)); }
            catch { /* skip unparseable files */ }
        }

        if (trees.Count == 0) return null;

        var refs = new[]
        {
            typeof(object).Assembly.Location,
            typeof(System.Collections.IEnumerable).Assembly.Location,
            typeof(System.ComponentModel.Component).Assembly.Location,
            typeof(System.Linq.Enumerable).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        }.Select(l =>
        {
            try { return MetadataReference.CreateFromFile(l); }
            catch { return null; }
        }).Where(r => r is not null).Cast<MetadataReference>().ToArray();

        var compilation = CSharpCompilation.Create("workspace", trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return new WorkspaceAnalyzer(root, compilation, csFiles);
    }

    static string? FindWorkspaceRoot(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return Directory.GetCurrentDirectory();

        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (dir is not null)
        {
            if (Directory.GetFiles(dir, "*.sln").Length > 0
                || Directory.GetFiles(dir, "*.csproj").Length > 0)
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return Path.GetDirectoryName(Path.GetFullPath(filePath));
    }

    public object WorkspaceSymbols(string query)
    {
        if (_compilation is null) return new { query, _symbols = Array.Empty<object>() };

        var lower = query.ToLowerInvariant();
        var results = new List<object>();

        foreach (var tree in _compilation.SyntaxTrees)
        {
            var root = tree.GetRoot();
            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (type.Identifier.Text.Contains(lower, StringComparison.OrdinalIgnoreCase))
                {
                    var line = type.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var kind = type switch
                    {
                        ClassDeclarationSyntax => "class",
                        InterfaceDeclarationSyntax => "interface",
                        StructDeclarationSyntax => "struct",
                        RecordDeclarationSyntax => "record",
                        _ => "type"
                    };
                    results.Add(new { kind, name = type.Identifier.Text, file = type.SyntaxTree.FilePath, line });
                }
            }
            // Also search for methods, properties matching query
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (method.Identifier.Text.Contains(lower, StringComparison.OrdinalIgnoreCase)
                    && !method.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.OverrideKeyword)))
                {
                    var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    results.Add(new { kind = "method", name = method.Identifier.Text, file = method.SyntaxTree.FilePath, line,
                        enclosing = GetEnclosingType(method)?.Identifier.Text ?? "" });
                }
            }
        }

        return new { query, _symbols = results };
    }

    public object FindImplementations(string path, string symbol)
    {
        if (_compilation is null) return new { symbol, _implementations = Array.Empty<object>() };

        var targetTree = _compilation.SyntaxTrees.FirstOrDefault(t => t.FilePath == path);
        if (targetTree is null) return new { symbol, _implementations = Array.Empty<object>() };
        var model = _compilation.GetSemanticModel(targetTree);
        if (model is null) return new { symbol, _implementations = Array.Empty<object>() };

        // Find the target type
        var root = model.SyntaxTree.GetRoot();
        var targetType = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == symbol);
        if (targetType is null) return new { symbol, _implementations = Array.Empty<object>() };

        var targetSymbol = model.GetDeclaredSymbol(targetType);
        if (targetSymbol is null) return new { symbol, _implementations = Array.Empty<object>() };

        var results = new List<object>();

        // Search all files for classes that implement/override the target
        foreach (var t in _compilation.SyntaxTrees)
        {
            var r = t.GetRoot();
            foreach (var type in r.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (type.BaseList is null) continue;
                var typeModel = _compilation.GetSemanticModel(t);
                var typeSym = typeModel.GetDeclaredSymbol(type);
                if (typeSym is null) continue;

                if (targetSymbol is INamedTypeSymbol namedTarget)
                {
                    // Interface/abstract class implementation
                    if (typeSym.AllInterfaces.Contains(namedTarget, SymbolEqualityComparer.Default)
                        || IsDerivedFrom(typeSym, namedTarget))
                    {
                        var line = type.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        results.Add(new { kind = type switch
                        {
                            ClassDeclarationSyntax => "class",
                            StructDeclarationSyntax => "struct",
                            RecordDeclarationSyntax => "record",
                            _ => "type"
                        }, name = type.Identifier.Text, file = t.FilePath, line });
                    }
                }
            }
        }

        return new { symbol, _implementations = results };
    }

    public object FindCallers(string path, string symbol)
    {
        if (_compilation is null) return new { symbol, _calls = Array.Empty<object>() };

        var results = new List<object>();
        var lower = symbol.ToLowerInvariant();

        foreach (var t in _compilation.SyntaxTrees)
        {
            var root = t.GetRoot();
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                foreach (var inv in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = inv.Expression switch
                    {
                        IdentifierNameSyntax id => id.Identifier.Text,
                        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                        _ => null
                    };
                    if (name is not null && name.Equals(symbol, StringComparison.Ordinal))
                    {
                        var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        results.Add(new { caller = method.Identifier.Text, callerFile = t.FilePath, callerLine = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1, callLine = line, file = t.FilePath });
                    }
                }
            }
        }

        return new { symbol, _calls = results };
    }

    public object FindReferences(string path, string symbol)
    {
        if (_compilation is null) return new { path, symbol, _references = Array.Empty<object>() };

        var results = new List<object>();
        var lower = symbol.ToLowerInvariant();

        foreach (var t in _compilation.SyntaxTrees)
        {
            var root = t.GetRoot();
            // Find identifier references
            foreach (var ident in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (ident.Identifier.Text == symbol)
                {
                    var line = ident.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    results.Add(new { file = t.FilePath, line });
                }
            }
        }

        return new { path, symbol, _references = results };
    }

    static bool IsDerivedFrom(INamedTypeSymbol? type, INamedTypeSymbol? baseType)
    {
        if (type is null || baseType is null) return false;
        var current = type.BaseType;
        while (current is not null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType)) return true;
            current = current.BaseType;
        }
        return false;
    }

    static TypeDeclarationSyntax? GetEnclosingType(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor is TypeDeclarationSyntax t) return t;
        }
        return null;
    }

}
