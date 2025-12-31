using System.Threading.Tasks;

namespace statsCollector.Services;

public interface IConfigLoaderService
{
    Task LoadAndExecuteConfigAsync(string fileName);
    Task ExecuteLinesAsync(string[] lines);
    bool IsPlayerWhitelisted(ulong steamId);
    Task ReloadWhitelistAsync();
}
