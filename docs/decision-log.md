# Decision log

These are the decisions made during the implementation, one section per phase. They are written as each phase ends and will be assembled into the README in Phase 5. The design decisions made before any code was written are in [implementation-plan.md](implementation-plan.md) §2.1. The discussion behind them is in [devdiary.md](devdiary.md).

## Phase 0: Skeleton and probing the test program

**Measure the test program before designing around it.** Several assumptions in the plan rested on how the test program behaves, so Phase 0 ran it against a capture server that does no work. Some findings changed the plan; the full list is in [implementation-plan.md](implementation-plan.md) §1.
- **Ground truth for the oracle.** Two runs with seed 444 sent exactly the same messages. A capture of one run is therefore the ground truth for the end-to-end oracle. The logging proxy that the review suggested as a fallback isn't needed.
- **Rejected messages get 2xx.** The test program retries a 4xx exactly as it retries a 5xx. A 400 for a message that can never succeed would only cause pointless retries, so such messages are recorded in `rejected_messages` and acknowledged.
- **A non-2xx near the end of a run loses the message.** Retries still waiting when the test program finishes are dropped, and it exits 0 anyway. So the service must avoid 503s under normal load, rather than relying on redelivery. The wait for a full queue is capped at about 5 s, well below the client's timeout of about 10 s. A timed-out request that the server is still processing gets resent, and that creates a real duplicate.
- **Reordering is mild but unbounded.** With concurrency 3, the furthest a message arrived out of place was 2 positions in one run and 279 in another. The pending buffer in the ledger stays small, which supports replaying pending messages on every new one.

**Keep throwaway code out of the service.** The capture server and the traffic analysis live in `tools/Rockets.Capture`, not in `Rockets.Api` as the plan first said.
- The service starts clean, with only a health endpoint.
- The tool stays available for the Phase 4 oracle (which uses the capture) and for further probing (`scripts/probe.sh`).
- The tool's analysis drives design decisions, so it has its own unit tests.

**Build setup.**
- .NET 10, with nullable reference types and warnings treated as errors.
- Package versions are managed centrally in `Directory.Packages.props`.
- xUnit v3 runs on Microsoft.Testing.Platform, which `global.json` opts into. On the .NET 10 SDK, xUnit v3 no longer supports the older VSTest runner.
- Test projects are created in the phase that first needs them, rather than left empty.

## Phase 1: Domain model and ordering

**The parser reads the type and then the payload, instead of using System.Text.Json polymorphism.** The type that selects the payload is in `metadata.messageType`, not inside the `message` object, so the built-in polymorphic deserialization doesn't fit. `MessageParser` reads the envelope with `JsonDocument`, then picks a payload reader for that message type. The benefits:
- Every rejection names the exact field, e.g. `message.by must be a non-negative integer`.
- Adding a message type means adding one reader.

**Validation rules.**
- `messageNumber` must be a positive integer.
- `messageTime` must be an ISO 8601 timestamp.
- Text fields must be non-empty.
- `by` and `launchSpeed` must be non-negative integers, because `by` is a size and the message type gives the direction.
- A message that fails validation is rejected on its own (see plan §2.6).

**Unknown message types are accepted and take up their place in the sequence.** If they didn't, a single unknown message would stop the checkpoint for that rocket for good. They change no state.

**The payload hash covers the message type and the compact payload JSON.** It ignores `messageTime` and formatting, so it identifies what a message says, not how it was written. The ledger compares hashes for duplicates still pending. A duplicate at or below the checkpoint can't be compared there, because applied messages aren't kept in memory; the store compares those when an insert hits an existing row.

**State rules.**
- `RocketLaunched` sets the speed to `launchSpeed`. In the observed data it is always #1, so no earlier speed changes are lost.
- Once a rocket has exploded, a later `RocketLaunched` doesn't change its status back.
- Speed may go negative, because there is no clamping. The data never does this.
- Speed uses checked arithmetic, so an overflow throws instead of producing a wrong speed.

**The ledger is immutable**, using `ImmutableSortedDictionary` for pending messages. `Apply` returns a new ledger, so if applying a message throws, the old ledger is untouched without any rollback code. The Phase 3 writer relies on this: it applies a batch to working copies and keeps them only after the commit.

