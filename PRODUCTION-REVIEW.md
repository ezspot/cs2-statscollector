# Production-readiness review

**Audited revision** `eeffb44` (18 November 2026); every file:line below refers to it. The fixes listed as made are an
uncommitted patch on top of it. During play nothing in the plugin approaches a tick: the largest managed cost on one tick
is a bomb plant or defuse (0.2–1.3 ms against a 15.6 ms tick at 64 Hz). The operational risks were at unload with a hung
database, in the spill file's lifetime, and in rows the database rejects.

## Baseline

- Clean `main...origin/main`. No AGENTS.md, CI workflow or global.json in the checkout. SDK 10.0.301 selected (8.0.414
  also installed). Both projects target `net10.0`; plugin 1.0.0.
- Pins: CounterStrikeSharp.API 1.0.379 (runtime assets excluded, the host provides them), Polly.Extensions 8.9.0,
  MySqlConnector 2.6.2, Dapper 2.1.89; tests: Microsoft.NET.Test.Sdk 18.11.0, xunit 2.9.3, xunit.runner.visualstudio 3.1.5.
- Documented deployment: CounterStrikeSharp 1.0.379+ with Metamod:Source 2.x build 1467+, MySQL 8.4 or 9.7 LTS with
  `local_infile=ON`, CS2 started with `-allow_round_backup_files` for `.restore`. Sized for one scrim server with about 16
  clients; several servers may share one database.
- `dotnet build -c Release`: 0 warnings, 0 errors. `dotnet test tests/statsCollector.Tests`: 37 passed, 0 failed, 0
  skipped. No pre-existing failures.

## Fixed in this patch

| Severity · confidence | Trigger → consequence | Evidence at eeffb44 | Change |
|---|---|---|---|
| High · high | Unload or reload while MySQL hangs. The game thread froze past the 10 s budget (measured 12.03 s, 2.0 s of it inside `Cancel()`); the stats worker spilled `pending_stats.json` 18.4 s after Unload returned, so a reload in that window never replayed it and a later start replayed it over newer totals (reproduced: lifetime kills 9 → 5). | `src/Plugin.cs:201` `Cancel()` runs the token's callbacks inline; MySqlConnector's cancel opens a KILL QUERY connection synchronously. `:204-205` waits with `GetResult()`. `src/Services/PersistenceChannel.cs:194-210` writes the leftover batch again with its own 10 s. No `DefaultCommandTimeout`/`CancellationTimeout` (`src/Config/PluginConfig.cs:91-112`): an in-flight query ran 32 s. No `BulkCopyTimeout` (`src/Services/Data/StatsRepository.cs:258,660`, `src/Services/Tracking/PositionTrackingService.cs:83,148,212`): LOAD DATA had no timeout at all. | `CancelAsync()`; a 7 s cleanup budget inside a hard 10 s wait, so what the budget's cancellation sets off normally finishes before Unload returns; the stop spills the leftover batch instead of writing it again; `DefaultCommandTimeout` = 5 s, `CancellationTimeout` = 1 s, `BulkCopyTimeout` = 5 s on every bulk copy (the init.sql script keeps 30 s). Measured: hung query 32.0 → 6.0 s, hung bulk copy > 45 s → 6.3 s, one pipeline call ≈ 48 → 22 s. |
| High · high | A start restores a spill, newer batches are written while a later one still fails, then the process crashes. The next start replayed the old file over the newer rows (reproduced: kills 7 → 5; also re-runs old voids and match starts). | `PersistenceChannel.cs:261-263` claims nothing newer is written first; `:465` deletes the file only when the whole buffer is empty. | The file is deleted as soon as the restored batches leave the head of the buffer, before anything newer is written. |
| Medium · high | A player with ≥ 100 per-round utility damage in their first recorded rounds (or a K/D ≥ 100). The analytics INSERT fails with error 1264, which is classed as permanent, so that row is dropped with a Critical log. | `database/init.sql:357,360,370,376,380,381` are `DECIMAL(5,3)`; the same ratios in `player_stats` are `DECIMAL(8,3)`. Reproduced: match rows 1, analytics rows 0. | Six columns widened to `DECIMAL(8,3)`. Existing database: `ALTER TABLE player_advanced_analytics MODIFY impact_score DECIMAL(8,3) NOT NULL, MODIFY utility_impact_score DECIMAL(8,3) NOT NULL, MODIFY kd_ratio DECIMAL(8,3) NOT NULL, MODIFY utility_usage_per_round DECIMAL(8,3) NOT NULL, MODIFY survival_rating DECIMAL(8,3) NOT NULL, MODIFY utility_score DECIMAL(8,3) NOT NULL;` |
| Low-Medium · high | `DatabaseSslCa` set with the default `Required`. MySqlConnector checks no certificate in that mode, so the CA was ignored and the password went to whoever terminated TLS. | `PluginConfig.cs:41,106,108`; MySqlConnector docs: Required "does not validate CA or hostname". | `Validate()` refuses a CA without `VerifyCA`/`VerifyFull`. |
| Low · high | Operator visibility: no line said a buffered backlog had been written; positions queued before the writer started were dropped without a line; sessions never seeded were lost at unload without a line. | `PersistenceChannel.cs:442-466`; `src/Services/Persistence/PositionPersistenceService.cs:158`; `src/Services/Core/PluginLifecycleService.cs:112-120`. | One Information line when the retry buffer drains; a Warning with the count in both loss cases. |
| Low · high | Docker healthcheck put the root password on a command line every 10 s and never logged in (root exists only as `root@localhost`; `mysqladmin ping` exits 0 even on "Access denied"). | `MySQL-docker/docker-compose.yml:35`. | Credential-free TCP ping; verified healthy only after init.sql and the final server are up. |
| Low · medium | After an outage, one batch can hold several versions of a row (a round row is written three times); which version won depended on the join's row order. | `StatsRepository.cs:287-299`. | `ORDER BY t.row_id`: the last staged version wins. |
| Low · high | A held session that continues on reconnect counted the time away as playtime. | `src/Services/Core/PlayerSessionService.cs:71-72`. | The session's start moves forward by the time away. |
| Low · high | Game-thread waste per event: Utility's `player_hurt` read both players before discarding gun damage; `weapon_fire` built a full snapshot per shot; purchases read strings only for disabled trace logs. | `src/Services/Processors/UtilityEventProcessor.cs:159-165`, `CombatEventProcessor.cs:570-571`, `EconomyEventProcessor.cs:39,43,57,62`. Each snapshot measured 0.69–1.11 µs and 0.9–1.2 KB. | Filter first; direct checks equivalent to the snapshot's validity; trace reads removed. |

