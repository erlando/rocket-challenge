# Dev diary

This is a log of the prompts given to the AI coding assistant (Claude Code) during the challenge. Each entry pairs the prompt with a summary of what the assistant found or did. Prompts are quoted verbatim.

---

## 2026-10-01 · 1. Initialise CLAUDE.md

**Prompt**
> /init

**Findings**
- The repo contained only a .NET `.gitignore`, so the solution is expected to be C#/.NET. There was no code, README or AI-tool config yet.
- The challenge brief and the `rockets` test program are in the sibling folder `../lunar-backend-engineer-challenge/`.
- The brief's key constraints are:
  - Messages arrive out of order and are delivered at least once.
  - Any non-2xx response triggers redelivery.
  - Speed messages are deltas, so they must be applied in `messageNumber` order without double-counting.
  - Grading uses the test program's defaults, so the service must listen on `http://localhost:8088/messages`.

**Actions**
- Created `CLAUDE.md`, which covers:
  - the requirements and how messages behave
  - how to run the test program
  - the assessment criteria, including the instruction to raise design decisions with the developer instead of making them silently

---

## 2026-10-01 · 2. Start a dev diary

**Prompt**
> As this is a coding challenge where the use of AI is part of the assessment I want you to keep a diary of prompts together with a summary of findings or actions taken. Put this diary in docs/devdiary.md. Pin this as a memory

**Actions**
- Created this diary (`docs/devdiary.md`) and backfilled the entry for the earlier prompt.
- Saved a pinned assistant memory, so future sessions keep adding an entry for every prompt.

---

## 2026-10-01 · 3. Record the diary rule in CLAUDE.md and commit

**Prompt**
> yes, add it to CLAUDE.md and commit

**Actions**
- Added a "Dev diary" section to `CLAUDE.md`. The rule now lives in the repo as well as in the assistant's local memory.
- Committed `CLAUDE.md` and `docs/devdiary.md`.

---

## 2026-10-01 · 4. Merge into main and commit there from now on

**Prompt**
> merge it into main and commit there from now on

**Actions**
- Fast-forward merged `docs/claude-setup` into `main` and deleted the branch.
- Saved an assistant memory so future commits in this repo go directly on `main`.
- Committed this diary entry on `main`.

---

## 2026-10-01 · 5. Commit the challenge brief

**Prompt**
> commit docs/CHALLENGE.md too

**Findings**
- `docs/CHALLENGE.md` (added by the developer) is an exact copy of the challenge `README.md` in `../lunar-backend-engineer-challenge/`.

**Actions**
- Updated `CLAUDE.md` to name `docs/CHALLENGE.md` as the in-repo source of truth for requirements.
- Committed `docs/CHALLENGE.md`, the `CLAUDE.md` change and this entry on `main`.

---

## 2026-10-01 · 6. Ask for an implementation plan

**Prompt**
> I want a plan for implementation of this coding challenge with focus on the assessment targets. Put this plan into docs/implementation-plan.md. The plan must be split into logical, progressive phases with verifiable targets underway.

**Findings**
- `rockets.exe launch --help` shows the defaults used for grading: `--concurrency-level 3`, `--max-messages 100000`, `--seed 444`, and no message delay. That means about 100k messages sent three at a time, as fast as the service answers.
- .NET SDK 10.0.401 and Docker 29.8 are installed.

**Actions**
- The prompt was given in plan mode, so no files were edited until the plan was approved. Entries 6–11 were written afterwards.
- Asked the developer to choose the persistence, ordering and topology approaches. They chose to discuss them one at a time instead (entries 7–10).

---

## 2026-10-01 · 7. Discuss the cost of SQLite

**Prompt**
> Let's discuss persistance and concurrency. What will be the cost of choosing SQLite?

**Findings (presented to the developer)**
- **Costs of SQLite:**
  - Only one write can happen at a time across the whole database.
  - Each commit needs a disk sync before returning 2xx. `synchronous=FULL` survives a power cut; `NORMAL` in WAL mode survives only a process crash.
  - It can't scale to several instances.
  - It adds a schema, a repository and rebuilding state at startup.
