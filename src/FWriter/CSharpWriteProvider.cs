using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FWriter;

public sealed class CSharpWriteProvider : IWriteProvider
{
    public string Language => "C#";

    public ValidationResult Validate(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var tree = CSharpSyntaxTree.ParseText(text);
            var diags = tree.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"Line {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}")
                .ToArray();
            return new ValidationResult(diags.Length == 0, diags);
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, [ex.Message]);
        }
    }

    public EditResult EditFunctionBody(string path, string name, string newBody, string[]? parameterTypes, string? className, bool skipValidation = false)
    {
        try
        {
            var text = File.ReadAllText(path);
            var tree = CSharpSyntaxTree.ParseText(text);
            var root = tree.GetRoot();

            var methods = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>();
            var targets = methods.Where(m =>
            {
                var id = m switch
                {
                    MethodDeclarationSyntax md => md.Identifier.Text,
                    ConstructorDeclarationSyntax cd => cd.Identifier.Text,
                    DestructorDeclarationSyntax dd => dd.Identifier.Text,
                    _ => null
                };
                return id != null && id.Equals(name, StringComparison.OrdinalIgnoreCase);
            }).ToList();

            if (targets.Count == 0)
                return new EditResult(false, null, null, $"Function '{name}' not found");

            var target = targets[0];
            var oldBody = target.Body?.ToFullString() ?? target.ExpressionBody?.ToFullString() ?? "";

            // Parse new body as a synthetic method to get the block
            var synthetic = $"void _() {{ {newBody} }}";
            var synTree = CSharpSyntaxTree.ParseText(synthetic);
            var synRoot = synTree.GetRoot();
            var synMethod = synRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
            var newBlock = synMethod.Body;

            BaseMethodDeclarationSyntax newMethod;
            if (newBlock != null && target.Body != null)
                newMethod = target.WithBody(newBlock);
            else if (newBlock != null && target.ExpressionBody != null)
                newMethod = target.WithBody(newBlock).WithExpressionBody(null!);
            else
                return new EditResult(false, null, null, "Cannot replace body: target has no body");

            var newRoot = root.ReplaceNode(target, newMethod);
            var newText = newRoot.ToFullString();

            if (skipValidation)
            {
                var svBackup = path + ".bak";
                try { File.Copy(path, svBackup, overwrite: true); }
                catch { return new EditResult(false, null, null, "Backup failed"); }
                try
                {
                    File.WriteAllText(path, newText);
                    _ = File.ReadAllText(path);
                    return new EditResult(true, oldBody, null, null);
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(svBackup)) File.Copy(svBackup, path, overwrite: true); } catch { }
                    return new EditResult(false, null, null, $"Write failed: {ex.Message}");
                }
                finally { try { if (File.Exists(svBackup)) File.Delete(svBackup); } catch { } }
            }

            // Validate result parses
            var newTree = CSharpSyntaxTree.ParseText(newText);
            var errors = newTree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                var msgs = string.Join("; ", errors.Select(e =>
                    $"Line {e.Location.GetLineSpan().StartLinePosition.Line + 1}: {e.GetMessage()}"));
                return new EditResult(false, null, null, $"Result has syntax errors: {msgs}");
            }

            // Backup + write + verify
            var backupPath = path + ".bak";
            File.Copy(path, backupPath, overwrite: true);
            try
            {
                File.WriteAllText(path, newText);
                var verify = File.ReadAllText(path);
                if (verify != newText)
                {
                    File.Copy(backupPath, path, overwrite: true);
                    return new EditResult(false, null, null, "Write verification failed: content mismatch after write");
                }
                File.Delete(backupPath);
                return new EditResult(true, null, oldBody, null);
            }
            catch (Exception ex)
            {
                if (File.Exists(backupPath))
                    File.Copy(backupPath, path, overwrite: true);
                return new EditResult(false, null, null, $"Write failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            return new EditResult(false, null, null, ex.Message);
        }
    }

    public CreateResult CreateFile(string path, string content, bool overwrite)
    {
        try
        {
            if (File.Exists(path) && !overwrite)
                return new CreateResult(false, $"File already exists: {path}");

            // Validate content parses
            var tree = CSharpSyntaxTree.ParseText(content);
            var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                var msgs = string.Join("; ", errors.Select(e =>
                    $"Line {e.Location.GetLineSpan().StartLinePosition.Line + 1}: {e.GetMessage()}"));
                return new CreateResult(false, $"Content has syntax errors: {msgs}");
            }

            File.WriteAllText(path, content);
            var verify = File.ReadAllText(path);
            if (verify != content)
                return new CreateResult(false, "Write verification failed: content mismatch after write");

            return new CreateResult(true, null);
        }
        catch (Exception ex)
        {
            return new CreateResult(false, ex.Message);
        }
    }
}
