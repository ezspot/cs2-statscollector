namespace statsCollector.Domain;

/// <summary>What happened to the bomb in a round: the site it was planted at ("A"/"B"), when into the round, and the
/// seconds left on its timer when it was defused. Null when it did not happen.</summary>
public sealed record RoundBombInfo(string? Site, decimal? PlantTimeSeconds, decimal? DefuseTimeLeftSeconds)
{
    public static readonly RoundBombInfo None = new(null, null, null);
}

/// <summary>Both sides' money at round start and equipment value at freeze end, summed over the players.</summary>
public sealed record RoundEconomy(int CtStartMoney, int TStartMoney, int CtEquipmentValue, int TEquipmentValue);
