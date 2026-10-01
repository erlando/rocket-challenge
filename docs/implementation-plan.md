# Implementation plan: Rockets

This plan covers the Lunar backend engineer challenge described in [CHALLENGE.md](CHALLENGE.md). It is organised around the assessment targets:
- design trade-offs
- depth and maintainability
- verification
- deliberate AI use

It is split into progressive phases, and each phase ends with targets that can be verified.

## 1. Goals and assessment mapping

| Assessment target | Where it is addressed |
|---|---|
| Design choices and trade-offs | The decision log in the README (Phase 5) and the design decisions in this document |
| Data structures | The immutable `RocketLedger`, which holds a checkpoint and the pending messages after it (Phase 1) |
| Concurrency | A single writer that batches commits, with lock-free reads from snapshots (Phase 3) |
| Persistence | The SQLite message log behind a storage interface (Phase 2); Postgres as a stretch goal (Phase 6) |
| Error handling | The error policy (§2.6), the storage-failure path (Phase 3), and the crash test (Phase 4) |
| Verification | Unit, contract and API tests in every phase; the end-to-end oracle and crash test (Phase 4) |
| AI use | [devdiary.md](devdiary.md), plus review and approval of each phase before it is committed (§4) |

### Load profile

The grading run uses the defaults of `rockets launch`:
- `--concurrency-level 3`
- `--max-messages 100000`
- `--seed 444`
- no message delay

So the service must handle about 100k messages, sent three at a time as fast as it answers, with redelivery on any non-2xx response.

## 2. Architecture

### 2.1 Design decisions

These were agreed during planning. The reasoning is in the dev diary.

| Decision | Choice | Main alternatives considered |
|---|---|---|
| Persistence | **SQLite** message log behind `IMessageStore`, so it can be swapped for Postgres | Postgres (production-like, but reviewers need Docker to run it); in-memory (loses messages that were already acknowledged) |
| Ordering | **Checkpoint plus replay**: newest data takes priority over strict correctness | Buffer until gaps fill (state lags behind); fold all messages from scratch on every change (cost O(n)) |
| Write concurrency | **Single writer loop with group commit** | A lock per rocket (one disk sync per message, and SQLite serialises writes anyway); the database doing the ordering (every read hits the DB) |
| Topology | **Single ASP.NET Core service** with internal layers | Separate ingest and query services (more moving parts within the 6-hour budget) |

### 2.2 Overview

```
POST /messages ─▶ validate/parse ─▶ bounded Channel<PendingWrite> ─▶ single writer loop
                                                                      ├─ apply batch to immutable ledgers (working copies)
                                                                      ├─ IMessageStore.CommitAsync(new messages + changed checkpoints)
                                                                      │     one transaction, one fsync per batch
                                                                      ├─ success: publish snapshots, complete requests → 204
                                                                      └─ failure: discard working copies, fail requests → 503
                                                                                  (redelivered; safe because writes are idempotent)

GET /rockets[/{channel}] ─▶ ConcurrentDictionary<channel, RocketSnapshot>   (immutable, lock-free)
```

Only the writer loop changes rocket state, so the domain code has no locks. Snapshots are published only after their commit succeeds. That way a reader never sees state that might be rolled back, and a request gets 2xx only once its message is stored.

### 2.3 Projects (.NET 10)

| Project | Responsibility |
|---|---|
| `src/Rockets.Domain` | Pure code with no IO: message types, polymorphic JSON parsing, `RocketState` and the function that applies a message to it, and `RocketLedger` |
| `src/Rockets.Application` | The writer loop, the `IMessageStore` interface, the snapshot registry, and recovery at startup |
| `src/Rockets.Storage.Sqlite` | The SQLite store (Microsoft.Data.Sqlite and Dapper). **All SQL lives here.** |
| `src/Rockets.Api` | Minimal-API endpoints, the composition root, and configuration (port 8088, storage provider, SQLite pragmas) |
| `src/Rockets.Storage.Postgres` | Stretch goal (Phase 6): the Npgsql store behind the same interface |
| `tests/Rockets.Domain.Tests` | Unit and property-style tests for the ordering logic |
| `tests/Rockets.Application.Tests` | Writer-loop behaviour, using fake stores |
| `tests/Rockets.Storage.Tests` | A contract-test base class, run against every `IMessageStore` implementation |
| `tests/Rockets.Api.Tests` | HTTP-level tests using `WebApplicationFactory` |

The tests use xUnit with its built-in asserts. FluentAssertions is avoided because of its license.

### 2.4 Ordering model: checkpoint plus replay

Each rocket is represented by an immutable `RocketLedger`:
- **Checkpoint:** the state after applying messages 1…N, where N is the highest message number reached with no gaps. Everything up to N is known, so this state is exactly correct.
- **Pending:** the messages numbered above N, kept in an `ImmutableSortedDictionary<long, Message>`.
- **Current state:** the checkpoint with the pending messages applied in order, skipping gaps. This is what the API returns.

