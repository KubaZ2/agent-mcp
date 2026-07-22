using System.Collections.Frozen;
using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace AgentMcp;

internal class RunAgentProvider(IOptionsMonitor<Options> options, ILogger<RunAgentProvider> logger, IModelRunnerProvider modelRunnerProvider) : IMcpServerToolProvider, IHostedService
{
    private FrozenDictionary<string, IModelRunner>? _modelRunners;

    private async Task<string> RunAgentAsync([Description("Agent")] string agent, [Description("Instruction")] string instruction)
    {
        logger.LogInformation("Running agent {Agent} with instruction: {Instruction}", agent, instruction);

        if (!_modelRunners!.TryGetValue(agent, out var modelRunner))
        {
            logger.LogWarning("No model runner found for agent {Agent}", agent);

            return $"No model runner found for agent {agent}";
        }

        var result = await modelRunner.RunModelAsync(instruction);

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
        var agents = options.CurrentValue.Agents;

        Dictionary<string, IModelRunner> modelRunners = new(StringComparer.OrdinalIgnoreCase);

        var models = await Task.WhenAll(agents.Select(async pair =>
        {
            var (name, agent) = pair;

            var modelRunner = await modelRunnerProvider.CreateModelRunnerAsync(agent);

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
