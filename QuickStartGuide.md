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
4) Execute `database/init.sql` on your MySQL server to create the schema.
5) Start the server. The plugin will connect and begin tracking.  
   Verify with: `css_plugins list`

## Build from source
1) Install .NET 8 SDK.  
2) From repo root, run:  
   - `dotnet restore`  
   - `dotnet build`  
   - (Release publish) `dotnet publish -c Release -o out`  
3) Copy the built plugin from `out/` to `game/csgo/addons/counterstrikesharp/plugins/statsCollector/`.

## Verify (Late 2025 Tracing)
- **Console**: Look for "statsCollector plugin loaded successfully with full observability".
- **Logs**: Check `logs/statscollector-*.log` for "Creating new session for player" and "Flushing batch".
- **DB**: Check `player_advanced_analytics` for Rating 2.0 snapshots.
- **Heatmaps**: Check `kill_positions` and `utility_positions` for spatial data.

## Environment overrides (optional)
Prefix with `STATSCOLLECTOR_`, e.g. `STATSCOLLECTOR_DATABASE_PASSWORD`.

## Troubleshooting (quick)
- DB connection failed: confirm MySQL is running, credentials, firewall, SSL mode.  
- Plugin won’t load: check CounterStrikeSharp install, .NET runtime, permissions, logs.  
- Stats not saving: DB perms, AutoSaveSeconds reasonable, check console errors.

## Resources
- README.md (full guide)
- GitHub: https://github.com/ezspot/cs2-statscollector
- CounterStrikeSharp docs: https://docs.cssharp.dev/
- CS2 game events: https://cs2.poggu.me/dumped-data/game-events
