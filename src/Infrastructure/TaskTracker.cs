using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace statsCollector.Infrastructure;

public interface ITaskTracker
{
    void Track(string name, Task task);
    Task WaitAllAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class TaskTracker : ITaskTracker
{
    private readonly ConcurrentDictionary<Task, string> _pendingTasks = new();
    private readonly ILogger<TaskTracker> _logger;

    public TaskTracker(ILogger<TaskTracker> logger)
    {
        _logger = logger;
    }

    public void Track(string name, Task task)
    {
        _pendingTasks.TryAdd(task, name);
        
        // Remove from collection when task completes
        _ = task.ContinueWith(t => 
        {
            _pendingTasks.TryRemove(t, out _);
            if (t.IsFaulted)
            {
                _logger.LogError(t.Exception, "Tracked task '{Name}' failed", name);
            }
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    public async Task WaitAllAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var tasks = _pendingTasks.ToList();
        if (tasks.Count == 0) return;

        _logger.LogInformation("Waiting for {Count} pending background tasks to complete...", tasks.Count);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await Task.WhenAll(tasks.Select(kvp => kvp.Key)).WaitAsync(linkedCts.Token).ConfigureAwait(false);
            _logger.LogInformation("All background tasks completed successfully.");
        }
        catch (OperationCanceledException)
        {
            var unfinished = _pendingTasks.Where(kvp => !kvp.Key.IsCompleted).Select(kvp => kvp.Value).ToList();
            _logger.LogWarning("Timeout reached while waiting for background tasks. {Count} tasks remaining: {TaskNames}", 
                unfinished.Count, string.Join(", ", unfinished));
            
            // Critical: Log that we are potentially losing data
            foreach (var taskName in unfinished)
            {
                _logger.LogCritical("Background task '{TaskName}' did NOT complete before timeout. Possible data loss!", taskName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while waiting for background tasks");
        }
    }
}
