# statsCollector

A comprehensive CounterStrikeSharp plugin for CS2 that collects advanced player statistics with robust architecture. Built with .NET 8+ using dependency injection, async/await, Polly resilience policies, structured logging, and thread-safe operations.

## Overview
statsCollector is a CounterStrikeSharp plugin for CS2 that records player performance, utility, bomb, and weapon analytics with a minimal, API-friendly schema.

## Features (high level)
- Combat: kills, deaths, assists, headshots, damage, hit groups, accuracy, entry/trade/multi-kills, clutches.
- Utility: thrown counts, effectiveness (flashes/smokes/HE/molotov/decoy), utility damage, flash assists, players blinded.
- Bomb: plants/defuses/attempts/aborts, drops/pickups, clutch defuses, bomb-related kills/deaths.
- Weapons: per-weapon kills/shots/hits/headshots, accuracy.
- Economy & movement: money spent, items purchased/picked up/dropped, equipment value, jumps, CT/T rounds.
- Ratings & analytics: HLTV 2.1 Rating (with Survival component), Leetify-grade Utility Score, Impact 2.0, KAST, ADR, Clutch Points (weighted), performance score, top weapon by kills.

## Installation
1. Install CounterStrikeSharp on your CS2 server.
2. Place the plugin in `addons/counterstrikesharp/plugins/statsCollector`.
3. Configure `config.json` (DB connection, logging, concurrency). The plugin auto-creates tables.
4. Restart or reload the plugin.

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

Environment overrides: prefix with `STATSCOLLECTOR_` (e.g., `STATSCOLLECTOR_DATABASE_PASSWORD`).

**PersistenceChannelCapacity**: controls the bounded in-memory queue for snapshot writes. Increase for high-throughput servers to avoid producer backpressure; default: 1000.
**TradeDistanceThreshold**: Maximum distance (in game units) between teammates to qualify as a trade opportunity. Default: 1000.
**ClutchSettings**: Tuning for difficulty-weighted clutch points.

## Data model
- `players`: steam_id, name, first_seen, last_seen (registry).
- `player_stats`: aggregated player stats (combat, utility, economy, movement, ratings).
- `weapon_stats`: per-weapon stats per player.
- `player_advanced_analytics`: snapshot analytics (HLTV 2.1, KAST, Impact 2.0, ADR, Clutch Points, Utility Score, success rates, KD, HS%, performance score). Primary key: (steam_id, calculated_at).

## Architecture (event routing at a glance)
- One event → one processor:
  - Combat: `player_death`, `player_hurt`, `weapon_fire`, `bullet_impact`, `round_mvp`, `player_avenged_teammate`.
  - Utility: `player_blind`, `hegrenade_detonate`, `flashbang_detonate`, `smokegrenade_detonate`, `molotov_detonate`.
  - Bomb: `bomb_planted`, `bomb_defused`, `bomb_exploded`, `bomb_dropped`, `bomb_pickup`, `bomb_beginplant/abortplant`, `bomb_begindefuse/abortdefuse`, `defuser_dropped/pickup`.
  - Economy: `item_purchase`, `item_pickup`, `item_equip`.
- Flow: CS2 event → event handler → processor → PlayerSessionService (thread-safe) → PlayerSnapshot → StatsRepository → MySQL.

## Event coverage
- Lifecycle: connect, spawn, round start/end, disconnect.
- Combat: kills, deaths, assists, damage, hit groups, bullet impacts.
- Utility: flashes, smokes, HE, molotov/incendiary, decoy; utility damage/effectiveness.
- Bomb: plant/defuse/abort, drops/pickups, clutch defuses.
- Economy & movement: money changes, buys, items picked up/dropped, jumps, CT/T rounds.
- Hostage: interactions, rescue, damage.
- Score: team rounds, MVPs, match lifecycle.

### Deathmatch mode
- Set `DeathmatchMode: true` to skip round-based resets and accounting (KAST/trade/clutch/round win-loss).
- In this mode, stats are aggregated across the continuous session; round-dependent metrics are not updated.

## Development
- Build/publish: `dotnet build` / `dotnet publish -c Release`
- Creator: Anders Giske Hagen
- Requirements: .NET 8 SDK, CounterStrikeSharp SDK, MySQL Connector
- License: MIT
## Support
- Issues: https://github.com/ezspot/cs2-statscollector/issues
- CounterStrikeSharp docs: https://docs.cssharp.dev/
- CS2 game events: https://cs2.poggu.me/dumped-data/game-events

## License
MIT
