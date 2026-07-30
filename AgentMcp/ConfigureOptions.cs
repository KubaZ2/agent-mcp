using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

internal partial class ConfigureOptions(IConfiguration configuration, IServiceProvider services) : IConfigureOptions<Options>
{
    public void Configure(Options options)
    {
        ConfigureProviders(options);
        ConfigureMcp(options);
    }

    private void ConfigureMcp(Options options)
    {
        var section = configuration.GetSection("Mcp");

        Dictionary<string, IMcpServerConfiguration> mcpServers = [];

        foreach (var mcpSection in section.GetChildren())
        {
            var mcpName = mcpSection.Key;

            IMcpServerConfiguration mcp = mcpSection switch
            {
                _ when mcpSection.GetValue<string?>("Command") is not null => Validated(mcpSection.Get<StdioMcpServerConfiguration>(), services, "stdio", mcpName),
                _ when mcpSection.GetValue<string?>("Endpoint") is not null => Validated(mcpSection.Get<HttpMcpServerConfiguration>(), services, "http", mcpName),
                _ => throw new InvalidOperationException($"MCP server '{mcpName}' must have either 'Command' or 'Endpoint' property specified."),
            };

            if (!mcpServers.TryAdd(mcpSection.Key, mcp))
                throw new InvalidOperationException($"Duplicate MCP server name '{mcpName}' found in configuration.");
        }

        options.Mcp = mcpServers;

        static T Validated<T>(T? configuration, IServiceProvider services, string type, string name)
            where T : class
        {
            return ValidatedCore(configuration, services, (type, name), BindingFailedMessage, ValidationFailedMessage);

            static string BindingFailedMessage((string Type, string Name) data) => $"Failed to bind MCP server '{data.Name}' of type '{data.Type}'.";

            static string ValidationFailedMessage((string Type, string Name) data, string failureMessage) => $"Validation failed for MCP server '{data.Name}' of type '{data.Type}': {failureMessage}";
        }
    }

    private void ConfigureProviders(Options options)
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

        options.Providers = providers;

        static T Validated<T>(T? configuration, IServiceProvider services, string type, string name)
            where T : class
        {
            return ValidatedCore(configuration, services, (type, name), BindingFailedMessage, ValidationFailedMessage);

            static string BindingFailedMessage((string Type, string Name) data) => $"Failed to bind provider '{data.Name}' of type '{data.Type}'.";

            static string ValidationFailedMessage((string Type, string Name) data, string failureMessage) => $"Validation failed for provider '{data.Name}' of type '{data.Type}': {failureMessage}";
        }
    }

    private static T ValidatedCore<T, TExceptionData>(T? configuration,
                                                  IServiceProvider services,
                                                  TExceptionData data,
                                                  Func<TExceptionData, string> bindingFailedMessageFunc,
                                                  Func<TExceptionData, string, string> validationFailedMessageFunc)
        where T : class
        where TExceptionData : struct
    {
        if (configuration is null)
            ThrowBindingFailed(data, bindingFailedMessageFunc);

        foreach (var validateOptions in services.GetServices<IValidateOptions<T>>())
        {
            var validationResult = validateOptions.Validate(null, configuration);

            if (validationResult.Failed)
                ThrowValidationFailed(data, validationResult.FailureMessage, validationFailedMessageFunc);
        }

        return configuration;

        [DoesNotReturn]
        static void ThrowBindingFailed(TExceptionData data, Func<TExceptionData, string> messageFunc)
        {
            throw new InvalidOperationException(messageFunc(data));
        }

        [DoesNotReturn]
        static void ThrowValidationFailed(TExceptionData data, string failureMessage, Func<TExceptionData, string, string> messageFunc)
        {
            throw new InvalidOperationException(messageFunc(data, failureMessage));
        }
    }
}

