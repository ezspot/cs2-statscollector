using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace statsCollector.Infrastructure;

public static class Instrumentation
{
    public const string ServiceName = "cs2-statscollector";
    public const string ServiceVersion = "1.3.0";

    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    // Global Counters
    public static readonly Counter<long> KillsCounter = Meter.CreateCounter<long>("cs2_player_kills_total", description: "Total player kills");
    public static readonly Counter<long> DeathsCounter = Meter.CreateCounter<long>("cs2_player_deaths_total", description: "Total player deaths");
    public static readonly Counter<long> DamageCounter = Meter.CreateCounter<long>("cs2_damage_dealt_total", description: "Total damage dealt");
    public static readonly Counter<long> GrenadesDetonatedCounter = Meter.CreateCounter<long>("cs2_grenades_detonated_total", description: "Total grenades detonated");
    public static readonly Counter<long> BlindDurationCounter = Meter.CreateCounter<long>("cs2_blind_duration_total_ms", description: "Total blind duration in milliseconds");
    public static readonly Counter<long> MoneySpentCounter = Meter.CreateCounter<long>("cs2_money_spent_total", description: "Total money spent by players");
    public static readonly Counter<long> DbOperationsCounter = Meter.CreateCounter<long>("cs2_db_operations_total", description: "Total database operations");
    
    // Gameplay Counters
    public static readonly Counter<long> TradeOpportunitiesCounter = Meter.CreateCounter<long>("cs2_trade_opportunities_total", description: "Total trade kill opportunities");
    public static readonly Counter<long> FlashWasteCounter = Meter.CreateCounter<long>("cs2_flash_waste_total", description: "Total flashes thrown that were considered waste");
    public static readonly Counter<long> RoundsPlayedCounter = Meter.CreateCounter<long>("cs2_rounds_played_total", description: "Total rounds played");
    
    // Bomb Counters
    public static readonly Counter<long> BombPlantsCounter = Meter.CreateCounter<long>("cs2_bomb_plants_total", description: "Total bomb plants");
    public static readonly Counter<long> BombDefusesCounter = Meter.CreateCounter<long>("cs2_bomb_defuses_total", description: "Total bomb defuses");
    public static readonly Counter<long> BombExplosionsCounter = Meter.CreateCounter<long>("cs2_bomb_explosions_total", description: "Total bomb explosions");

    // Bomb Histograms (Durations)
    public static readonly Histogram<double> BombPlantDurationsRecorder = Meter.CreateHistogram<double>("cs2_bomb_plant_duration_seconds", description: "Duration of bomb plants");
    public static readonly Histogram<double> BombDefuseDurationsRecorder = Meter.CreateHistogram<double>("cs2_bomb_defuse_duration_seconds", description: "Duration of bomb defuses");
    public static readonly Histogram<double> BombExplosionDurationsRecorder = Meter.CreateHistogram<double>("cs2_bomb_explosion_duration_seconds", description: "Time from plant to explosion");

    // Persistence Counters
    public static readonly Counter<long> StatsEnqueuedCounter = Meter.CreateCounter<long>("cs2_stats_enqueued_total", description: "Total player stats snapshots enqueued");
    public static readonly Counter<long> StatsDroppedCounter = Meter.CreateCounter<long>("cs2_stats_dropped_total", description: "Total player stats snapshots dropped due to full channel");
    public static readonly Counter<long> PositionEventsEnqueuedCounter = Meter.CreateCounter<long>("cs2_position_events_enqueued_total", description: "Total position events enqueued");
    public static readonly Counter<long> PositionEventsDroppedCounter = Meter.CreateCounter<long>("cs2_position_events_dropped_total", description: "Total position events dropped due to full channel");
    public static readonly Counter<long> PositionEventsTrackedCounter = Meter.CreateCounter<long>("cs2_position_events_tracked_total", description: "Total position events tracked in DB");
}
