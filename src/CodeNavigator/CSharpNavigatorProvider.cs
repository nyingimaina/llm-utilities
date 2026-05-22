using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeNavigator;

sealed class CSharpNavigatorProvider
{
    static readonly HashSet<string> ObjectMethods = new(StringComparer.Ordinal)
    {
        "Equals", "GetHashCode", "GetType", "ToString", "Finalize", "MemberwiseClone"
    };

    public object Symbols(string path)
    {
        var tree = Parse(path);
        var root = tree.GetRoot();
        var result = new List<object>();

        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var tLine = type.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var tEnd = type.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
            var kind = type switch
            {
                ClassDeclarationSyntax => "class",
                InterfaceDeclarationSyntax => "interface",
                StructDeclarationSyntax => "struct",
                RecordDeclarationSyntax => "record",
                _ => "type"
            };

            var typeEntry = new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["name"] = type.Identifier.Text,
                ["line"] = tLine,
                ["lineEnd"] = tEnd
            };

            var modifiers = string.Join(" ", type.Modifiers.Select(m => m.Text));
            if (modifiers.Length > 0)
                typeEntry["modifiers"] = modifiers;

            if (type.BaseList is not null)
                typeEntry["base"] = string.Join(", ", type.BaseList.Types.Select(t => t.ToString()));

