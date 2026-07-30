using ModelContextProtocol.Client;

namespace AgentMcp;

internal interface IMcpClientProvider
{
    public ValueTask<McpClient?> CreateAsync(IMcpServerConfiguration configuration, McpClientOptions options);
}

internal class DefaultMcpClientProvider : IMcpClientProvider
{
    public ValueTask<McpClient?> CreateAsync(IMcpServerConfiguration configuration, McpClientOptions options)
    {
        IClientTransport? transport = configuration switch
        {
            StdioMcpServerConfiguration stdioConfig => new StdioClientTransport(new()
            {
                Command = stdioConfig.Command,
                Arguments = stdioConfig.Args?.ToArray(),
            }),
            HttpMcpServerConfiguration httpConfig => new HttpClientTransport(new()
            {
                Endpoint = new(httpConfig.Endpoint),
            }),
            _ => throw new InvalidOperationException($"Unknown {nameof(IMcpServerConfiguration)} type '{configuration.GetType().Name}'"),
        };

        return new(McpClient.CreateAsync(transport, options)!);
    }
}
