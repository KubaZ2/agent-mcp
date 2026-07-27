using ModelContextProtocol.Client;

namespace AgentMcp;

// internal record struct McpServerConfigurationInfo(string Name, McpServerConfiguration Configuration);
//
// internal interface IMcpClientLoader
// {
//     public ValueTask<IReadOnlyList<McpClient>> LoadAsync(IReadOnlyList<McpServerConfigurationInfo> configurations);
// }
//
// internal class DefaultMcpClientLoader(ILogger<DefaultMcpClientLoader> logger, IMcpClientProvider provider) : IMcpClientLoader
// {
//     private async Task<McpClient?> ConnectAsync(McpServerConfigurationInfo configuration)
//     {
//         var client = await provider.CreateAsync(configuration.Configuration);
//
//         if (client is null)
//         {
//             logger.LogWarning("MCP server '{Name}' has no valid configuration. Skipping this MCP server.", configuration.Name);
//             return null;
//         }
//
//         return client;
//     }
//
//     public async ValueTask<IReadOnlyList<McpClient>> LoadAsync(IReadOnlyList<McpServerConfigurationInfo> configurations)
//     {
//         return (await Task.WhenAll(configurations.Select(ConnectAsync))).Where(info => info is not null).ToArray()!;
//     }
// }
