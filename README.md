# statsCollector v1.7.0

A high-performance CounterStrikeSharp plugin for CS2 that collects advanced player statistics with enterprise-grade architecture. Built for late 2025 standards using .NET 8+, Polly v8 Resilience, and full OpenTelemetry observability.

## Features
- **Relational Match Tracking**: Automated `matches` and `rounds` management with support for `SeriesUuid` (BO3/BO5).
- **Decoupled Architecture**: Mediator pattern via `IEventDispatcher` for extensible, high-performance event processing.
- **Dedicated Analytics**: Centralized `AnalyticsService` for precise HLTV Rating 2.0, Impact, KAST, and ADR calculations.
- **Spatial Analytics (Heatmaps)**: High-resolution tracking using MySQL **POINT** types and **SPATIAL indexes** for optimized geometric queries.
- **Optimized Persistence**: **Dirty Flag** pattern ensures only changed player data is flushed to the database, minimizing I/O.
- **Accurate ADR Tracking**: Uses `player_hurt` events for precise damage calculation (not capped at 100).
- **Enterprise Observability**: Full OpenTelemetry (OTEL) integration for traces and metrics.
- **Robust Resilience**: Polly v8 pipelines with automated retries and circuit breakers for all database operations.
- **Tactical Pause System**: MatchZy-style `.pause` and `.unpause` with technical/tactical limits.
- **Scrim Management**: Automated knife rounds, side selection, and captain-based picking.
- **Round Backup & Restore**: `.restore <round>` to instantly roll back match state and scores.
- **Dashboard Optimized**: Pre-aggregated views for global leaderboards and Leetify-style analytics.

## Installation
1. Install [CounterStrikeSharp](https://docs.cssharp.dev/) on your CS2 server.
2. Place the plugin files in `game/csgo/addons/counterstrikesharp/plugins/statsCollector`.
3. Configure `config.json` (see `config.sample.json`).
4. Execute `database/init.sql` on your MySQL/MariaDB server.
5. Ensure .NET 8.0 or 9.0 runtime is installed.

### Minimal config example
```json
{
  "DatabaseHost": "127.0.0.1",
  "DatabasePort": 3306,
  "DatabaseName": "cs2_statscollector",
  "DatabaseUsername": "cs2_statscollector",
  "DatabasePassword": "your_password",
  "DatabaseSslMode": "Required",
  "FlushConcurrency": 4,
  "PersistenceChannelCapacity": 1000,
  "AutoSaveSeconds": 60,
  "TradeWindowSeconds": 5,
  "TradeDistanceThreshold": 1000.0,
  "ClutchSettings": {
    "BaseMultiplier": 1.0,
    "DifficultyWeight": 0.2
  },
  "DeathmatchMode": false,
  "LogLevel": "Information"
}
```

## Data Model (Late 2025 Relational)
- **`matches` / `rounds`**: Core lifecycle entities for grouping events.
- **`players`**: Core registry with first/last seen timestamps.
- **`player_stats`**: Lifetime aggregated statistics across 100+ metrics.
- **`match_player_stats`**: Per-match performance summaries (Fast for dashboards).
- **`match_weapon_stats`**: Per-match weapon lethality and accuracy.
- **`player_advanced_analytics`**: Temporal snapshots of performance (Rating 2.0, Impact, etc.).
- **`kill_positions` / `death_positions` / `utility_positions`**: XYZ coordinates linked to `match_id` for heatmaps.

## Dashboard Integration (Optimized Views)
The database includes pre-optimized views for near-instant dashboard rendering:
- `view_enhanced_player_analytics`: HLTV/Leetify style performance summary.
- `view_clutch_performance`: Detailed 1vX success rates.
- `view_entry_efficiency`: Opening duel metrics.
- `view_global_leaderboard`: Ranked by Rating 2.0 with activity filters.
- `view_player_match_history`: Complete match-by-match breakdown.

## Architecture
- **Centralized Instrumentation**: Global `Instrumentation` class for OTEL ActivitySource and Meters.
- **Resilience**: Polly v8 Pipelines for all DB interactions.
- **Async Flow**: CS2 event → processor → PlayerSessionService → PlayerSnapshot → Channel → StatsRepository → MySQL.

## Enterprise Best Practices (Late 2025)
- **Non-blocking I/O**: Game thread never waits for the database.
- **Resilience**: Database transient failures are handled gracefully by Polly v8.
- **Observability**: Metrics (Meters) and Traces (ActivitySource) ready for Prometheus/Grafana/Jaeger.
- **Resource Management**: Proper `IAsyncDisposable` implementation for clean shutdowns.

## Development
- Build: `dotnet build`
- Publish: `dotnet publish -c Release`
- Requirements: .NET 8 SDK, CounterStrikeSharp SDK, MySQL Connector
- License: MIT

## Support
- Issues: https://github.com/ezspot/cs2-statscollector/issues
- CounterStrikeSharp docs: https://docs.cssharp.dev/
- CS2 game events: https://cs2.poggu.me/dumped-data/game-events

## License
MIT
