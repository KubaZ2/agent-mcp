using System.Text.RegularExpressions;
using Meziantou.Framework.Globbing;

namespace AgentMcp;

internal interface IToolInvocationFilterProvider
{
    public ValueTask<IToolInvocationFilter> CreateAsync(AgentConfiguration agent);
}

internal class DefaultToolInvocationFilterProvider : IToolInvocationFilterProvider
{

    public ValueTask<IToolInvocationFilter> CreateAsync(AgentConfiguration agent)
    {
        return new(new DefaultToolInvocationFilter((ToolFilterResult)agent.DefaultToolPolicy,
                                                   agent.AutoApproveTools ?? [],
                                                   agent.AutoDenyTools ?? []));
    }
}
