using System.Text.RegularExpressions;

namespace FReader;

public static class TypeAliaser
{
    static readonly Regex WordRegex = new(@"\b[A-Z][a-zA-Z0-9]{2,}\b", RegexOptions.Compiled);

    static readonly HashSet<string> PrimitiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "string", "bool", "long", "double", "float", "short",
        "byte", "char", "object", "decimal", "uint", "ulong", "ushort",
        "sbyte", "nint", "nuint",
        "Guid", "DateTime", "TimeSpan", "DateTimeOffset",
        "Task", "ValueTask", "List", "Dictionary",
        "IEnumerable", "ICollection", "IList", "ISet",
        "IReadOnlyList", "IReadOnlyCollection", "IReadOnlyDictionary",
        "HashSet", "Stack", "Queue",
        "String", "Boolean", "Int32", "Int64", "Double", "Single",
        "Decimal", "Char", "Byte", "Int16", "UInt32", "UInt64",
        "Object", "Void", "Exception", "InvalidOperationException",
        "ArgumentNullException", "ArgumentException", "NotSupportedException",
        "IDisposable", "IAsyncDisposable", "IEnumerable", "IEnumerator",
        "IQueryable", "CancellationToken",
        "ToString", "GetHashCode", "Equals", "GetType",
        "Select", "Where", "Join", "OrderBy", "GroupBy", "First", "Last",
        "Single", "Count", "Sum", "Min", "Max", "Any", "All", "ToList",
        "ToArray", "FirstOrDefault", "LastOrDefault", "SingleOrDefault",
        "ThenBy", "Distinct", "Cast", "OfType",
        "Line", "Type", "Name", "Value", "Key", "Item", "Length",
        "Index", "Start", "End", "Kind", "Mode", "State", "Status",
        "Code", "Data", "Info", "Text", "Path", "File", "Size",
        "Result", "Error", "Source", "Target",
        "Func", "Action", "Predicate", "Tuple", "Nullable", "Task", "ValueTask",
        "List", "Dictionary", "HashSet", "Queue", "Stack", "SortedList",
        "IEnumerable", "ICollection", "IList", "IDictionary", "ISet",
        "IQueryable", "IComparer", "IEqualityComparer",
        "StringBuilder", "StringReader", "StringWriter",
        "StreamReader", "StreamWriter", "MemoryStream", "FileStream",
        "Console", "Convert", "Math", "Random", "Regex", "Stopwatch",
        "Array", "Buffer", "BitConverter"
    };

    public static (Dictionary<string, string>? Aliases, string[] OutputLines, string? Warning) Apply(
        string[] lines,
        int minOccurrences = 3,
        int minAliasLen = 2)
    {
        var contentOnly = lines.Select(l =>
        {
            var tabIdx = l.IndexOf('\t');
            return tabIdx >= 0 ? l.Substring(tabIdx + 1) : l;
        }).ToList();

        var allCode = string.Join("\n", contentOnly);

        // Count type name occurrences
        var counts = new Dictionary<string, int>();
        foreach (Match m in WordRegex.Matches(allCode))
        {
            var word = m.Value;
            if (PrimitiveTypes.Contains(word)) continue;
            counts[word] = counts.GetValueOrDefault(word) + 1;
        }

        var toAlias = counts
            .Where(kv => kv.Value >= minOccurrences)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        if (toAlias.Count == 0)
            return (null, lines, null);

        // Collect all existing identifiers in the content
        var existingWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(allCode, @"\b[a-zA-Z_]\w*\b"))
            existingWords.Add(m.Value);

        var aliasMap = new Dictionary<string, string>();
        var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        usedAliases.UnionWith(existingWords);

        foreach (var typeName in toAlias)
        {
            var alias = GenerateAlias(typeName, usedAliases, minAliasLen);
            aliasMap[typeName] = alias;
            usedAliases.Add(alias);
        }

        // Apply replacements — longest names first to avoid partial replacement
        var sorted = toAlias.OrderByDescending(n => n.Length).ToList();
        var resultLines = new string[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            var tabIdx = l.IndexOf('\t');
            if (tabIdx < 0) { resultLines[i] = l; continue; }

            var prefix = l.Substring(0, tabIdx + 1);
            var content = l.Substring(tabIdx + 1);

            foreach (var typeName in sorted)
            {
                content = Regex.Replace(content, @"\b" + Regex.Escape(typeName) + @"\b", aliasMap[typeName]);
            }
            resultLines[i] = prefix + content;
        }

        return (aliasMap, resultLines, "Aliases active \u2014 use full type names when writing code, not aliases");
    }

    static string GenerateAlias(string typeName, HashSet<string> usedAliases, int minLen)
    {
        var initials = new string(typeName.Where(char.IsUpper).ToArray());
        if (initials.Length < minLen)
        {
            initials = typeName.Length >= minLen
                ? typeName.Substring(0, minLen).ToUpperInvariant()
                : typeName.ToUpperInvariant();
        }

        var alias = initials;
        int suffix = 2;
        while (usedAliases.Contains(alias))
        {
            alias = initials + suffix;
            suffix++;
        }

        return alias;
    }
}
