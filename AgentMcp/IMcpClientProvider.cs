using ModelContextProtocol.Client;

namespace AgentMcp;

internal interface IMcpClientProvider
{
    public ValueTask<McpClient?> CreateAsync(McpServerConfiguration configuration, McpClientOptions options);
}

internal class DefaultMcpClientProvider(ILogger<DefaultMcpClientProvider> logger) : IMcpClientProvider
{
    public ValueTask<McpClient?> CreateAsync(McpServerConfiguration configuration, McpClientOptions options)
    {
        IClientTransport? transport = configuration switch
        {
            { Command: { } command } => new StdioClientTransport(new()
            {
                Command = command,
                Arguments = configuration.Args?.ToArray(),
            }),
            { Endpoint: { } endpoint } => new HttpClientTransport(new()
            {
                Endpoint = new(endpoint),
            }),
            _ => null,
        };

        if (transport is null)
        {
            logger.LogWarning("MCP server configuration is invalid. Either 'Command' or 'Endpoint' must be specified.");

            return default;
        }

        return new(McpClient.CreateAsync(transport, options)!);
    }
}
