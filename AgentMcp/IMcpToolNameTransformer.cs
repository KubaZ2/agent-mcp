using ModelContextProtocol.Client;

namespace AgentMcp;

internal interface IMcpToolNameTransformer
{
    public string Transform(McpClientTool tool, McpServerConnectionInfo connectionInfo);
}
