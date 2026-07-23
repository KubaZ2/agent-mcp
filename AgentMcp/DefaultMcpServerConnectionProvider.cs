namespace AgentMcp;

internal class DefaultMcpServerConnectionProvider(ILoggerFactory loggerFactory) : IMcpServerConnectionProvider
{
    public async Task<IMcpServerConnection?> CreateAsync(McpServerConfiguration mcpServerInfo)
    {
        return await DefaultMcpServerConnection.CreateAsync(mcpServerInfo, loggerFactory.CreateLogger<DefaultMcpServerConnection>());
    }
}
