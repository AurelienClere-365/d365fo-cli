using System.Text.Json;
using System.Text.Json.Nodes;
using D365FO.Core;
using D365FO.Core.Index;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace D365FO.Mcp;

/// <summary>
/// Hosts the MCP server backed by the official
/// <c>ModelContextProtocol</c> SDK. The dispatcher's responsibilities shrink
/// to:
///   • publishing <see cref="ToolCatalog"/> entries as MCP tools,
///   • routing <c>tools/call</c> to <see cref="ToolHandlers"/>,
///   • wrapping the structured <see cref="ToolResult{T}"/> envelope into an
///     MCP <see cref="CallToolResult"/> with a single text content block.
/// Everything else (framing, lifecycle, error envelopes, notifications) is
/// handled by the SDK.
/// </summary>
public static class McpServerHost
{
    private const string ServerName = "d365fo-mcp";
    private const string ServerVersion = "0.2.0";

    public static Task RunStdioAsync(string? databasePath = null, ILoggerFactory? loggerFactory = null, CancellationToken ct = default)
    {
        var settings = D365FoSettings.FromEnvironment(databasePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(settings.DatabasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var repo = new MetadataRepository(settings.DatabasePath);
        repo.EnsureSchema();
        return RunStdioAsync(new ToolHandlers(repo), loggerFactory, ct);
    }

    public static async Task RunStdioAsync(ToolHandlers handlers, ILoggerFactory? loggerFactory = null, CancellationToken ct = default)
    {
        loggerFactory ??= LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));

        var options = BuildOptions(handlers);
        var transport = new StdioServerTransport(options, loggerFactory);
        var server = McpServer.Create(transport, options, loggerFactory, serviceProvider: null);
        await server.RunAsync(ct);
    }

    public static async Task RunHttpAsync(string? databasePath = null, int port = 8080, CancellationToken ct = default)
    {
        var settings = D365FoSettings.FromEnvironment(databasePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(settings.DatabasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var repo = new MetadataRepository(settings.DatabasePath);
        repo.EnsureSchema();
        var handlers = new ToolHandlers(repo);
        var mcpOptions = BuildOptions(handlers);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddMcpServer(opts =>
        {
            opts.ServerInfo = mcpOptions.ServerInfo;
            opts.Capabilities = mcpOptions.Capabilities;
            opts.Handlers = mcpOptions.Handlers;
        }).WithHttpTransport();

        var app = builder.Build();
        app.MapMcp("/mcp");

        await app.RunAsync(ct);
    }

    public static McpServerOptions BuildOptions(ToolHandlers handlers)
    {
        var tools = ToolCatalog.All.Select(d => new Tool
        {
            Name = d.Name,
            Description = d.Description,
            InputSchema = JsonSerializer.SerializeToElement((JsonNode)d.InputSchema),
        }).ToList();

        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Version = ServerVersion },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = false },
            },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) =>
                    ValueTask.FromResult(new ListToolsResult { Tools = tools }),
                CallToolHandler = (ctx, _) => ValueTask.FromResult(Invoke(handlers, ctx.Params)),
            },
        };
    }

    public static CallToolResult Invoke(ToolHandlers handlers, CallToolRequestParams? request)
    {
        if (request is null)
            return ErrorResult("BAD_REQUEST", "Missing tools/call parameters.");

        var descriptor = ToolCatalog.All.FirstOrDefault(d => d.Name == request.Name);
        if (descriptor.Name is null)
            return ErrorResult("UNKNOWN_TOOL", $"Unknown tool: {request.Name}");

        var args = SerializeArguments(request.Arguments);

        object raw;
        try
        {
            raw = descriptor.Invoke(handlers, args);
        }
        catch (Exception ex)
        {
            raw = ToolResult<object>.Fail("HANDLER_THREW", ex.Message, ex.GetType().Name);
        }

        var body = D365Json.Serialize(raw);
        var isError = raw is ToolResult<object> tr && !tr.Ok;

        return new CallToolResult
        {
            IsError = isError,
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = body },
            },
        };
    }

    private static JsonElement SerializeArguments(IDictionary<string, JsonElement>? args)
    {
        var obj = new JsonObject();
        if (args is not null)
        {
            foreach (var kvp in args)
                obj[kvp.Key] = JsonNode.Parse(kvp.Value.GetRawText());
        }
        return JsonSerializer.SerializeToElement((JsonNode)obj);
    }

    private static CallToolResult ErrorResult(string code, string message)
    {
        var body = D365Json.Serialize(ToolResult<object>.Fail(code, message));
        return new CallToolResult
        {
            IsError = true,
            Content = new List<ContentBlock> { new TextContentBlock { Text = body } },
        };
    }
}
