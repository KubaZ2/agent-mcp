namespace AgentMcp;

internal interface IMcpServerConnectionProvider
{
    public Task<IMcpServerConnection?> CreateAsync(McpServerConfiguration mcpServerInfo, McpServerConnectionProperties properties);
}
