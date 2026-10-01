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
