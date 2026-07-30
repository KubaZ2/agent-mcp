using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using AgentMcp;
using Microsoft.Extensions.Options;

internal partial class Options
{
    [OptionsValidator]
    internal partial class Validator : IValidateOptions<Options>;

    internal IReadOnlyDictionary<string, IProviderConfiguration> Providers { get; set; } = null!;

    [Required]
    public IReadOnlyDictionary<string, AgentConfiguration> Agents { get; set; } = null!;

    public IReadOnlyDictionary<string, McpServerConfiguration>? Mcp { get; set; }

    [ValidateEnumeratedItems]
    public IEnumerable<AgentConfiguration>? AgentValues => Agents?.Values;

    internal void ConfigureProviders(IConfiguration configuration, IServiceProvider services)
    {
        var section = configuration.GetSection("Providers");

        Dictionary<string, IProviderConfiguration> providers = [];

        foreach (var providerSection in section.GetChildren())
        {
            var providerName = providerSection.Key;

            var type = providerSection.GetValue<string>("Type")
                ?? throw new InvalidOperationException($"Provider '{providerName}' is missing the 'Type' property.");

            const StringComparison c = StringComparison.InvariantCultureIgnoreCase;

            IProviderConfiguration? provider = type switch
            {
                _ when type.Equals("openai", c) => Validated(providerSection.Get<OpenAIProviderConfiguration>(), services, type, providerName),
                _ when type.Equals("anthropic", c) => Validated(providerSection.Get<AnthropicProviderConfiguration>(), services, type, providerName),
                _ when type.Equals("ollama", c) => Validated(providerSection.Get<OllamaProviderConfiguration>(), services, type, providerName),
                _ => throw new InvalidOperationException($"Unknown provider type '{type}' for provider '{providerName}'."),
            };

            if (!providers.TryAdd(providerName, provider))
                throw new InvalidOperationException($"Duplicate provider name '{providerName}' found in configuration.");
        }

        Providers = providers;

        static T Validated<T>(T? provider, IServiceProvider services, string type, string providerName) where T : class, IProviderConfiguration
        {
            if (provider is null)
                ThrowBindingFailed(providerName, type);

            foreach (var validateOptions in services.GetServices<IValidateOptions<T>>())
            {
                var validationResult = validateOptions.Validate(null, provider);

                if (validationResult.Failed)
                    ThrowValidationFailed(providerName, type, validationResult.FailureMessage);
            }

            return provider;

            [DoesNotReturn]
            static void ThrowBindingFailed(string providerName, string type)
            {
                throw new InvalidOperationException($"Provider '{providerName}' of type '{type}' could not be bind to a configuration object.");
            }

            [DoesNotReturn]
            static void ThrowValidationFailed(string providerName, string type, string failureMessage)
            {
                throw new InvalidOperationException($"Provider '{providerName}' of type '{type}' failed validation: {failureMessage}");
            }
        }
    }
}

internal interface IProviderConfiguration
{
}

internal class OpenAIProviderConfiguration : IProviderConfiguration
{
    public string? ApiKey { get; set; }

    public string? Endpoint { get; set; }

    public double? TimeoutSeconds { get; set; }
}

internal class AnthropicProviderConfiguration : IProviderConfiguration
{
    public string? ApiKey { get; set; }

    public string? Endpoint { get; set; }

    public double? TimeoutSeconds { get; set; }
}

internal class OllamaProviderConfiguration : IProviderConfiguration
{
    public string? Endpoint { get; set; }

    public double? TimeoutSeconds { get; set; }
}

internal class AgentConfiguration
{
    public string? Description { get; set; }

    public string? SystemPrompt { get; set; }

    [Required]
    public string Provider { get; set; } = null!;

    [Required]
    public string Model { get; set; } = null!;

    public IReadOnlyList<string>? Mcp { get; set; }

    public ToolApprovalPolicy DefaultToolPolicy { get; set; } = ToolApprovalPolicy.Ask;

    public IReadOnlyList<string>? AutoApproveTools { get; set; }

    public IReadOnlyList<string>? AutoDenyTools { get; set; }
}

internal enum ToolApprovalPolicy : byte
{
    Ask = ToolFilterResult.Ask,
    Allow = ToolFilterResult.Allow,
    Deny = ToolFilterResult.Deny,
}

internal class McpServerConfiguration
{
    public string? Command { get; set; }

    public IReadOnlyList<string>? Args { get; set; }

    public string? Endpoint { get; set; }

    public string? Name { get; set; }
}
