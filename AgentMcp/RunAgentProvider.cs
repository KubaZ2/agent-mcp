using System.Collections.Frozen;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentMcp;

internal class RunAgentProvider(IOptionsMonitor<Options> options, ILogger<RunAgentProvider> logger, IModelRunnerProvider modelRunnerProvider) : IMcpServerToolProvider, IHostedService
{
    private FrozenDictionary<string, IModelRunner>? _modelRunners;

    private async ValueTask<object?> HandleFunctionInvocationAsync(FunctionInvocationContext context, string agent, McpServer server, CancellationToken cancellationToken)
    {
        var function = context.Function;

        logger.LogInformation("Agent {Agent} is requesting to call {FunctionName} with arguments: {Arguments}", agent, function.Name, context.Arguments);

        if (server.ClientCapabilities is not { Elicitation: { } })
        {
            logger.LogWarning("Client does not support function invocation.");

            return "Error: The client does not support function invocation.";
        }

        var message = $"Agent {agent} is requesting to call {function.Name}. Please select the action to take.";

        var result = await server.ElicitAsync(new()
        {
            Message = message,
            RequestedSchema = new()
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>()
                {
                    ["action"] = new ElicitRequestParams.TitledSingleSelectEnumSchema()
                    {
                        Title = "Tool Call Request",
                        Description = message,
                        OneOf = [
                            new ElicitRequestParams.EnumSchemaOption()
                            {
                                Title = "Approve",
                                Const = "approve",
                            },
                            new ElicitRequestParams.EnumSchemaOption()
                            {
                                Title = "Deny",
                                Const = "deny",
                            },
                        ],
                    }
                }
            }
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

        if (function is TaskMcpClientTool taskTool)
        {
            logger.LogInformation("Calling function {FunctionName} as a task.", function.Name);

            return await taskTool.CallWithPollingAsync(context.Arguments, cancellationToken: cancellationToken);
        }

        logger.LogInformation("Calling function {FunctionName} directly.", function.Name);

        return await function.InvokeAsync(context.Arguments, cancellationToken);
    }

    private async Task<string> RunAgentAsync([Description("Agent")] string agent, [Description("Instruction")] string instruction, McpServer server)
    {
        logger.LogInformation("Running agent {Agent} with instruction: {Instruction}", agent, instruction);

        try
        {
            if (!_modelRunners!.TryGetValue(agent, out var modelRunner))
            {
                logger.LogWarning("No model runner found for agent {Agent}", agent);

                return $"No model runner found for agent {agent}";
            }

            ModelRunProperties properties = new()
            {
                Instruction = instruction,
                OnFunctionCall = (function, cancellationToken) => HandleFunctionInvocationAsync(function, agent, server, cancellationToken)
            };

            var result = await modelRunner.RunModelAsync(properties);

            if (result is ModelRunResult.Failure failure)
            {
                logger.LogError("Agent {Agent} failed with error: {Error}", agent, failure.ErrorMessage);

                return $"Agent {agent} failed with error: {failure.ErrorMessage}";
            }
            else if (result is ModelRunResult.Success success)
            {
                logger.LogInformation("Agent {Agent} succeeded with result: {Result}", agent, success.Result);

                return success.Result;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent {Agent} failed with exception: {Exception}", agent, ex.Message);

            return $"Agent {agent} failed with exception: {ex.Message}";
        }

        return "Unknown result";
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var optionsValue = options.CurrentValue;
        var agents = optionsValue.Agents;

        Dictionary<string, IModelRunner> modelRunners = new(StringComparer.OrdinalIgnoreCase);

        var models = await Task.WhenAll(agents.Select(async pair =>
        {
            var (name, agent) = pair;

            var modelRunner = await modelRunnerProvider.CreateModelRunnerAsync(name, agent, optionsValue);

            if (modelRunner is null)
            {
                logger.LogWarning("No model runner found for agent {Agent}", name);

                return ((string, IModelRunner)?)null;
            }

            return (name, modelRunner);
        }));

        int length = models.Length;
        for (int i = 0; i < length; i++)
        {
            var model = models[i];

            if (model is null)
                continue;

            var (name, modelRunner) = model.Value;

            modelRunners.Add(name, modelRunner);
        }

        _modelRunners = modelRunners.ToFrozenDictionary();

        logger.LogInformation("Loaded {Count} model runner(s)", modelRunners.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
