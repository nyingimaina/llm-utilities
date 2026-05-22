namespace FWriter;

public record ValidationResult(bool Valid, string[] Errors);
public record EditResult(bool Success, string? BackupPath, string? OldBody, string? Error);
public record CreateResult(bool Success, string? Error);

public interface IWriteProvider
{
    string Language { get; }
    ValidationResult Validate(string path);
    EditResult EditFunctionBody(string path, string name, string newBody, string[]? parameterTypes, string? className, bool skipValidation = false);
    CreateResult CreateFile(string path, string content, bool overwrite);
}
