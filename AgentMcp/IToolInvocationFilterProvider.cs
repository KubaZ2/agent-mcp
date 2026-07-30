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
        return new(new DefaultToolInvocationFilter((ToolFilterResult)agent.DefaultToolPolicy.GetValueOrDefault(ToolApprovalPolicy.Ask),
                                                   agent.AutoApproveTools ?? [],
                                                   agent.AutoDenyTools ?? []));
    }
}
