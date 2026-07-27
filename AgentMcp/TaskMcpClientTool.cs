using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace AgentMcp;

internal sealed class TaskMcpClientTool(AIFunction function, McpClientTool tool) : DelegatingAIFunction(function)
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_client")]
    private extern static ref McpClient GetClient(McpClientTool tool);

    public ValueTask<ResultOrCreatedTask<CallToolResult>> CallAsTaskAsync(IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken cancellationToken = default)
    {
        return GetClient(tool).CallToolAsTaskAsync(new()
        {
            Name = tool.ProtocolTool.Name,
            Arguments = ToArgumentsDictionary(arguments, JsonSerializerOptions),
        }, cancellationToken);
    }

    public ValueTask<CallToolResult> CallWithPollingAsync(IReadOnlyDictionary<string, object?>? arguments = null, int maxConsecutiveStuckPolls = 60, CancellationToken cancellationToken = default)
    {
        return GetClient(tool).CallToolWithPollingAsync(new()
        {
            Name = tool.ProtocolTool.Name,
            Arguments = ToArgumentsDictionary(arguments, JsonSerializerOptions),
        }, maxConsecutiveStuckPolls, cancellationToken);
    }

    public async ValueTask<object?> InvokeWithPollingAsync(IReadOnlyDictionary<string, object?>? arguments = null, int maxConsecutiveStuckPolls = 60, CancellationToken cancellationToken = default)
    {
        var callToolResult = await CallWithPollingAsync(arguments, maxConsecutiveStuckPolls, cancellationToken);

        return JsonSerializer.SerializeToElement(callToolResult, McpJsonUtilities.DefaultOptions.GetTypeInfo<CallToolResult>());
    }

    private static Dictionary<string, JsonElement>? ToArgumentsDictionary(IReadOnlyDictionary<string, object?>? arguments, JsonSerializerOptions options)
    {
        JsonTypeInfo<object?> typeInfo = options.GetTypeInfo<object?>();
        Dictionary<string, JsonElement>? dictionary = null;
        if (arguments != null)
        {
            dictionary = new Dictionary<string, JsonElement>(arguments.Count);
            foreach (KeyValuePair<string, object?> argument in arguments)
            {
                dictionary.Add(argument.Key, (argument.Value is JsonElement jsonElement) ? jsonElement : JsonSerializer.SerializeToElement(argument.Value, typeInfo));
            }
        }

        return dictionary;
    }
}

