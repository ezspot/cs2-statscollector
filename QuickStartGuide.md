# statsCollector Quick Start v1.2.0

Get up and running fast with enterprise-grade CS2 stats collection. For full details, see README.md.

## Prerequisites
- CounterStrikeSharp v1.0.340+
- MySQL 8.0+ or MariaDB 10.11+
- .NET 8.0 or 9.0 runtime
- CS2 server with admin access

## Install
1) Stop the CS2 server.  
2) Download the latest release and extract to:  
   `game/csgo/addons/counterstrikesharp/plugins/statsCollector/`  
3) Create `config.json` in that folder (see `config.sample.json` for all options):
```json
{
  "DatabaseHost": "127.0.0.1",
  "DatabasePort": 3306,
  "DatabaseName": "cs2_statscollector",
  "DatabaseUsername": "cs2_statscollector",
  "DatabasePassword": "your_password",
  "DatabaseSslMode": "Required",
  "AutoSaveSeconds": 60,
  "LogLevel": "Information"
}
```
4) Execute `database/init.sql` on your MySQL server to create the schema and optimized views.
5) Start the server. The plugin will connect and begin tracking.  
   Verify with: `css_plugins list`

## Verify Relational Data (Late 2025)
- **Matches**: `SELECT * FROM matches;` (Check for 'IN_PROGRESS' or 'COMPLETED' status)
- **Player Profiles**: `SELECT * FROM view_player_profile;` (Aggregated summary)
- **Leaderboard**: `SELECT * FROM view_global_leaderboard LIMIT 10;`
- **Heatmaps**: `SELECT * FROM kill_positions WHERE match_id = 1;` (Spatial data)

## Verify (Late 2025 Tracing)
- **Console**: Look for "statsCollector plugin loaded successfully with full observability".
- **Logs**: Check `logs/statscollector-*.log` for "Creating new session for player" and "Flushing batch".
- **DB**: Check `player_advanced_analytics` for Rating 2.0 snapshots.
- **Heatmaps**: Check `kill_positions` and `utility_positions` for spatial data.

## Build from source
1) Install .NET 8 SDK.  
2) From repo root, run: `dotnet publish -c Release -o out`  
3) Copy the built plugin from `out/` to your server.

## Troubleshooting
- **No data in views**: Views like `view_global_leaderboard` have a 10-round activity threshold by default.
- **DB Errors**: Check `logs/statscollector-*.log` for "Failed to persist batch".
- DB connection failed: confirm MySQL is running, credentials, firewall, SSL mode.  
- Plugin won’t load: check CounterStrikeSharp install, .NET runtime, permissions, logs.  
- Stats not saving: DB perms, AutoSaveSeconds reasonable, check console errors.

## Environment overrides (optional)
Prefix with `STATSCOLLECTOR_`, e.g. `STATSCOLLECTOR_DATABASE_PASSWORD`.

## Resources
- README.md (full guide)
- GitHub: https://github.com/ezspot/cs2-statscollector
- CounterStrikeSharp docs: https://docs.cssharp.dev/
- CS2 game events: https://cs2.poggu.me/dumped-data/game-events
