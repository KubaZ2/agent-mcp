using ModelContextProtocol.Client;

namespace AgentMcp;

internal record McpServerToolInfo(string Name, McpClientTool Tool);

internal record McpServerConnectionInfo(string ServerName, IMcpServerConnection Connection);

internal record McpServerInfo(McpServerConnectionInfo ConnectionInfo, IReadOnlyList<McpServerToolInfo> Tools);

internal interface IMcpServerOrchestrator
{
    public Task<IReadOnlyList<McpServerInfo>> RunAsync(IReadOnlyList<string> mcpServerKeys);
}
