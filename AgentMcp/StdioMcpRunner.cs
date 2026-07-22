using ModelContextProtocol.Client;

namespace AgentMcp;

internal sealed class StdioMcpRunner : McpRunner
{
    private StdioMcpRunner(McpClient client) : base(client)
    {
    }

    public new static async Task<StdioMcpRunner> CreateAsync(McpServerInfo serverInfo)
    {
        StdioClientTransport transport = new(new()
        {
            Command = serverInfo.Command!,
            Arguments = serverInfo.Args?.ToArray(),
        });

        var client = await McpClient.CreateAsync(transport);

        return new(client);
    }
}
