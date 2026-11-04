# statsCollector — Quick Start (v3.1.0)

Get up and running fast. For full details, see [README.md](README.md).

## Prerequisites
- CounterStrikeSharp v1.0.379+ (.NET 10) with Metamod:Source 2.x build 1467+
- .NET 10 SDK to build
- MySQL 8.4 LTS or 9.7 LTS with `local_infile=ON`
- CS2 server with admin access, started with `-allow_round_backup_files` if you use `.restore`

## Database (Docker)
A MySQL 9.7 LTS + phpMyAdmin stack is included:
```bash
cd MySQL-docker
cp secrets/mysql_root_password.example secrets/mysql_root_password.txt
cp secrets/mysql_user_password.example secrets/mysql_user_password.txt
# Replace both copied values before starting MySQL.
docker compose up -d
```
This applies `database/init.sql` automatically on first start (empty volume only). To use an existing
MySQL server instead, create the database and load the schema into it
(`mysql -u <user> -p cs2_statscollector < database/init.sql`; the script does not select a database itself), and set
it up as in the README's *Database prerequisites* (`local_infile=ON`, `DELETE` and `CREATE TEMPORARY TABLES` grants).

## Install
1. Stop the CS2 server.
2. Extract the plugin to `game/csgo/addons/counterstrikesharp/plugins/statsCollector/` and copy `cfg/*.cfg`
   to `game/csgo/cfg/`.
3. Edit `game/csgo/addons/counterstrikesharp/configs/plugins/statsCollector/statsCollector.json` (created on
   first start; see `config.sample.json`):
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
   `init.sql` manually (the DB user also needs `CREATE, ALTER, INDEX, REFERENCES, CREATE VIEW, DROP`).
   For a remote database, use `DatabaseSslMode: "VerifyCA"` with `DatabaseSslCa` (see the README).
4. Start the server and verify with `css_plugins list`.

## Verify
- **Console / logs**: look for `statsCollector v3.1.0 loading...` (console and `addons/counterstrikesharp/logs/log-statsCollector*.txt`) and `Background persistence services started.` (`plugins/statsCollector/logs/statscollector-*.log`).
- **Matches**: `SELECT * FROM matches;`
- **Round completion**: `SELECT round_number, end_time, official_end_time FROM rounds;`
- **Utility lifecycle**: `SELECT utility_type, lifecycle_start_event, lifecycle_end_event, lifecycle_duration_ms FROM utility_positions;`
- **Exact lifetime counters**: `SELECT kast_rounds, multi_kill_2, multi_kill_3, multi_kill_4, multi_kill_5, clutch_kills, bomb_explosions FROM player_stats;`
- **Players**: `SELECT * FROM view_player_profile;`
- **Leaderboard**: `SELECT * FROM view_global_leaderboard LIMIT 10;`
- **Heatmaps**: `SELECT * FROM kill_positions LIMIT 10;`

## Multiple servers, one database
You can point several CS2 servers (target: up to ~5) at the same MySQL database — just give each server the same DB connection settings. Stats are keyed by SteamID64, and each server seeds a player's lifetime totals from the DB on connect, so career stats accumulate correctly even as players move between servers. See *Multi-server (shared database)* in the [README](README.md#multi-server-shared-database) for details.

> **Important:** each server uses up to **50** DB connections. For *N* servers, raise MySQL `max_connections` above `N × 50` (e.g. `300` for 5 servers) or you'll hit *"Too many connections"* under load.

## Build from source
```bash
dotnet publish -c Release -o out
```
Copy the built plugin from `out/` to your server.

## Troubleshooting
- **Plugin won't load**: check the CounterStrikeSharp install, .NET 10 runtime, `statsCollector.json`, and `addons/counterstrikesharp/logs/` (`log-statsCollector*.txt`, or `log-cssharp*.txt` for a config that fails to parse). The plugin's own log, `plugins/statsCollector/logs/statscollector-*.log`, starts only once the config is valid.
- **DB connection failed**: confirm MySQL is reachable, credentials are correct, and `DatabaseSslMode` matches the server.
- **Error 3948 / "Loading local data is disabled"**: enable `local_infile` on the MySQL server.
- **No data**: `view_global_leaderboard` has a 10-round activity threshold; check the log for `Failed to process persistence batch`.
- **Config overrides**: any option can be set via `STATSCOLLECTOR_`-prefixed environment variables; nested options
  use `__` (`STATSCOLLECTOR_Scrim__PlayersPerTeam`).
- **"Too many connections" (multi-server)**: raise MySQL `max_connections` to at least `50 × number_of_servers`.

## Links
- [README.md](README.md)
- GitHub: https://github.com/ezspot/cs2-statscollector
- CounterStrikeSharp docs: https://docs.cssharp.dev/
