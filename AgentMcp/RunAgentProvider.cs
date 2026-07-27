using System.Collections.Frozen;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using ElicitationHandler = System.Func<ModelContextProtocol.Protocol.ElicitRequestParams?, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<ModelContextProtocol.Protocol.ElicitResult>>;

namespace AgentMcp;

internal class RunAgentProvider(IOptionsMonitor<Options> options, ILogger<RunAgentProvider> logger, IChatClientProvider chatClientProvider, IMcpClientProvider mcpClientProvider) : IMcpServerToolProvider, IHostedService
{
    private record AgentData(string Name, FunctionInvokingChatClient ChatClient, IReadOnlyList<AITool> Tools, string? SystemPrompt, StrongBox<ElicitationHandler> ElicitationHandler)
    {
        private class State(ImmutableHashSet<Task<GetTaskResult>> tasks, TaskCompletionSource<GetTaskResult?> completionSource)
        {
            public IEnumerable<Task<GetTaskResult?>> AwaitableTasks => tasks.Prepend(completionSource.Task!)!;

            public ImmutableHashSet<Task<GetTaskResult>> ToolTasks => tasks;

            public TaskCompletionSource<GetTaskResult?> CompletionSource => completionSource;
        }

        private byte _lock;

        private State _state = new([], new(TaskCreationOptions.RunContinuationsAsynchronously));

        public bool TryEnter()
        {
            return Interlocked.CompareExchange(ref _lock, 1, 0) is 0;
        }

        public void Exit()
        {
            Interlocked.Exchange(ref _lock, 0);
        }

        public void AddToolTask(Task<GetTaskResult> task)
        {
            var state = Volatile.Read(ref _state);

            while (true)
            {
                State newState = new(state.ToolTasks.Add(task), new(TaskCreationOptions.RunContinuationsAsynchronously));

                var oldState = Interlocked.CompareExchange(ref _state, newState, state);

                if (oldState == state)
                {
                    _ = state.CompletionSource.TrySetResult(null);
                    break;
                }

                state = oldState;
            }
        }

        public async Task<GetTaskResult?> WaitForToolTaskCompletionAsync()
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);

                if (state.ToolTasks.IsEmpty)
                    return null;

                var completedTask = await Task.WhenAny(state.AwaitableTasks);

