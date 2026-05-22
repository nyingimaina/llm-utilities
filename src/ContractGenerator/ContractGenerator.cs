using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContractGenerator;

static class ContractGenerator
{
    static readonly HashSet<string> ValueTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "long", "short", "byte", "sbyte", "ushort", "uint", "ulong",
        "double", "float", "decimal",
        "bool", "char",
    };

    static readonly HashSet<string> StringLikeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "datetime", "datetimeoffset", "guid",
    };

    static readonly HashSet<string> CollectionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ienumerable", "list", "icollection", "ireadonlylist", "ireadonlycollection",
        "hashset", "immutablelist", "immutablehashset",
    };

    static readonly HashSet<string> DictionaryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "idictionary", "dictionary", "ireadonlydictionary",
        "immutabledictionary", "immutablesorteddictionary",
    };

    public record Result(Dictionary<string, string> Interfaces, List<string> Errors);

    public static Result Generate(string csPath)
    {
        var source = File.ReadAllText(csPath, Encoding.UTF8);
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => t.Modifiers.Any(m => m.Text is "public" or "internal"))
            .ToList();

        var interfaces = new Dictionary<string, string>();
        var errors = new List<string>();

        foreach (var type in types)
        {
            try
            {
                var ts = GenerateInterface(type);
                var interfaceName = $"I{type.Identifier.Text}";
                interfaces[interfaceName] = ts;
            }
            catch (Exception ex)
            {
                errors.Add($"Error processing {type.Identifier.Text}: {ex.Message}");
            }
        }

        return new Result(interfaces, errors);
    }

    static string GenerateInterface(TypeDeclarationSyntax type)
    {
        var typeName = type.Identifier.Text;
        var sb = new StringBuilder();

        if (type is ClassDeclarationSyntax cls && cls.Modifiers.Any(m => m.Text == "abstract"))
            sb.AppendLine("// NOTE: abstract class — manual review recommended");

        var baseTypes = type.BaseList?.Types
            .Select(b => b.ToString())
            .Where(b => !b.StartsWith("I")) // skip interfaces (imported separately)
            .ToList();

        var extends = "";
        if (baseTypes is { Count: > 0 })
        {
            var filtered = baseTypes.Where(b => !IsSystemType(b)).ToList();
            if (filtered.Count > 0)
                extends = $" extends I{filtered[0]}";
        }

        if (type.TypeParameterList is not null)
        {
            var params_ = type.TypeParameterList.Parameters.Select(p => p.ToString());
            extends += $"<{string.Join(", ", params_)}>";
        }

        sb.AppendLine($"export default interface I{typeName}{extends} {{");

        var props = type.Members.OfType<PropertyDeclarationSyntax>();
        foreach (var prop in props)
        {
            if (HasAttribute(prop, "JsonIgnore"))
                continue;

            var tsName = GetPropertyName(prop);
            var tsType = MapType(prop.Type.ToString(), prop);
            sb.AppendLine($"  {tsName}: {tsType};");
        }

        var fields = type.Members.OfType<FieldDeclarationSyntax>()
            .Where(f => f.Modifiers.Any(m => m.Text is "public" or "internal"));
        foreach (var field in fields)
        {
            foreach (var variable in field.Declaration.Variables)
            {
                if (HasAttribute(field, "JsonIgnore"))
                    continue;

                var tsName = ToCamelCase(variable.Identifier.Text);
                var tsType = MapType(field.Declaration.Type.ToString(), field);
                sb.AppendLine($"  {tsName}: {tsType};");
            }
        }

        // Handle record primary constructor parameters
        if (type is RecordDeclarationSyntax record)
        {
            var paramList = record.ParameterList;
            if (paramList is not null)
            {
                foreach (var param in paramList.Parameters)
                {
                    var tsName = ToCamelCase(param.Identifier.Text);
                    var tsType = MapType(param.Type?.ToString() ?? "unknown", null);
                    sb.AppendLine($"  {tsName}: {tsType};");
                }
            }
        }

        var enumVals = type.Members.OfType<EnumMemberDeclarationSyntax>().ToList();
        if (enumVals.Count > 0)
        {
            sb.AppendLine("  // NOTE: C# enum values — consider narrowing");
        }

        // Check for custom JsonConverter
        var hasConverter = type.AttributeLists
            .SelectMany(a => a.Attributes)
            .Any(a => a.Name.ToString().Contains("JsonConverter"));
        if (hasConverter)
            sb.AppendLine("  // NOTE: custom JsonConverter — verify TypeScript type manually");

        sb.AppendLine("}");
        return sb.ToString();
    }

    static string GetPropertyName(PropertyDeclarationSyntax prop)
    {
        var jsonName = prop.AttributeLists
            .SelectMany(a => a.Attributes)
            .FirstOrDefault(a => a.Name.ToString() is "JsonPropertyName" or "JsonPropertyNameAttribute");
        if (jsonName is not null)
        {
            var arg = jsonName.ArgumentList?.Arguments.First().ToString().Trim('"');
            if (arg is not null) return arg;
        }
        return ToCamelCase(prop.Identifier.Text);
    }

    static string MapType(string csType, SyntaxNode? context)
    {
        var nullable = csType.EndsWith("?");
        if (nullable) csType = csType.TrimEnd('?');

        var baseType = csType;
        var isArray = csType.EndsWith("[]");
        if (isArray) baseType = csType.TrimEnd('[', ']');
        var isNullableGeneric = csType.StartsWith("Nullable<") && csType.EndsWith(">");

        string result;

        if (isNullableGeneric)
        {
            var inner = csType.Substring("Nullable<".Length).TrimEnd('>');
            result = MapType(inner, context) + " | null";
            return result;
        }

        // Check for generic collections: List<T>, Dictionary<K,V>, etc.
        var genericMatch = System.Text.RegularExpressions.Regex.Match(csType, @"^(\w+)<(.+)>$");
        if (genericMatch.Success)
        {
            var genericName = genericMatch.Groups[1].Value;
            var genericArgs = genericMatch.Groups[2].Value;

            if (CollectionTypes.Contains(genericName))
            {
                result = MapType(genericArgs, context) + "[]";
                goto done;
            }

            if (DictionaryTypes.Contains(genericName))
            {
                var parts = SplitGenericArgs(genericArgs);
                var k = parts.Length > 0 ? MapType(parts[0], context) : "string";
                var v = parts.Length > 1 ? MapType(parts[1], context) : "unknown";
                result = $"Record<{k}, {v}>";
                goto done;
            }
        }

        if (isArray)
        {
            result = MapType(baseType, context) + "[]";
            goto done;
        }

        if (csType is "object") { result = "unknown"; goto done; }
        if (csType is "void") { result = "void"; goto done; }
        if (csType is "bool" or "boolean") { result = "boolean"; goto done; }

        if (ValueTypes.Contains(csType)) { result = "number"; goto done; }
        if (StringLikeTypes.Contains(csType)) { result = "string"; goto done; }

        // Reference to another type — assume it's a model class
        if (csType.Length > 0 && char.IsUpper(csType[0]) && csType != "Task")
        {
            result = csType;
            goto done;
        }

        result = csType;

    done:
        if (nullable)
            result += " | null";
        return result;
    }

    static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name.Length == 1) return name.ToLowerInvariant();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    static string[] SplitGenericArgs(string args)
    {
        var depth = 0;
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var c in args)
        {
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0) { parts.Add(current.ToString().Trim()); current.Clear(); continue; }
            current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString().Trim());
        return parts.ToArray();
    }

    static bool HasAttribute(SyntaxNode node, string name)
    {
        return node is MemberDeclarationSyntax member && member.AttributeLists
            .SelectMany(a => a.Attributes)
            .Any(a => a.Name.ToString() is var n && (n == name || n == $"{name}Attribute"));
    }

    static bool IsSystemType(string name)
    {
        return name is "object" or "ValueType" or "Enum" or "IEquatable<T>" or "IComparable<T>";
    }
}