`Apply(message)` returns a new ledger and an outcome: `Duplicate`, `Accepted` (added to pending), or `Advanced` (the checkpoint moved forward).
- A message is a duplicate if its number is ≤ N or already pending.
- After a message is added, the checkpoint keeps moving forward while message N+1 is pending.
- Each message costs O(number of pending messages), which is bounded by how far out of order messages arrive. State is never rebuilt from the start.

**Rules for applying messages:**
- `RocketLaunched` sets the type, the launch speed and the mission (counted as the mission at message #1).
- `RocketSpeedIncreased` and `RocketSpeedDecreased` add or subtract `by`.
- `RocketMissionChanged` sets the mission.
- `RocketExploded` sets the status to exploded and records the reason. The status is permanent, while other fields keep updating. This assumption is checked against real traffic in Phase 0.
- Until launch data arrives, the status is `AwaitingLaunch`. This stops a dashboard from showing a speed made of changes alone as if it were real.

**Design note.** With today's message types, applying messages in any order gives the same result:
- speed changes add up
- the mission is the one with the highest message number
- an explosion is permanent

So the current state could be updated in O(1) per message with no replay. Checkpoint plus replay was chosen anyway because it stays correct if a future message type or rule depends on order, such as setting an absolute speed or never letting speed go below 0. The O(1) shortcut is documented as an optimisation we chose not to take.

### 2.5 Storage

The **storage interface** (`IMessageStore`) is the seam for swapping in Postgres:
- `InitializeAsync()`: create the schema if it doesn't exist.
- `LoadRecoveryStateAsync()`: return every checkpoint, plus the messages above each checkpoint.
- `CommitAsync(newMessages, changedCheckpoints)`: write both atomically, in one transaction.

**Schema:**
- `messages(channel, message_number, message_type, message_time, payload_json, received_at)`, with primary key `(channel, message_number)`.
  - This append-only log is the source of truth.
  - Inserts use `ON CONFLICT DO NOTHING`, a backstop behind the ledger's own duplicate check.
- `rocket_checkpoints(channel PK, checkpoint_number, type, mission, speed, status, explosion_reason, launched_at, updated_at)`.
  - Checkpoints are written in the same transaction as the messages.
  - Recovery therefore only replays messages after each checkpoint, so startup time depends on how many messages are pending, not the total.

**SQLite pragmas:**
- `journal_mode=WAL`, so reads don't block the writer.
- `synchronous=FULL` by default, so a commit is safe even across a power cut. This can be set to `NORMAL`, which is safe across a process crash only. Phase 2 measures the throughput cost of each.
- `busy_timeout`.

### 2.6 API and error policy

| Endpoint | Behaviour |
|---|---|
| `POST /messages` | Returns 204 once the message is stored, and 204 for a duplicate too |
| `GET /rockets/{channel}` | Returns the rocket's state, or 404 if the rocket is unknown |
| `GET /rockets?sortBy=channel\|type\|mission\|speed\|status\|updatedAt&order=asc\|desc` | Lists all rockets, sorted; 400 for an invalid sort |
| `GET /health` | Liveness check |

Besides its state, each rocket in a response includes:
- `lastMessageNumber`: the highest message number received
- `checkpointMessageNumber`: N
- `missingMessageCount`: the number of messages missing below `lastMessageNumber`

Together these let a dashboard show whether a rocket's data is complete.

| Situation | Response |
|---|---|
| Malformed envelope (invalid JSON, missing channel or number) | 400, and the message is logged. Phase 0 checks whether the test program keeps resending after a 4xx. |
| Unknown `messageType` | Stored and acknowledged with 2xx, but not applied. It is counted and logged, so new message types don't break the service. |
| Storage failure | 503. The message is resent, which is safe because writes are idempotent. |
| Write queue full | The request waits with a timeout, then gets 503. |
| Shutdown | The service stops accepting messages and drains the queue before exiting. |

## 3. Phases

Every phase ends with:
- all tests green
- the developer reviewing the diff
- an entry in the dev diary
- a commit on `main`

Hour estimates are measured against the 6-hour budget.

### Phase 0: Skeleton and observing real traffic (~0.5h)

**Work**
- Create the solution with the project layout from §2.3.
- Make `Rockets.Api` listen on `http://localhost:8088`. For now, `POST /messages` appends each raw body to an NDJSON capture file and returns 204.
- Add a small analysis script or test that reports on the captured traffic:
  - whether numbering starts at 1 and `RocketLaunched` is always #1
  - the duplicate rate
  - the largest reorder distance
  - whether messages arrive after `RocketExploded`
  - how many rockets there are
- Return a 4xx once, to see whether the test program resends after it.

**Verify**
- [ ] `dotnet build` and `dotnet test` pass.
- [ ] `rockets.exe launch "http://localhost:8088/messages"` with default flags completes with no errors.
- [ ] The traffic findings are recorded in the dev diary, and the assumptions in §2.4 and §2.6 are confirmed or adjusted.

### Phase 1: Domain model and ordering (~1h)

**Work**
- Message types and polymorphic JSON parsing.
- `RocketState` and the function that applies a message to it.
- `RocketLedger`: checkpoint advance, the pending buffer, duplicate detection, and the current state.

**Verify**
- [ ] Unit tests for each message type, a message arriving before launch, and the explosion rule.
- [ ] A property-style test that takes a generated message sequence, shuffles it with a seeded random generator, and injects duplicates. It checks that:
  - the final state equals applying the messages in order
  - the checkpoint never moves past the first gap
  - after every step, the current state equals the checkpoint plus pending messages applied in order, skipping gaps

### Phase 2: Storage and the SQLite store (~1h)

**Work**
- The `IMessageStore` interface.
- The SQLite implementation: schema creation, pragmas, an atomic commit, and the recovery query.

**Verify**
- [ ] Contract tests on a temporary-file database:
  - a commit writes both tables atomically
  - inserting a duplicate does nothing
  - recovery returns only the messages above each checkpoint
  - a failed transaction leaves nothing behind
- [ ] A micro-benchmark that records commits per second for `synchronous=FULL` and `NORMAL`, at batch sizes 1 and 64. The numbers are recorded and confirm (or challenge) the group-commit decision.

### Phase 3: Ingestion pipeline and API (~1.25h)

**Work**
- The bounded `Channel<PendingWrite>`.
- The writer loop with group commit: apply each batch to working copies, commit it, then publish snapshots and complete the waiting requests through `TaskCompletionSource`.
- The snapshot registry and the endpoints from §2.6, including sorting.
- Recovery at startup, finished before Kestrel accepts requests.
- Draining the queue on graceful shutdown.

**Verify**
- [ ] Application tests:
  - concurrent posts to the same rocket
  - duplicates within one batch
  - a store failure gives 503 and leaves the snapshots unchanged
- [ ] API tests with `WebApplicationFactory`: status codes, sorting, and the 404 and 400 cases.
- [ ] A restart test: post messages, dispose the host, start a new host on the same database, and get the same state.

### Phase 4: End-to-end verification and resilience (~1h)

**Work**
- A `scripts/e2e.ps1` script that:
  - starts the service on a fresh database
  - runs `rockets.exe` with the defaults (with a quick `--max-messages` variant for fast runs)
  - runs the oracle
- The **oracle** is a deliberately separate, simple implementation, so it doesn't share bugs with the main code. It reads the full `messages` log, sorts each rocket's messages, folds them from scratch, and asserts that:
  - its result equals `GET /rockets` for every rocket
  - every rocket has all messages from 1 to its last
  - `missingMessageCount == 0`
  - each checkpoint equals `lastMessageNumber`
- A **crash test**: kill the service mid-run, restart it, and let `rockets.exe` resend. The oracle must still pass, which shows that no acknowledged message was lost.
- A **stress run** at `--concurrency-level 20`.

**Verify**
- [ ] The e2e script exits 0 for the default run, the crash test and the stress run.
- [ ] Throughput and duration for the default run are recorded for the README.

### Phase 5: Documentation (~0.75h)

**Work**
- `README.md`:
  - how to run the service, the tests and the e2e script
  - the API, with examples
  - an architecture diagram
  - a decision log covering the decisions in §2.1, including the alternatives and trade-offs
  - known limitations: a gap that never fills stops the checkpoint and grows pending; the service runs as a single process; the explosion rule is an assumption
  - how to scale: split rockets across several writers by channel, move to Postgres, and separate the ingest and query services
  - a summary of the AI workflow, linking to the dev diary
- Update `CLAUDE.md` with the real commands and architecture.

**Verify**
- [ ] A fresh clone can be built, tested and run against `rockets.exe` by following only the README.

### Phase 6 (stretch): Postgres store

**Work**
- `Rockets.Storage.Postgres` behind the same `IMessageStore`, selected through the `Storage:Provider` setting.
- A `docker-compose.yml` for Postgres.

**Verify**
- [ ] The same contract-test suite passes against Postgres, using Testcontainers. This shows the swap needs no changes outside the storage project.

## 4. AI workflow

- Work happens one phase at a time. The developer reviews and approves each phase before it is committed.
- The developer owns design decisions. The assistant raises them as questions instead of deciding silently, and every prompt is recorded in [devdiary.md](devdiary.md) with its outcome.
- The domain and ordering logic are covered by tests before the code that makes them pass is written.
- The oracle in Phase 4 is a separate, simple implementation, so the end-to-end check doesn't rely on the code it verifies.
