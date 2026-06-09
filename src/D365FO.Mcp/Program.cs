using D365FO.Mcp;

// Entry point for the `d365fo-mcp` executable. Wires the default
// MetadataRepository + ToolHandlers stack and runs an MCP server over stdio
// (official `ModelContextProtocol` SDK) until stdin closes, or over
// streamable-http when --transport streamable-http is specified.
//
// Usage:
//   d365fo-mcp                                         # stdio (default)
//   d365fo-mcp --transport streamable-http --port 8080 # HTTP server
//   d365fo-mcp --db /path/to/idx.sqlite                # override DB path
//   d365fo-mcp --legacy                                # use built-in StdioDispatcher (no SDK)

string? dbPath = null;
bool legacy = false;
string transport = "stdio";
int port = 8080;
for (int i = 0; i < args.Length; i++)
{
    if ((args[i] == "--db" || args[i] == "-d") && i + 1 < args.Length)
    {
        dbPath = args[++i];
    }
    else if (args[i] == "--legacy")
    {
        legacy = true;
    }
    else if ((args[i] == "--transport" || args[i] == "-t") && i + 1 < args.Length)
    {
        transport = args[++i];
    }
    else if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length)
    {
        int.TryParse(args[++i], out port);
    }
    else if (args[i] == "--help" || args[i] == "-h")
    {
        Console.Error.WriteLine("""
            d365fo-mcp — Model Context Protocol server for D365 F&O metadata.

            Options:
              --db, -d <PATH>        Override index database path.
              --transport <MODE>     Transport: stdio (default) or streamable-http.
              --port <PORT>          Port for streamable-http transport (default: 8080).
              --legacy               Use the built-in StdioDispatcher (pre-SDK transport).
              --help, -h             Print this message.

            Route any log output to stderr — stdout is reserved for protocol frames.
            """);
        return 0;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (transport == "streamable-http" || transport == "http")
{
    await McpServerHost.RunHttpAsync(dbPath, port, cts.Token);
}
else if (legacy)
{
    var dispatcher = StdioDispatcher.CreateDefault(dbPath);
    await dispatcher.RunAsync(Console.In, Console.Out, cts.Token);
}
else
{
    await McpServerHost.RunStdioAsync(dbPath, loggerFactory: null, cts.Token);
}
return 0;
