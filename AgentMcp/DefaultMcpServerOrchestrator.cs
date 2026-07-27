using Microsoft.Extensions.Options;

namespace AgentMcp;

internal class DefaultMcpServerOrchestrator(IOptions<Options> options,
                                            IMcpServerConnectionProvider provider,
                                            ILogger<DefaultMcpServerOrchestrator> logger) : IMcpServerOrchestrator
{
    private async Task<McpServerInfo?> ConnectAsync(string key,
                                                    McpServerConnectionProperties properties,
                                                    IReadOnlyDictionary<string, McpServerConfiguration> mcpServers)
    {
        if (!mcpServers.TryGetValue(key, out var info))
        {
            logger.LogWarning("MCP server '{Name}' is not defined in the configuration but is referenced by an agent. Skipping this server.", key);
            return null;
        }

        var connection = await provider.CreateAsync(info, properties);

        var name = info.Name ?? key;

        if (connection is null)
        {
            logger.LogWarning("MCP server '{Name}' has no valid configuration. Skipping this MCP server.", name);
            return null;
        }

        var tools = await connection.GetToolsAsync();

        McpServerConnectionInfo connectionInfo = new(name, connection);

        return new(connectionInfo, [.. tools]);
    }

    public async Task<IReadOnlyList<McpServerInfo>> RunAsync(IReadOnlyList<string> mcpServerKeys, McpServerConnectionProperties properties)
    {
        if (options.Value.Mcp is not { Count: > 0 } mcpServers)
        {
            logger.LogWarning("No MCP servers are defined in the configuration, but an agent is configured to use MCP. Skipping MCP server connections.");

            return [];
        }

        return (await Task.WhenAll(mcpServerKeys.Select(key => ConnectAsync(key, properties, mcpServers)))).Where(info => info is not null).ToArray()!;
    }
}