                if (completedTask != state.CompletionSource.Task)
                {
                    RemoveCompletedTask(completedTask!);

                    return completedTask.GetAwaiter().GetResult();
                }
            }
        }

        private void RemoveCompletedTask(Task<GetTaskResult> completedTask)
        {
            var state = Volatile.Read(ref _state);

            while (true)
            {
                State newState = new(state.ToolTasks.Remove(completedTask), state.CompletionSource);

                var oldState = Interlocked.CompareExchange(ref _state, newState, state);

                if (oldState == state)
                    break;

                state = oldState;
            }
        }
    }

    private FrozenDictionary<string, AgentData>? _agentData;

    private static JsonElement MarshalMcpResult<T>(T result)
    {
        return JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions.GetTypeInfo<T>());
    }
    //
    // private async ValueTask<ElicitResult> HandleElicitationAsync(ElicitRequestParams? request, CancellationToken cancellationToken)
    // {
    //     try
    //     {
    //         logger.LogInformation("Handling elicitation request");
    //
    //         if (request is null)
    //         {
    //             logger.LogWarning("Elicitation request is null");
    //
    //             return new ElicitResult();
    //         }
    //
    //         logger.LogInformation("Elicitation request: {Request}", JsonSerializer.Serialize(request, McpJsonUtilities.DefaultOptions.GetTypeInfo<ElicitRequestParams>()));
    //
    //         ElicitResult? result = null;
    //
    //         ExecutionContext.Run(_currentExecutionContext!, _ =>
    //         {
    //             result = _currentServer!.ElicitAsync(request, cancellationToken).AsTask().GetAwaiter().GetResult();
    //         }, null);
    //
    //         logger.LogInformation("Elicitation request handled successfully");
    //
    //         return result!;
    //     }
    //     catch (Exception ex)
    //     {
    //         logger.LogError(ex, "Error handling elicitation request: {Message}", ex.Message);
    //
    //         throw;
    //     }
    // }
    //
    private async ValueTask<object?> HandleFunctionInvocationAsync(FunctionInvocationContext context, AgentData agent, McpServer server, CancellationToken cancellationToken)
    {
        var function = context.Function;

        logger.LogInformation("Agent {Agent} is requesting to call {FunctionName} with arguments: {Arguments}", agent.Name, function.Name, context.Arguments);

        if (server.ClientCapabilities is not { Elicitation: { } })
        {
            logger.LogWarning("Client does not support function invocation.");

            return "Error: The client does not support function invocation.";
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

            agent.AddToolTask(PollTaskAsync(taskCreated, wrapper, server, cancellationToken));

            return MarshalMcpResult(taskCreated);
        }

        logger.LogInformation("Calling function {FunctionName} directly.", function.Name);

        return await function.InvokeAsync(context.Arguments, cancellationToken);
    }

    private async Task<GetTaskResult> PollTaskAsync(CreateTaskResult taskCreated, McpClientToolWrapper tool, McpServer server, CancellationToken cancellationToken)
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

                        return getTaskResult;
                    }
                case McpTaskStatus.Failed:
                    {
                        logger.LogInformation("Task {TaskId} failed.", taskCreated.TaskId);

                        return getTaskResult;
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

            if (result is not CompletedTaskResult completedTask)
            {
                logger.LogWarning("Agent {Agent} received non-completed task result: {Result}", name, JsonSerializer.Serialize(result, McpTasksJsonContext.Default.GetTaskResult));

                continue;
            }

            logger.LogInformation("Agent {Agent} received completed task result: {Result}", name, JsonSerializer.Serialize(completedTask, McpTasksJsonContext.Default.CompletedTaskResult));

            var toolResultMessage = JsonSerializer.Deserialize(completedTask.Result, McpJsonUtilities.DefaultOptions.GetTypeInfo<CallToolResult>())!;

            messages.Add(toolResultMessage.ToChatMessage(completedTask.TaskId));

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

    private sealed class McpClientToolWrapper : DelegatingAIFunction
    {
        private readonly string _serverName;

        public McpClientToolWrapper(McpClientTool tool, string serverName) : base(tool)
        {
            _serverName = serverName;
        }

        public override string Name => $"{_serverName}_{base.Name}";

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_client")]
        private extern static ref McpClient GetClientCore(McpClientTool tool);

        private McpClient Client => GetClientCore(Tool);

        private McpClientTool Tool => Unsafe.As<McpClientTool>(InnerFunction);

        public ValueTask<ResultOrCreatedTask<CallToolResult>> CallAsTaskAsync(IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken cancellationToken = default)
        {
            return Client.CallToolAsTaskAsync(new()
            {
                Name = Tool.ProtocolTool.Name,
                Arguments = ToArgumentsDictionary(arguments, JsonSerializerOptions),
            }, cancellationToken);
        }

        public ValueTask<CallToolResult> CallWithPollingAsync(IReadOnlyDictionary<string, object?>? arguments = null, int maxConsecutiveStuckPolls = 60, CancellationToken cancellationToken = default)
        {
            return Client.CallToolWithPollingAsync(new()
            {
                Name = Tool.ProtocolTool.Name,
                Arguments = ToArgumentsDictionary(arguments, JsonSerializerOptions),
            }, maxConsecutiveStuckPolls, cancellationToken);
        }

        public async ValueTask<object?> InvokeWithPollingAsync(IReadOnlyDictionary<string, object?>? arguments = null, int maxConsecutiveStuckPolls = 60, CancellationToken cancellationToken = default)
        {
            var callToolResult = await CallWithPollingAsync(arguments, maxConsecutiveStuckPolls, cancellationToken);

            return JsonSerializer.SerializeToElement(callToolResult, McpJsonUtilities.DefaultOptions.GetTypeInfo<CallToolResult>());
        }

        public ValueTask<GetTaskResult> GetTaskAsync(GetTaskRequestParams requestParams, CancellationToken cancellationToken = default)
        {
            return Client.GetTaskAsync(requestParams, cancellationToken);
        }

        public ValueTask<UpdateTaskResult> UpdateTaskAsync(UpdateTaskRequestParams requestParams, CancellationToken cancellationToken = default)
        {
            return Client.UpdateTaskAsync(requestParams, cancellationToken);
        }

        public ValueTask<CancelTaskResult> CancelTaskAsync(CancelTaskRequestParams requestParams, CancellationToken cancellationToken = default)
        {
            return Client.CancelTaskAsync(requestParams, cancellationToken);
        }

        private static Dictionary<string, JsonElement>? ToArgumentsDictionary(IReadOnlyDictionary<string, object?>? arguments, JsonSerializerOptions options)
        {
            var typeInfo = options.GetTypeInfo<object?>();
            Dictionary<string, JsonElement>? dictionary = null;
            if (arguments != null)
            {
                dictionary = new Dictionary<string, JsonElement>(arguments.Count);
                foreach (KeyValuePair<string, object?> argument in arguments)
                    dictionary.Add(argument.Key, (argument.Value is JsonElement jsonElement) ? jsonElement : JsonSerializer.SerializeToElement(argument.Value, typeInfo));
            }

            return dictionary;
        }
    }

    private async Task<IReadOnlyList<AITool>> GetMcpToolsAsync(string mcpServerKey, ElicitationHandler elicitationHandler)
    {
        var mcpConfigs = options.CurrentValue.Mcp;

        if (mcpConfigs is null || !mcpConfigs.TryGetValue(mcpServerKey, out var mcpConfig))
        {
            logger.LogWarning("MCP configuration for key '{Key}' not found in options", mcpServerKey);

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
            logger.LogWarning("MCP client for key '{Key}' could not be created", mcpServerKey);

            return [];
        }

        var tools = await mcpClient.ListToolsAsync();

        logger.LogInformation("Loaded {Count} functions from MCP server '{ServerName}' with key '{Key}'", tools.Count, mcpServerKey, mcpServerKey);

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

        var tools = agent.Mcp is { } mcpKeys ? (await Task.WhenAll(mcpKeys.Select(k => GetMcpToolsAsync(k, elicitationHandler)))).SelectMany(j => j).ToArray() : [];

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
