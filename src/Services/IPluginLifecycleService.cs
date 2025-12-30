using CounterStrikeSharp.API.Core;

namespace statsCollector.Services;

public interface IPluginLifecycleService
{
    void Initialize(BasePlugin plugin);
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    void OnTick();
}
