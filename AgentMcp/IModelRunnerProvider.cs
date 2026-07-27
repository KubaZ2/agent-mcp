namespace AgentMcp;

internal interface IModelRunnerProvider
{
    public Task<IModelRunner?> CreateModelRunnerAsync(string name, AgentConfiguration agentInfo, Options options, McpServerConnectionProperties mcpServerConnectionProperties);
}
