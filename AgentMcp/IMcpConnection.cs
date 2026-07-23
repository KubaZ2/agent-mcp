using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentMcp;

internal interface IMcpServerConnection
{
    public ValueTask<IList<McpClientTool>> GetToolsAsync();

    public ValueTask<CallToolResult> CallToolAsync(string name, IDictionary<string, JsonElement> arguments);
}
