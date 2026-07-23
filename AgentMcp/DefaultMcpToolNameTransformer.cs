using ModelContextProtocol.Client;

namespace AgentMcp;

internal class DefaultMcpToolNameTransformer : IMcpToolNameTransformer
{
    public string Transform(McpClientTool tool, McpServerConnectionInfo connectionInfo)
    {
        return $"{connectionInfo.ServerName}_{tool.Name}";
    }
}

