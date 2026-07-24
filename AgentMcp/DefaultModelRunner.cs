using Microsoft.Extensions.AI;

namespace AgentMcp;

internal class DefaultModelRunner(IChatClient client, ChatOptions chatOptions, string? systemPrompt) : IModelRunner
{
    private readonly FunctionInvokingChatClient _client = new(client);

    public async Task<ModelRunResult> RunModelAsync(string instruction, CancellationToken cancellationToken = default)
    {
        ChatMessage userMessage = new(ChatRole.User, instruction);

        IEnumerable<ChatMessage> messages = systemPrompt is null
            ? [userMessage]
            : [new(ChatRole.System, systemPrompt), userMessage];

        var response = await _client.GetResponseAsync(messages,
                                       chatOptions,
                                       cancellationToken);

        return new ModelRunResult.Success(response.Text);
    }
}
