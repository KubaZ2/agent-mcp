using Microsoft.Extensions.AI;

namespace AgentMcp;

internal class DefaultModelRunner(FunctionInvokingChatClient client, ChatOptions options, string? systemPrompt) : IModelRunner
{
    public async Task<ModelRunResult> RunModelAsync(ModelRunProperties properties, CancellationToken cancellationToken = default)
    {
        client.FunctionInvoker = properties.OnFunctionCall;

        ChatMessage userMessage = new(ChatRole.User, properties.Instruction);

        IEnumerable<ChatMessage> messages = systemPrompt is null
            ? [userMessage]
            : [new(ChatRole.System, systemPrompt), userMessage];

        var response = await client.GetResponseAsync(messages, options, cancellationToken);

        return new ModelRunResult.Success(response.Text);
    }
}
