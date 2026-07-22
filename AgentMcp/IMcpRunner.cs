using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentMcp;

internal interface IMcpRunner
{
    public ValueTask<IList<McpClientTool>> GetToolsAsync();

    public ValueTask<CallToolResult> CallToolAsync(string name, IDictionary<string, JsonElement> arguments);
}
