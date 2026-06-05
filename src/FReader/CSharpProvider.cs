using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FReader;

public sealed class CSharpProvider : IProvider
{
    public string Language => "C#";

    static readonly HashSet<string> ObjectMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "ToString", "GetHashCode", "Equals", "GetType"
    };

    public List<FunctionInfo> ListFunctions(string path, bool includeSystem = false, CancellationToken ct = default)
    {
        var code = File.ReadAllText(path);
        ct.ThrowIfCancellationRequested();
        var tree = CSharpSyntaxTree.ParseText(code, cancellationToken: ct);
        var root = tree.GetRoot(ct);
        var functions = new List<FunctionInfo>();

        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (!includeSystem && ShouldSkip(member))
                continue;

            var (name, lineStart, lineEnd, signature) = member switch
            {
                MethodDeclarationSyntax m => (
                    m.Identifier.Text,
                    m.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    m.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                    m.ReturnType.ToString() + " " + m.Identifier.Text + "(" +
                    string.Join(", ", m.ParameterList.Parameters.Select(p =>
                        (p.Type?.ToString() ?? "var") + " " + p.Identifier.Text)) + ")"
                ),
                ConstructorDeclarationSyntax c => (
                    c.Identifier.Text,
                    c.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    c.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                    c.Identifier.Text + "(" +
                    string.Join(", ", c.ParameterList.Parameters.Select(p =>
                        (p.Type?.ToString() ?? "var") + " " + p.Identifier.Text)) + ")"
                ),
                DestructorDeclarationSyntax d => (
                    d.Identifier.Text,
                    d.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    d.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                    "~" + d.Identifier.Text + "()"
                ),
                PropertyDeclarationSyntax p when p.AccessorList is not null => (
                    p.Identifier.Text,
                    p.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    p.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                    (p.Type?.ToString() ?? "var") + " " + p.Identifier.Text + " { " +
                    string.Join(" ", p.AccessorList.Accessors.Select(a => a.Kind().ToString().Replace("AccessorDeclaration", "").ToLower())) + " }"
                ),
                _ => (null, 0, 0, "")
            };

            if (name is not null)
                functions.Add(new FunctionInfo(name, lineStart, lineEnd, signature));
        }

        return functions;
    }

    bool ShouldSkip(MemberDeclarationSyntax member)
    {
        if (IsInCompilerGeneratedType(member))
            return true;

        if (member is DestructorDeclarationSyntax)
            return true;

        if (member is MethodDeclarationSyntax method &&
            ObjectMethods.Contains(method.Identifier.Text))
            return true;

        if (member is PropertyDeclarationSyntax prop && prop.AccessorList is not null)
        {
            if (prop.AccessorList.Accessors.All(a => a.Body is null && a.ExpressionBody is null))
                return true;
        }

        return false;
    }

    static bool IsInCompilerGeneratedType(MemberDeclarationSyntax member)
    {
        foreach (var ancestor in member.Ancestors())
        {
            if (ancestor is TypeDeclarationSyntax t && t.Identifier.Text.Contains('<'))
                return true;
        }
        return false;
    }

    public FunctionInfo? GetFunction(string path, string name, CancellationToken ct = default)
    {
        return ListFunctions(path, includeSystem: true, ct).Find(f =>
            string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.Name, name.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
    }

    public List<FunctionInfo> GetFunctions(string path, string name, CancellationToken ct = default)
    {
        return ListFunctions(path, includeSystem: true, ct).FindAll(f =>
            string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.Name, name.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
    }

    public List<FunctionInfo> GetFunctions(string path, string name, string[]? parameterTypes, CancellationToken ct = default)
    {
        var all = GetFunctions(path, name, ct);
        if (parameterTypes is null || parameterTypes.Length == 0)
            return all;

        return all.Where(f => ParameterTypesMatch(f.Signature, parameterTypes)).ToList();
    }

    static bool ParameterTypesMatch(string signature, string[] parameterTypes)
    {
        var openParen = signature.IndexOf('(');
        var closeParen = signature.LastIndexOf(')');
        if (openParen < 0 || closeParen <= openParen)
            return parameterTypes.Length == 0;

        var paramsPart = signature.Substring(openParen + 1, closeParen - openParen - 1);
        if (string.IsNullOrWhiteSpace(paramsPart))
            return parameterTypes.Length == 0;

        var sigTypes = paramsPart.Split(',')
            .Select(p => p.Trim().Split(' ')[0])
            .Where(t => t.Length > 0)
            .ToArray();

        if (sigTypes.Length != parameterTypes.Length)
            return false;

        for (int i = 0; i < sigTypes.Length; i++)
        {
            var st = sigTypes[i];
            var pt = MapTypeAlias(parameterTypes[i]);
            if (!string.Equals(st, pt, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    static readonly Dictionary<string, string> TypeAliasMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["int"] = "int", ["Int32"] = "int", ["System.Int32"] = "int",
        ["long"] = "long", ["Int64"] = "long", ["System.Int64"] = "long",
        ["short"] = "short", ["Int16"] = "short", ["System.Int16"] = "short",
        ["byte"] = "byte", ["Byte"] = "byte", ["System.Byte"] = "byte",
        ["string"] = "string", ["String"] = "string", ["System.String"] = "string",
        ["bool"] = "bool", ["Boolean"] = "bool", ["System.Boolean"] = "bool",
        ["char"] = "char", ["Char"] = "char", ["System.Char"] = "char",
        ["double"] = "double", ["Double"] = "double", ["System.Double"] = "double",
        ["float"] = "float", ["Single"] = "float", ["System.Single"] = "float",
        ["decimal"] = "decimal", ["Decimal"] = "decimal", ["System.Decimal"] = "decimal",
        ["uint"] = "uint", ["UInt32"] = "uint", ["System.UInt32"] = "uint",
        ["ulong"] = "ulong", ["UInt64"] = "ulong", ["System.UInt64"] = "ulong",
        ["ushort"] = "ushort", ["UInt16"] = "ushort", ["System.UInt16"] = "ushort",
        ["sbyte"] = "sbyte", ["SByte"] = "sbyte", ["System.SByte"] = "sbyte",
        ["object"] = "object", ["Object"] = "object", ["System.Object"] = "object",
    };

    static string MapTypeAlias(string typeName)
    {
        return TypeAliasMap.TryGetValue(typeName, out var canonical) ? canonical : typeName;
    }

    public string? Summarize(string path, CancellationToken ct = default)
    {
        string? code;
        try { code = File.ReadAllText(path); }
        catch { return null; }
        ct.ThrowIfCancellationRequested();

        var tree = CSharpSyntaxTree.ParseText(code, cancellationToken: ct);
        var root = tree.GetRoot(ct);
        var sb = new StringBuilder();

        var fileLineCount = code.Split('\n').Length;
        var fileName = Path.GetFileName(path);

        // Namespace
        var ns = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var nsName = ns?.Name.ToString() ?? "(no namespace)";

        // Using aliases
        var usingAliases = root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Where(u => u.Alias is not null)
            .Select(u => u.ToString())
            .ToList();

        // File-scoped attributes
        var fileAttrs = root.DescendantNodes()
            .OfType<AttributeListSyntax>()
            .Where(a => a.Parent is CompilationUnitSyntax)
            .SelectMany(a => a.Attributes)
            .Select(a => a.ToString())
            .ToList();

        sb.AppendLine($"// File: {fileName} ({fileLineCount} lines)");
        sb.AppendLine($"// Namespace: {nsName}");
        if (fileAttrs.Count > 0)
            sb.AppendLine($"// [{string.Join(" ", fileAttrs)}]");
        if (usingAliases.Count > 0)
            sb.AppendLine($"// using {string.Join("; ", usingAliases.Select(u => u.Replace("using ", "")))}");

        var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>();
        foreach (var type in types)
        {
            if (type.Parent is TypeDeclarationSyntax) continue;
            SummarizeType(type, sb, 0);
        }

        return sb.ToString();
    }

    static string FormatParamList(ParameterListSyntax paramList)
    {
        return string.Join(", ", paramList.Parameters.Select(p =>
        {
            var type = p.Type?.ToString() ?? "var";
            var name = p.Identifier.Text;
            var dflt = p.Default?.Value.ToString();
            return dflt is not null ? $"{type} {name} = {dflt}" : $"{type} {name}";
        }));
    }

    void SummarizeType(TypeDeclarationSyntax type, StringBuilder sb, int indent)
    {
        var pad = new string(' ', indent * 2);
        var kind = type.Kind().ToString().Replace("Declaration", "").ToLower();
        var typeName = type.Identifier.Text;

        var attrs = type.AttributeLists
            .SelectMany(a => a.Attributes)
            .Select(a => a.ToString())
            .ToList();

        var baseList = type.BaseList?.Types
            .Select(b => b.ToString())
            .ToList();

        var decl = $"{kind} {typeName}";
        if (type.TypeParameterList is not null)
            decl += type.TypeParameterList.ToString();

        if (baseList is { Count: > 0 })
            decl += $" : {string.Join(", ", baseList)}";

        if (type is ClassDeclarationSyntax cls && cls.Modifiers.Any(m => m.Text == "partial"))
            decl += " // partial";

        if (attrs.Count > 0)
            sb.AppendLine($"{pad}{string.Join(" ", attrs)}");
        sb.AppendLine($"{pad}{decl}");

        var members = type.Members;
        var methods = members.OfType<MethodDeclarationSyntax>().ToList();
        var ctors = members.OfType<ConstructorDeclarationSyntax>().ToList();
        var props = members.OfType<PropertyDeclarationSyntax>().ToList();
        var nested = members.OfType<TypeDeclarationSyntax>().ToList();

        if (ctors.Count > 0)
        {
            foreach (var c in ctors)
            {
                var cLine = c.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var cEnd = c.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
                var cParams = FormatParamList(c.ParameterList);
                sb.AppendLine($"{pad}  ctor({cParams}) @{cLine}-{cEnd}");
            }
        }

        foreach (var m in methods)
        {
            if (ObjectMethods.Contains(m.Identifier.Text)) continue;
            var mLine = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var mEnd = m.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

            var mAttrs = m.AttributeLists
                .SelectMany(a => a.Attributes)
                .Select(a => a.ToString())
                .ToList();

            var mParams = FormatParamList(m.ParameterList);
            var mRet = m.ReturnType.ToString();
            var mSig = mAttrs.Count > 0
                ? $"[{string.Join(" ", mAttrs)}] {mRet} {m.Identifier.Text}({mParams})"
                : $"{mRet} {m.Identifier.Text}({mParams})";
            sb.AppendLine($"{pad}  {mSig} @{mLine}-{mEnd}");
        }

        foreach (var p in props)
        {
            if (p.AccessorList?.Accessors.All(a => a.Body is null && a.ExpressionBody is null) == true)
                continue;

            var pLine = p.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var pEnd = p.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
            sb.AppendLine($"{pad}  {p.Type} {p.Identifier.Text} @{pLine}-{pEnd}");
        }

        foreach (var n in nested)
        {
            sb.AppendLine();
            SummarizeType(n, sb, indent + 1);
        }
    }
}
