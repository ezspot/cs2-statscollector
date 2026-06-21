# statsCollector — Quick Start (v3.1.0)

Get up and running fast. For full details, see [README.md](README.md).

## Prerequisites
- CounterStrikeSharp v1.0.369+ (.NET 10)
- .NET 10 SDK to build, .NET 10 runtime to run
- MySQL 8.0+ / 9.x or MariaDB 10.6+ (required for `POINT` and spatial indexes)
- CS2 server with admin access

## Database (Docker)
A MySQL 9.6 + phpMyAdmin stack is included:
```bash
cd MySQL-docker
docker compose up -d
```
This applies `database/init.sql` automatically on first start. To use an existing
MySQL server instead, run `database/init.sql` against it manually.

## Install
1. Stop the CS2 server.
2. Extract the plugin to `game/csgo/addons/counterstrikesharp/plugins/statsCollector/`.
3. Create `config.json` in that folder (see `config.sample.json`):
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
   To have the plugin create the schema for you, set `"AutoCreateSchema": true` instead of running
   `init.sql` manually (the DB user needs `CREATE` privileges).
4. Start the server and verify with `css_plugins list`.

## Verify
- **Console / logs**: look for `statsCollector v3.1.0 loading...` and `Background persistence services started.` (logs under `logs/statscollector-*.log`).
- **Matches**: `SELECT * FROM matches;`
- **Players**: `SELECT * FROM view_player_profile;`
- **Leaderboard**: `SELECT * FROM view_global_leaderboard LIMIT 10;`
- **Heatmaps**: `SELECT * FROM kill_positions LIMIT 10;`

## Build from source
```bash
dotnet publish -c Release -o out
```
Copy the built plugin from `out/` to your server.

## Troubleshooting
- **Plugin won't load**: check the CounterStrikeSharp install, .NET 10 runtime, and `logs/statscollector-*.log`.
- **DB connection failed**: confirm MySQL is reachable, credentials are correct, and `DatabaseSslMode` matches the server.
- **No data**: `view_global_leaderboard` has a 10-round activity threshold; check the log for `Failed to process persistence batch`.
- **Config overrides**: any option can be set via `STATSCOLLECTOR_`-prefixed environment variables.

## Links
- [README.md](README.md)
- GitHub: https://github.com/ezspot/cs2-statscollector
- CounterStrikeSharp docs: https://docs.cssharp.dev/
