using Microsoft.Extensions.Logging;

namespace AgentMcp;

internal abstract record ModelRunResult
{
    private ModelRunResult()
    {
    }

    public sealed record Success(string Result) : ModelRunResult;

    public sealed record Failure(string ErrorMessage) : ModelRunResult;
}

internal interface IModelRunner
{
    public Task<ModelRunResult> RunModelAsync(string instruction, CancellationToken cancellationToken = default);
}