- **Benefits of SQLite:**
  - Reviewers don't need to install anything.
  - Acknowledged messages survive a restart. In-memory storage breaks at-least-once delivery, because a message that got 2xx is never resent.
  - Duplicates are easy to detect with a unique key on `(channel, messageNumber)`.
  - Tests need no Docker.
- **Effect on concurrency:** since SQLite already allows only one writer, a single writer loop that batches commits costs no parallelism. It removes locks from domain code and needs one disk sync per batch. Its risks are that it is a single bottleneck and needs a clear error and shutdown path.

---

## 2026-10-01 · 8. Choose SQLite, swappable for Postgres

**Prompt**
> Let's go with SQLite but make sure that it can easily be swapped with Postgres if need be. Continue the planning

**Decision**
- Persistence uses SQLite behind a narrow `IMessageStore` interface, with all SQL kept in the storage project.
- A shared contract-test suite will show that a Postgres store behaves the same (stretch goal, Phase 6 of the plan).

**Actions**
- Asked about ordering, concurrency and topology. The developer chose to discuss ordering first.

---

## 2026-10-01 · 9. Discuss out-of-order messages

**Prompt**
> Let's discuss out-of-order messages. I think the newest data is more important than being exactly correct all the time. Instead of recalculating the state from the entire set of messages for a rocket maybe there should be checkpoints whenever the entire sequence is known. The recalculation can happen from there.

**Decision (proposed by the developer, refined together)**
- **Checkpoint plus replay:**
  - The checkpoint is each rocket's state up to the highest message number N reached with no gaps.
  - Messages above N are kept pending, sorted by number.
  - The current state is the checkpoint with the pending messages applied in order, skipping gaps.
  - The checkpoint moves forward as gaps fill.
  - Each message costs O(number of pending messages), which is small.
- Checkpoints are stored in the same transaction as the messages, so startup only replays messages after each checkpoint.

**Findings (raised by the assistant)**
- With today's message types, applying messages in any order gives the same result:
  - speed changes add up
  - the mission is the one with the highest message number
  - an explosion is permanent
- So the current state could be updated in O(1) per message with no replay. Checkpoint plus replay was kept anyway, because it stays correct if a future message type or rule depends on order (e.g. an absolute speed, or speed never going below 0).
- Edge cases to settle:
  - a gap that never fills
  - messages that arrive before `RocketLaunched` (status `AwaitingLaunch`)
  - messages after `RocketExploded`
  - API fields that show whether a rocket's data is complete

---

## 2026-10-01 · 10. Choose the concurrency model and topology

**Prompt**
> show me the options

**Options presented**
- **Concurrency:** single writer with batching; a lock per rocket; or the database doing the ordering.
- **Topology:** a single service, or separate ingest and query services.

**Decisions**
- **Concurrency: single writer with batching.**
  - Requests queue their message on a bounded channel.
  - One writer commits a batch in one transaction, then publishes immutable snapshots and completes the requests.
  - Reads take no locks.
- **Topology: a single ASP.NET Core service** with internal layers. Splitting it into services is documented as the way to scale.

---

## 2026-10-01 · 11. Plan approved

**Prompt**
> (Plan approved in plan mode.)

**Actions**
- Wrote `docs/implementation-plan.md` with:
  - the assessment mapping
  - the architecture and design decisions
  - the ordering model
  - the storage interface and schema
  - the API and error policy
  - seven phases (0–6), each with checklists of targets to verify
  - the AI workflow
- Backfilled diary entries 6–11. Nothing committed yet.

---

## 2026-10-01 · 12. Commit the plan

**Prompt**
> commit this

**Actions**
- Committed `docs/implementation-plan.md` and diary entries 6–12 on `main`.

---

## 2026-10-01 · 13. Add .gitattributes and get an independent review of the plan

**Prompt**
> make the change to .gitattributes. Then let a second agent review the implementation plan. Report the findings.

**Actions**
- Added `.gitattributes` (`* text=auto eol=lf`). `git add --renormalize .` changed nothing, because the index already stored LF. Not committed.
- Started a separate reviewer agent with fresh context and read-only instructions. It read `CHALLENGE.md`, `implementation-plan.md` and this diary, and it ran `rockets.exe launch --help` (not `launch` itself).

