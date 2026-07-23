namespace AgentMcp;

internal sealed class DefaultModelRunnerProvider(ILogger<DefaultModelRunnerProvider> logger) : IModelRunnerProvider
{
    public Task<IModelRunner?> CreateModelRunnerAsync(string name, AgentInfo agentInfo, Options options)
    {
        if (!options.Providers.TryGetValue(agentInfo.Provider, out var provider))
            return Task.FromResult<IModelRunner?>(null);

        return provider switch
        {
            OpenAIProviderInfo openAIProvider => OpenAIRunner.CreateAsync(name, openAIProvider, agentInfo, options, logger),
            _ => Task.FromResult<IModelRunner?>(null)
        };
    }
}
