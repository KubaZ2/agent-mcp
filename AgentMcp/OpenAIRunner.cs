using System.ClientModel;
using System.Collections.Frozen;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

namespace AgentMcp;

internal sealed class OpenAIRunner : IModelRunner, IModelRunnerFactory
{
    private record ToolInfo(string OriginalName, IMcpRunner Runner);

    private readonly string _model;

    private readonly string? _systemPrompt;

    private readonly OpenAIClientOptions _clientOptions;

    private readonly ApiKeyCredential _apiKey;

    private readonly ChatCompletionOptions _completionOptions;

    private readonly IReadOnlyList<IMcpRunner> _mcpRunners;

    private readonly FrozenDictionary<string, ToolInfo> _toolMap;

    private OpenAIRunner(AgentInfo agent, IReadOnlyList<IMcpRunner> mcpRunners, FrozenDictionary<string, ToolInfo> toolMap, IReadOnlyList<ChatTool> tools)
    {
        _model = agent.Model;

        _systemPrompt = agent.SystemPrompt;

        OpenAIClientOptions clientOptions = new();

        if (agent.Endpoint is { } endpoint)
            clientOptions.Endpoint = new(endpoint);

        _clientOptions = clientOptions;

        _apiKey = new(agent.ApiKey ?? "-");

        ChatCompletionOptions completionOptions = new();

        var optionsTools = completionOptions.Tools;

        foreach (var tool in tools)
            optionsTools.Add(tool);

        _completionOptions = completionOptions;

        _mcpRunners = mcpRunners;
        _toolMap = toolMap;
    }

    public static string ProviderName => "OpenAI";

    private static async Task CreateMcpServers(IReadOnlyDictionary<string, McpServerInfo> mcpServers,
                                               List<IMcpRunner> mcpRunners,
                                               Dictionary<string, ToolInfo> toolMap,
                                               List<ChatTool> tools,
                                               ILogger logger)
    {
        foreach (var (name, info) in mcpServers)
        {
            var runner = await DefaultMcpRunner.CreateAsync(info);

            if (runner is null)
            {
                logger.LogWarning("MCP runner '{RunnerName}' has no valid configuration. Skipping this runner.", name);
                continue;
            }

            var runnerTools = await runner.GetToolsAsync();

            int runnerToolCount = runnerTools.Count;

            for (int i = 0; i < runnerToolCount; i++)
            {
                var tool = runnerTools[i];

                var originalToolName = tool.Name;

                var toolName = $"{name}_{originalToolName}";

                if (!toolMap.TryAdd(toolName, new ToolInfo(originalToolName, runner)))
                {
                    logger.LogWarning("Duplicate tool name '{ToolName}' found in MCP runner '{RunnerName}'. Skipping this tool.", toolName, name);
                    continue;
                }

                tools.Add(ChatTool.CreateFunctionTool(toolName, tool.Description, BinaryData.FromString(tool.JsonSchema.GetRawText())));
            }

            mcpRunners.Add(runner);
        }
    }

    public static async Task<IModelRunner> CreateAsync(AgentInfo agentInfo, ILogger logger)
    {
        List<IMcpRunner> mcpRunners = [];
        Dictionary<string, ToolInfo> toolMap = [];
        List<ChatTool> tools = [];

        if (agentInfo.Mcp is { } mcpServers)
            await CreateMcpServers(mcpServers, mcpRunners, toolMap, tools, logger);

        return new OpenAIRunner(agentInfo, mcpRunners, toolMap.ToFrozenDictionary(), tools);
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
