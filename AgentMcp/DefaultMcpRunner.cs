using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentMcp;

internal sealed class DefaultMcpRunner(McpClient client) : IMcpRunner
{
    public static async Task<DefaultMcpRunner?> CreateAsync(McpServerInfo serverInfo)
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

        var client = await McpClient.CreateAsync(transport);

        return new(client);
    }

    public ValueTask<IList<McpClientTool>> GetToolsAsync()
    {
        return client.ListToolsAsync();
    }

    public ValueTask<CallToolResult> CallToolAsync(string name, IDictionary<string, JsonElement> arguments)
    {
        return client.CallToolAsync(new CallToolRequestParams()
        {
            Name = name,
            Arguments = arguments,
        });
    }
}
