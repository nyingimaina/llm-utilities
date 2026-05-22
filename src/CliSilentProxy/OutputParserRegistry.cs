namespace CliSilentProxy;

static class OutputParserRegistry
{
    static readonly List<IOutputParser> _parsers = new()
    {
        new Parsers.DotnetBuildParser(),
        new Parsers.DotnetTestParser(),
        new Parsers.FlutterDartAnalyzeParser(),
        new Parsers.TscParser(),
        new Parsers.EslintParser(),
        new Parsers.RuffParser(),
        new Parsers.TestRunnerParser(),
        new Parsers.CargoParser(),
        new Parsers.NpmAuditParser(),
        new Parsers.MypyParser(),
        new Parsers.GoVetParser(),
    };

    public static IOutputParser? Find(string executable, string[] args)
    {
        if (string.IsNullOrEmpty(executable)) return null;
        var exeName = Path.GetFileNameWithoutExtension(executable);
        foreach (var parser in _parsers)
        {
            if (parser.CanHandle(exeName, args))
                return parser;
        }
        return null;
    }
}
