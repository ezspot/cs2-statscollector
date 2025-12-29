using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using statsCollector.Domain;

namespace statsCollector.Services;

public record ScrimRecoveryData(
    ScrimState State,
    int? MatchId,
    List<ulong> Team1,
    List<ulong> Team2,
    Dictionary<int, ulong> Captains,
    string? SelectedMap,
    DateTime Timestamp
);

public interface IScrimPersistenceService
{
    Task SaveStateAsync(ScrimRecoveryData data);
    Task<ScrimRecoveryData?> LoadStateAsync();
    Task ClearStateAsync();
}
