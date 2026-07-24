using System.ComponentModel.DataAnnotations;
using AgentMcp;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Extensions.Tasks;

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
    _ => throw new InvalidOperationException($"Unknown {nameof(TransportMode)}"),
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

services.AddSingleton<IMcpServerConnectionProvider, DefaultMcpServerConnectionProvider>();
services.AddSingleton<IMcpServerOrchestrator, DefaultMcpServerOrchestrator>();

services.AddSingleton<RunAgentProvider>();
services.AddHostedService(services => services.GetRequiredService<RunAgentProvider>());
services.AddSingleton(services => services.GetRequiredService<RunAgentProvider>().GetTool());

services.AddSingleton<IValidateOptions<Options>, Options.Validator>();

services
    .AddOptions<Options>()
    .Configure((Options options, IConfiguration configuration) =>
    {
        var section = configuration.GetSection("Providers");

        Dictionary<string, IProviderConfiguration> providers = [];

        foreach (var providerSection in section.GetChildren())
        {
            var providerName = providerSection.Key;

            var type = providerSection.GetValue<string>("Type")
                ?? throw new InvalidOperationException($"Provider '{providerName}' is missing the 'Type' property.");

            const StringComparison c = StringComparison.InvariantCultureIgnoreCase;

            IProviderConfiguration? providerInfo = type switch
            {
                _ when type.Equals("openai", c) => providerSection.Get<OpenAIProviderConfiguration>(),
                _ when type.Equals("ollama", c) => providerSection.Get<OllamaProviderConfiguration>(),
                _ => throw new InvalidOperationException($"Unknown provider type '{type}' for provider '{providerName}'."),
            };

            if (providerInfo is null)
                throw new InvalidOperationException($"Provider '{providerName}' of type '{type}' could not be bind to a configuration object.");

            if (!providers.TryAdd(providerName, providerInfo))
                throw new InvalidOperationException($"Duplicate provider name '{providerName}' found in configuration.");
        }

        options.Providers = providers;
    })
    .BindConfiguration(string.Empty);

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

internal partial class Options
{
    [OptionsValidator]
    internal partial class Validator : IValidateOptions<Options>;

    internal IReadOnlyDictionary<string, IProviderConfiguration> Providers { get; set; } = null!;

    [Required]
    public IReadOnlyDictionary<string, AgentConfiguration> Agents { get; set; } = null!;

    public IReadOnlyDictionary<string, McpServerConfiguration>? Mcp { get; set; }

    [ValidateEnumeratedItems]
    public IEnumerable<AgentConfiguration>? AgentValues => Agents?.Values;
}

internal interface IProviderConfiguration
{
}

internal class OpenAIProviderConfiguration : IProviderConfiguration
{
    public string? ApiKey { get; set; }

    public string? Endpoint { get; set; }
}

internal class OllamaProviderConfiguration : IProviderConfiguration
{
    public string? Endpoint { get; set; }
}

internal class AgentConfiguration
{
    public string? Description { get; set; }

    public string? SystemPrompt { get; set; }

    [Required]
    public string Provider { get; set; } = null!;

    [Required]
    public string Model { get; set; } = null!;

    public IReadOnlyList<string>? Mcp { get; set; }
}

internal class McpServerConfiguration
{
    public string? Command { get; set; }

    public IReadOnlyList<string>? Args { get; set; }

    public string? Endpoint { get; set; }

    public string? Name { get; set; }
}
