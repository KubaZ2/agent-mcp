using System.Collections.Frozen;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
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

internal partial class RunAgentProvider(IOptionsMonitor<Options> options, ILogger<RunAgentProvider> logger, IChatClientProvider chatClientProvider, IMcpClientProvider mcpClientProvider, IToolInvocationFilterProvider toolFilterProvider) : IMcpServerToolProvider, IHostedService
{
    private FrozenDictionary<string, AgentData>? _agentData;

    private static JsonElement MarshalMcpResult<T>(T result)
    {
        return JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions.GetTypeInfo<T>());
    }

    private async ValueTask<object?> HandleFunctionInvocationAsync(FunctionInvocationContext context, AgentData agent, McpServer server, CancellationToken cancellationToken)
    {
        logger.LogDebug("Agent {Agent} is invoking {FunctionCount} function(s) in parallel", agent.Name, context.FunctionCount);

        var function = context.Function;

        logger.LogInformation("Agent {Agent} is requesting to call {FunctionName} with arguments: {Arguments}", agent.Name, function.Name, context.Arguments);

        var semaphore = agent.ToolInvocationFilterSemaphore;

        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var filterResult = await agent.ToolInvocationFilter.FilterAsync(function, context.Arguments, cancellationToken);

            if (await HandleFilterResultAsync(context, agent, server, function, filterResult, cancellationToken) is { } response)
                return response;
        }
        finally
        {
            semaphore.Release();
        }

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

    private async ValueTask<string?> HandleFilterResultAsync(FunctionInvocationContext context, AgentData agent, McpServer server, AIFunction function, ToolFilterResult filterResult, CancellationToken cancellationToken)
    {
        switch (filterResult)
        {
            case ToolFilterResult.Deny:
                {
                    logger.LogInformation("Function {FunctionName} invocation denied by filter.", function.Name);

                    return "Error: Function invocation denied by filter.";
                }
            case ToolFilterResult.Allow:
                {
                    logger.LogInformation("Function {FunctionName} invocation allowed by filter.", function.Name);

                    break;
                }
            case ToolFilterResult.Ask:
                {
                    logger.LogInformation("Function {FunctionName} invocation requires user approval.", function.Name);

                    var askResult = await AskAsync(context, agent, server, function, cancellationToken);

                    if (await HandleAskResultAsync(agent, function, askResult, cancellationToken) is { } response)
                        return response;

                    break;
                }
        }

        return null;
    }

    private async ValueTask<string?> HandleAskResultAsync(AgentData agent, AIFunction function, AskResult askResult, CancellationToken cancellationToken)
    {
        switch (askResult)
        {
            case var _ when askResult.HasFlag(AskResult.Deny):
                logger.LogInformation("Function {FunctionName} invocation denied by user.", function.Name);

                if (askResult.HasFlag(AskResultAlwaysFlag))
                {
                    await agent.ToolInvocationFilter.AddAutoDenyToolAsync(function.Name, cancellationToken);

                    logger.LogInformation("Added function {FunctionName} to auto-deny list.", function.Name);
                }

                break;
            case var _ when askResult.HasFlag(AskResult.Approve):
                logger.LogInformation("Function {FunctionName} invocation approved by user.", function.Name);

                if (askResult.HasFlag(AskResultAlwaysFlag))
                {
                    await agent.ToolInvocationFilter.AddAutoApproveToolAsync(function.Name, cancellationToken);

                    logger.LogInformation("Added function {FunctionName} to auto-approve list.", function.Name);
                }

                return null;
            case var _ when askResult.HasFlag(AskResult.NotSupported):
                logger.LogInformation("Elicitation not supported by the client. Function {FunctionName} invocation denied.", function.Name);

                break;
        }

        return "Error: Function invocation denied by user.";
    }

    private const AskResult AskResultAlwaysFlag = (AskResult)(1 << 3);

    private enum AskResult : byte
    {
        Approve = 1 << 0,
        Deny = 1 << 1,
        NotSupported = 1 << 2,
        AlwaysApprove = Approve | AskResultAlwaysFlag,
        AlwaysDeny = Deny | AskResultAlwaysFlag,
    }

    private async Task<AskResult> AskAsync(FunctionInvocationContext context, AgentData agent, McpServer server, AIFunction function, CancellationToken cancellationToken)
    {
        if (!server.IsMrtrSupported && server.ClientCapabilities is not { Elicitation: { Form: { } } })
            return AskResult.NotSupported;

        StringBuilder messageBuilder = new();

        messageBuilder.AppendLine($"Agent {agent.Name} is requesting to call {function.Name} with ");

        var arguments = context.Arguments;

        if (arguments.Count is 0)
            messageBuilder.Append("no arguments.");
        else
        {
            messageBuilder.Append("the following arguments:");

            foreach (var (key, value) in arguments)
                messageBuilder.Append($"\n- {key}: {value}");
        }

        var message = messageBuilder.ToString();

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
                        OneOf = [
                            new ElicitRequestParams.EnumSchemaOption
                            {
                                Title = "Approve Once",
                                Const = "approve",
                            },
                            new ElicitRequestParams.EnumSchemaOption
                            {
                                Title = "Approve Always",
                                Const = "approve-always",
                            },
                            new ElicitRequestParams.EnumSchemaOption
                            {
                                Title = "Deny Once",
                                Const = "deny",
                            },
                            new ElicitRequestParams.EnumSchemaOption
                            {
                                Title = "Deny Always",
                                Const = "deny-always",
                            },
                        ],
                    },
                },
            },
        }, cancellationToken);

        if (result.Action is not "accept"
            || result.Content is not { } content
            || !content.TryGetValue("action", out var actionValue)
            || actionValue.ValueKind is not JsonValueKind.String)
        {
            logger.LogWarning("Elicitation result is invalid or missing action. Denying function invocation.");

            return AskResult.Deny;
        }

        return actionValue switch
        {
            _ when actionValue.ValueEquals("approve-always"u8) => AskResult.AlwaysApprove,
            _ when actionValue.ValueEquals("approve"u8) => AskResult.Approve,
            _ when actionValue.ValueEquals("deny"u8) => AskResult.Deny,
            _ when actionValue.ValueEquals("deny-always"u8) => AskResult.AlwaysDeny,
            _ => AskResult.Deny,
        };
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
        logger.LogDebug("Handling elicitation request: {Request}", JsonSerializer.Serialize(request, McpJsonUtilities.DefaultOptions.GetTypeInfo<ElicitRequestParams>()));

        var result = await server.ElicitAsync(request, cancellationToken);

        logger.LogDebug("Elicitation request handled successfully: {Result}", JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions.GetTypeInfo<ElicitResult>()));

        return result;
    }

    private async Task<string> RunAgentAsyncCore(AgentData agent, string instruction, McpServer server)
    {
        var (name, chatClient, tools, systemPrompt, elicitationHandler, _) = agent;

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

                return $"Agent '{agent}' does not exist.";
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
            switch (description)
            {
                case "Agent":
                    TransformAgentProperty(node);
                    break;
                case "Instruction":
                    node["description"] = "The instruction to give to the agent";
                    break;
            }
        }

        return node;
    }

    private void TransformAgentProperty(JsonNode node)
    {
        JsonArray agentsEnumSchema = [];

        var agents = options.CurrentValue.Agents;

        if (agents is null)
        {
            logger.LogWarning("No agents found in options");

            return;
        }

        StringBuilder descriptionBuilder = new("The agent to run. Available agents:\n");

        foreach (var (name, agent) in agents)
        {
            agentsEnumSchema.Add((JsonNode)JsonValue.Create(name));

            descriptionBuilder.Append($"- {name}: {agent.Description}\n");
        }

        node["enum"] = agentsEnumSchema;

        node["description"] = descriptionBuilder.ToString(0, descriptionBuilder.Length - 1);
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

        FunctionInvokingChatClient functionInvokingChatClient = new(chatClient)
        {
            AllowConcurrentInvocation = true,
        };

        StrongBox<ElicitationHandler> elicitationHandlerBox = new();

        ElicitationHandler elicitationHandler = (request, cancellationToken) => elicitationHandlerBox.Value!(request, cancellationToken);

        IReadOnlyList<AITool> tools = agent.Mcp is { } mcpKeys
            ? [.. (await Task.WhenAll(mcpKeys.Select(k => GetMcpToolsAsync(k, elicitationHandler, name)))).SelectMany(j => j)]
            : [];

        var filter = await toolFilterProvider.CreateAsync(agent);

        AgentData agentData = new(name, functionInvokingChatClient, tools, agent.SystemPrompt, elicitationHandlerBox, filter);

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
