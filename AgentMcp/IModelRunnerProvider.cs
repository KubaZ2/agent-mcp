namespace AgentMcp;

internal interface IModelRunnerProvider
{
    public Task<IModelRunner?> CreateModelRunnerAsync(string name, AgentInfo agentInfo, Options options);
}
