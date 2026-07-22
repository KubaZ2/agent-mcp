using ModelContextProtocol.Server;

namespace AgentMcp;

internal interface IMcpServerToolProvider
{
    public McpServerTool GetTool();
}

