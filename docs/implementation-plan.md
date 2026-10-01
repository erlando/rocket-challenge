# Implementation plan: Rockets

This plan covers the Lunar backend engineer challenge described in [CHALLENGE.md](CHALLENGE.md). It is organised around the assessment targets:
- design trade-offs
- depth and maintainability
- verification
- deliberate AI use

It is split into progressive phases, and each phase ends with targets that can be verified.

**Revision 2.** This version was revised after an independent review of the plan by a second AI agent; the findings are in [devdiary.md](devdiary.md), entries 13 and 14. The main changes:
- The end-to-end oracle now compares against ground truth from outside the service.
- An error in one message no longer fails its whole batch.
- Checkpoints are no longer saved to disk; the service replays the message log at startup instead.
- After a storage error, the service reloads the affected rockets from the database.
- Phase 0 now probes how the test program behaves on errors.
- The schedule has been re-estimated.

## 1. Goals and assessment mapping

| Assessment target | Where it is addressed |
|---|---|
| Design choices and trade-offs | The decision log, written during each phase and finished in the README (Phase 5); the design decisions in this document |
| Data structures | The immutable `RocketLedger`, which holds a checkpoint and the pending messages after it (Phase 1) |
| Concurrency | A single writer that batches commits, with lock-free reads from snapshots (Phase 3) |
| Persistence | The SQLite message log behind a storage interface (Phase 2); Postgres as a stretch goal (Phase 6) |
| Error handling | The error policy (§2.6): errors in one message are kept separate from storage errors; the service reconciles after a commit that failed in an ambiguous way (Phase 3); the crash test (Phase 4) |
| Verification | Unit, contract and API tests in every phase; an end-to-end oracle checked against ground truth captured outside the service; the crash test (Phase 4) |
| AI use | [devdiary.md](devdiary.md); review and approval of each phase before it is committed; an independent agent's review of this plan (§4) |

### Load profile

The grading run uses the defaults of `rockets launch`:
- `--concurrency-level 3`
- `--max-messages 100000`
- `--seed 444`
- no message delay

So the service must handle about 100k messages, sent three at a time as fast as it answers, with redelivery on any non-2xx response. At most three requests are in flight at once, so a batch of commits never holds more than three messages during the grading run.

### Observed behaviour of the test program (Phase 0)

Details are in [devdiary.md](devdiary.md), entry 15.
- **Determinism:** two runs with seed 444 sent exactly the same 100,000 messages, ignoring `messageTime`. A capture of one run is the ground truth for the oracle.
- **The data:**
  - There are 20 rockets, with up to 6,498 messages each.
  - Every rocket starts at #1 with `RocketLaunched`, and there are no gaps.
  - 6 rockets explode, and no messages are numbered after an explosion.
  - Speed never goes below 500.
- **Reordering and duplicates:**
  - Reordering comes only from concurrent requests, and it depends on timing. The furthest a message arrived out of place was 2 positions in one run and 279 in another.
  - There are no duplicates when every message gets a 2xx.
- **Retries:**
  - A 4xx, a 5xx and a refused connection are all retried after a fixed 500 ms, again and again.
  - The client times out after about 10 s and then resends. If the first attempt is still being processed, that creates a real duplicate.
- **When the run ends:**
  - Retries that are still waiting when the program has generated its last message are dropped, and it still exits 0. So a non-2xx near the end of a run loses that message.
  - The program can also crash (`panic: send on closed channel`) when a retry overlaps its shutdown.
- **Connections:** the test program opens a new connection for every message. On Windows, two back-to-back 100k runs use up the client ports (TIME_WAIT) for about a minute.
- **Speed:** a capture server that does no work handles the whole default run in about 3 s, roughly 33k messages per second.

## 2. Architecture

### 2.1 Design decisions

These were agreed during planning. The reasoning is in the dev diary.

