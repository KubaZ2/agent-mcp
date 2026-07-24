using System.ComponentModel.DataAnnotations;
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
}

internal interface IProviderConfiguration
{
}

internal class OpenAIProviderConfiguration : IProviderConfiguration
{
    public string? ApiKey { get; set; }

    public string? Endpoint { get; set; }
}

internal class AnthropicProviderConfiguration : IProviderConfiguration
{
    public string? ApiKey { get; set; }

    public string? Endpoint { get; set; }
}

internal class OllamaProviderConfiguration : IProviderConfiguration
{
    public string? Endpoint { get; set; }
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
}

internal class McpServerConfiguration
{
    public string? Command { get; set; }

    public IReadOnlyList<string>? Args { get; set; }

    public string? Endpoint { get; set; }

    public string? Name { get; set; }
}
