using ModelContextProtocol.Client;

namespace AgentMcp;

internal sealed class HttpMcpRunner : McpRunner
{
    private HttpMcpRunner(McpClient client) : base(client)
    {
    }

    public new static async Task<HttpMcpRunner> CreateAsync(McpServerInfo serverInfo)
    {
        HttpClientTransport transport = new(new()
        {
            Endpoint = new(serverInfo.Endpoint!),
        });

        var client = await McpClient.CreateAsync(transport);

        return new(client);
    }
}