**What the tests cover.**
- 200 seeded runs each shuffle a generated message sequence and redeliver some messages.
- After every single delivery, the ledger is compared with a simple reference that applies all distinct messages received so far in order.
- That reference uses `RocketState.Apply`, so these runs test ordering and duplicates. What each message does is tested separately.
- A mutation check confirmed both kinds of test catch real bugs. With a pending-order bug, 198 of 242 tests failed. With a speed-sign bug, 3 tests failed, all in the state tests.

## Phase 2: Storage and the SQLite store

**`synchronous=FULL` by default, with `NORMAL` configurable.** This was the developer's choice, made from these measured numbers. Each row is 1,000 sequential commits, the way the single writer will commit:

| synchronous | batch size | commits/s | messages/s | mean commit |
|---|---:|---:|---:|---:|
| FULL | 1 | 1,762 | 1,762 | 0.57 ms |
| FULL | 3 | 1,749 | 5,246 | 0.57 ms |
| FULL | 20 | 1,209 | 24,172 | 0.83 ms |
| NORMAL | 1 | 45,359 | 45,359 | 0.02 ms |
| NORMAL | 3 | 19,854 | 59,563 | 0.05 ms |
| NORMAL | 20 | 3,251 | 65,028 | 0.31 ms |

- **FULL:** a 2xx means the message survives even a power cut. That is the strict reading of at-least-once delivery: once a message is acknowledged, it is never resent, so losing it would be permanent. The estimated default grading run takes 19–57 s on this machine, and longer on a slower disk.
- **NORMAL:** about 2 s for the whole run, as fast as the test program itself. The last commits can be lost in a power cut, though not in a process crash.

**Group commit is worth it under FULL, and only there.** With FULL, the cost is the disk sync, so a batch of 3 costs the same as a batch of 1 and triples throughput. With NORMAL, batching barely helps. This confirms the review's point (M1): the single writer is justified mainly because it keeps locks out of the domain code, and batching is a bonus that matters only for durable commits.

**Replaying the full log at startup is cheap.** Reading 100,000 messages takes about 0.16 s; reading them and applying them to ledgers takes about 0.2 s. Saving checkpoints to disk (dropped after the review, M4) would save almost nothing at this scale.

**One commit holds both messages and rejections.** The plan had a separate `RejectAsync`. With rejections in the batch transaction instead, the single writer does every write, and a rejection is durable before its 2xx just like a message.

**Plain Microsoft.Data.Sqlite, not Dapper.** The store has five statements. A prepared insert with reused parameters is the fastest way to write a batch, and Dapper would add a dependency without making the code clearer.

**Schema details.**
- `messages` is a `WITHOUT ROWID` table keyed on `(channel, message_number)`, so rows are physically ordered by rocket and number. Reading one rocket, or all rockets in order, is a plain scan.
- Times are stored in round-trip ISO 8601 format, which keeps the offset.
- `received_at` comes from an injected `TimeProvider`.
- A stored message is rebuilt through `MessageParser.FromStored`, which applies the same validation as on arrival. A row that no longer validates fails loudly instead of being replayed silently.

**What the tests cover.** A contract-test base class defines what every store must do. A Postgres store will subclass it the same way the SQLite tests do. A mutation check confirmed the contract tests catch three planted bugs: last write wins, a content change never flagged, and a failing insert skipped with the rest of the batch committed.

**Test setup.** A shared `tests/Directory.Build.props` now holds the xUnit v3 setup. It also turns off analyzer rule xUnit1051, which wants a cancellation token in every async call; these tests are short-lived and local, so the token would only add noise.

## Phase 3: Ingestion pipeline and API

**The API shape was the developer's choice.**
- A rocket has its state at the top level and a nested `sequence` object (`lastMessageNumber`, `checkpointMessageNumber`, `pendingMessageCount`, `missingMessageCount`, `isComplete`). This keeps the bookkeeping out of the way of the state a dashboard actually shows.
- The list is `{ "count": n, "rockets": [...] }`, without echoing the sort back.
  - Why an envelope: fields such as paging or an `asOf` timestamp can be added later without breaking clients. That suits the brief's dashboard consumer and its question about future requirements.
  - What a bare array would have offered: the simplest shape, with paging possible through headers later.
- There is no filtering yet.

