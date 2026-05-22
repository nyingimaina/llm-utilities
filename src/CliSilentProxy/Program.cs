using CliSilentProxy;

if (args.Length > 0 && !args.Contains("--mcp"))
{
    Console.Error.WriteLine("Usage: CliSilentProxy --mcp");
    return 1;
}

var server = new CliSilentProxyServer();
server.Run(Console.In, Console.Out);
return 0;
