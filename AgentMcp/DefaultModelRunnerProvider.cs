namespace AgentMcp;

internal sealed class DefaultModelRunnerProvider(IMcpServerOrchestrator orchestrator) : IModelRunnerProvider
{
    public Task<IModelRunner?> CreateModelRunnerAsync(string name, AgentConfiguration agentInfo, Options options)
    {
        if (!options.Providers.TryGetValue(agentInfo.Provider, out var provider))
            return Task.FromResult<IModelRunner?>(null);

        return provider switch
        {
            OpenAIProviderConfiguration openAIProvider => OpenAIRunner.CreateAsync(openAIProvider, orchestrator, agentInfo),
            _ => Task.FromResult<IModelRunner?>(null)
        };
    }
}