**Sorting rules.** `sortBy` and `order` are case-insensitive, and the defaults are `channel` and `asc`.
- A rocket with no value for the field sorts last in either order. For example, a rocket has no type until its launch message arrives, and such rockets shouldn't jump to the top when the order is reversed.
- Ties are broken by channel, so the order is always the same.
- Strings compare ordinally, so the result doesn't depend on the server's culture.
- `status` sorts in lifecycle order: awaiting launch, launched, exploded.
- An unknown value gets 400, and the message lists the valid values.

**The writer loop.** It reads whatever is queued, up to 256 messages, and never waits for a batch to fill up.
- Each message is applied to a working copy of its ledger. Because ledgers are immutable, a working copy is just a reference.
- The batch is committed in one transaction. Only after the commit succeeds are the snapshots published and the requests completed. A reader therefore never sees uncommitted state, and a 2xx always means the message is stored.
- Completions use `TaskCreationOptions.RunContinuationsAsynchronously`, so request code never runs on the writer's thread.

**Errors are kept to the request that caused them.**
- An invalid body or a message that can't be applied (such as a speed overflow) is recorded in `rejected_messages` and answered with 2xx. The rest of its batch continues.
  - Consequence: a message rejected while applying leaves a permanent gap, because it is never resent. That is accepted: it can only happen with absurd data, and it shows up as `missingMessageCount`.
  - Limitation: if a gap-filling message makes an earlier pending message overflow, the gap-filler is the one rejected.
- A storage error fails the whole batch with 503, so it is resent. Before anyone gets an answer, the affected rockets are reloaded from the store, because the commit may have succeeded before the error surfaced.
  - If the reload fails too, the rocket is marked stale. It is reloaded before every later batch, and its new messages get 503 until that works.
  - Applying a message to state that may be out of date would silently corrupt the rocket, which is worse than a retry.
- An unexpected exception fails only the current batch. It never stops the writer, which would leave every later request waiting forever.

**Cancellation and shutdown.**
- A client disconnecting (`RequestAborted`) cancels only the wait to get into the queue, never a write already in progress.
- When the queue stays full for 5 s, the request gets 503. That is well below the test program's client timeout of about 10 s.
- On shutdown the queue stops accepting messages, the writer drains what is already queued, and only then does the host stop.

**Configuration is found however the service is started.**
- The first real run against the test program listened on port 5000. The DLL had been started from the repo root, so ASP.NET Core looked for `appsettings.json` in the working directory, didn't find it, and fell back to its defaults. Logging also wrote a line per request.
- Meanwhile the test program retried refused connections 3.6 million times in 10 minutes, without ever stopping.
- The fix is to set the content root to the app's own folder, so `appsettings.json` (the port, log levels and storage settings) always loads.
- A relative `Storage:DatabasePath` is resolved against that folder, and the full path is logged at startup.

**Storage is chosen in one place.** `Storage:Provider` (currently only `Sqlite`) picks the `IMessageStore` implementation in the composition root. Adding Postgres means adding one more case there. The store reads its configuration when it is first resolved, so test hosts can override it.

**What the tests and checks cover.**
- **Tests:** 14 application tests use a fake store that can hold, fail, or commit-then-throw. With these, the tests can build up batches on purpose and test the failure paths deterministically. There are also 16 API tests that run the real host on a temporary database.
- **Flakiness:** 10 repeated runs of both suites were all green.
- **Mutation check:** six planted pipeline bugs were all caught: no reload after a failed commit, stale rockets not refused, apply errors failing the whole batch, applied duplicates not checked by the store, no batching, and snapshots published before the commit.
- **Real run:** the test program with default settings against the Release build stored 100,000 messages in 38.5 s with `synchronous=FULL`, inside the Phase 2 estimate of 19–57 s. There were no duplicates, rejections or storage failures, and all 20 rockets were complete. After a hard stop and a restart, all 100,000 messages were replayed and the rocket list was byte-for-byte identical.

## Phase 4: End-to-end verification and resilience

**The oracle is independent of the service, and its ground truth comes from outside it.** `Rockets.Capture` gained two commands, `expect` and `verify`.
- `expect` folds every rocket from a seed-444 capture, made against the capture server rather than the service. It uses its own simple rules and doesn't reference `Rockets.Domain`, so a bug in the service's ordering or state logic can't hide in both. For each rocket it records the final state, the message count, and a SHA-256 hash over all messages in number order.
- `verify` compares those with `GET /rockets` and with the service's SQLite log, which it opens read-only. Every expected message must be stored with byte-identical content.
- Message times aren't compared, because the test program sets them when it sends, so they differ between runs.

