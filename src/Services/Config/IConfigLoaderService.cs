using System.Threading.Tasks;

namespace statsCollector.Services;

public interface IConfigLoaderService
{
    Task LoadAndExecuteConfigAsync(string fileName);
    bool IsPlayerWhitelisted(ulong steamId);
}
