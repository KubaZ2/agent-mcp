// using System.ClientModel;
// using System.Collections.Frozen;
// using System.Text.Json;
// using System.Text.Json.Serialization;
// using Microsoft.Extensions.AI;
// using ModelContextProtocol.Protocol;
// using OpenAI;
// using OpenAI.Chat;
//
// namespace AgentMcp;
//
// internal partial class OpenAIRunner : IModelRunner
// {
//     [JsonSerializable(typeof(CallToolResult))]
//     [JsonSerializable(typeof(IDictionary<string, JsonElement>))]
//     internal partial class Serialization : JsonSerializerContext;
//
//     private readonly ChatClient _client;
//
//     private readonly string? _systemPrompt;
//
//     private readonly ChatCompletionOptions _completionOptions;
//
//     private readonly IReadOnlyList<IMcpServerConnection> _mcpConnections;
//
//     private readonly FrozenDictionary<string, ToolInfo> _toolMap;
//
//     private OpenAIRunner(AgentConfiguration agent,
//                          OpenAIProviderConfiguration provider,
//                          IReadOnlyList<IMcpServerConnection> mcpConnections,
//                          FrozenDictionary<string, ToolInfo> toolMap,
//                          IReadOnlyList<ChatTool> tools)
//     {
//         OpenAIClientOptions clientOptions = new();
//         if (provider.Endpoint is { } endpoint)
//             clientOptions.Endpoint = new(endpoint);
//
//         ChatCompletionOptions completionOptions = new();
//         var optionsTools = completionOptions.Tools;
//
//         foreach (var tool in tools)
//             optionsTools.Add(tool);
//
//         _client = new(agent.Model, new ApiKeyCredential(provider.ApiKey ?? "-"), clientOptions);
//
//         _systemPrompt = agent.SystemPrompt;
//         _completionOptions = completionOptions;
//         _mcpConnections = mcpConnections;
//         _toolMap = toolMap;
//     }
//
//     public static string ProviderType => "OpenAI";
//
//     public static async Task<IModelRunner?> CreateAsync(OpenAIProviderConfiguration provider,
//                                                         IMcpServerOrchestrator mcpConnectionOrchestrator,
//                                                         AgentConfiguration agentInfo)
//     {
//         var mcpInfos = agentInfo.Mcp is { } mcpKeys
//             ? await mcpConnectionOrchestrator.RunAsync(mcpKeys)
//             : [];
//
//         var toolMap = mcpInfos.SelectMany(info =>
//         {
//             return info.Tools.Select(tool => (ToolInfo: tool, info.ConnectionInfo.Connection));
//         }).ToFrozenDictionary(d => d.ToolInfo.Name, d => new ToolInfo(d.ToolInfo.Tool.Name, d.Connection));
//
//         var tools = mcpInfos.SelectMany(info =>
//         {
//             return info.Tools.Select(tool =>
//             {
//                 return ChatTool.CreateFunctionTool(tool.Name,
//                                                    tool.Tool.Description,
//                                                    BinaryData.FromString(tool.Tool.JsonSchema.GetRawText()));
//             });
//         }).ToArray();
//
//         return new OpenAIRunner(agentInfo,
//                                 provider,
//                                 [.. mcpInfos.Select(info => info.ConnectionInfo.Connection)],
//                                 toolMap,
//                                 tools);
//     }
//
//     public async Task<ModelRunResult> RunModelAsync(string instruction, CancellationToken cancellationToken = default)
//     {
//         try
//         {
//             var instructionMessage = ChatMessage.CreateUserMessage(instruction);
//
//             List<ChatMessage> conversation = _systemPrompt is { } systemPrompt
//                 ? [ChatMessage.CreateSystemMessage(systemPrompt), instructionMessage]
//                 : [instructionMessage];
//
//
//             while (true)
//             {
//                 var completion = await _client.CompleteChatAsync(conversation, _completionOptions, cancellationToken);
//
//                 var completionValue = completion.Value;
//
//                 switch (completionValue.FinishReason)
//                 {
//                     case ChatFinishReason.ToolCalls:
//                         conversation.Add(ChatMessage.CreateAssistantMessage(completionValue));
//
//                         foreach (var toolCall in completion.Value.ToolCalls)
//                         {
//                             var arguments = toolCall.FunctionArguments.ToObjectFromJson(Serialization.Default.IDictionaryStringJsonElement)!;
//
//                             if (!_toolMap.TryGetValue(toolCall.FunctionName, out var toolInfo))
//                                 return new ModelRunResult.Failure($"No '{toolCall.FunctionName}' tool found.");
//
//                             var toolResult = await toolInfo.ServerConnection.CallToolAsync(toolInfo.OriginalName, arguments);
//
//                             conversation.Add(ChatMessage.CreateToolMessage(toolCall.Id, JsonSerializer.Serialize(toolResult, Serialization.Default.CallToolResult)));
//                         }
//                         break;
//                     case ChatFinishReason.Stop:
//                         return new ModelRunResult.Success(completion.Value.Content[0].Text);
//                     default:
//                         return new ModelRunResult.Failure($"Unexpected finish reason: {completionValue.FinishReason}");
//                 }
//             }
//         }
//         catch (Exception ex)
//         {
//             return new ModelRunResult.Failure(ex.Message);
//         }
//     }
// }
