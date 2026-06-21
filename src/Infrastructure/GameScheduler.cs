using System;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API;

namespace statsCollector.Infrastructure;

public interface IGameScheduler
{
    // Yields execution to the next game frame.
    Task YieldToGameThread(CancellationToken ct = default);
    // Schedules an action to run on the next game frame.
    void Schedule(Action action);
    // Executes a function on the game thread and returns the result.
    Task<T> ExecuteAsync<T>(Func<T> func, CancellationToken ct = default);
}

public sealed class GameScheduler : IGameScheduler
{
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(5);

    public async Task YieldToGameThread(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_defaultTimeout);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Don't touch `cts` inside the callback: on timeout the method returns and disposes it
        // before the next frame runs, which would throw ObjectDisposedException on the game thread.
        // TrySetResult is a harmless no-op if the awaiter already timed out.
        Server.NextFrame(() => tcs.TrySetResult());

        await tcs.Task.WaitAsync(cts.Token);
    }

    public void Schedule(Action action)
    {
        Server.NextFrame(action);
    }

    public async Task<T> ExecuteAsync<T>(Func<T> func, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_defaultTimeout);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        // See YieldToGameThread: avoid referencing the (possibly disposed) cts inside the callback.
        Server.NextFrame(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return await tcs.Task.WaitAsync(cts.Token);
    }
}