## Open, in priority order

| Severity · confidence | Trigger → consequence | Evidence at eeffb44 | Smallest corrective action |
|---|---|---|---|
| Medium · high in code, engine sequence unverified | `mp_restartgame` mid-round. The aborted round stays in lifetime totals, in the ended match's row, in the position tables, and its `rounds` row stays open. | `src/Services/Handlers/MatchFlowHandler.cs:388` returns on a restart; `:148-153` finalizes from current totals; `src/Services/Match/RoundBackupService.cs:122-127` drops the round starts without rolling back. | Roll the interrupted round back as a restore does (`RollBackStats`) before finalizing. First confirm on a test server that a restart fires `round_officially_ended` without `round_end`. README now states the limitation. |
| Medium · high | Plugin reload during a scrim (DLL replace hot-reloads). Nothing is recorded until an admin runs `.scrim recover`; the match continues under a new UUID; a tactical pause loses its auto-unpause and the knife side choice its deadline. | `src/Services/Match/ScrimManager.cs:47` starts Idle; `src/Infrastructure/EventDispatcher.cs:52-55` drops events when not Live; `src/Plugin.cs:120-122` starts a new match. | On load, recover automatically when `scrim_state.json` says KnifeRound/Live on the current map; otherwise warn in chat. Until then, deploy between matches. |
| Medium · high | A phase cfg is missing or misnamed (Linux is case-sensitive). The loader warns and reports success, and the match goes live with knife settings (`mp_maxmoney 0`, no C4). | `src/Services/Config/ConfigLoaderService.cs:43-47`; `ScrimManager.cs:82,290-291`. | Throw for a missing file and keep the scrim in its phase; check all five files at startup. |
| Medium · high | A position write commits and its acknowledgement is lost (or the 5 s timeout fires after the commit). The retry adds a second kill, death or utility point. Measured 1 event → 2 rows. | `PositionTrackingService.cs:87-111,152-163,216-233`; `init.sql:392-463` has no event identity. | `event_id BINARY(16) NULL` with a UNIQUE key on the three tables, a GUID set at capture, and a dedupe on merge. Edit init.sql in place; an existing database needs `ALTER TABLE … ADD COLUMN event_id BINARY(16) NULL, ADD UNIQUE KEY (event_id)`. |
| Medium · high | A player acts before their lifetime row has been read (slow database, startup). Lifetime is right, but that play is missing from the match and round rows; a player who leaves before the seed gets no match row; a session still unseeded at unload is lost (now logged). Test case: stored 7 kills plus 1 early kill gives lifetime 8 but match 0 instead of 1. | `src/Services/Handlers/PlayerLifecycleHandler.cs:69-78`; `src/Services/Match/MatchStatsService.cs:69-86,116`. | Needs a design: keep the unseeded session's raw totals at match and round start and adjust them by the stored row when the seed lands. A baseline taken from the stored row alone counts an earlier match's play when the seed lands in a later match. |
| Medium-Low · high | Timestamps after an outage or a spill replay: matches and rounds get the write time, so start = end = replay time. | `StatsRepository.cs:360-416`; the updates carry no event time (`PersistenceChannel.cs:35-60`). | Carry the event's UTC time in the match and round updates and write it inside the existing COALESCE (the spill format gains a field). |
| Low-Medium · high | A player who joins or reconnects during freeze time plays the round but gets no `player_round_stats` row, so round rows no longer add up to the match. | Round starts are recorded at round_start (`MatchFlowHandler.cs:224`, `RoundBackupService.cs:76-78`); freeze end stamps everyone as playing (`MatchFlowHandler.cs:286-298`). | At freeze end, add seeded sessions missing from that round's record. |
| Low-Medium · high in code | Remainder of the unload race. Against a hung server the stats worker spills once its statement in flight gives up, up to about 6 s after Unload returned. A process that exits at once loses that spill; a reload inside the window misses it until a later start; and if the new instance had restored an older spill, removing that one also removes the late spill. | `src/Services/PersistenceChannel.cs` (spill and `RemoveWrittenSpill`), .NET 10 `BackgroundService.StopAsync` (waits on the already-cancelled token). | Make one instance own the spill (an exclusive lock file the next worker waits for before restoring). |
| Low · high | A spill written by a build with renamed or missing snapshot fields replays them as 0 (lifetime columns are overwritten). | `PersistenceChannel.cs:243` default JSON options. | `UnmappedMemberHandling.Disallow` and `RespectRequiredConstructorParameters` when reading; a mismatching file is then moved aside. |
| Low · high | The retry buffer's "50 MiB" counts 1 KiB per update; real heap is 3.8–4.5 KiB, so the cap is ≈ 186–218 MiB plus a spill built as one string. | `PersistenceChannel.cs:84,428`. | Cap by update count and serialize the spill to a stream. This changes how much is kept in an outage. |
| Low · high | Bomb plant and defuse each walk every map entity on the game thread (0.17–1.3 ms and up to 2.5 MB garbage for 1,000–8,000 entities). | `src/Services/Processors/BombEventProcessor.cs:322,327`. | Keep the planted C4 from the plant and reuse it for the defuse after an `IsValid` check. |
| Low · high | When a writer never started, every refused write logs synchronously on the game thread (≈ 10 µs each, up to 6 ms). | `PositionPersistenceService.cs:146`; `PersistenceChannel.cs:111-115`. | Log the first refusal and then a count. |
| Low · high | With `VerifyCA`/`VerifyFull` MySqlConnector serves the verified server's LOAD DATA LOCAL requests for files by name, so that server can read files the CS2 process can (server.cfg). Under `Required` only the plugin's own bulk data is sent. | MySqlConnector 2.6.2 `ResultSet.cs:64-73,196`. README now says so. | Use a database host trusted like the game server, or keep `Required` and pin the certificate with `RemoteCertificateValidationCallback`. |
| Low · high | The Compose stack grants the plugin account ALL PRIVILEGES on the database; the README's five grants suffice (verified on MySQL 8.4.13). | Image entrypoint behaviour. | An init script that revokes ALL and grants the five. |
| Low · high | Bot victims create a `players` row with steam_id 0 for the position foreign key. | `PositionTrackingService.cs:107-109`. | A nullable victim column written as NULL for bots. |
| Low · high | `.ready` broadcasts and rewrites `scrim_state.json` on every call, even unchanged; scrim commands use the claimed SteamID. | `ScrimManager.cs:337-371`. | Return early when unchanged; require `AuthorizedSteamID`. |
| Low · high | When cleanup outlives the unload, late lines (such as the spill's "Kept N batches") are written after the log file was closed. | `src/Plugin.cs:259`. | Close the logger after the writers have finished. |

Run CounterStrikeSharp v1.0.380 (12 Nov 2026) on the server: it carries the gamedata for CS2 1.41.9.7 and fix #1484
(removing a timer from inside a timer callback could leave a freed timer in the timer list); `ScrimManager.cs:607-611 →
673` can kill one timer from another's callback. The plugin itself needs no rebuild.

Two suspected problems were checked and do not exist: under `Required`, `AllowLoadLocalInfile` does not let a man in the
middle read game-server files (MySqlConnector refuses named files below VerifyCA); and Polly's telemetry does not
duplicate the retry and breaker logs (the strategies are built in an inner builder without telemetry, so it only logs
"pipeline executed" at Information, which the existing override hides). Of the previous review's findings (at
20e1fa0), the spill-loss, restore-timeout and weapon-carry items were fixed by 2c73926 and eeffb44; the spill fix
introduced the replay-over-newer-rows problem fixed above.

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
  snapshot, match finalization, the stats flush, the position stop and container disposal (their budget ends at 7 s);
  what is left continues on the pool.