**Expected-state files are committed instead of capturing on every run.** Capturing a 100k-message run on every e2e run would cost an extra run plus a wait of about a minute for Windows client ports. Instead, the files for 10k and 100k messages (about 7 KB each) live in `tests/e2e/`, and `-RegenerateExpected` recaptures them.

**Checking the checker.**
- Against the database of the separate Phase 3 default run, `verify` passed.
- Against the wrong expectation (10k instead of 100k), it failed with 124 differences.
- Five unit tests cover the folding, the content hash, and the reporting of wrong state, an incomplete sequence, a missing rocket, and a missing or altered stored message.

**Results** (`pwsh ./scripts/e2e.ps1 -Scenario all`, Release build, `synchronous=FULL`, this machine)

| Scenario | Messages | Time | Test program retries | Duplicates | Oracle |
|---|---:|---:|---:|---:|---|
| quick | 10,000 | 5.3 s | 0 | 0 | PASS |
| default (the grading run) | 100,000 | 52.0 s | 0 | 0 | PASS |
| crash: hard kill 4 s in, restart 3 s later | 10,000 | 36.3 s | 3,201 | 1 | PASS |
| stress: concurrency 20 | 100,000 | 48.2 s | 0 | 0 | PASS |

The crash run shows at-least-once delivery handled correctly:
- While the service was down, the test program retried 3,201 times.
- One message was committed just before the kill but never acknowledged. It was resent and recognised as a duplicate.
- No retry was dropped, and the log holds all 10,000 messages exactly.

**Where the time goes: one disk sync per message.** A new `commits` counter in `/health` gives the average batch size (stored messages divided by commits).
- Even at concurrency 20, the average batch is **1.12**, so requests almost never overlap at the writer.
- The test program is the limit. It opens a connection per message, and against the do-nothing capture server it needs 3–4 s for 100k messages at concurrency 3, but **20 s at concurrency 20**: more concurrency makes it slower.
- With `synchronous=FULL`, each message therefore pays a whole disk sync (about 0.5 ms). The default run takes 38–52 s, about 2,000 messages/s.
- The same run with `synchronous=NORMAL` takes **6.5 s** and also passes the oracle. So the service adds little beyond the disk sync.
- This doesn't change the developer's choice of FULL (a 2xx survives a power cut), but it puts an exact price on it.
- It also corrects the expectation that group commit would help under load: with this client, it can't. A client that overlapped its requests, or a service that acknowledged before the disk sync, would change that. The latter breaks what a 2xx is meant to promise, so it wasn't built.

**Script robustness.**
- The script refuses to start if port 8088 is already in use.
- It waits for the service's health endpoint, and fails if the service exits or doesn't answer within 60 s.
- On Windows it waits for client ports in TIME_WAIT to drop below 1,000 between scenarios.
- It picks the `rockets` binary for the OS and architecture.
- It writes `artifacts/e2e/results.json`, because the console table cuts off columns.
- It fails if the oracle fails, if the test program exits non-zero, or if any retry was dropped.

## Phase 5: Documentation

**The README summarises; the decision log is the detail.** The README covers the quick start, the API with real responses, how the service works, a decision table, verification, limitations, scaling and how AI was used. It links to this log for the reasoning and measurements of each phase rather than copying them, so there is only one detailed copy of each.

**The README was checked by following it.** In a fresh `git clone` in a temporary folder:
- `dotnet test` passed all 308 tests.
- The quick start (`dotnet run`, the test program with its defaults, then `curl`) stored 100,000 messages in 42.2 s, with the database where the README says it is.
- `pwsh ./scripts/e2e.ps1` passed.

**A stale process found while writing the README.** The example responses were first fetched from a service left running since the Phase 4 NORMAL measurement. In Git Bash, a plain `kill` hadn't stopped the native `dotnet` process. The new service then couldn't bind port 8088 and exited quietly.
- The odd `/health` counters gave it away.
- The Phase 4 results weren't affected: they ran earlier, and `e2e.ps1` refuses a busy port and force-kills its processes.
- `scripts/probe.sh` now does the same: it checks the port before starting and uses `kill -9`.

## Phase 6: Postgres store

