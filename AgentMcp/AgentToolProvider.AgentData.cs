using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

using ElicitationHandler = System.Func<ModelContextProtocol.Protocol.ElicitRequestParams?, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<ModelContextProtocol.Protocol.ElicitResult>>;

namespace AgentMcp;

internal partial class AgentToolProvider
{
    private record AgentData(string Name,
                             FunctionInvokingChatClient ChatClient,
                             IReadOnlyList<AITool> Tools,
                             string? SystemPrompt,
                             CompositeFormat ToolCallTaskFinishPromptFormat,
                             StrongBox<ElicitationHandler> ElicitationHandler,
                             IToolInvocationFilter ToolInvocationFilter)
    {
        private class State(ImmutableHashSet<Task<PollTaskResult>> tasks, TaskCompletionSource<PollTaskResult?> completionSource)
        {
            public IEnumerable<Task<PollTaskResult?>> AwaitableTasks => tasks.Prepend(completionSource.Task!)!;

            public ImmutableHashSet<Task<PollTaskResult>> ToolTasks => tasks;

            public TaskCompletionSource<PollTaskResult?> CompletionSource => completionSource;
        }

        private static State CreateInitialState() => new([], new(TaskCreationOptions.RunContinuationsAsynchronously));

        private State _state = CreateInitialState();

        public SemaphoreSlim ToolInvocationFilterSemaphore { get; } = new(1, 1);

        public SemaphoreSlim InvocationSemaphore { get; } = new(1, 1);

        public void ClearToolTasks()
        {
            var state = Volatile.Read(ref _state);

            while (true)
            {
                var newState = CreateInitialState();

                var oldState = Interlocked.CompareExchange(ref _state, newState, state);

                if (oldState == state)
                    break;

                state = oldState;
            }
        }

        public void AddToolTask(Task<PollTaskResult> task)
        {
            var state = Volatile.Read(ref _state);

            while (true)
            {
                State newState = new(state.ToolTasks.Add(task), new(TaskCreationOptions.RunContinuationsAsynchronously));

                var oldState = Interlocked.CompareExchange(ref _state, newState, state);

                if (oldState == state)
                {
                    _ = state.CompletionSource.TrySetResult(null);
                    break;
                }

                state = oldState;
            }
        }

        public async Task<PollTaskResult?> WaitForToolTaskCompletionAsync()
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);

                if (state.ToolTasks.IsEmpty)
                    return null;

                var completedTask = await Task.WhenAny(state.AwaitableTasks);

                if (completedTask != state.CompletionSource.Task)
                {
                    RemoveCompletedTask(completedTask!);

                    return completedTask.GetAwaiter().GetResult();
                }
            }
        }

        private void RemoveCompletedTask(Task<PollTaskResult> completedTask)
        {
            var state = Volatile.Read(ref _state);

            while (true)
            {
                State newState = new(state.ToolTasks.Remove(completedTask), state.CompletionSource);

                var oldState = Interlocked.CompareExchange(ref _state, newState, state);

                if (oldState == state)
                    break;

                state = oldState;
            }
        }
    }
}
