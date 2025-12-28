# statsCollector Quick Start

Get up and running fast. For full details, see README.md.

## Prerequisites
- CounterStrikeSharp v180+ under `addons/counterstrikesharp`
- MySQL 8.0+
- .NET 8 runtime
- CS2 server with admin access

## Install
1) Stop the CS2 server.  
2) Download the latest release and extract to:  
   `game/csgo/addons/counterstrikesharp/plugins/statsCollector/`  
3) Create `config.json` in that folder:
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
  "DeathmatchMode": false,
  "LogLevel": "Information"
}
```
4) Start the server. The plugin creates tables automatically.  
   Reload if needed: `css_plugins reload statsCollector`

## Build from source
1) Install .NET 8 SDK.  
2) From repo root, run:  
   - `dotnet restore`  
   - `dotnet build`  
   - (Release publish) `dotnet publish -c Release -o out`  
3) Copy the built plugin from `out/` to `game/csgo/addons/counterstrikesharp/plugins/statsCollector/`.

## Verify
- Console: look for successful load and DB connection messages.  
- In-game: play a few rounds, then run `css_stats`.  
- DB: `SELECT * FROM player_stats LIMIT 5;`

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
