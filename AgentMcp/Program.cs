using AgentMcp;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
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
    _ => throw new InvalidOperationException($"Unknown {nameof(TransportMode)}"),
};

// services.AddSingleton<IModelRunnerProvider, DefaultModelRunnerProvider>();

services.AddSingleton<IChatClientProvider, DefaultChatClientProvider>();
services.AddSingleton<IMcpClientProvider, DefaultMcpClientProvider>();
// services.AddSingleton<IMcpClientLoader, DefaultMcpClientLoader>();


// services.AddSingleton<IMcpServerConnectionProvider, DefaultMcpServerConnectionProvider>();
// services.AddSingleton<IMcpServerOrchestrator, DefaultMcpServerOrchestrator>();

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
                _ when type.Equals("anthropic", c) => providerSection.Get<AnthropicProviderConfiguration>(),
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
