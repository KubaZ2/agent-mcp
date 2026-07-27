using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace AgentMcp;

internal partial class RunAgentProvider
{
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
}

