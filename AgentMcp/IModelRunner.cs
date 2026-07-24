using Microsoft.Extensions.AI;

namespace AgentMcp;

internal abstract record ModelRunResult
{
    private ModelRunResult()
    {
    }

    public sealed record Success(string Result) : ModelRunResult;

    public sealed record Failure(string ErrorMessage) : ModelRunResult;
}

internal class ModelRunProperties
{
    public required string Instruction { get; init; }

    public Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>? OnFunctionCall { get; init; }
}

internal interface IModelRunner
{
    public Task<ModelRunResult> RunModelAsync(ModelRunProperties properties, CancellationToken cancellationToken = default);
}
