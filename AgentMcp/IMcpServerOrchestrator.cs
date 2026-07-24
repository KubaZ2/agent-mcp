using Microsoft.Extensions.AI;

namespace AgentMcp;

internal record McpServerConnectionInfo(string ServerName, IMcpServerConnection Connection);

internal record McpServerInfo(McpServerConnectionInfo ConnectionInfo, IReadOnlyList<AIFunction> Tools);

internal interface IMcpServerOrchestrator
{
    public Task<IReadOnlyList<McpServerInfo>> RunAsync(IReadOnlyList<string> mcpServerKeys);
}
