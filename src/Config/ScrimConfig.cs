using System.Collections.Generic;

namespace statsCollector.Config;

public class ScrimConfig
{
    public List<string> MapPool { get; set; } = new()
    {
        "de_ancient", "de_anubis", "de_dust2", "de_inferno", "de_mirage", "de_nuke", "de_overpass", "de_vertigo"
    };

    public int PlayersPerTeam { get; set; } = 5;
    public int MinReadyPlayers { get; set; } = 10;
    public bool KnifeRoundEnabled { get; set; } = true;
    public bool WhitelistEnabled { get; set; } = false;
    public string MatchZyConfigPath { get; set; } = "/cfg";
    public int VoteTimeoutSeconds { get; set; } = 60;
    public int MaxTacticalPauses { get; set; } = 4;
    public int TacticalPauseDuration { get; set; } = 30;
}
