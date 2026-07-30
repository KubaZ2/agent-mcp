using System.Diagnostics.CodeAnalysis;
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

HostApplicationBuilder CreateApplicationBuilder()
{
    return Host.CreateEmptyApplicationBuilder(new() { Args = args });
}

WebApplicationBuilder CreateWebApplicationBuilder()
{
    var builder = WebApplication.CreateEmptyBuilder(new() { Args = args });

    builder.WebHost.UseKestrel();
    builder.Services.AddRoutingCore();

    return builder;
}

IHostApplicationBuilder builder = mode switch
{
    TransportMode.Stdio => CreateApplicationBuilder(),
    TransportMode.Http => CreateWebApplicationBuilder(),
    _ => throw new InvalidOperationException($"Unknown {nameof(TransportMode)}"),
};

var configuration = builder.Configuration;

configuration.AddEnvironmentVariables();

if (configuration.GetValue<string>("Config") is { } configPath)
    _ = Path.GetExtension(configPath) switch
    {
        ".json" => configuration.AddJsonFile(configPath, optional: false, reloadOnChange: true),
        ".ini" => configuration.AddIniFile(configPath, optional: false, reloadOnChange: true),
        var extenion => throw new InvalidOperationException($"Unknown config file extension '{extenion}'"),
    };

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

services.AddSingleton<IChatClientProvider, DefaultChatClientProvider>();
services.AddSingleton<IMcpClientProvider, DefaultMcpClientProvider>();
services.AddSingleton<IToolInvocationFilter, DefaultToolInvocationFilter>();
services.AddSingleton<IToolInvocationFilterProvider, DefaultToolInvocationFilterProvider>();

services.AddSingleton<RunAgentProvider>();
services.AddHostedService(services => services.GetRequiredService<RunAgentProvider>());
services.AddSingleton(services => services.GetRequiredService<RunAgentProvider>().GetTool());

services
    .AddOptions<Options>()
    .Validate<Options.Validator>()
    .BindConfiguration(string.Empty);

services.AddSingleton<IValidateOptions<StdioMcpServerConfiguration>, StdioMcpServerConfiguration.Validator>();
services.AddSingleton<IValidateOptions<HttpMcpServerConfiguration>, HttpMcpServerConfiguration.Validator>();

services.AddSingleton<IConfigureOptions<Options>, ConfigureOptions>();

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
