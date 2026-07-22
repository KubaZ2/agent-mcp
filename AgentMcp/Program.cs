using ModelContextProtocol.Extensions.Tasks;
using System.ComponentModel.DataAnnotations;
using AgentMcp;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.ComponentModel;
using ModelContextProtocol.Protocol;

TransportMode mode;

switch (args)
{
    case ["stdio", ..]:
        args = args[1..];
        goto default;
    case ["http", ..]:
        mode = TransportMode.Http;
        args = args[1..];
        break;
    default:
        mode = TransportMode.Stdio;
        break;
}

IHostApplicationBuilder builder = mode switch
{
    TransportMode.Stdio => Host.CreateApplicationBuilder(args),
    TransportMode.Http => WebApplication.CreateSlimBuilder(args),
    _ => throw new InvalidOperationException("Unknown TransportMode"),
};

var x = Host.CreateApplicationBuilder();

builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

var services = builder.Services;

var mcpServerBuilder = services
    .AddMcpServer()
    .WithTools<Name>()
    .WithTasks(new InMemoryMcpTaskStore());

_ = mode switch
{
    TransportMode.Stdio => mcpServerBuilder.WithStdioServerTransport(),
    TransportMode.Http => mcpServerBuilder.WithHttpTransport(),
    _ => throw new InvalidOperationException("Unknown TransportMode"),
};

services
    .AddSingleton<IModelRunnerProvider, DefaultModelRunnerProvider>();

services.AddSingleton<RunAgentProvider>();
services.AddHostedService(services => services.GetRequiredService<RunAgentProvider>());
services.AddSingleton(services => services.GetRequiredService<RunAgentProvider>().GetTool());

services
    .AddOptions<Options>()
    .BindConfiguration(string.Empty)
    .ValidateDataAnnotations()
    .ValidateOnStart();

IHost host = mode switch
{
    TransportMode.Stdio => ((HostApplicationBuilder)builder).Build(),
    TransportMode.Http => ((WebApplicationBuilder)builder).Build(),
    _ => throw new InvalidOperationException("Unknown TransportMode"),
};

if (mode is TransportMode.Http)
{
    var app = (WebApplication)host;

    app.MapMcp();
}

await host.RunAsync();

internal enum TransportMode
{
    Stdio,
    Http,
}

internal class Name(McpServer server)
{
    [McpServerTool, Description("Provides the LLM name")]
    public async Task<string> GetNameAsync()
    {
        await Task.Delay(5_000);

        if (server.ClientCapabilities is not { Elicitation: { } })
            return "Edward (No elicitation support)";

        var userInput = await server.ElicitAsync(new()
        {
            Message = "Full?",
            RequestedSchema = new()
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    ["action"] = new ElicitRequestParams.BooleanSchema()
                    {
                        Title = "Return full name?",
                        Description = "Whether to return the full name or just the first name.",
                        Default = true,
                    },
                },
            }
        });

        await Task.Delay(5_000);

        if (userInput.Action == "accept" && userInput.Content?["action"].ValueKind == System.Text.Json.JsonValueKind.True)
            return "Edward Warchocki";

        return "Edward";
    }
}

internal class Options
{
    [Required]
    public required IReadOnlyDictionary<string, AgentInfo> Agents { get; set; }

    [ValidateEnumeratedItems]
    public IEnumerable<AgentInfo>? AgentValues => Agents?.Values;
}

internal class AgentInfo
{
    public string? Description { get; set; }

    public string? SystemPrompt { get; set; }

    [Required]
    public required string Provider { get; set; }

    [Required]
    public required string Model { get; set; }

    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    public IReadOnlyDictionary<string, McpServerInfo>? Mcp { get; set; }

    [ValidateEnumeratedItems]
    public IEnumerable<McpServerInfo>? McpValues => Mcp?.Values;
}

internal class McpServerInfo
{
    public string? Command { get; set; }

    public IReadOnlyList<string>? Args { get; set; }

    public string? Endpoint { get; set; }
}
