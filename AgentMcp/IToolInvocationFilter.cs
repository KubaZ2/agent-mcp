using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Meziantou.Framework.Globbing;
using Microsoft.Extensions.AI;

namespace AgentMcp;

internal enum ToolFilterResult : byte
{
    Ask,
    Allow,
    Deny,
}

internal interface IToolInvocationFilter
{
    public ValueTask<ToolFilterResult> FilterAsync(AIFunction tool, AIFunctionArguments arguments, CancellationToken cancellationToken);

    public ValueTask AddAutoApproveToolAsync(string name, CancellationToken cancellationToken);

    public ValueTask AddAutoDenyToolAsync(string name, CancellationToken cancellationToken);
}

internal class DefaultToolInvocationFilter(ToolFilterResult defaultResult, IReadOnlyList<string> autoApproveTools, IReadOnlyList<string> autoDenyTools) : IToolInvocationFilter
{
    private class State(ImmutableList<Func<string, bool>> autoApproveTools, ImmutableList<Func<string, bool>> autoDenyTools)
    {
        public ImmutableList<Func<string, bool>> AutoApproveTools => autoApproveTools;

        public ImmutableList<Func<string, bool>> AutoDenyTools => autoDenyTools;
    }

    private State _state = new([.. autoApproveTools.Select(CreateFunc)], [.. autoDenyTools.Select(CreateFunc)]);

    private static Func<string, bool> CreateFunc(string pattern)
    {
        if (pattern is ['/', .., '/'])
        {
            Regex regex = new(pattern[1..^1]);
            return regex.IsMatch;
        }

        var glob = Glob.Parse(pattern, GlobOptions.None);
        return glob.IsMatch;
    }

    public ValueTask<ToolFilterResult> FilterAsync(AIFunction tool, AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var name = tool.Name;

        var state = Volatile.Read(ref _state);

        if (state.AutoApproveTools.Any(f => f(name)))
            return new(ToolFilterResult.Allow);

        if (state.AutoDenyTools.Any(f => f(name)))
            return new(ToolFilterResult.Deny);

        return new(defaultResult);
    }

    public ValueTask AddAutoApproveToolAsync(string name, CancellationToken cancellationToken)
    {
        ImmutableInterlocked.Update(ref _state, s => new(s.AutoApproveTools.Add(name.Equals), s.AutoDenyTools));
        return default;
    }

    public ValueTask AddAutoDenyToolAsync(string name, CancellationToken cancellationToken)
    {
        ImmutableInterlocked.Update(ref _state, s => new(s.AutoApproveTools, s.AutoDenyTools.Add(name.Equals)));
        return default;
    }
}
