namespace FReader;

public static class FReaderError
{
    public const string NOT_FOUND = "NOT_FOUND";
    public const string AMBIGUOUS = "AMBIGUOUS";
    public const string INVALID_RANGE = "INVALID_RANGE";
    public const string BINARY_FILE = "BINARY_FILE";
    public const string TIMEOUT = "TIMEOUT";
    public const string REGEX_TIMEOUT = "REGEX_TIMEOUT";
    public const string UNSUPPORTED_LANGUAGE = "UNSUPPORTED_LANGUAGE";
    public const string READ_ERROR = "READ_ERROR";
    public const string GREP_ERROR = "GREP_ERROR";
    public const string INVALID_REGEX = "INVALID_REGEX";
    public const string EMPTY_PATTERN = "EMPTY_PATTERN";
    public const string FILE_NOT_FOUND = "FILE_NOT_FOUND";
    public const string INVALID_REQUEST = "INVALID_REQUEST";
    public const string MAX_CHARS_EXCEEDED = "MAX_CHARS_EXCEEDED";

    public static Dictionary<string, object?> Make(string message, string code, params (string Key, object? Value)[] extras)
    {
        var dict = new Dictionary<string, object?>
        {
            ["error"] = message,
            ["error_code"] = code
        };
        foreach (var (key, value) in extras)
            dict[key] = value;
        return dict;
    }
}
