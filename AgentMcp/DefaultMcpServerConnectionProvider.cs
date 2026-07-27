namespace AgentMcp;

internal class DefaultMcpServerConnectionProvider(ILoggerFactory loggerFactory) : IMcpServerConnectionProvider
{
    public async Task<IMcpServerConnection?> CreateAsync(McpServerConfiguration mcpServerInfo, McpServerConnectionProperties properties)
    {
        return await DefaultMcpServerConnection.CreateAsync(mcpServerInfo, properties, loggerFactory.CreateLogger<DefaultMcpServerConnection>());
    }
}
