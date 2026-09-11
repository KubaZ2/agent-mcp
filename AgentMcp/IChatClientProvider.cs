using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Ollama;
using OpenAI;
using OpenAI.Chat;

namespace AgentMcp;

internal interface IChatClientProvider
{
    public ValueTask<IChatClient?> CreateChatClientAsync(AgentConfiguration agent);
}

internal partial class DefaultChatClientProvider(ILogger<DefaultChatClientProvider> logger, IOptions<Options> options) : IChatClientProvider
{
    private const double DefaultTimeoutSeconds = 1 << 10;

    private static HttpClient CreateHttpClient(double? timeoutSeconds)
    {
        return new()
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds ?? DefaultTimeoutSeconds)
        };
    }

    public ValueTask<IChatClient?> CreateChatClientAsync(AgentConfiguration agent)
    {
        var providerKey = agent.Provider;
        if (!options.Value.Providers.TryGetValue(providerKey, out var provider))
        {
            logger.LogWarning("Provider '{Provider}' is not defined in the configuration but is referenced by an agent.", providerKey);

            return default;
        }

        var client = provider switch
        {
            OpenAIProviderConfiguration openAIProvider => CreateOpenAIClient(agent, openAIProvider),
            AnthropicProviderConfiguration anthropicProvider => CreateAnthropicClient(anthropicProvider),
            OllamaProviderConfiguration ollamaProvider => CreateOllamaClient(agent, ollamaProvider),
            _ => throw new NotSupportedException($"Provider type {provider.GetType().Name} is not supported.")
        };

        return new(client.AsBuilder().ConfigureOptions(o =>
        {
            o.ModelId ??= agent.Model;
            o.MaxOutputTokens ??= agent.MaxOutputTokens;
        }).Build());
    }

    private static IChatClient CreateOpenAIClient(AgentConfiguration agent, OpenAIProviderConfiguration provider)
    {
        OpenAIClientOptions clientOptions = new()
        {
            NetworkTimeout = Timeout.InfiniteTimeSpan,
            RetryPolicy = new ClientRetryPolicy(0),
            Transport = new HttpClientPipelineTransport(CreateHttpClient(provider.TimeoutSeconds)),
        };

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

        return new(CreateHttpClient(provider.TimeoutSeconds), provider.Endpoint is { } endpoint ? new(endpoint) : null, authorizations: authorizations);
    }

    private static IChatClient CreateOllamaClient(AgentConfiguration agent, OllamaProviderConfiguration provider)
    {
        OllamaClient client = new(CreateHttpClient(provider.TimeoutSeconds), provider.Endpoint is { } endpoint ? new(endpoint) : null);

        return ((IChatClient)client).AsBuilder().ConfigureOptions(o =>
        {
            o.RawRepresentationFactory = client =>
            {
                ChatRequest request = new()
                {
                    Model = agent.Model,
                    Messages = [],
                };

                if (provider.Options is { } options)
                    (request.Options ??= new()).AdditionalProperties = options;

                return request;
            };
        }).Build();
    }
}
