using CodeNavigator;

if (args.Length > 0 && !args.Contains("--mcp"))
{
    Console.Error.WriteLine("Usage: CodeNavigator --mcp");
    return 1;
}

var server = new McpServer();
server.Run(Console.In, Console.Out);
return 0;
