// using System.Collections.Frozen;
// using Microsoft.Extensions.AI;
// using OllamaSharp;
// using OllamaSharp.Models;
// using OllamaSharp.Models.Chat;
// using OpenAI.Chat;
//
// namespace AgentMcp;
//
// internal partial class OllamaRunner : IModelRunner
// {
//     private readonly OllamaApiClient _client;
//
//     private OllamaRunner(AgentConfiguration agent,
//                          OllamaProviderConfiguration provider,
//                          IReadOnlyList<IMcpServerConnection> mcpConnections,
//                          FrozenDictionary<string, ToolInfo> toolMap,
//                          IReadOnlyList<ChatTool> tools)
//     {
//         _client = new(provider.Endpoint ?? "http://localhost:11434", agent.Model);
//     }
//
//     public static async Task<IModelRunner?> CreateAsync(OllamaProviderConfiguration provider,
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
//         return new OllamaRunner(agentInfo,
//                                 provider,
//                                 [.. mcpInfos.Select(info => info.ConnectionInfo.Connection)],
//                                 toolMap,
//                                 tools);
//     }
//
//     public Task<ModelRunResult> RunModelAsync(string instruction, CancellationToken cancellationToken = default)
//     {
//         var options = new ChatOptions();
//         options.AddOllamaOption(OllamaOption.NumCtx, 10);
//     }
// }
