using System.Collections.Frozen;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using ElicitationHandler = System.Func<ModelContextProtocol.Protocol.ElicitRequestParams?, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<ModelContextProtocol.Protocol.ElicitResult>>;

namespace AgentMcp;

internal partial class RunAgentProvider(IOptionsMonitor<Options> options, ILogger<RunAgentProvider> logger, IChatClientProvider chatClientProvider, IMcpClientProvider mcpClientProvider) : IMcpServerToolProvider, IHostedService
{
    private FrozenDictionary<string, AgentData>? _agentData;

    private static JsonElement MarshalMcpResult<T>(T result)
    {
        return JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions.GetTypeInfo<T>());
    }

    private async ValueTask<object?> HandleFunctionInvocationAsync(FunctionInvocationContext context, AgentData agent, McpServer server, CancellationToken cancellationToken)
    {
        var function = context.Function;

        logger.LogInformation("Agent {Agent} is requesting to call {FunctionName} with arguments: {Arguments}", agent.Name, function.Name, context.Arguments);

        if (!server.IsMrtrSupported && server.ClientCapabilities is not { Elicitation: { Form: { } } })
        {
            logger.LogWarning("Client does not support elicitation.");

            return "Error: The client does not support elicitation.";
        }

        var message = $"Agent {agent.Name} is requesting to call {function.Name}. Please select the action to take.";

        var result = await server.ElicitAsync(new()
        {
            Message = message,
            RequestedSchema = new()
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    ["action"] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                    {
                        Title = "Tool Call Request",
                        Description = message,
                        OneOf = [
                            new ElicitRequestParams.EnumSchemaOption
                            {
                                Title = "Approve",
                                Const = "approve",
                            },
                            new ElicitRequestParams.EnumSchemaOption
                            {
                                Title = "Deny",
                                Const = "deny",
                            },
                        ],
                    },
                },
            },
        }, cancellationToken);

        if (result.Action is not "accept"
            || result.Content is not { } content
            || !content.TryGetValue("action", out var actionValue)
            || actionValue.ValueKind is not JsonValueKind.String
            || !actionValue.ValueEquals("approve"u8))
        {
            logger.LogWarning("User denied the tool call.");

            return "Error: The user denied the tool call.";
        }

        logger.LogInformation("User approved the tool call. Invoking function {FunctionName} with arguments: {Arguments}", function.Name, context.Arguments);

        if (function is McpClientToolWrapper wrapper)
        {
            logger.LogInformation("Calling function {FunctionName} as a task.", function.Name);

            var callResult = await wrapper.CallAsTaskAsync(context.Arguments, cancellationToken: cancellationToken);

            if (!callResult.IsTask)
            {
                logger.LogInformation("Function {FunctionName} completed immediately with result: {Result}", function.Name, JsonSerializer.Serialize(callResult.Result, McpJsonUtilities.DefaultOptions.GetTypeInfo<CallToolResult?>()));

                return MarshalMcpResult(callResult.Result);
            }

            var taskCreated = callResult.TaskCreated!;

            logger.LogInformation("Function {FunctionName} started as a task with ID: {TaskId}. Polling for completion.", function.Name, taskCreated.TaskId);

            agent.AddToolTask(PollTaskAsync(taskCreated, context.CallContent.CallId, wrapper, server, cancellationToken));

            return MarshalMcpResult(taskCreated);
        }

        logger.LogInformation("Calling function {FunctionName} directly.", function.Name);

        return await function.InvokeAsync(context.Arguments, cancellationToken);
    }

    private record PollTaskResult(string CallId, GetTaskResult TaskResult);

    private async Task<PollTaskResult> PollTaskAsync(CreateTaskResult taskCreated, string callId, McpClientToolWrapper tool, McpServer server, CancellationToken cancellationToken)
    {
        var pollIntervalMs = taskCreated.PollIntervalMs.GetValueOrDefault(1000);

        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(pollIntervalMs), cancellationToken);

            var getTaskResult = await tool.GetTaskAsync(new()
            {
                TaskId = taskCreated.TaskId,
            }, cancellationToken);

            pollIntervalMs = getTaskResult.PollIntervalMs.GetValueOrDefault(1000);

            switch (getTaskResult.Status)
            {
                case McpTaskStatus.Working:
                    {
                        logger.LogDebug("Task {TaskId} is still working.", taskCreated.TaskId);

                        continue;
                    }
                case McpTaskStatus.Completed:
                    {
                        logger.LogInformation("Task {TaskId} completed successfully.", taskCreated.TaskId);

                        return new(callId, getTaskResult);
                    }
                case McpTaskStatus.Failed:
                    {
                        logger.LogInformation("Task {TaskId} failed.", taskCreated.TaskId);

                        return new(callId, getTaskResult);
                    }
                case McpTaskStatus.InputRequired:
                    {
                        logger.LogInformation("Task {TaskId} requires input.", taskCreated.TaskId);

                        var inputRequiredResult = (InputRequiredTaskResult)getTaskResult;

                        Dictionary<string, InputResponse>? inputResponses;

                        if (inputRequiredResult.InputRequests is { } inputRequests)
                        {
                            inputResponses = (await Task.WhenAll(inputRequests.Select(async p =>
                            {
                                var (key, inputRequest) = p;

                                if (inputRequest.ElicitationParams is not { } elicitationParams)
                                {
                                    logger.LogWarning("Task {TaskId} input request {Key} has no elicitation parameters.", taskCreated.TaskId, key);

                                    return new KeyValuePair<string, InputResponse>(key, new InputResponse());
                                }

                                var result = await HandleElicitationAsync(elicitationParams, server, cancellationToken);

                                return new KeyValuePair<string, InputResponse>(key, InputResponse.FromElicitResult(result));
                            }))).ToDictionary();
                        }
                        else
                        {
                            logger.LogWarning("Task {TaskId} requires input, but no input requests were provided.", taskCreated.TaskId);

                            inputResponses = null;
                        }

                        await tool.UpdateTaskAsync(new()
                        {
                            TaskId = taskCreated.TaskId,
                            InputResponses = inputResponses,
                        }, cancellationToken);

                        break;
                    }
            }
        }
    }

    private async ValueTask<ElicitResult> HandleElicitationAsync(ElicitRequestParams request, McpServer server, CancellationToken cancellationToken)
    {
        logger.LogInformation("Handling elicitation request: {Request}", JsonSerializer.Serialize(request, McpJsonUtilities.DefaultOptions.GetTypeInfo<ElicitRequestParams>()));

        var result = await server.ElicitAsync(request, cancellationToken);

        logger.LogInformation("Elicitation request handled successfully: {Result}", JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions.GetTypeInfo<ElicitResult>()));

        return result;
    }

    private async Task<string> RunAgentAsyncCore(AgentData agent, string instruction, McpServer server)
    {
        var (name, chatClient, tools, systemPrompt, elicitationHandler) = agent;

        ChatMessage userMessage = new(ChatRole.User, instruction);

        List<ChatMessage> messages = systemPrompt is null
            ? [userMessage]
            : [new(ChatRole.System, systemPrompt), userMessage];

        chatClient.FunctionInvoker = (context, cancellationToken) => HandleFunctionInvocationAsync(context, agent, server, cancellationToken);

        elicitationHandler.Value = (request, cancellationToken) =>
        {
            if (request is null)
            {
                logger.LogWarning("Elicitation request is null");

                return new(new ElicitResult());
            }

            return HandleElicitationAsync(request, server, cancellationToken);
        };

        var response = await chatClient.GetResponseAsync(messages, new ChatOptions
        {
            Tools = [.. tools],
        });

        while (true)
        {
            var result = await agent.WaitForToolTaskCompletionAsync();

            if (result is null)
                break;

            if (result.TaskResult is not CompletedTaskResult completedTask)
            {
                logger.LogWarning("Agent {Agent} received non-completed task result: {Result}", name, JsonSerializer.Serialize(result, McpTasksJsonContext.Default.GetTaskResult));

                continue;
            }

            logger.LogInformation("Agent {Agent} received completed task result: {Result}", name, JsonSerializer.Serialize(completedTask, McpTasksJsonContext.Default.CompletedTaskResult));

            var toolResultMessage = JsonSerializer.Deserialize(completedTask.Result, McpJsonUtilities.DefaultOptions.GetTypeInfo<CallToolResult>())!;

            messages.Add(toolResultMessage.ToChatMessage(result.CallId));

            response = await chatClient.GetResponseAsync(messages, new ChatOptions
            {
                Tools = [.. tools],
            });

            messages.AddMessages(response);
        }


        return response.Text;
    }

    private async Task<string> RunAgentAsync([Description("Agent")] string agent, [Description("Instruction")] string instruction, McpServer server)
    {
        logger.LogInformation("Running agent {Agent} with instruction: {Instruction}", agent, instruction);

        try
        {
            if (!_agentData!.TryGetValue(agent, out var agentData))
            {
                logger.LogWarning("No agent data found for agent {Agent}", agent);

                return $"No agent data found for agent {agent}";
            }

            if (!agentData.TryEnter())
            {
                logger.LogWarning("Agent {Agent} is already running. Please wait for it to finish.", agentData.Name);

                return $"Agent {agent} is already running. Please wait for it to finish.";
            }

            try
            {
                return await RunAgentAsyncCore(agentData, instruction, server);
            }
            finally
            {
                agentData.Exit();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent {Agent} failed with exception: {Exception}", agent, ex.Message);

            return $"Agent {agent} failed with exception: {ex.Message}";
        }
    }

    private JsonNode TransformSchemaNode(AIJsonSchemaCreateContext context, JsonNode node)
    {
        var jsonDescription = node["description"];
        if (jsonDescription is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var description))
        {
            if (description is not "Agent")
            {
                if (description is "Instruction")
                {
                    node["description"] = "The instruction to give to the agent";

                    return node;
                }
            }

            node["description"] = "The agent to run";
        }

        JsonArray agentsSchema = [];

        var agents = options.CurrentValue.Agents;

        if (agents is null)
        {
            logger.LogWarning("No agents found in options");

            return node;
        }

        foreach (var (name, agent) in agents)
        {
            agentsSchema.Add((JsonNode)new JsonObject()
            {
                ["const"] = name,
                ["description"] = agent.Description,
            });
        }

        node["oneOf"] = agentsSchema;

        return node;
    }

    public McpServerTool GetTool()
    {
        var agents = options.CurrentValue.Agents;

        var tool = McpServerTool.Create(RunAgentAsync, new()
        {
            Name = "run_agent",
            Description = "Runs an agent",
            SchemaCreateOptions = new()
            {
                TransformSchemaNode = TransformSchemaNode,
            },
        });

        return tool;
    }

    private async Task<IReadOnlyList<AITool>> GetMcpToolsAsync(string mcpServerKey, ElicitationHandler elicitationHandler, string agentName)
    {
        var mcpConfigs = options.CurrentValue.Mcp;

        if (mcpConfigs is null || !mcpConfigs.TryGetValue(mcpServerKey, out var mcpConfig))
        {
            logger.LogWarning("MCP server configuration '{ServerName}' not found, but agent '{AgentName}' references it. Ignoring it.", mcpServerKey, agentName);

            return [];
        }

        var mcpClient = await mcpClientProvider.CreateAsync(mcpConfig, new()
        {
            Capabilities = new()
            {
                Elicitation = new()
                {
                    Form = new(),
                },
            },
            Handlers = new()
            {
                ElicitationHandler = elicitationHandler,
            },
        });

        if (mcpClient is null)
        {
            logger.LogWarning("MCP client for for MCP server '{ServerName}' could not be created for agent '{AgentName}'", mcpServerKey, agentName);

            return [];
        }

        var tools = await mcpClient.ListToolsAsync();

        logger.LogInformation("Loaded {Count} functions from MCP server '{ServerName}' for agent '{AgentName}'", tools.Count, mcpServerKey, agentName);

        return [.. tools.Select(tool => new McpClientToolWrapper(tool, mcpServerKey))];
    }

    private async Task<KeyValuePair<string, AgentData>?> CreateAgentDataAsync(KeyValuePair<string, AgentConfiguration> pair)
    {
        var (name, agent) = pair;

        var chatClient = await chatClientProvider.CreateChatClientAsync(agent);

        if (chatClient is null)
            return null;

        FunctionInvokingChatClient functionInvokingChatClient = new(chatClient);

        StrongBox<ElicitationHandler> elicitationHandlerBox = new();

        ElicitationHandler elicitationHandler = (request, cancellationToken) => elicitationHandlerBox.Value!(request, cancellationToken);

        IReadOnlyList<AITool> tools = agent.Mcp is { } mcpKeys
            ? [.. (await Task.WhenAll(mcpKeys.Select(k => GetMcpToolsAsync(k, elicitationHandler, name)))).SelectMany(j => j)]
            : [];

        AgentData agentData = new(name, functionInvokingChatClient, tools, agent.SystemPrompt, elicitationHandlerBox);

        return new(name, agentData);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var optionsValue = options.CurrentValue;
        var agents = optionsValue.Agents;

        var agentData = _agentData = (await Task.WhenAll(agents.Select(CreateAgentDataAsync)))
            .Where(d => d.HasValue)
            .Select(d => d.GetValueOrDefault())
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        logger.LogInformation("Loaded {Count} agents", agentData.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