| Decision | Choice | Main alternatives considered |
|---|---|---|
| Persistence | **SQLite**, as an append-only message log behind `IMessageStore`, so it can be swapped for Postgres. The log is the only stored state; rocket state is derived from it. | Postgres (production-like, but reviewers need Docker to run it); in-memory (loses messages that were already acknowledged) |
| Ordering | **Checkpoint plus replay**: newest data takes priority over strict correctness | Buffer until gaps fill (state lags behind); fold all messages from scratch on every change (cost O(n)) |
| Write concurrency | **Single writer loop with group commit.** The main reason is that only one thread changes state, so the domain code needs no locks. Batching saves at most about as many disk syncs as there are concurrent requests (3× under the default load). | A lock per rocket (one disk sync per message, and SQLite serialises writes anyway); the database doing the ordering (every read hits the DB) |
| Topology | **Single ASP.NET Core service** with internal layers | Separate ingest and query services (more moving parts within the 6-hour budget) |
| Recovery | **Replay the full log at startup**; checkpoints live only in memory | Saving checkpoints to disk (faster startup at large scale, but a bug in applying messages would be saved too, and survive the fix) |

### 2.2 Overview

```
POST /messages ─▶ parse + validate ─(invalid)─▶ rejected (see §2.6)
                     │
                     ▼
            bounded Channel<PendingWrite> ─▶ single writer loop (drains whatever is queued; never waits to fill a batch)
                                              ├─ per message: apply to a working copy of its ledger
                                              │    an apply error rejects only that message (see §2.6)
                                              ├─ IMessageStore.CommitAsync(new messages)   one transaction, one fsync
                                              ├─ success: publish snapshots, complete requests → 204
                                              └─ store error: complete requests → 503 (redelivered; idempotent),
                                                   then reload the affected rockets from the store,
                                                   because the commit may in fact have succeeded

GET /rockets[/{channel}] ─▶ ConcurrentDictionary<channel, RocketSnapshot>   (immutable, lock-free)
```

Only the writer loop changes rocket state, so the domain code has no locks. Snapshots are published only after their commit succeeds. That way a reader never sees state that might be rolled back, and a request gets 2xx only once its message is stored. Only a storage error fails a whole batch. A problem with one message affects only that message.

### 2.3 Projects (.NET 10)

| Project | Responsibility |
|---|---|
| `src/Rockets.Domain` | Pure code with no IO: message types, polymorphic JSON parsing, validation, `RocketState` and the function that applies a message to it, and `RocketLedger` |
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
- Each message costs O(number of pending messages), which is bounded by how far out of order messages arrive. State is never rebuilt from the start, except when the log is replayed at startup.

**Same number, different payload.** The first write wins. Each stored message carries a hash of its payload. If a duplicate's hash differs from the stored one, the duplicate is still acknowledged, but the mismatch is logged and counted. The ledger checks this for pending messages, and the store checks it when an insert hits an existing row.

