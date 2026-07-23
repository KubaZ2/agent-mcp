using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using AgentMcp;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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

builder.Configuration.AddIniFile("appsettings.ini", optional: true, reloadOnChange: true);

builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

var services = builder.Services;

var mcpServerBuilder = services
    .AddMcpServer()
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
    .Configure((Options options, IConfiguration configuration) =>
    {
        var section = configuration.GetSection("Providers");

        Dictionary<string, IProviderInfo> providers = [];

        foreach (var providerSection in section.GetChildren())
        {
            var providerName = providerSection.Key;

            var type = providerSection.GetValue<string>("Type")
                ?? throw new InvalidOperationException($"Provider '{providerName}' is missing the 'Type' property.");

            var providerInfo = type switch
            {
                "OpenAI" => providerSection.Get<OpenAIProviderInfo>() ?? throw new InvalidOperationException($"Failed to bind provider '{providerName}' to OpenAIProviderInfo."),
                _ => throw new InvalidOperationException($"Unknown provider type '{type}' for provider '{providerName}'."),
            };

            if (!providers.TryAdd(providerName, providerInfo))
                throw new InvalidOperationException($"Duplicate provider name '{providerName}' found in configuration.");
        }

        options.Providers = providers;
    })
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

internal class Options
{
    [Required]
    public required IReadOnlyDictionary<string, IProviderInfo> Providers { get; set; }

    [Required]
    public required IReadOnlyDictionary<string, AgentInfo> Agents { get; set; }

    public IReadOnlyDictionary<string, McpServerInfo>? Mcp { get; set; }

    [ValidateEnumeratedItems]
    public IEnumerable<IProviderInfo>? ProviderValues => Providers?.Values;

    [ValidateEnumeratedItems]
    public IEnumerable<AgentInfo>? AgentValues => Agents?.Values;
}

internal interface IProviderInfo
{
}

internal class OpenAIProviderInfo : IProviderInfo
{
    public string? ApiKey { get; set; }

    public string? Endpoint { get; set; }
}

internal class AgentInfo
{
    public string? Description { get; set; }

    public string? SystemPrompt { get; set; }

    [Required]
    public required string Provider { get; set; }

    [Required]
    public required string Model { get; set; }

    public IReadOnlyList<string>? Mcp { get; set; }
}

internal class McpServerInfo
{
    public string? Command { get; set; }

    public IReadOnlyList<string>? Args { get; set; }

    public string? Endpoint { get; set; }

    public string? Name { get; set; }
}
