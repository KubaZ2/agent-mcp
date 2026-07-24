using System.ClientModel;
using Anthropic;
using Microsoft.Extensions.AI;
using Ollama;
using OpenAI;
using OpenAI.Chat;

namespace AgentMcp;

internal partial class DefaultModelRunnerProvider(IMcpServerOrchestrator orchestrator) : IModelRunnerProvider
{
    private sealed class NamespacedAIFunction(AIFunction function, string serverName) : DelegatingAIFunction(function)
    {
        public override string Name => $"{serverName}_{base.Name}";
    }

    public async Task<IModelRunner?> CreateModelRunnerAsync(string name, AgentConfiguration agent, Options options)
    {
        if (!options.Providers.TryGetValue(agent.Provider, out var provider))
            return null;

        var mcpInfos = agent.Mcp is { } mcpKeys
            ? await orchestrator.RunAsync(mcpKeys)
            : [];

        var tools = mcpInfos.SelectMany(info =>
        {
            var serverName = info.ConnectionInfo.ServerName;
            return info.Tools.Select(tool => new NamespacedAIFunction(tool, serverName));
        }).ToArray();

        IChatClient client = provider switch
        {
            OpenAIProviderConfiguration openAIProvider => CreateOpenAIClient(agent, openAIProvider),
            AnthropicProviderConfiguration anthropicProvider => CreateAnthropicClient(anthropicProvider),
            OllamaProviderConfiguration ollamaProvider => CreateOllamaClient(ollamaProvider),
            _ => throw new NotSupportedException($"Provider type {provider.GetType().Name} is not supported.")
        };

        ChatOptions chatOptions = new()
        {
            Tools = tools,
            ModelId = agent.Model,
        };

        return new DefaultModelRunner(client, chatOptions, agent.SystemPrompt);
    }

    private static IChatClient CreateOpenAIClient(AgentConfiguration agent, OpenAIProviderConfiguration provider)
    {
        OpenAIClientOptions clientOptions = new();
        if (provider.Endpoint is { } endpoint)
            clientOptions.Endpoint = new(endpoint);

        ChatClient rawClient = new(agent.Model, new ApiKeyCredential(provider.ApiKey ?? "-"), clientOptions);
        return rawClient.AsIChatClient();
    }

    private static AnthropicClient CreateAnthropicClient(AnthropicProviderConfiguration provider)
    {
        List<Anthropic.EndPointAuthorization>? authorizations = null;

        if (provider.ApiKey is { } apiKey)
        {
            Anthropic.EndPointAuthorization authorization = new()
            {
                Type = "ApiKey",
                Location = "Header",
                Name = "x-api-key",
                Value = apiKey
            };
            authorizations = [authorization];
        }

        return new(baseUri: provider.Endpoint is { } endpoint ? new(endpoint) : null, authorizations: authorizations);
    }

    private static OllamaClient CreateOllamaClient(OllamaProviderConfiguration provider)
    {
        return new(baseUri: provider.Endpoint is { } endpoint ? new(endpoint) : null);
    }
}
