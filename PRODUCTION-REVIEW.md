# Production-readiness review

**Audited revision** `eeffb44` (18 November 2026); every file:line below refers to it unless another revision is named.
The fixes in the first table are commit `e79ef9c`, apart from the rows marked follow-up. The follow-up (`ba72500`)
removes what was kept only for compatibility (the plugin is not in production) and fixes one defect found by running the
plugin on a live CS2 server. The second follow-up, uncommitted when this was written, fixes four defects found with a
player on that server. During play nothing in the plugin approaches a tick: the largest managed cost on one tick is a
round end (about 0.2 ms against a 15.6 ms tick at 64 Hz), and on the live server no tick exceeded 200 ms outside the
unloads against a frozen database. The operational risks were at unload with a hung database, in the spill file's
lifetime, and in rows the database rejects.

## Baseline

- Clean `main...origin/main`. No AGENTS.md, CI workflow or global.json in the checkout. SDK 10.0.301 selected (8.0.414
  also installed). Both projects target `net10.0`; plugin 1.0.0.
- Pins: CounterStrikeSharp.API 1.0.379 (runtime assets excluded, the host provides them), Polly.Extensions 8.9.0
  (Polly.Core 8.9.0 since the follow-up), MySqlConnector 2.6.2, Dapper 2.1.89; tests: Microsoft.NET.Test.Sdk 18.11.0,
  xunit 2.9.3, xunit.runner.visualstudio 3.1.5.
- Documented deployment: CounterStrikeSharp 1.0.379+ with Metamod:Source 2.x build 1467+, MySQL 8.4 or 9.7 LTS with
  `local_infile=ON`, CS2 started with `-allow_round_backup_files` for `.restore`. Sized for one scrim server with about 16
  clients; several servers may share one database.
- `dotnet build -c Release`: 0 warnings, 0 errors. `dotnet test tests/statsCollector.Tests`: 37 passed, 0 failed, 0
  skipped. No pre-existing failures.

## Fixed