            var members = new List<object>();
            foreach (var member in type.Members)
            {
                var mLine = member.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var mEnd = member.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

                if (member is MethodDeclarationSyntax method && !ObjectMethods.Contains(method.Identifier.Text))
                {
                    members.Add(new
                    {
                        kind = "method",
                        name = method.Identifier.Text,
                        signature = $"{method.ReturnType} {method.Identifier.Text}({string.Join(", ", method.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"))})",
                        line = mLine,
                        lineEnd = mEnd
                    });
                }
                else if (member is PropertyDeclarationSyntax prop)
                {
                    members.Add(new
                    {
                        kind = "property",
                        name = prop.Identifier.Text,
                        type = prop.Type.ToString(),
                        line = mLine,
                        lineEnd = mEnd
                    });
                }
                else if (member is FieldDeclarationSyntax field)
                {
                    foreach (var v in field.Declaration.Variables)
                    {
                        members.Add(new
                        {
                            kind = "field",
                            name = v.Identifier.Text,
                            type = field.Declaration.Type.ToString(),
                            line = mLine,
                            lineEnd = mEnd
                        });
                    }
                }
                else if (member is EventDeclarationSyntax evt)
                {
                    members.Add(new
                    {
                        kind = "event",
                        name = evt.Identifier.Text,
                        type = evt.Type.ToString(),
                        line = mLine,
                        lineEnd = mEnd
                    });
                }
                else if (member is ConstructorDeclarationSyntax ctor)
                {
                    members.Add(new
                    {
                        kind = "constructor",
                        signature = $"ctor({string.Join(", ", ctor.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"))})",
                        line = mLine,
                        lineEnd = mEnd
                    });
                }
            }
            typeEntry["members"] = members;
            result.Add(typeEntry);
        }

        return new { path, _symbols = result };
    }

    public object FindDefinition(string? path, string symbol)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            var tree = Parse(path);
            var root = tree.GetRoot();
            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (type.Identifier.Text == symbol)
                {
                    var line = type.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    return new { _def = new { file = path, line } };
                }
                foreach (var member in type.Members)
                {
                    if (member is MethodDeclarationSyntax m && m.Identifier.Text == symbol)
                    {
                        var line = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        return new { _def = new { file = path, line } };
                    }
                    if (member is PropertyDeclarationSyntax p && p.Identifier.Text == symbol)
                    {
                        var line = p.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        return new { _def = new { file = path, line } };
                    }
                }
            }
        }

        return new { _def = (object?)null, error = $"Symbol '{symbol}' not found", error_code = "NOT_FOUND" };
    }

    public object Hover(string path, string symbol)
    {
        var tree = Parse(path);
        var root = tree.GetRoot();

        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (type.Identifier.Text == symbol)
            {
                var doc = type.AttributeLists.Any()
                    ? "// " + string.Join(" ", type.AttributeLists.SelectMany(a => a.Attributes).Select(a => a.ToString()))
                    : null;
                return new
                {
                    _hover = new
                    {
                        signature = type switch
                        {
                            ClassDeclarationSyntax => $"class {type.Identifier.Text}",
                            InterfaceDeclarationSyntax => $"interface {type.Identifier.Text}",
                            _ => type.Identifier.Text
                        },
                        doc
                    }
                };
            }

            foreach (var member in type.Members)
            {
                if (member is MethodDeclarationSyntax m && m.Identifier.Text == symbol)
                {
                    var sig = $"{m.ReturnType} {m.Identifier.Text}({string.Join(", ", m.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"))})";
                    return new { _hover = new { signature = sig, doc = (string?)null } };
                }
                if (member is PropertyDeclarationSyntax p && p.Identifier.Text == symbol)
                {
                    return new { _hover = new { signature = $"{p.Type} {p.Identifier.Text}", doc = (string?)null } };
                }
            }
        }

        return new { _hover = (object?)null, error = $"Symbol '{symbol}' not found", error_code = "NOT_FOUND" };
    }

    public object FindCallees(string path, string symbol)
    {
        var tree = Parse(path);
        var root = tree.GetRoot();
        var results = new List<object>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Identifier.Text != symbol) continue;
            var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();
            foreach (var inv in invocations)
            {
                var name = inv.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    _ => null
                };
                if (name is not null)
                {
                    var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    results.Add(new { callee = name, file = path, line });
                }
            }
            break;
        }

        return new { symbol, file = path, _calls = results };
    }

    // Workspace-aware methods — delegate to WorkspaceAnalyzer if available
    public object WorkspaceSymbols(string query, WorkspaceAnalyzer? ws)
    {
        if (ws is not null) return ws.WorkspaceSymbols(query);
        return new { query, _symbols = Array.Empty<object>(), hint = "No .cs files found in workspace. Use symbols(path) for single-file search." };
    }

    public object FindReferences(string path, string symbol, WorkspaceAnalyzer? ws)
    {
        if (ws is not null) return ws.FindReferences(path, symbol);
        // Fallback: file-scoped regex
        return FindReferencesLocal(path, symbol);
    }

    public object FindImplementations(string path, string symbol, WorkspaceAnalyzer? ws)
    {
        if (ws is not null) return ws.FindImplementations(path, symbol);
        return new { symbol, _implementations = Array.Empty<object>(), hint = "Cross-file implementation resolution requires workspace analysis. Use symbols + grep as fallback." };
    }

    public object FindCallers(string path, string symbol, WorkspaceAnalyzer? ws)
    {
        if (ws is not null) return ws.FindCallers(path, symbol);
        return new { symbol, _calls = Array.Empty<object>(), hint = "Cross-file call graph requires workspace analysis. Use workspace_symbols + grep as fallback." };
    }

    object FindReferencesLocal(string path, string symbol)
    {
        var content = File.ReadAllText(path);
        var results = new List<object>();
        var lines = content.Split('\n');
        var regex = new Regex($@"\b{Regex.Escape(symbol)}\b");

        for (int i = 0; i < lines.Length; i++)
        {
            if (regex.IsMatch(lines[i]))
            {
                var snippet = lines[i].Trim().Length > 80
                    ? lines[i].Trim().Substring(0, 80) + "..."
                    : lines[i].Trim();
                results.Add(new { file = path, line = i + 1, snippet });
            }
        }

        return new { path, symbol, _references = results };
    }

    static SyntaxTree Parse(string path)
    {
        var content = File.ReadAllText(path);
        return CSharpSyntaxTree.ParseText(content, new CSharpParseOptions(LanguageVersion.Latest));
    }
}
