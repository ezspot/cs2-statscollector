using Microsoft.Extensions.Logging.Abstractions;
using statsCollector.Config;
using statsCollector.Infrastructure;
using statsCollector.Services;
using Xunit;

namespace statsCollector.Tests;

public class ScrimRecoveryTests
{
    [Fact]
    public void SettingRuntimeOverrideWhileIdlePreservesRecoveryFromPreviousRun()
    {
        var recovery = new RecordingRecovery();
        using var manager = new ScrimManager(NullLogger<ScrimManager>.Instance,
            new PluginConfig(), null!, recovery, new ImmediateScheduler());

        manager.SetOverride("minready", "4");

        Assert.Equal(0, recovery.Writes);
    }

    private sealed class ImmediateScheduler : IGameScheduler
    {
        public void Schedule(Action action) => action();
    }

    private sealed class RecordingRecovery : IJsonRecoveryService
    {
        public int Writes { get; private set; }

        public Task SaveScrimStateAsync<T>(T data)
        {
            Writes++;
            return Task.CompletedTask;
        }

        public Task<T?> LoadScrimStateAsync<T>() => throw new NotSupportedException();
        public Task ClearScrimStateAsync() => throw new NotSupportedException();
    }
}