- **Stats writer**: one reader, batches of 100, lifecycle updates inline in queue order, bulk upserts at batch end.
  Outages keep batches in order in memory; data errors drop only the rejected update; at stop the rest is spilled and
  replayed first at the next start. Every stats write is idempotent on retry (measured with a proxy that drops the
  acknowledgement); position inserts are not.
- **Position writer**: separate bounded channel, retry queue of 5,000, restore void as an ordered barrier; not spilled.

## Cleanup and reachability

Deleted (behaviour-identical apart from log text):
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

Kept, and what would decide them:
- Seven write-only `PlayerSnapshot` fields, `StatsUpdate.RoundNumber` and `ScrimRecoveryData.SavedAtUtc`: they are part of
  `pending_stats.json`/`scrim_state.json`. Old files were shown to stay readable without them; removing them still
  changes what new files contain.
- `cfg/live_wingman.cfg` (selectable through `Scrim.LiveConfigPath`; needs the deployed config), SQL columns and indexes
  no code or view reads (`players.first_seen`, the `created_at` columns, three time indexes, `matches.status` 'ABORTED';
  needs an inventory of dashboard queries), the pairs that always hold the same value (`players_blinded`/`enemies_flashed`,
  `flash_assists`/`flash_assisted_kills`), and the 149-column `player_stats` lists kept three times in
  `StatsRepository` (consolidate only after a schema test covers them).
