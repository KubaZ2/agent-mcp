using System.ClientModel;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using OpenAI;
using OpenAI.Chat;

namespace AgentMcp;

internal sealed class OpenAIRunner : IModelRunner
{
    private record ToolInfo(string OriginalName, IMcpServerConnection ServerConnection);

    private readonly string _model;

    private readonly string? _systemPrompt;

    private readonly OpenAIClientOptions _clientOptions;

    private readonly ApiKeyCredential _apiKey;

    private readonly ChatCompletionOptions _completionOptions;

    private readonly IReadOnlyList<IMcpServerConnection> _mcpConnections;

    private readonly FrozenDictionary<string, ToolInfo> _toolMap;

    private OpenAIRunner(AgentConfiguration agent, OpenAIProviderConfiguration provider, IReadOnlyList<IMcpServerConnection> mcpConnections, FrozenDictionary<string, ToolInfo> toolMap, IReadOnlyList<ChatTool> tools)
    {
        _model = agent.Model;

        _systemPrompt = agent.SystemPrompt;

        OpenAIClientOptions clientOptions = new();

        if (provider.Endpoint is { } endpoint)
            clientOptions.Endpoint = new(endpoint);

        _clientOptions = clientOptions;

        _apiKey = new(provider.ApiKey ?? "-");

        ChatCompletionOptions completionOptions = new();

        completionOptions.Metadata["num_ctx"] = "9035";

        var optionsTools = completionOptions.Tools;

        foreach (var tool in tools)
            optionsTools.Add(tool);

        _completionOptions = completionOptions;

        _mcpConnections = mcpConnections;
        _toolMap = toolMap;
    }

    public static string ProviderType => "OpenAI";

    public static async Task<IModelRunner?> CreateAsync(OpenAIProviderConfiguration provider,
                                                        IMcpServerOrchestrator mcpConnectionOrchestrator,
                                                        IMcpToolNameTransformer toolNameTransformer,
                                                        AgentConfiguration agentInfo)
    {
        var mcpInfos = agentInfo.Mcp is { } mcpKeys
            ? await mcpConnectionOrchestrator.RunAsync(mcpKeys)
            : [];

        var toolMap = mcpInfos.SelectMany(info =>
        {
            return info.Tools.Select(tool => (ToolInfo: tool, info.ConnectionInfo.Connection));
        }).ToFrozenDictionary(d => d.ToolInfo.Name, d => new ToolInfo(d.ToolInfo.Tool.Name, d.Connection));

        var tools = mcpInfos.SelectMany(info =>
        {
            return info.Tools.Select(tool =>
            {
                return ChatTool.CreateFunctionTool(tool.Name,
                                                   tool.Tool.Description,
                                                   BinaryData.FromString(tool.Tool.JsonSchema.GetRawText()));
            });
        }).ToArray();

        return new OpenAIRunner(agentInfo,
                                provider,
                                [.. mcpInfos.Select(info => info.ConnectionInfo.Connection)],
                                toolMap,
                                tools);
    }

    public async Task<ModelRunResult> RunModelAsync(string instruction, CancellationToken cancellationToken = default)
    {
        try
        {
            var instructionMessage = ChatMessage.CreateUserMessage(instruction);

            List<ChatMessage> conversation = _systemPrompt is { } systemPrompt
                ? [ChatMessage.CreateSystemMessage(systemPrompt), instructionMessage]
                : [instructionMessage];

            ChatClient client = new(_model, _apiKey, _clientOptions);

            while (true)
            {
                var completion = await client.CompleteChatAsync(conversation, _completionOptions, cancellationToken);

                var completionValue = completion.Value;

                switch (completionValue.FinishReason)
                {
                    case ChatFinishReason.ToolCalls:
                        conversation.Add(ChatMessage.CreateAssistantMessage(completionValue));

                        foreach (var toolCall in completion.Value.ToolCalls)
                        {
                            var arguments = toolCall.FunctionArguments.ToObjectFromJson(Serialization.Default.IDictionaryStringJsonElement)!;

                            if (!_toolMap.TryGetValue(toolCall.FunctionName, out var toolInfo))
                                return new ModelRunResult.Failure($"No '{toolCall.FunctionName}' tool found.");

                            var toolResult = await toolInfo.ServerConnection.CallToolAsync(toolInfo.OriginalName, arguments);

                            conversation.Add(ChatMessage.CreateToolMessage(toolCall.Id, JsonSerializer.Serialize(toolResult, Serialization.Default.CallToolResult)));
                        }
                        break;
                    case ChatFinishReason.Stop:
                        return new ModelRunResult.Success(completion.Value.Content[0].Text);
                    default:
                        return new ModelRunResult.Failure($"Unexpected finish reason: {completionValue.FinishReason}");
                }
            }
        }
        catch (Exception ex)
        {
            return new ModelRunResult.Failure(ex.Message);
        }
    }
}

[JsonSerializable(typeof(CallToolResult))]
[JsonSerializable(typeof(IDictionary<string, JsonElement>))]
internal partial class Serialization : JsonSerializerContext;
