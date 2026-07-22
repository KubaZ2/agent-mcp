using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentMcp;

internal abstract class McpRunner(McpClient client) : IMcpRunner
{
    public static async Task<McpRunner?> CreateAsync(McpServerInfo serverInfo)
    {
        return serverInfo switch
        {
            { Command: { } } => await StdioMcpRunner.CreateAsync(serverInfo),
            { Endpoint: { } } => await HttpMcpRunner.CreateAsync(serverInfo),
            _ => null,
        };
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