**The swap needed nothing outside the storage project except one `case` in the composition root.** `Rockets.Storage.Postgres` implements `IMessageStore` with Npgsql and a pooled `NpgsqlDataSource`. `Storage:Provider=Postgres` selects it, with `Storage:ConnectionString`. The domain, the pipeline and the API are unchanged.

**The schema follows the contract, not Postgres habits:**
- `message_time` is `TEXT` in round-trip format. `timestamptz` would drop the offset and keep only microseconds, but the contract requires a message to read back exactly as it was received.
- `payload_json` is `TEXT`, not `jsonb`, because `jsonb` normalises the JSON and would change the content behind `payload_hash`.
- `received_at` is a real `timestamptz`, since it's the service's own timestamp. Npgsql only accepts UTC for it.
- Reads order by `channel COLLATE "C"`, so channels sort by byte value as in SQLite, whatever the database's locale.

**Tests use Testcontainers and are skipped without Docker.**
- The 10 contract tests run against a `postgres:18-alpine` container: one container per test class, and a fresh database per test.
- If Docker isn't available, the fixture records the reason and the tests call `Assert.Skip`. Reviewers without Docker therefore get a green `dotnet test` (308 passed, 10 skipped) with the reason shown.
- This was checked by stopping Docker Desktop. Pointing `DOCKER_HOST` at a dead address didn't work, because Testcontainers falls back to Docker Desktop's named pipe on its own.
- Red step: all 10 failed against a stub.

**End to end on Postgres.** The service ran on `docker compose` Postgres with the full default 100k run.
- The oracle passed, now able to read Postgres when given a connection string.
- After a restart, it replayed 100,000 messages, answered within 1.1 s of starting, and passed again.

**Postgres is slower here: 118.3 s for the default run** (about 850 messages/s), against 38–52 s for SQLite with FULL.
- Each message still commits alone (average batch about 1.5), and each commit is a network round trip from Windows into the Docker VM plus a durable commit.
- The store sends one statement per message, mirroring the SQLite store, for clarity.
- The obvious optimisation is one round trip per batch: a multi-row `INSERT … ON CONFLICT DO NOTHING RETURNING`, then one `SELECT` of the stored hashes for the conflicts. It isn't built, because with this client batches are about one message, so it would barely help.

**Docker housekeeping.** Docker Desktop wasn't running when the phase started. It was started for the tests and the end-to-end run, the compose container and its volume were removed with `down -v`, and Docker Desktop was stopped again, as it had been before.

## After Phase 6: Starting with a clean database

**Standalone executable: not pursued.** The developer asked for a way to run the service without the .NET SDK. A self-contained single-file publish works, but it is about 52 MB per platform, and `appsettings.json` has to travel with it. When asked how to deliver it, the developer decided the current deployment (`dotnet run`) is fine. What was missing was a clean start.

**`--Storage:ResetOnStart=true`.** Before this, starting clean meant deleting `rockets.db` and its `-wal`/`-shm` files by hand, and with Postgres, dropping the tables.
- It is a configuration key like the other `Storage:` settings, so it also works as the environment variable `Storage__ResetOnStart=true`. A bare `--reset` switch was considered. The configuration system needs a value for every key, so it would have meant parsing arguments by hand for one flag.
- `IMessageStore.ClearAsync` deletes all messages and rejections in one transaction, keeping the schema, and returns the number of messages deleted. Both stores implement it, and three new contract tests cover it.
- The store clears rows instead of deleting the SQLite file. That works the same for both providers, needs no file handling with pooled connections or the WAL, and is atomic. The SQLite file keeps its size until it is reused, which doesn't matter at this scale.
- `IngestionHostedService` clears the log before the pipeline replays it, so the service starts with no rockets and accepts no requests until the clear is done. It logs a warning with the number of deleted messages, because the option destroys data.
- The option is not a request-time endpoint (such as `DELETE /rockets`). Clearing while the writer runs would race with in-flight batches, and a destructive endpoint on an unauthenticated service is a bad default.

**Verification.**
- Red first: the new contract tests failed against `NotImplementedException` stubs, and so did the API test.
- 325 tests pass, with Postgres run under Docker (29 storage tests).
- Four planted bugs were each caught: not deleting rejections (in SQLite, and in Postgres), returning 0 as the count, and the hosted service reading the wrong key.
- A smoke test of the Release service with a temporary database showed one rocket, then none after starting with the option (with the warning logged), then still none after a plain restart.
