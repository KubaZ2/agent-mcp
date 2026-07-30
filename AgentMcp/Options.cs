using System.ComponentModel.DataAnnotations;
using AgentMcp;
using Microsoft.Extensions.Options;

internal partial class Options
{
    [OptionsValidator]
    internal partial class Validator : IValidateOptions<Options>;

    internal IReadOnlyDictionary<string, IProviderConfiguration> Providers { get; set; } = null!;

    [Required]
    public IReadOnlyDictionary<string, AgentConfiguration> Agents { get; set; } = null!;

    internal IReadOnlyDictionary<string, IMcpServerConfiguration> Mcp { get; set; } = null!;

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

internal interface IMcpServerConfiguration
{
    public string? Name { get; set; }
}

internal partial class StdioMcpServerConfiguration : IMcpServerConfiguration
{
    [OptionsValidator]
    internal partial class Validator : IValidateOptions<StdioMcpServerConfiguration>;

    public string? Name { get; set; }

    [Required]
    public string Command { get; set; } = null!;

    public IReadOnlyList<string>? Args { get; set; }

    public IReadOnlyDictionary<string, string?>? Env { get; set; }

    public bool InheritEnv { get; set; } = true;

    public string? Cwd { get; set; }
}

internal partial class HttpMcpServerConfiguration : IMcpServerConfiguration
{
    [OptionsValidator]
    internal partial class Validator : IValidateOptions<HttpMcpServerConfiguration>;

    public string? Name { get; set; }

    [Required]
    public string Endpoint { get; set; } = null!;
}
