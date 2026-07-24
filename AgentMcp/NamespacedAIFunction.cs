using Microsoft.Extensions.AI;

namespace AgentMcp;

internal sealed class NamespacedAIFunction(AIFunction function, string serverName) : DelegatingAIFunction(function)
{
    public override string Name => $"{serverName}_{base.Name}";
}