**Review findings (the plan is not changed yet; that waits for the developer's decisions)**
- **High**
  - **H1:** The oracle folds the service's own `messages` table, so it can't detect an acknowledged message that was lost. Ground truth must come from outside the service: a deterministic capture with seed 444, or a logging proxy.
  - **H2:** A deterministic failure, such as one poison message, makes every request in its batch return 503 forever, through endless redelivery. Per-message validation and apply errors must be separated from store errors.
- **Medium**
  - **M1:** At concurrency 3 a batch holds at most 3 messages, so group commit helps little. The benchmark should use batch sizes 1, 3 and 20, and the choice between `FULL` and `NORMAL` should be made from those numbers.
  - **M2:** A commit that fails in an ambiguous way (it may have succeeded) can leave memory out of step with the database. After a store error, reload the affected rockets or stop the service.
  - **M3:** Nobody knows whether `rockets.exe` resends after a refused connection or a 5xx. Probe this in Phase 0, and use a hard kill in the crash test.
  - **M4:** Persisted checkpoints are derived state, so a projection bug would be saved to disk. Either replay the whole log at startup, or add a rebuild option and a test that recovery matches a full fold of the log.
  - **M5:** The schedule is optimistic: Phase 3 is probably about 2h. Write the decision log during each phase, and drop Phase 6 unless time remains.
- **Low:**
  - L1: the same message number arriving with a different payload
  - L2: the fallback if 4xx responses are resent, and semantic validation
  - L3: what `updatedAt` means
  - L4: recovering rockets that have no checkpoint yet
  - L5: client disconnects must not cancel writes already in flight
  - L6: the e2e script assumes Windows
  - L7: the number of projects

**Assistant's assessment**
- All the High and Medium findings are valid.
- M1 corrects an overstated claim from entry 7. Group commit gains at most about 3× at the default concurrency. The single writer is still justified because it keeps locks out of the domain code, but not mainly for throughput.

---

## 2026-10-01 · 14. Revise the plan from the review and commit

**Prompt**
> yes, update the plan and commit

**Decisions** (the developer accepted the assistant's recommendations)
- **H1:** The oracle checks against ground truth from outside the service. First choice: the deduplicated capture from a deterministic seed-444 run, checked in Phase 0. Fallback: a logging proxy that records every message that got a 2xx.
- **M4:** Checkpoints are no longer saved to disk. The message log is the only stored state, and startup replays it in full. Snapshots saved to disk are documented as a way to scale, not built.

**Actions**: rewrote `docs/implementation-plan.md` as revision 2.
- **H2:** Problems with a single message (malformed, invalid, or failing to apply) are rejected on their own and recorded in `rejected_messages`. Only storage errors fail a batch.
- **M1:** Group commit is now justified by keeping locks out of the domain code, not by throughput. The writer drains whatever is queued. The benchmark uses batch sizes 1, 3 and 20, and the `synchronous` setting is chosen from the results.
- **M2:** After a storage error, the affected rockets are reloaded from the store. A test uses a fake store that commits and then throws.
- **M3:** Phase 0 now probes the test program's behaviour after a 4xx, a 5xx, a timeout and a refused connection, and checks whether runs are deterministic. The crash test uses a hard kill, with an in-process fallback.
- **M5:** Re-estimated the schedule (Phase 3 at 2h). The decision log is written during each phase. Phase 6 is dropped unless more than 1h remains.
- **Low findings:**
  - L1: the first write wins, and a payload-hash mismatch is counted
  - L2: whether rejected messages get 4xx or 2xx is decided from Phase 0
  - L3: `updatedAt` means `messageTime`
  - L4: no longer applies, since there are no saved checkpoints
  - L5: `RequestAborted` never cancels a write that is already being processed
  - L6: the e2e script requires `pwsh` and picks the binary by OS and architecture
  - L7: not acted on, because the separate storage project keeps all SQL in one place
- Committed `.gitattributes`, the revised plan and diary entries 13–14 on `main`.

---

## 2026-10-01 · 15. Phase 0: skeleton and probing the test program

**Prompt**
> start phase 0

**Actions**
- **Solution skeleton:**
  - Created `RocketChallenge.slnx` with `Rockets.Domain`, `Rockets.Application`, `Rockets.Storage.Sqlite` and `Rockets.Api` (port 8088, `/health` only), with the project references set up in layers.
  - Added `global.json`, `Directory.Build.props` (nullable, warnings as errors) and central package management.
- **Deviation from the plan:** the capture server and the analysis went into a separate tool, `tools/Rockets.Capture`, instead of `Rockets.Api`. This keeps throwaway code out of the service, and the tool is reused for the Phase 4 oracle. It has three commands:
  - `serve`: records every delivery attempt; probe options can fail or stall the first attempt of each message
  - `analyze`: reports delivery, ordering and content statistics
  - `compare`: checks whether two captures hold the same messages
- Added `scripts/probe.sh`, which picks the right `rockets` binary for the OS and architecture.
- **Tests:** 14 unit tests for the capture tool (record round-trip, analyzer statistics, comparer).
- **Setup problems fixed:**
  - xUnit v3 needed a global `using Xunit`.
  - On the .NET 10 SDK, xUnit v3 no longer supports VSTest under `dotnet test`. Opted into Microsoft.Testing.Platform in `global.json`, and dropped `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio`.
  - The analyzer couldn't read a capture the server still had open. It now opens files with `FileShare.ReadWrite`.

**Findings**
- **Default run:** 100,000 messages in about 3 s against the capture server, exit code 0.
  - 20 rockets, up to 6,498 messages each.
  - Every rocket starts at #1 with `RocketLaunched`, and there are no gaps.
  - 6 explosions, and no messages after them.
  - Speed never goes below 500.
  - No duplicates when every message gets a 2xx.
- **Determinism:** two seed-444 runs sent exactly the same 100,000 messages (`compare` reported EQUAL). Their reordering differed: the furthest a message arrived out of place was 2 positions in one run and 279 in the other, so reordering depends on timing and comes only from concurrent requests.
- **4xx and 5xx (50 messages, first attempt failed):**
  - Both are retried the same way, after a fixed 500 ms.
  - Retries still waiting when the program has generated its last message are dropped ("Redelivering message failed: writer closed"), and the program still exits 0. In the 50 ms-delay probe, 10 of 50 messages were never delivered.
- **Timeout (first attempt stalled 15 s):**
  - The client times out after about 10 s and resends. The stalled first attempt still completes, so the server receives a real duplicate.
  - `rockets.exe` crashed with `panic: send on closed channel` (exit 2) when a retry overlapped its shutdown.
- **Hard kill mid-run (500 messages, 20 ms delay; killed for 3 s, then restarted):**
  - 562 refused connections, each retried every 500 ms.
  - All 500 messages were delivered after the restart, with no gaps. The crash test is viable if the run outlasts the restart.
- **Ports:** `rockets.exe` opens a new connection for every message. After two 100k runs, 17,384 client sockets sat in TIME_WAIT, and the next run failed with "Only one usage of each socket address". The sockets drained in under a minute.

**Decisions settled from the findings** (as the plan prescribed; the developer can override them in review)
- The oracle's ground truth is a deterministic seed-444 capture, so no logging proxy is needed.
- Rejected messages get 2xx, because a 4xx is retried like a 5xx.
- The wait for a full queue is capped at about 5 s, below the client's 10 s timeout. A 503 near the end of a run loses the message, so the service shouldn't rely on redelivery under normal load.
- The explosion rule stays a documented assumption, since the seed-444 data never exercises it.
- The crash test uses `--message-delay`, so the run outlasts the restart. The e2e script waits for TIME_WAIT between large runs.

**Updated docs**
- Recorded the observed behaviour in `docs/implementation-plan.md` §1, settled §2.4 and §2.6, ticked Phase 0, and adjusted Phase 4.
- Started `docs/decision-log.md`.
- Updated `CLAUDE.md` with the build, test and probe commands.

**Verification:** `dotnet build` has 0 warnings and 0 errors, and `dotnet test` passes 14 of 14. The default `rockets.exe` run completed with exit 0. Not committed yet, so the phase diff can be reviewed first.

---

## 2026-10-01 · 16. Make the repo self-contained

**Prompt**
> This repo needs to be self-contained. Move the rocket binaries from the external folder into a folder in this repo

**Actions**
- Moved the six `rockets` binaries (version 0.1.0, about 9 MB each, about 55 MB in total) from `../lunar-backend-engineer-challenge/` to `vendor/rockets/<os>_<arch>/`. The original zip stays outside the repo as a backup.
- Added `vendor/rockets/README.md` with the source, version and usage.
- Marked the binaries as `binary` in `.gitattributes`, so the LF rule never touches them.
- Pointed `scripts/probe.sh` and `CLAUDE.md` at `vendor/rockets/`. `CLAUDE.md` now states the rule that the repo must not reference anything outside itself. Older diary entries still mention the old path, because they record what happened at the time.
- Saved an assistant memory: the repo must stay self-contained.
- The macOS and Linux binaries must be committed with the executable bit (`git update-index --chmod=+x`), because Windows doesn't record it.

**Verification:** no file except the diary references the external folder. `scripts/probe.sh` ran a 20-message probe from the vendored binary with exit 0.

---

## 2026-10-01 · 17. Commit Phase 0 and the vendored binaries

**Prompt**
> commit it

**Actions**
- Committed on `main` in two commits:
  - the vendored `rockets` binaries, with the executable bit set on the macOS and Linux builds
  - Phase 0: the skeleton, the capture tool and its tests, the probe script, the plan and doc updates, and diary entries 15–17

---

## 2026-10-01 · 18. Phase 1: domain model and ordering

**Prompt**
> start phase 1

**Actions** (test-first, as the plan prescribes)
1. **Stubs:** wrote the public shape of the domain as stubs that throw `NotImplementedException`:
   - message records (one payload record per message type, plus `UnknownMessage`)
   - `MessageParser`
   - `RocketState`
   - `RocketLedger`, with the outcomes `Duplicate` / `Accepted` / `Advanced`
2. **Tests:** added `tests/Rockets.Domain.Tests` with 242 tests:
   - **Parser:** the example from the brief, every message type, unknown types, the payload hash, and 18 invalid inputs, each of which must name the problem field.
   - **`RocketState`:** each message type, speed changes before launch, explosion is permanent, a later launch doesn't revive an exploded rocket, unknown messages change nothing, and overflow throws.
   - **`RocketLedger`:** advancing the checkpoint, pending messages above a gap, filling a gap, messages arriving before the launch, duplicates below the checkpoint and in pending (with a payload-mismatch flag), an unknown type filling a gap, the wrong channel, and a failed apply leaving the ledger unchanged.
   - **Property tests:** 200 seeds, each shuffling a generated sequence of up to 300 messages and redelivering about 20% of them. After every delivery, the ledger is checked against a reference that applies all distinct messages received so far in order. There is also a test where messages arrive in reverse order.
3. **Red:** 241 of 242 failed against the stubs. The one that passed checks the initial state, which was already written.
4. **Implementation:**
   - `MessageParser` reads the envelope with `JsonDocument`, then picks a payload reader for that message type.
   - `RocketState.Apply` uses `with` expressions and checked arithmetic.
   - `RocketLedger.Apply` uses an `ImmutableSortedDictionary` for pending messages, advances the checkpoint while the next number is pending, and recomputes the current state in O(number pending).
5. **Green:** all 256 tests passed (242 domain and 14 capture), with 0 build warnings.
6. **Mutation check** (added because everything passed on the first run): four bugs were planted one at a time, and the files were restored afterwards. Failing tests per bug:
   - the current state ignores pending messages: 202
   - pending messages applied in reverse order: 198
   - a message at the checkpoint isn't treated as a duplicate: 39
   - a speed decrease adds instead of subtracting: 3, all in `RocketStateTests`, as designed, because the property tests use `Apply` in their reference calculation

**Decisions** (in `docs/decision-log.md`; the developer can override them in review)
- Unknown message types fill their place in the sequence, so they don't stop the checkpoint.
- The payload hash ignores `messageTime` and formatting.
- `by` and `launchSpeed` must be non-negative.
- Speed isn't clamped at 0.
- A later `RocketLaunched` doesn't revive an exploded rocket.

**Updated docs:** ticked Phase 1 in the plan (plus the added mutation check), added the Phase 1 section to the decision log, and described the domain in `CLAUDE.md`. Not committed yet, so the phase can be reviewed first.

---

## 2026-10-01 · 19. Commit Phase 1

**Prompt**
> commit it

**Actions**
- Committed Phase 1 on `main`: the domain model, its tests, the doc updates, and diary entries 18–19.

---

## 2026-10-01 · 20. Phase 2: storage and the SQLite store

**Prompt**
> start phase 2

**Actions** (test-first)
1. **Contract:** defined the storage contract in `Rockets.Application/Storage/IMessageStore`:
   - `InitializeAsync`
   - `CommitAsync(messages, rejections)`, which returns the duplicates with a payload-mismatch flag
   - `ReadAllAsync`, `ReadChannelAsync` and `ReadRejectedAsync`
2. **Tests, then stubs:** wrote 17 new tests against stubs:
   - 4 domain tests for `MessageParser.FromStored`, which rebuilds a message from its stored columns
   - an abstract contract-test suite (10 tests) that every store must pass, with a SQLite subclass on a temporary file
   - 3 SQLite-specific tests: data survives reopening the file, and connections use WAL and the configured `synchronous` level
3. **Red:** all 17 new tests failed, and the 256 existing ones passed.
4. **Analyzer fix:** the xUnit analyzer (xUnit1051, which wants a cancellation token in every async call) failed the build, because warnings count as errors. I moved the shared test setup into `tests/Directory.Build.props` and turned that rule off there, explaining why in a comment.
5. **Implementation:**
   - `MessageParser.FromStored` shares a payload builder with `Parse`, so a stored message goes through the same validation as on arrival.
   - `SqliteMessageStore` uses plain Microsoft.Data.Sqlite, with WAL and a `synchronous` pragma on every pooled connection.
   - A commit writes everything in one transaction, with a prepared `INSERT … ON CONFLICT DO NOTHING`. When the insert hits an existing row, it compares payload hashes.
   - `messages` is a `WITHOUT ROWID` table keyed on `(channel, message_number)`.
6. **Green:** all 273 tests passed, with 0 warnings.
7. **Mutation check:** three bugs were planted one at a time, and the store was restored afterwards. Storage tests failing per bug (of 13):
   - last write wins on a conflict: 3
   - a content change is never flagged: 1
   - a failing insert is skipped and the rest of the batch committed: 1
   
   A fourth attempt, committing every row on its own by removing the transaction, didn't compile because of the nullable checks, so the third bug replaced it.
8. **Benchmark tool** `tools/Rockets.StoreBenchmark`, run in Release. The first run printed numbers in Danish format, so it now uses invariant formatting.
   - **FULL:** about 1,750 commits per second (0.57 ms each) at batch sizes 1 and 3, and 24k messages per second at batch size 20.
   - **NORMAL:** 45k commits per second at batch size 1.
   - **Replay:** reading 100k messages and applying them to ledgers takes about 0.2 s.
   - **Default grading run, estimated:** 19–57 s with FULL, about 2 s with NORMAL.

**Decision raised with the developer:** the `synchronous` level. I offered FULL (a 2xx means the message survives a power cut; the run takes 19–57 s) or NORMAL (about 2 s; survives a process crash only), and recommended FULL. The developer chose **FULL by default, with NORMAL configurable**.

**Other decisions** (in the decision log; the developer can override them in review)
- Rejections are committed in the same transaction as the batch, instead of through a separate `RejectAsync`, so the single writer does every write.
- Plain ADO.NET instead of Dapper.
- `received_at` comes from an injected `TimeProvider`.

**Updated docs:** ticked Phase 2 in the plan and updated §2.3 and §2.5 there (the interface, Dapper removed, FULL chosen, the replay timing), added the Phase 2 section to the decision log with the benchmark table, and updated the architecture and commands in `CLAUDE.md`. Not committed yet, so the phase can be reviewed first.

---

## 2026-10-01 · 21. Commit Phase 2

**Prompt**
> commit it

**Actions**
- Committed Phase 2 on `main`: the storage contract, the SQLite store, the contract tests, the benchmark tool, the doc updates, and diary entries 20–21.