- Polly.Extensions and the four Microsoft.Extensions DLLs it ships (byte-identical to the host's, never loaded because
  CounterStrikeSharp prefers shared types). Excluding them is a packaging change to make deliberately.

## Verification and rollout

| Check | eeffb44 | Patched |
|---|---|---|
| `dotnet build -c Release` | 0 warnings, 0 errors | 0 warnings, 0 errors |
| `dotnet test tests/statsCollector.Tests` | 37 passed | 44 passed (7 new tests and one extended theory; those 10 cases all fail against eeffb44's code) |
| `dotnet publish -c Release` | 16 files | same 16 files; third-party DLLs and deps.json byte-identical; init.sql embedded with the widened columns |
| Hung query / bulk copy / pipeline call | 32.0 s / > 45 s / ≈ 48 s | 6.0 s / 6.3 s / 21.7–22.0 s |
| `Cancel()` with a query in flight | 2.02 s on the game thread | moved to the pool by `CancelAsync` (1.02 s there) |
| Unload with MySQL hung, seed reads and both writers mid-write (`Plugin.Unload` driven by reflection) | 12.03–16.05 s blocked; spill written after it returned | 7.01–7.02 s blocked; logger and container disposed and spill on disk before it returns |
| Unload with MySQL healthy, 480 lifetime and 240 position updates queued | not measured | 0.44 s; everything written, no spill file |
| Spill replay fuzz (1,200 random outage/recovery/crash orderings) | 246 runs violated ordering, 37 replayed over a newer write | 0 violations |

Database evidence came from disposable MySQL 9.7.4 and 8.4.13 containers initialized from `database/init.sql`, driven by
the real repository and writer classes; a TCP proxy simulated a server that stops answering and a lost acknowledgement.
No production server or database was used.

Rollout: replace the plugin between matches. Run the `ALTER TABLE` above on an existing database, or recreate it. A
config that sets `DatabaseSslCa` without `VerifyCA`/`VerifyFull` no longer loads. The spill file format is unchanged.
Rolling back the binary is safe: the wider columns accept everything the old code writes.

Not verified without a CS2 server: unload and reload timing inside a real CounterStrikeSharp runtime (measured only
with the unload driven by reflection); a reload while the previous instance's cleanup still runs; the restart and restore event sequences; joins during freeze time; recovery after a
reload mid-scrim; the bomb entity walk on real maps. The hang tests ran without TLS so the proxy could see the commands.