**Rules for applying messages:**
- `RocketLaunched` sets the type, the launch speed and the mission (counted as the mission at message #1).
- `RocketSpeedIncreased` and `RocketSpeedDecreased` add or subtract `by`, using checked arithmetic so an overflow is an error, not a wrong number.
- `RocketMissionChanged` sets the mission.
- `RocketExploded` sets the status to exploded and records the reason. The status is permanent, while other fields keep updating. Phase 0 found no messages after an explosion with seed 444, so this rule doesn't affect the graded run. It remains a documented assumption.
- Until launch data arrives, the status is `AwaitingLaunch`. This stops a dashboard from showing a speed made of changes alone as if it were real.
- `updatedAt` is the `messageTime` of the highest-numbered message applied to the current state. It is deterministic, unlike the time a message was received. `launchedAt` is the `messageTime` of `RocketLaunched`.

**Design note.** With today's message types, applying messages in any order gives the same result:
- speed changes add up
- the mission is the one with the highest message number
- an explosion is permanent

So the current state could be updated in O(1) per message with no replay. Checkpoint plus replay was chosen anyway because it stays correct if a future message type or rule depends on order, such as setting an absolute speed or never letting speed go below 0. The O(1) shortcut is documented as an optimisation we chose not to take.

### 2.5 Storage

The **storage interface** (`IMessageStore`) is the seam for swapping in Postgres:
- `InitializeAsync()`: create the schema if it doesn't exist.
- `ReadAllAsync()`: stream every stored message, used to replay the log at startup.
- `ReadChannelAsync(channel)`: read one rocket's messages, used to reload it after a storage error.
- `CommitAsync(newMessages)`: write a batch atomically, in one transaction. It returns the messages that were already stored, together with any payload-hash mismatches.
- `RejectAsync(rejected)`: record a message that was rejected (see §2.6).

**Schema:**
- `messages(channel, message_number, message_type, message_time, payload_json, payload_hash, received_at)`, with primary key `(channel, message_number)`.
  - This append-only log is the only stored state.
  - Inserts use `ON CONFLICT DO NOTHING`, a backstop behind the ledger's own duplicate check.
- `rejected_messages(id, received_at, reason, body)`, for messages that fail validation or applying.

**Recovery.** At startup the service replays the full log through the same `RocketLedger` code, then starts accepting requests.
- Phase 2 measures how long replaying 100k messages takes.
- If startup ever became too slow, the next step would be snapshots saved to disk, plus a "rebuild from the log" option and a test that a snapshot equals a full replay. That trade-off is documented rather than built.

**SQLite pragmas:**
- `journal_mode=WAL`, so reads don't block the writer.
- `synchronous=FULL` or `NORMAL`, chosen in Phase 2 from measured numbers. `FULL` makes a commit safe even across a power cut; `NORMAL` makes it safe across a process crash only.
- `busy_timeout`.

### 2.6 API and error policy

| Endpoint | Behaviour |
|---|---|
| `POST /messages` | Returns 204 once the message is stored, and 204 for a duplicate too. Rejected messages: see below. |
| `GET /rockets/{channel}` | Returns the rocket's state, or 404 if the rocket is unknown |
| `GET /rockets?sortBy=channel\|type\|mission\|speed\|status\|updatedAt&order=asc\|desc` | Lists all rockets, sorted; 400 for an invalid sort |
| `GET /health` | Liveness check |

Besides its state, each rocket in a response includes:
- `lastMessageNumber`: the highest message number received
- `checkpointMessageNumber`: N
- `missingMessageCount`: the number of messages missing below `lastMessageNumber`

Together these let a dashboard show whether a rocket's data is complete.

A message can fail in two ways, and they are handled differently:
- **Problems with a single message** are caught per message and never fail its batch. They come in three kinds:
  - a malformed envelope: invalid JSON, or a missing channel or number
  - semantic validation: a negative or missing `by`, or missing launch fields
  - an error while applying it: an overflow, or a bug
- **Storage errors** fail the batch, and every message in it is resent.

| Situation | Response |
|---|---|
| A problem with a single message | Recorded in `rejected_messages` and logged, and answered with 2xx. Phase 0 showed that the test program resends after a 4xx just as after a 5xx, so a 4xx would only make it retry a message that can never succeed. |
| Unknown `messageType` | Stored and acknowledged with 2xx, but not applied. It is counted and logged, so new message types don't break the service. |
| Same number, different payload | 2xx, and the first write wins. The mismatch is logged and counted (see §2.4). |
| Storage error | 503 for the batch, then the affected rockets are reloaded from the store. A redelivered message is safe because writes are idempotent. |
| Write queue full | The request waits with a timeout, then gets 503. The timeout (about 5 s) must stay well below the client's timeout of about 10 s, so the service answers before the client gives up and sends a duplicate. |
| Client disconnects | `RequestAborted` cancels only the wait to get into the queue. It never cancels a write that is already being processed. |
| Shutdown | The service stops accepting messages and drains the queue before exiting. |

## 3. Phases

Every phase ends with:
- all tests green
- the developer reviewing the diff
- a paragraph added to the decision log, for the README
- an entry in the dev diary
- a commit on `main`

Hour estimates are measured against the 6-hour budget and add up to 6h. Phase 6 is not included.

### Phase 0: Skeleton and probing the test program (~0.75h) ✅

**Work**
- Create the solution with the project layout from §2.3.
- Make `Rockets.Api` listen on `http://localhost:8088`.
- A separate developer tool, `tools/Rockets.Capture`, serves `POST /messages` on port 8088 and appends each delivery attempt to an NDJSON capture file. It was moved out of `Rockets.Api` to keep throwaway code out of the service.
  - Probe options make the first attempt of each message fail or stall.
  - `scripts/probe.sh` runs the test program against it.
- Add a small analysis script or test that reports on the captured traffic:
  - whether numbering starts at 1 and `RocketLaunched` is always #1
  - the duplicate rate
  - the largest reorder distance
  - whether messages arrive after `RocketExploded`
  - how many rockets there are
- **Determinism check:** capture two runs with `--seed 444` and compare the deduplicated sets of messages. If they match, the deduplicated capture is the ground truth for the oracle in Phase 4.
- **Probing how the test program behaves on errors.** Use a small `--max-messages` and, for each case below, record whether it resends, retries forever, or gives up:
  - a 4xx response
  - a 5xx response
  - a slow response or timeout
  - a refused connection, with the service hard-killed mid-run

**Verify**
- [x] `dotnet build` and `dotnet test` pass.
- [x] `rockets.exe launch "http://localhost:8088/messages"` with default flags completes with no errors.
- [x] The traffic findings, the determinism result and the error behaviour are recorded in the dev diary (entry 15) and summarised in §1.
- [x] The open choices in §2.4 and §2.6 are settled from these findings:
  - the explosion rule stays an assumption
  - rejected messages get 2xx
  - the ground truth is the deterministic capture

### Phase 1: Domain model and ordering (~1h) ✅

**Work**
- Message types, polymorphic JSON parsing and validation.
- `RocketState` and the function that applies a message to it.
- `RocketLedger`: checkpoint advance, the pending buffer, duplicate detection with the payload-hash check, and the current state.

**Verify**
- [x] Unit tests for each message type, a message arriving before launch, the explosion rule, overflow, and validation failures.
- [x] A property-style test that takes a generated message sequence, shuffles it with a seeded random generator, and injects duplicates. It checks that:
  - the final state equals applying the messages in order
  - the checkpoint never moves past the first gap
  - after every step, the current state equals the checkpoint plus pending messages applied in order, skipping gaps
- [x] Added: a mutation check. Four deliberately planted bugs each make tests fail, which shows the tests can catch real mistakes.

### Phase 2: Storage and the SQLite store (~0.75h)

**Work**
- The `IMessageStore` interface.
- The SQLite implementation: schema creation, pragmas, an atomic commit, payload-hash conflict detection, reading the log, and rejected messages.

**Verify**
- [ ] Contract tests on a temporary-file database:
  - a batch commits atomically
  - inserting a duplicate does nothing, and returns the duplicate along with any payload-hash mismatch
  - `ReadAllAsync` and `ReadChannelAsync` return exactly what was committed
  - a failed transaction leaves nothing behind
- [ ] One benchmark run that measures:
  - commits per second for `FULL` and `NORMAL` at batch sizes 1, 3 (the default load) and 20 (the stress run)
  - how long replaying 100k messages takes
- [ ] The `synchronous` setting is chosen from those numbers, and the duration of the default grading run is estimated.

### Phase 3: Ingestion pipeline and API (~2h)

**Work**
- The bounded `Channel<PendingWrite>`.
- The writer loop. For each pass it:
  - drains whatever is queued
  - applies each message to a working copy of its ledger, rejecting a failing message on its own
  - commits the batch
  - publishes the snapshots and completes the waiting requests through `TaskCompletionSource`
- After a storage error, the writer reloads the affected rockets through `ReadChannelAsync`.
- The snapshot registry and the endpoints from §2.6, including sorting.
- Recovery at startup: the full log is replayed before Kestrel accepts requests.
- Draining the queue on graceful shutdown, and the cancellation rule from §2.6.

**Verify**
- [ ] Application tests:
  - concurrent posts to the same rocket
  - duplicates within one batch
  - **a bad message in a batch with good ones**: only the bad message is rejected, and the good ones are stored and acknowledged
  - a store failure gives 503 and leaves the snapshots unchanged
  - **a fake store that commits and then throws**: after the reload, memory matches the store
  - a client that disconnects mid-write doesn't stop the message from being stored
- [ ] API tests with `WebApplicationFactory`: status codes, sorting, and the 404 and 400 cases.
- [ ] A restart test: post messages, dispose the host, start a new host on the same database, and get the same state.

### Phase 4: End-to-end verification and resilience (~1h)

**Work**
- A `scripts/e2e.ps1` script that:
  - requires `pwsh`, the cross-platform PowerShell
  - picks the `rockets` binary that matches the OS and architecture
  - starts the service on a fresh database
  - runs `rockets.exe` with the defaults (with a quick `--max-messages` variant for fast runs)
  - runs the oracle
  - waits for client ports in TIME_WAIT to free up between back-to-back 100k runs, because the test program opens a new connection for every message
- **The oracle checks against ground truth from outside the service.**
  - The expected messages come from a capture of a seed-444 run. Phase 0 showed these runs are deterministic, so no logging proxy is needed. The capture is regenerated by the script, not committed.
  - The oracle is a deliberately separate, simple implementation: it deduplicates the expected messages, sorts each rocket's messages, and folds them from scratch.
  - It asserts that its result equals `GET /rockets` for every rocket, and that every expected message is in the service's log.
  - It asserts that every rocket has `missingMessageCount == 0` and a checkpoint equal to `lastMessageNumber`.
- **A crash test:** hard-kill the service mid-run (`Stop-Process -Force`), restart it, and let `rockets.exe` resend.
  - Phase 0 showed that it keeps retrying refused connections every 500 ms.
  - Use `--message-delay`, so the run lasts well beyond the restart. Retries still waiting when the test program finishes are dropped.
  - The oracle must still pass. That shows no acknowledged message was lost, because the expected messages come from outside the service.
- **A stress run** at `--concurrency-level 20`.

**Verify**
- [ ] The e2e script exits 0 for the default run, the crash test and the stress run.
- [ ] Throughput and duration for the default run are recorded for the README.

### Phase 5: Documentation (~0.5h)

**Work**
- `README.md`:
  - how to run the service, the tests and the e2e script
  - the API, with examples
  - an architecture diagram
  - the decision log, assembled from the paragraphs written during each phase, covering §2.1 with its alternatives and trade-offs
  - known limitations: a gap that never fills stops the checkpoint and grows pending; the service runs as a single process; the explosion rule is an assumption; the first write wins when payloads differ
  - how to scale: split rockets across several writers by channel, move to Postgres, add snapshots saved to disk, and separate the ingest and query services
  - a summary of the AI workflow, linking to the dev diary
- Update `CLAUDE.md` with the real commands and architecture.

**Verify**
- [ ] A fresh clone can be built, tested and run against `rockets.exe` by following only the README.

### Phase 6 (stretch): Postgres store

This phase is **dropped unless more than 1h of the budget remains** after Phase 5. Without it, the case for swapping in Postgres rests on the `IMessageStore` interface and its contract-test suite.

**Work**
- `Rockets.Storage.Postgres` behind the same `IMessageStore`, selected through the `Storage:Provider` setting.
- A `docker-compose.yml` for Postgres.

**Verify**
- [ ] The same contract-test suite passes against Postgres, using Testcontainers. This shows the swap needs no changes outside the storage project.

## 4. AI workflow

- Work happens one phase at a time. The developer reviews and approves each phase before it is committed.
- The developer owns design decisions. The assistant raises them as questions instead of deciding silently, and every prompt is recorded in [devdiary.md](devdiary.md) with its outcome.
- A second AI agent, given fresh context and read-only access, reviewed this plan before any code was written. Its findings, and what was decided about each, are in the dev diary (entries 13 and 14). The same independent review can be repeated at the end of each phase.
- The domain and ordering logic are covered by tests before the code that makes them pass is written.
- The oracle in Phase 4 is a separate, simple implementation that checks against ground truth captured outside the service, so the end-to-end check doesn't rely on the code it verifies.
