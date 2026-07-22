using System.Collections.Frozen;
using Microsoft.Extensions.Logging;

namespace AgentMcp;

internal sealed class DefaultModelRunnerProvider(ILogger<DefaultModelRunnerProvider> logger) : IModelRunnerProvider
{
    private readonly FrozenDictionary<string, Func<AgentInfo, ILogger, Task<IModelRunner>>> _modelRunners = new Dictionary<string, Func<AgentInfo, ILogger, Task<IModelRunner>>>(StringComparer.OrdinalIgnoreCase)
    {
        { OpenAIRunner.ProviderName, OpenAIRunner.CreateAsync },
    }.ToFrozenDictionary();

    public Task<IModelRunner?> CreateModelRunnerAsync(AgentInfo agentInfo)
    {
        if (_modelRunners.TryGetValue(agentInfo.Provider, out var factory))
#pragma warning disable CS8619 // Nullability of reference types in value doesn't match target type.
            return factory(agentInfo, logger);
#pragma warning restore CS8619 // Nullability of reference types in value doesn't match target type.

        return Task.FromResult<IModelRunner?>(null);
    }
}