| Severity · confidence | Trigger → consequence | Evidence at eeffb44 | Change |
|---|---|---|---|
| High · high | Unload or reload while MySQL hangs. The game thread froze past the 10 s budget (measured 12.03 s, 2.0 s of it inside `Cancel()`); the stats worker spilled `pending_stats.json` 18.4 s after Unload returned, so a reload in that window never replayed it and a later start replayed it over newer totals (reproduced: lifetime kills 9 → 5). | `src/Plugin.cs:201` `Cancel()` runs the token's callbacks inline; MySqlConnector's cancel opens a KILL QUERY connection synchronously. `:204-205` waits with `GetResult()`. `src/Services/PersistenceChannel.cs:194-210` writes the leftover batch again with its own 10 s. No `DefaultCommandTimeout`/`CancellationTimeout` (`src/Config/PluginConfig.cs:91-112`): an in-flight query ran 32 s. No `BulkCopyTimeout` (`src/Services/Data/StatsRepository.cs:258,660`, `src/Services/Tracking/PositionTrackingService.cs:83,148,212`): LOAD DATA had no timeout at all. | `CancelAsync()`; a 7 s cleanup budget inside a hard 10 s wait, so what the budget's cancellation sets off normally finishes before Unload returns; the stop spills the leftover batch instead of writing it again; `DefaultCommandTimeout` = 5 s, `CancellationTimeout` = 1 s, `BulkCopyTimeout` = 5 s on every bulk copy (the init.sql script keeps 30 s). Measured: hung query 32.0 → 6.0 s, hung bulk copy > 45 s → 6.3 s, one pipeline call ≈ 48 → 22 s. |
| High · high | A start restores a spill, newer batches are written while a later one still fails, then the process crashes. The next start replayed the old file over the newer rows (reproduced: kills 7 → 5; also re-runs old voids and match starts). | `PersistenceChannel.cs:261-263` claims nothing newer is written first; `:465` deletes the file only when the whole buffer is empty. | The file is deleted as soon as the restored batches leave the head of the buffer, before anything newer is written. |
| Medium · high (follow-up, found on the live server) | Unload or server shutdown while the database host is frozen and a connection attempt is in flight (the first calls of an outage, or the breaker's probe). The spill was written 3.0 s after Unload returned and its log lines missed the log file; a server that exits in that window loses everything the retry buffer held. | `src/Infrastructure/Database/ConnectionFactory.cs:30`: MySqlConnector's connect ignores the token until `ConnectionTimeout`. `src/Services/PersistenceChannel.cs:288` at `e79ef9c`: `base.StopAsync` returns when the caller's token is cancelled, before the worker has spilled. | The caller of a connect stops waiting when its token is cancelled (the attempt ends by itself and is then disposed), and the stop waits up to 2 s for the worker. Live server, database frozen: Unload blocks 7.03 s as before, the spill is on disk 0.08 s before it returns, and its lines are in the log file. |
| Medium · high | A player with ≥ 100 per-round utility damage in their first recorded rounds (or a K/D ≥ 100). The analytics INSERT fails with error 1264, which is classed as permanent, so that row is dropped with a Critical log. | `database/init.sql:357,360,370,376,380,381` are `DECIMAL(5,3)`; the same ratios in `player_stats` are `DECIMAL(8,3)`. Reproduced: match rows 1, analytics rows 0. | Six columns widened to `DECIMAL(8,3)`. |
| Low-Medium · high | `DatabaseSslCa` set with the default `Required`. MySqlConnector checks no certificate in that mode, so the CA was ignored and the password went to whoever terminated TLS. | `PluginConfig.cs:41,106,108`; MySqlConnector docs: Required "does not validate CA or hostname". | `Validate()` refuses a CA without `VerifyCA`/`VerifyFull`. |
| Low · high | Operator visibility: no line said a buffered backlog had been written; positions queued before the writer started were dropped without a line; sessions never seeded were lost at unload without a line. | `PersistenceChannel.cs:442-466`; `src/Services/Persistence/PositionPersistenceService.cs:158`; `src/Services/Core/PluginLifecycleService.cs:112-120`. | One Information line when the retry buffer drains; a Warning with the count in both loss cases. |
| Low · high | Docker healthcheck put the root password on a command line every 10 s and never logged in (root exists only as `root@localhost`; `mysqladmin ping` exits 0 even on "Access denied"). | `MySQL-docker/docker-compose.yml:35`. | Credential-free TCP ping; verified healthy only after init.sql and the final server are up. |
| Low · medium | After an outage, one batch can hold several versions of a row (a round row is written three times); which version won depended on the join's row order. | `StatsRepository.cs:287-299`. | `ORDER BY t.row_id`: the last staged version wins. |
| Low · high | A held session that continues on reconnect counted the time away as playtime. | `src/Services/Core/PlayerSessionService.cs:71-72`. | The session's start moves forward by the time away. |
| Low · high | Game-thread waste per event: Utility's `player_hurt` read both players before discarding gun damage; `weapon_fire` built a full snapshot per shot; purchases read strings only for disabled trace logs. | `src/Services/Processors/UtilityEventProcessor.cs:159-165`, `CombatEventProcessor.cs:570-571`, `EconomyEventProcessor.cs:39,43,57,62`. Each snapshot measured 0.69–1.11 µs and 0.9–1.2 KB. | Filter first; direct checks equivalent to the snapshot's validity; trace reads removed. |
| Medium · high in code, not yet seen live (second follow-up, found with a player on the live server) | A player joins or reconnects during freeze time. The round was not counted as played for them and got no `player_round_stats` row, while its kills and deaths went into the lifetime and match totals (seen: a death in an uncounted round; a match row of 7 kills in "1 round"). | The engine puts a returning player back on their team before the session exists, so no `player_team` or `round_start` set `CurrentTeam` (`src/Services/Handlers/PlayerLifecycleHandler.cs:187-196`, `MatchFlowHandler.cs:330` at `ba72500`); and round starts were recorded only at `round_start` (`src/Services/Match/RoundBackupService.cs:74-81`). | The freeze end sets the team of every session on a side and adds the sessions missing from the round's recorded start. Unit test for the second part; the fix was deployed but no player joined again to confirm it. |
| Medium · high in code, not yet seen live (second follow-up) | `mp_restartgame` mid-round. The aborted round's kills stayed in lifetime totals, in the ended match's row and in the position tables (seen with a player: 3 kills of an abandoned round kept). | `MatchFlowHandler.cs:373-375` at `ba72500` returned on a restart; engine order confirmed: `round_officially_ended` without `round_end`. | A restart while a round is live (after its freeze end, before its `round_end`) rolls that round back as a restore does, before the match is finalized. A restart in freeze time or after the match ended rolls nothing back. |
| Low-Medium · high in code, not yet seen live (second follow-up, found with a player on the live server) | A player leaves mid-round. Their kills and deaths of that round stayed in the lifetime and match totals, but the round was not counted as played and had no round row (seen: 12 kills over "3 rounds" where 4 were played, kills per round 4.0). | `PlayerLifecycleHandler.cs:144-166` at `ba72500` removed the session at the disconnect; the round end counts rounds only for sessions that exist (`MatchFlowHandler.cs:326-360`). | Decided: the round counts, complete. While a round is being played a leaver's session is kept until that round is over, so the round end counts it for them like for everyone else (played, won or lost, side, KAST, a round row) and only then writes their match and lifetime rows; they count as not having survived it, and playtime ends at the disconnect. Coming back before the round ends continues the session. A restart that abandons the round rolls it back for them too. When the last player on the server leaves, nothing is kept: an empty server may hibernate without ending the round, so everyone who left during it is written at once, with the round not counted (as before). Unit tests for the kept session, its playtime and that rollback; the two event handlers that decide it have none. |
| Low · high in code, not yet seen live (second follow-up, found with a player on the live server) | A match finalized after a reconnect wrote `top_weapon_by_kills` "None" (weapon totals restart with the session), replacing the value written at the disconnect. | `src/Services/Analysis/AnalyticsService.cs` (`topWeapon`), `MatchStatsService.BuildAnalyticsSnapshot` at `ba72500`. | The match's analytics row takes the top weapon from the match's weapon kills, which carry the earlier session. Unit test. |

## Open, in priority order

| Severity · confidence | Trigger → consequence | Evidence at eeffb44 | Smallest corrective action |
|---|---|---|---|
| Medium · high | Plugin reload during a scrim (DLL replace hot-reloads). Nothing is recorded until an admin runs `.scrim recover`; the match continues under a new UUID; a tactical pause loses its auto-unpause and the knife side choice its deadline. CounterStrikeSharp also reloads on its own when a plugin file changes (see the live-server run). | `src/Services/Match/ScrimManager.cs:47` starts Idle; `src/Infrastructure/EventDispatcher.cs:52-55` drops events when not Live; `src/Plugin.cs:120-122` starts a new match. | On load, recover automatically when `scrim_state.json` says KnifeRound/Live on the current map; otherwise warn in chat. Until then, deploy between matches. |
| Medium · high | A phase cfg is missing or misnamed (Linux is case-sensitive). The loader warns and reports success, and the match goes live with knife settings (`mp_maxmoney 0`, no C4). | `src/Services/Config/ConfigLoaderService.cs:43-47`; `ScrimManager.cs:82,290-291`. | Throw for a missing file and keep the scrim in its phase; check all five files at startup. |
| Medium · high | A position write commits and its acknowledgement is lost (or the 5 s timeout fires after the commit). The retry adds a second kill, death or utility point. Measured 1 event → 2 rows. | `PositionTrackingService.cs:87-111,152-163,216-233`; `init.sql:392-463` has no event identity. | `event_id BINARY(16) NULL` with a UNIQUE key on the three tables, a GUID set at capture, and a dedupe on merge. Edit init.sql in place. |
| Medium · high | A player acts before their lifetime row has been read (slow database, startup). Lifetime is right, but that play is missing from the match and round rows; a player who leaves before the seed gets no match row; a session still unseeded at unload is lost (now logged). Test case: stored 7 kills plus 1 early kill gives lifetime 8 but match 0 instead of 1. | `src/Services/Handlers/PlayerLifecycleHandler.cs:69-78`; `src/Services/Match/MatchStatsService.cs:69-86,116`. | Needs a design: keep the unseeded session's raw totals at match and round start and adjust them by the stored row when the seed lands. A baseline taken from the stored row alone counts an earlier match's play when the seed lands in a later match. |
| Medium-Low · high | Timestamps after an outage or a spill replay: matches and rounds get the write time, so start = end = replay time. Seen on the live server: a match that ended at 15:24:45 during the freeze has `end_time` 15:28:44. | `StatsRepository.cs:360-416`; the updates carry no event time (`PersistenceChannel.cs:35-60`). | Carry the event's UTC time in the match and round updates and write it inside the existing COALESCE (the spill format gains a field). |
| Low · high | Remainder of the unload race. If the stats worker needs more than 2 s after the cleanup budget to stop, its spill still lands after Unload returned (not reached on the live server: 0.08–0.09 s before the return). And if a new instance had restored an older spill, removing that one also removes a late one. | `src/Services/PersistenceChannel.cs` (spill, `RemoveWrittenSpill`, `SpillGrace`). | Make one instance own the spill (an exclusive lock file the next worker waits for before restoring). |
| Low · high | A spill written by a build with renamed or missing snapshot fields replays them as 0 (lifetime columns are overwritten). | `PersistenceChannel.cs:243` default JSON options. | `UnmappedMemberHandling.Disallow` and `RespectRequiredConstructorParameters` when reading; a mismatching file is then moved aside. |
| Low · high | The retry buffer's "50 MiB" counts 1 KiB per update; real heap is 3.8–4.5 KiB, so the cap is ≈ 186–218 MiB plus a spill built as one string. | `PersistenceChannel.cs:84,428`. | Cap by update count and serialize the spill to a stream. This changes how much is kept in an outage. |
| Low · high | When a writer never started, every refused write logs synchronously on the game thread (≈ 10 µs each, up to 6 ms). | `PositionPersistenceService.cs:146`; `PersistenceChannel.cs:111-115`. | Log the first refusal and then a count. |
| Low · high | With `VerifyCA`/`VerifyFull` MySqlConnector serves the verified server's LOAD DATA LOCAL requests for files by name, so that server can read files the CS2 process can (server.cfg). Under `Required` only the plugin's own bulk data is sent. | MySqlConnector 2.6.2 `ResultSet.cs:64-73,196`. README now says so. | Use a database host trusted like the game server, or keep `Required` and pin the certificate with `RemoteCertificateValidationCallback`. |
| Low · high | The Compose stack grants the plugin account ALL PRIVILEGES on the database; the README's five grants suffice (verified on MySQL 8.4.13). | Image entrypoint behaviour. | An init script that revokes ALL and grants the five. |
| Low · high | Bot victims create a `players` row with steam_id 0 for the position foreign key. | `PositionTrackingService.cs:107-109`. | A nullable victim column written as NULL for bots. |
| Low · high | `.ready` broadcasts and rewrites `scrim_state.json` on every call, even unchanged; scrim commands use the claimed SteamID. | `ScrimManager.cs:337-371`. | Return early when unchanged; require `AuthorizedSteamID`. |
| Low · high | When cleanup outlives the unload, late lines are written after the log file was closed (since the follow-up the spill's own lines reach it in the frozen-database case). | `src/Plugin.cs:259`. | Close the logger after the writers have finished. |
| Low · high (live server) | A stop that is cut short logs "Position persistence stopped before catching up: 0 queued and 0 buffered position events were not written" even when nothing was pending. | `src/Services/Persistence/PositionPersistenceService.cs` (`StopAsync`). | Skip the warning when both counts are 0 and no batch was being written. |
| Low · high | A player leaves mid-round (their session is now kept to the round's end). That round's engine-side figures are not added for them: money spent, kill reward, score and live time are read from the player's controller at the round end, and it is gone. Kills, deaths, damage and the round's result are complete. | `MatchFlowHandler.Observe` runs only for connected players. | Read the engine counters once more in the disconnect handler, if the controller is still readable there (not checked on a live server). |
| Low · medium (expected from the server log, not seen with a kept session) | A player leaves mid-round while alive. The engine kills them after the disconnect (logged as a suicide), and the kept session now records it: one death and one suicide for that round. Before, the session was gone and the death was not recorded. | Server log of the live run: `disconnected`, then `committed suicide with "world"`; `CombatEventProcessor.cs:271-281`. | None if a leaver's death should count, as the engine's scoreboard does. To leave it out, skip a self-kill of a session whose player has left. |

Run CounterStrikeSharp v1.0.380 (12 Nov 2026) or later on the server: it carries the gamedata for CS2 1.41.9.7 and fix
#1484 (removing a timer from inside a timer callback could leave a freed timer in the timer list); `ScrimManager.cs:607-611
→ 673` can kill one timer from another's callback. The plugin, built against API 1.0.379, ran on v1.0.380 with CS2
1.41.9.8 (17 Nov 2026) in the live-server run.

Three suspected problems were checked and do not exist. Under `Required`, `AllowLoadLocalInfile` does not let a man in the
middle read game-server files (MySqlConnector refuses named files below VerifyCA). Polly's telemetry did not duplicate the
retry and breaker logs (the strategies are built in an inner builder without telemetry; the follow-up drops
Polly.Extensions and its telemetry altogether). And the planted-bomb lookup is not a cost on the game thread: 2–4 µs and
about 450 bytes per plant, defuse or explosion on de_dust2 with 310–325 entities (438 µs once, when first compiled); the
earlier 0.17–1.3 ms was an estimate for 1,000–8,000 entities. Of the previous review's findings (at 20e1fa0), the
spill-loss, restore-timeout and weapon-carry items were fixed by 2c73926 and eeffb44; the spill fix introduced the
replay-over-newer-rows problem fixed above.

## Game-thread and writer trace

- **Events** (`player_hurt`, `player_death`, `weapon_fire`, …) reach `EventDispatcher.Dispatch`, which gates scrim,
  warmup and deathmatch, then runs each subscribed handler in its own try/catch. Handlers read the controller and pawn,
  take the player's lock to mutate counters, and hand owned copies to the writers with `TryWrite`/`TryEnqueue`, which
  never wait. No SQL or file I/O. A seed holds a player's lock for 1.3–2.5 µs once per join.
- **Round flow** (`round_start` … `round_officially_ended`, `MatchFlowHandler`) runs on the game thread: engine counter
  reads, snapshots (38–40 µs and 32.6 KB for 10 players), deltas and queue writes; about 0.2 ms managed per round end.
- **Joins**: `player_connect_full` or `OnClientAuthorized` → `EnsurePlayer` → the seed's first step runs inline (≈ 2 µs)
  and yields at the flush; the database read and retries run on the pool.
- **Autosave** (`AddTimer`, 60 s) snapshots dirty seeded sessions and queues them (47 µs).
- **Unload** (game thread): kill the scrim and pause timers, `CancelAsync`, then wait at most 10 s for the final
  snapshot, match finalization, the stats flush, the position stop and container disposal (their budget ends at 7 s,
  after which the stats writer gets up to 2 s to spill); what is left continues on the pool.
- **Stats writer**: one reader, batches of 100, lifecycle updates inline in queue order, bulk upserts at batch end.
  Outages keep batches in order in memory; data errors drop only the rejected update; at stop the rest is spilled and
  replayed first at the next start. Every stats write is idempotent on retry (measured with a proxy that drops the
  acknowledgement); position inserts are not.
- **Position writer**: separate bounded channel, retry queue of 5,000, restore void as an ordered barrier; not spilled.

## Cleanup and reachability

Deleted in `e79ef9c` (behaviour-identical apart from log text):
- 25 whole-body try/catch blocks in event handlers (Bomb 9, Combat 7, Utility 7, Economy 2). Each handler is private and
  referenced only by its `Subscribe` call; `EventDispatcher.Dispatch` catches per handler, logs at Error with the stack
  trace and runs the next handler. Kept: `HandleBombDefused`/`HandleBombExploded` (their `finally` resets the bomb
  state) and `UpdateClutchStats` (reached from `OnRoundEnd`, which has no outer catch). Utility and Economy lost their
  now-unused logger.
- `MatchContext.MapName` (never read: every holder reads `MatchUuid`; not serialized or bound), six `IAnalyticsService`
  members used only inside the class (now private), the fallbacks in `PauseService.cs:39,98` and
  `PluginLifecycleService.cs:57` (`Validate` enforces the ranges before the config is stored), the unused `IsDirty`
  setter, three no-op `(MySqlConnection)` casts, a redundant copy and empty check in `UpsertPlayersCoreAsync`, the
  per-attempt error log and duplicate success log in `UpsertPlayersAsync`, and the Serilog override for
  `System.Net.Http` (no HTTP use).

Deleted in the follow-up. The plugin is not in production: there is no deployed database, spill file, scrim state or
config to stay compatible with, so `database/init.sql` and the JSON records were changed in place.
- Seven write-only `PlayerSnapshot` fields (`RoundStartUtc`, `AliveOnTeamAtRoundStart`, `AliveEnemyAtRoundStart`,
  `DidTradeThisRound`, `WasFlashedForKill`, `PlayerRank`, `AverageAssistsPerRound`) and what fed them: five `RoundStats`
  properties, the alive count in `OnRoundFreezeEnd` and two setters in `CombatEventProcessor`. Each name occurred only
  in the record, its feeder, the constructor call and a test's name list; not in SQL, views, Dapper rows or JSON readers.
- Two duplicate columns. `player_stats.players_blinded` always held `enemies_flashed` (both came from
  `Utility.EnemiesBlinded`); `flash_assisted_kills` in `player_stats` and `player_advanced_analytics` always held
  `flash_assists` (both from `Utility.FlashAssists`). The lifetime seed now reads `enemies_flashed`.
- `StatsUpdate.RoundNumber` (the void logic reads the snapshot's own round number) and `ScrimRecoveryData.SavedAtUtc`:
  written to the JSON files and never read.
- The three parallel 149-entry lists of the lifetime upsert (DataTable columns, row values, update assignments), now
  one `LifetimeColumns` list from which the bulk copy and the merge are generated. Compared mechanically with
  `e79ef9c`: the same property and CLR type for every column and the same assignments. The merge names its columns
  instead of `SELECT *`.
- The 181 positional arguments of `AnalyticsService.CreateSnapshot`, now named; compared pair by pair with `e79ef9c`.
- Polly.Extensions, replaced by Polly.Core: the pipeline is one singleton injected into both repositories (the same
  instance in both, checked through the real `BuildServices`). Its Serilog override, `Polly.Extensions.dll` and two
  Microsoft.Extensions DLLs go with it.
- `matches.status` 'ABORTED' (never written); `idx_matches_time`, `idx_player_stats_updated_at` and
  `idx_player_advanced_analytics_calculated_at` (no query or view uses them); `cfg/live_wingman.cfg` (referenced by
  nothing; `Scrim.LiveConfigPath` defaults to `live.cfg`); the `HumansBySteamId(IEnumerable)` overload.

Kept:
- `players.first_seen` and the `created_at` columns. Nothing in the repository reads them, but they record when a
  player was first seen and when a kill happened, which nothing else does.
- `Microsoft.Extensions.Logging.Abstractions.dll` and `Microsoft.Extensions.DependencyInjection.Abstractions.dll` in the
  package (MySqlConnector's dependencies). On the live server the plugin loads and writes without them, as
  CounterStrikeSharp supplies its own; leaving them out of the package would take two more version pins in the project
  file.

## Verification and rollout

| Check | eeffb44 | `e79ef9c` |
|---|---|---|
| `dotnet build -c Release` | 0 warnings, 0 errors | 0 warnings, 0 errors |
| `dotnet test tests/statsCollector.Tests` | 37 passed | 44 passed (7 new tests and one extended theory; those 10 cases all fail against eeffb44's code) |
| `dotnet publish -c Release` | 16 files | same 16 files; third-party DLLs and deps.json byte-identical; init.sql embedded with the widened columns |
| Hung query / bulk copy / pipeline call | 32.0 s / > 45 s / ≈ 48 s | 6.0 s / 6.3 s / 21.7–22.0 s |
| `Cancel()` with a query in flight | 2.02 s on the game thread | moved to the pool by `CancelAsync` (1.02 s there) |
| Unload with MySQL hung, seed reads and both writers mid-write (`Plugin.Unload` driven by reflection) | 12.03–16.05 s blocked; spill written after it returned | 7.01–7.02 s blocked; logger and container disposed and spill on disk before it returns |
| Unload with MySQL healthy, 480 lifetime and 240 position updates queued | not measured | 0.44 s; everything written, no spill file |
| Spill replay fuzz (1,200 random outage/recovery/crash orderings) | 246 runs violated ordering, 37 replayed over a newer write | 0 violations |

| Check | `e79ef9c` | Follow-up |
|---|---|---|
| Build and tests | 0 warnings, 0 errors; 44 passed | 0 warnings, 0 errors; 48 passed. New: two schema tests for the lifetime column list, and `ConnectStopsWaitingWhenItsTokenIsCancelled` (5.03 s before the fix) and `StopReturnsOnlyAfterAWorkerThatIsSlowToStopHasSpilled`, which both fail without the fix |
| `dotnet publish -c Release` | 16 files | 12 files: Polly.Core in place of Polly.Extensions, two Microsoft.Extensions DLLs and `live_wingman.cfg` gone |
| Live server: Unload with the database frozen | 7.03 s blocked; spill written 3.0 s after it returned, its lines missing from the log file | 7.03–7.04 s blocked (two runs); spill on disk 0.08–0.09 s before it returns, lines in the log file |
| Independent review of the cleanup diff | | no finding that changes recorded data; six leftovers, all fixed |
| Second follow-up (after `ba72500`) | 0 warnings, 0 errors; 48 passed | 0 warnings, 0 errors; 52 passed. New: `PlayerSeededDuringFreezeTimeGetsARoundStartAtFreezeEnd`, `LeaverIsKeptWhileARoundIsPlayedAndRemovedOnceItIsOver`, `RollingBackARoundEndsTheSessionOfAPlayerWhoLeftDuringIt`, `TopWeaponOfTheMatchCoversTheSessionBeforeAReconnect` (the last three fail without their fix). An independent review of the kept-session change found three defects, all fixed: a kept session written by a rollback or an unload counted the time since its player left as playtime, and an emptied server could keep it unwritten; a player back between the round end and the next round start got a second, empty round row; and a restart while the database was slow dropped the abandoned match's rows |

Database evidence for the first table came from disposable MySQL 9.7.4 and 8.4.13 containers initialized from
`database/init.sql`, driven by the real repository and writer classes; a TCP proxy simulated a server that stops
answering and a lost acknowledgement. No production server or database was used.

Live-server run (follow-up): a disposable `joedwards32/cs2` container with CS2 1.41.9.8, Metamod:Source 2.0.0-git1491,
CounterStrikeSharp v1.0.380 with its .NET 10.0.3 runtime and MySQL 9.7.4 (`DatabaseSslMode` `Required`), de_dust2 in
competitive mode with ten bots, `EnableScrim` off, `AutoCreateSchema` on. A probe plugin logged the order of round and
match events, every tick longer than 200 ms and the planted-bomb lookup; `docker pause` froze the database.
- The plugin loads, applies the embedded `init.sql` to an empty database and writes matches and rounds (winner, reason,
  bomb site, plant time, official end). It also loads with the two Microsoft.Extensions DLLs removed from its folder.
- No tick over 200 ms during play, the round writes and the autosave. With the database healthy (RCON round trips): unload
  55 ms, load 51 ms, reload 79 ms.
- Event order. A round: `round_end`, then 3 s later `cs_pre_restart`, `round_officially_ended`, `round_prestart`,
  `round_start`, `round_poststart`, and `round_freeze_end` after the freeze time. `round_announce_match_start` follows
  round 1's `round_freeze_end` on the same tick. Half time adds `announce_phase_end` after `round_end`. The last round
  ends with `round_end`, `cs_intermission` and `cs_win_panel_match`, and no `round_officially_ended` until the next
  restart. `mp_restartgame` mid-round gives `cs_pre_restart`, `round_officially_ended` and the round events without a
  `round_end`. These match what the handlers assume.
- A spill written at an unload against the frozen database is restored, written and removed at the next start.
- CounterStrikeSharp reloads the plugin by itself when a file in its folder changes: one copied DLL caused two reloads
  in the same second. The instance unloaded before its writer started dropped its two queued updates (a match start
  and end) with the warning "Stopped before the database was reachable".

Live-server run with a player (second follow-up, build `ba72500`): the same rig with a game server login token instead
of LAN mode, `-allow_round_backup_files`, and one human among nine bots for about 13 minutes.
- Join: Steam validated the player and the session started 3 s later, at `player_connect_full`; the lifetime row was
  read again at each of two reconnects.
- Rounds 1 and 2 against the server's own kill log: 8 kills, 4 headshot kills, one trade kill, two 4-kill rounds, the
  opening kill, both flashbangs (3 and 1 enemies blinded) and the money spent all match; 9 kill positions and 2 utility
  positions were stored.
- Round restore (`mp_backup_restore_load_file` for round 3, sent during round 5): lifetime totals went from 17 kills
  back to the 8 of the end of round 2, with headshots, damage, money, grenades and kill rewards; the round rows and
  positions of rounds 3 to 5 were removed; playtime kept counting. The replayed round 3 was recorded on top (10 kills,
  3 rounds).
- Reconnect: the 11 s away were not counted as playtime. With bots as the only other players the engine restarted the
  match when the player came back ("Game Commencing"), so a reconnect inside a running match was not exercised.
- No Error or Warning line from the plugin and no tick over 200 ms during the session.
- It exposed the four defects fixed in the second follow-up.

Rollout: replace the plugin files between matches. The schema changed, so drop the database and create it again (or
let `AutoCreateSchema` create it on an empty one); nothing is migrated. A config that sets `DatabaseSslCa` without
`VerifyCA`/`VerifyFull` no longer loads.

Not verified: the four fixes of the second follow-up on a live server (unit tests and code only; no player joined
again after them, and the round-end and disconnect handlers themselves have no unit tests); a reconnect inside a running match, which needs a second human; the scrim flow
(`.ready`, the knife round, pauses, `.restore` as a command, `.scrim recover`), whose commands need players and ran
only in the unit tests; bomb plants and defuses by a human; and a spill holding player rows (the frozen-database
unloads ran with bots only). The proxy-driven hang tests ran without TLS so the proxy could see the commands.
