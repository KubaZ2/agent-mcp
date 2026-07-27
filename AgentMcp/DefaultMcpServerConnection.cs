using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentMcp;

internal class DefaultMcpServerConnection : IMcpServerConnection
{
    private readonly McpClient _client;
    private readonly ILogger _logger;

    private DefaultMcpServerConnection(McpClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    public static async Task<DefaultMcpServerConnection?> CreateAsync(McpServerConfiguration serverInfo, McpServerConnectionProperties properties, ILogger logger)
    {
        IClientTransport? transport = serverInfo switch
        {
            { Command: { } command } => new StdioClientTransport(new()
            {
                Command = command,
                Arguments = serverInfo.Args?.ToArray(),
            }),
            { Endpoint: { } endpoint } => new HttpClientTransport(new()
            {
                Endpoint = new(endpoint),
            }),
            _ => null,
        };

        if (transport is null)
            return null;

        McpClientOptions options = new();

        if (properties.ElicitationHandler is { } elicitationHandler)
        {
            options.Capabilities = new()
            {
                Elicitation = new()
                {
                    Form = new(),
                },
            };

            options.Handlers = new()
            {
                ElicitationHandler = elicitationHandler,
            };
        }

        var client = await McpClient.CreateAsync(transport, options);

        return new(client, logger);
    }

    public ValueTask<IList<McpClientTool>> GetToolsAsync()
    {
        _logger.LogInformation("Listing tools");

        return _client.ListToolsAsync();
    }

    public async ValueTask<CallToolResult> CallToolAsync(string name, IDictionary<string, JsonElement> arguments)
    {
        _logger.LogInformation("Calling tool {ToolName} with arguments: {Arguments}", name, arguments);

        var result = await _client.CallToolAsync(new CallToolRequestParams()
        {
            Name = name,
            Arguments = arguments,
        });

        _logger.LogInformation("Tool {ToolName} finished", name);

        return result;
    }
}
