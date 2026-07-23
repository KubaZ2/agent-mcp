namespace AgentMcp;

internal sealed class DefaultModelRunnerProvider(IMcpServerOrchestrator orchestrator, IMcpToolNameTransformer toolNameTransformer) : IModelRunnerProvider
{
    public Task<IModelRunner?> CreateModelRunnerAsync(string name, AgentConfiguration agentInfo, Options options)
    {
        if (!options.Providers.TryGetValue(agentInfo.Provider, out var provider))
            return Task.FromResult<IModelRunner?>(null);

        return provider switch
        {
            OpenAIProviderConfiguration openAIProvider => OpenAIRunner.CreateAsync(openAIProvider, orchestrator, toolNameTransformer, agentInfo),
            _ => Task.FromResult<IModelRunner?>(null)
        };
    }
}
