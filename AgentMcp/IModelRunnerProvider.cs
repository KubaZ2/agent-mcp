namespace AgentMcp;

internal interface IModelRunnerProvider
{
    public Task<IModelRunner?> CreateModelRunnerAsync(AgentInfo agentInfo);
}
