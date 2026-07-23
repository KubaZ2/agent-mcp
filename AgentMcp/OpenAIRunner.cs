using System.ClientModel;
using System.Collections.Frozen;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

namespace AgentMcp;

internal sealed class OpenAIRunner : IModelRunner
{
    private record ToolInfo(string OriginalName, IMcpRunner Runner);

    private readonly string _model;

    private readonly string? _systemPrompt;

    private readonly OpenAIClientOptions _clientOptions;

    private readonly ApiKeyCredential _apiKey;

    private readonly ChatCompletionOptions _completionOptions;

    private readonly IReadOnlyList<IMcpRunner> _mcpRunners;

    private readonly FrozenDictionary<string, ToolInfo> _toolMap;

    private OpenAIRunner(AgentInfo agent, OpenAIProviderInfo provider, IReadOnlyList<IMcpRunner> mcpRunners, FrozenDictionary<string, ToolInfo> toolMap, IReadOnlyList<ChatTool> tools)
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

        _mcpRunners = mcpRunners;
        _toolMap = toolMap;
    }

    public static string ProviderType => "OpenAI";

    private static async Task CreateMcpServers(IReadOnlyList<string> mcpKeys,
                                               IReadOnlyDictionary<string, McpServerInfo> mcpServers,
                                               List<IMcpRunner> mcpRunners,
                                               Dictionary<string, ToolInfo> toolMap,
                                               List<ChatTool> tools,
                                               ILogger logger)
    {
        int count = mcpKeys.Count;
        for (int i = 0; i < count; i++)
        {
            var key = mcpKeys[i];

            if (!mcpServers.TryGetValue(key, out var info))
            {
                logger.LogWarning("MCP '{RunnerName}' is not defined in the configuration but is referenced by an agent. Skipping this server.", key);
                continue;
            }

            var runner = await DefaultMcpRunner.CreateAsync(info);

            var name = info.Name ?? key;

            if (runner is null)
            {
                logger.LogWarning("MCP runner '{RunnerName}' has no valid configuration. Skipping this runner.", name);
                continue;
            }

            var runnerTools = await runner.GetToolsAsync();

            int runnerToolCount = runnerTools.Count;

            for (int k = 0; k < runnerToolCount; k++)
            {
                var tool = runnerTools[k];

                var originalToolName = tool.Name;

                var toolName = $"{name}_{originalToolName}";

                if (!toolMap.TryAdd(toolName, new(originalToolName, runner)))
                {
                    logger.LogWarning("Duplicate tool name '{ToolName}' found in MCP runner '{RunnerName}'. Skipping this tool.", toolName, name);
                    continue;
                }

                tools.Add(ChatTool.CreateFunctionTool(toolName, tool.Description, BinaryData.FromString(tool.JsonSchema.GetRawText())));
            }

            mcpRunners.Add(runner);
        }
    }

    public static async Task<IModelRunner?> CreateAsync(string name, OpenAIProviderInfo provider, AgentInfo agentInfo, Options options, ILogger logger)
    {
        List<IMcpRunner> mcpRunners = [];
        Dictionary<string, ToolInfo> toolMap = [];
        List<ChatTool> tools = [];

        if (agentInfo.Mcp is { } mcpKeys)
        {
            if (options.Mcp is { } mcpServers)
                await CreateMcpServers(mcpKeys, mcpServers, mcpRunners, toolMap, tools, logger);
            else
                logger.LogWarning("Agent {Agent} references MCP servers but no MCP servers are defined in the configuration.", name);
        }

        return new OpenAIRunner(agentInfo, provider, mcpRunners, toolMap.ToFrozenDictionary(), tools);
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
                            var arguments = toolCall.FunctionArguments.ToObjectFromJson<IDictionary<string, JsonElement>>()!;

                            if (!_toolMap.TryGetValue(toolCall.FunctionName, out var toolInfo))
                                return new ModelRunResult.Failure($"No '{toolCall.FunctionName}' tool found.");

                            var toolResult = await toolInfo.Runner.CallToolAsync(toolInfo.OriginalName, arguments);

                            conversation.Add(ChatMessage.CreateToolMessage(toolCall.Id, JsonSerializer.Serialize(toolResult)));
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
