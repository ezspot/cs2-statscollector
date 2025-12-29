# statsCollector

A comprehensive CounterStrikeSharp plugin for CS2 that collects advanced player statistics with robust architecture. Built with .NET 8+ using dependency injection, async/await, Polly v8 ResiliencePipelines, structured logging (Serilog), and full observability (OpenTelemetry).

## Overview
statsCollector is an enterprise-grade CounterStrikeSharp plugin for CS2 that records player performance, utility, bomb, and weapon analytics with high-performance async persistence and extensive telemetry.

## Features
- **Enterprise Observability**: Full OpenTelemetry (OTEL) integration for traces and metrics.
- **Structured Logging**: Serilog integration with daily rolling files and console output.
- **Robust Persistence**: Polly v8 resilience pipelines for database operations.
- **High Performance**: `System.Threading.Channels` for non-blocking database writes.
- Combat: kills, deaths, assists, headshots, damage, accuracy, entry/trade/multi-kills, clutches, **trade windows missed**.
- Utility: thrown counts, effectiveness, utility damage, flash assists, **flash waste tracking**.
- Bomb: plants/defuses (with duration telemetry), clutch defuses, bomb-related kills/deaths.
- **Heatmaps**: Comprehensive position tracking for kills, deaths, and utility usage.

## Installation
1. Install CounterStrikeSharp on your CS2 server.
2. Place the plugin in `addons/counterstrikesharp/plugins/statsCollector`.
3. Configure `config.json` (DB connection, logging, concurrency).
4. Run the provided `database/init.sql` on your MySQL server.
5. Ensure the .NET 8+ runtime is installed on the server.

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

## Data model
- `players`: steam_id, name, first_seen, last_seen.
- `player_stats`: aggregated player stats (combat, utility, economy, movement, ratings).
- `weapon_stats`: per-weapon stats per player.
- `player_advanced_analytics`: high-resolution snapshot analytics.
- `kill_positions`: coordinates, weapon, distance, and round context.
- `death_positions`: death coordinates and cause.
- `utility_positions`: throw/land coordinates and affected player counts.

## Architecture
- **Centralized Instrumentation**: Global `Instrumentation` class for OTEL ActivitySource and Meters.
- **Resilience**: Polly v8 Pipelines for all DB interactions.
- **Async Flow**: CS2 event → processor → PlayerSessionService → PlayerSnapshot → Channel → StatsRepository → MySQL.

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
