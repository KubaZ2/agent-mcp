using ModelContextProtocol.Client;

namespace AgentMcp;

internal interface IMcpClientProvider
{
    public ValueTask<McpClient?> CreateAsync(string key, IMcpServerConfiguration configuration, McpClientOptions options);
}

internal class DefaultMcpClientProvider : IMcpClientProvider
{
    public async ValueTask<McpClient?> CreateAsync(string key, IMcpServerConfiguration configuration, McpClientOptions options)
    {
        IClientTransport? transport = configuration switch
        {
            StdioMcpServerConfiguration stdioConfig => CreateStdioTransport(stdioConfig),
            HttpMcpServerConfiguration httpConfig => CreateHttpTransport(httpConfig),
            _ => throw new InvalidOperationException($"Unknown {nameof(IMcpServerConfiguration)} type '{configuration.GetType().Name}'"),
        };

        try
        {
            return await McpClient.CreateAsync(transport, options);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to connect to '{key}' MCP server.", ex);
        }
    }

    private static StdioClientTransport CreateStdioTransport(StdioMcpServerConfiguration stdioConfig)
    {
        StdioClientTransportOptions options = new()
        {
            Command = stdioConfig.Command,
            Arguments = stdioConfig.Args?.ToArray(),
            EnvironmentVariables = stdioConfig.Env?.ToDictionary(),
            WorkingDirectory = stdioConfig.Cwd,
        };

        if (stdioConfig.InheritEnv is { } inheritEnv)
            options.InheritEnvironmentVariables = inheritEnv;

        if (stdioConfig.ShutdownTimeoutSeconds is { } shutdownTimeout)
            options.ShutdownTimeout = TimeSpan.FromSeconds(shutdownTimeout);

        return new(options);
    }

    private static HttpClientTransport CreateHttpTransport(HttpMcpServerConfiguration httpConfig)
    {
        // TODO: Add OAuth support
        HttpClientTransportOptions options = new()
        {
            Endpoint = new(httpConfig.Endpoint),
            AdditionalHeaders = httpConfig.Headers?.ToDictionary(),
            KnownSessionId = httpConfig.SessionId,
        };

        if (httpConfig.ConnectionTimeoutSeconds is { } connectionTimeout)
            options.ConnectionTimeout = TimeSpan.FromSeconds(connectionTimeout);

        if (httpConfig.DefaultReconnectionIntervalSeconds is { } defaultReconnectionInterval)
            options.DefaultReconnectionInterval = TimeSpan.FromSeconds(defaultReconnectionInterval);

        if (httpConfig.MaxReconnectionAttempts is { } maxReconnectionAttempts)
            options.MaxReconnectionAttempts = maxReconnectionAttempts;

        if (httpConfig.OwnsSession is { } ownsSession)
            options.OwnsSession = ownsSession;

        if (httpConfig.Mode is { } mode)
            options.TransportMode = (HttpTransportMode)mode;

        return new(options);
    }
}
