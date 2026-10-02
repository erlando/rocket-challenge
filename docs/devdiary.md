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

---

## 2026-10-01 · 22. Phase 3: asking about the API shape

**Prompt**
> start phase 3

**Actions**
- Before building the endpoints, asked the developer three questions, with mock-ups of each option: the JSON shape of a rocket, the shape of the list response, and whether to filter by status.
- The developer chose the **nested `sequence` object** for a rocket, and asked to discuss the list shape before deciding.
- *Logged late:* this entry and entry 23 were written during the Phase 3 work instead of in their own turns, which broke the diary rule. They are backfilled in order.

---

## 2026-10-01 · 23. Envelope vs bare array for the list

**Prompt**
> What would be pros and cons of the list point returning an envelope vs the bare array

**Findings (presented to the developer)**
- **Envelope.** Pros:
  - new fields such as paging or an `asOf` timestamp can be added without breaking clients
  - echoing the sort makes the defaults visible
  - it matches the Microsoft, Google and JSON:API conventions
  
  Cons: it's more verbose, the echo is partly redundant, and paging isn't needed for 20 rockets.
- **Bare array.** Pros: it's the simplest shape, and paging can still be added through headers (`Link`, `X-Total-Count`), as the GitHub API does. Cons: adding anything to the body later is a breaking change, and a browser dashboard can only read custom headers if the server exposes them.
- **Recommendation:** an envelope, possibly slimmed down to `{ count, rockets }`.

---

## 2026-10-01 · 24. API decided; Phase 3 implemented

**Prompt**
> let's go with the envelope without the echo. No filtering right now. Let's continue

**Decisions (the developer's)**
- The list is `{ "count": n, "rockets": [...] }`, without echoing the sort.
- No filtering for now.
- Sorting uses `sortBy` = channel (default) | type | mission | speed | status | launchedAt | updatedAt, and `order` = asc (default) | desc. Empty values sort last in either order, ties are broken by channel, and an unknown value gets 400.

**Actions** (test-first)
1. **Application layer:**
   - `RocketSnapshot` and `RocketRegistry`, for lock-free reads
   - `IngestionOptions`, `IngestionOutcome`, `IngestionUnavailableException` and `IngestionStats`
   - `IngestionPipeline`, written as a stub first
2. **Application tests:** 13 tests using a `FakeMessageStore` that can hold, fail or commit-then-throw commits. All 13 failed against the stub, as expected.
3. **Implementation:**
   - a bounded channel feeding the single writer loop
   - each batch applied to working copies, then committed, then published and completed
   - errors kept to the message that caused them (rejected with 2xx)
   - after a storage error, the affected rockets are reloaded before anyone is answered; rockets that stay stale refuse messages
   - enqueueing times out after 5 s, `RequestAborted` cancels only the enqueue, and shutdown drains the queue
   
   All 13 tests passed.
4. **Extra test:** a review of the tests found nothing exercising stale rockets refusing messages, so a test was added. That makes 14.
5. **API tests:** 16 tests using `WebApplicationFactory` on a temporary database, all failing at first. Then implemented the endpoints, `RocketResponse` / `SequenceResponse` / `RocketListResponse`, `RocketSorting`, `IngestionHostedService` (replay on start, drain on stop), and choosing the store through `Storage:Provider`. All 16 passed.
6. **Flakiness:** 10 repeated runs of both suites were all green.
7. **Mutation check:** six planted pipeline bugs were all caught. Two of them first failed to compile under the warnings-as-errors checks and were rewritten.
8. **Real run against `rockets.exe` with defaults:**
   - **First attempt failed.** The service listened on port 5000: started from the repo root, it didn't find `appsettings.json` in the working directory. `rockets.exe` retried refused connections 3.6 million times in 10 minutes without stopping. Also, my wait-for-the-service loop didn't fail when the service never came up.
   - **Fix:** the content root is now the app's own folder, so the configuration always loads. The database path is logged at startup, and the wait loop now uses `curl -sf` and stops on failure.
   - **Second attempt passed:** 100,000 messages stored in **38.5 s** with `synchronous=FULL`, inside the estimate of 19–57 s. There were 0 duplicates, rejections or storage failures, all 20 rockets were complete, and 6 exploded, matching Phase 0. Logging was down to 14 lines with no per-request lines.
   - **Restart:** after a hard stop, all 100,000 messages were replayed, and the rocket list was byte-for-byte identical.

**Verification:** 303 tests passed with 0 warnings: 14 capture, 246 domain, 13 storage, 14 application and 16 API.

**Updated docs:** the plan (the API shape in §2.6, Phase 3 ticked with the extra checks), the Phase 3 section of the decision log, and `CLAUDE.md` (the architecture and how to run the service). Not committed yet, so the phase can be reviewed first.

---

## 2026-10-01 · 25. Commit Phase 3

**Prompt**
> commit it

**Actions**
- Committed Phase 3 on `main`: the ingestion pipeline, the API, their tests, the doc updates, and diary entries 22–25.

---

## 2026-10-01 · 26. Phase 4: end-to-end verification and resilience

**Prompt**
> start phase 4

**Actions**
1. **Oracle** (`tools/Rockets.Capture/Oracle.cs`, new `expect` and `verify` commands). It doesn't reference `Rockets.Domain`.
   - `expect` folds each rocket from a capture with its own simple rules. For each rocket it records the final state, the message count, and a SHA-256 hash of all its messages in order.
   - `verify` compares with `GET /rockets`, and opens the service's SQLite log read-only to check every expected message's content.
   - 5 unit tests.
2. **Deviation from the plan:** the expected-state files (`tests/e2e/expected-seed444-{10000,100000}.json`, about 7 KB each) are committed instead of capturing on every run. A capture on every run would cost an extra 100k run and a wait for client ports. They were generated from fresh seed-444 captures against the capture server.
3. **Checked the checker:** the database from the separate Phase 3 run passed against the 100k expectation, and failed with 124 differences against the 10k one.
4. **`scripts/e2e.ps1`**, which needs pwsh 7. It has four scenarios (quick, default, crash, stress) and a `-RegenerateExpected` switch.
   - It refuses to start if port 8088 is busy, waits for health, and waits for Windows client ports between scenarios.
   - In the crash scenario it hard-kills the service 4 s in and restarts it 3 s later.
   - It picks the binary for the OS and architecture, and fails on an oracle failure, a non-zero exit, or a dropped retry.
   - The first run cut off columns and used Danish number formatting, so the script now uses invariant formatting and also writes `artifacts/e2e/results.json`.
5. **`-Scenario all`:** every scenario passed.

   | Scenario | Messages | Time | Result |
   |---|---:|---:|---|
   | quick | 10k | 5.3 s | PASS |
   | default | 100k | 52.0 s | PASS |
   | crash | 10k | 36.3 s | PASS: 3,201 retries during the outage, 1 duplicate (committed before the kill but not acknowledged), 0 dropped |
   | stress | 100k at concurrency 20 | 48.2 s | PASS |
6. **Looked into why the stress run wasn't faster:**
   - Added a `commits` counter to `IngestionStats` and `/health`, and covered it in a test. The average batch at concurrency 20 is **1.12**.
   - The do-nothing capture server needs **20 s** at concurrency 20, against 3–4 s at concurrency 3, so the test program itself is the limit and its requests barely overlap.
   - The default run with `synchronous=NORMAL` takes **6.5 s** (PASS). So with FULL, the time is almost entirely one disk sync per message, about 0.5 ms each.
   - This corrects the earlier expectation that group commit would help under load: with this client, it can't. FULL stays the default, as the developer decided; this only puts an exact price on it.

**Verification:** `dotnet test` shows 308 tests passing with 0 warnings, and `pwsh ./scripts/e2e.ps1 -Scenario all` passed every scenario.

**Updated docs:** ticked Phase 4 in the plan (with what was actually built and the results), added the Phase 4 section to the decision log (the results table and where the time goes), and added the e2e commands and the oracle's independence rule to `CLAUDE.md`. Not committed yet, so the phase can be reviewed first.

---

## 2026-10-01 · 27. Commit Phase 4

**Prompt**
> commit it

**Actions**
- Committed Phase 4 on `main`: the oracle, the e2e script, the expected-state files, the commit counter, the doc updates, and diary entries 26–27.

---

## 2026-10-01 · 28. Phase 5: documentation

**Prompt**
> start phase 5

**Actions**
- Fetched real API responses for the README examples. The `/health` counters showed 100,000 messages stored since startup, which shouldn't happen right after a restart. They revealed a **stale service** still running from the Phase 4 NORMAL measurement: Git Bash's `kill` hadn't stopped the native `dotnet` process, so the new service couldn't bind port 8088 and exited quietly.
  - Stopped it with `Stop-Process -Force`.
  - Confirmed the Phase 4 results weren't affected (they ran earlier, and `e2e.ps1` refuses a busy port).
  - Changed `scripts/probe.sh` to check the port first and use `kill -9`.
  - The example responses are genuine output from that service (a 100k run), so they were kept.
- Wrote `README.md`:
  - a summary table and the quick start
  - the tests and verification commands
  - the API, with real responses
  - how it works, with a diagram, the ordering model, durability and acknowledgements, and concurrency
  - a decision table, with alternatives and who decided
  - the facts measured in Phase 0, and the verification layers with the e2e results
  - known limitations, scaling, and the project layout
  - how AI was used: the workflow, the developer's decisions, what was delegated, how quality was kept, and where the AI went wrong
- Corrected one README claim during review: the process that held the port came from an ad-hoc command, not from a repo script.
- **Verified the README by following it** in a fresh `git clone` in a temporary folder (with the uncommitted README copied in):
  - `dotnet test`: 308 passed
  - the quick start (`dotnet run -c Release`, `rockets.exe` with its defaults, `curl`): exit 0 in 42.2 s, 100,000 messages stored, the database where the README says it is, and nothing left listening afterwards
  - `pwsh ./scripts/e2e.ps1`: PASS
  - The clone was deleted afterwards.
- Ticked Phase 5 in the plan, added the Phase 5 section to the decision log, and updated `CLAUDE.md` (the phase status, and keeping the README in step).

**Open:** Phase 6 (the Postgres store) is optional; the plan does it only if more than 1 h of the budget remains. That's the developer's call. Not committed yet, so the phase can be reviewed first.

---

## 2026-10-01 · 29. The README diagram as a Mermaid sequence diagram

**Prompt**
> I would like the "diagram" under "How it works" in README.md to be a mermaid sequence diagram

**Actions**
- Replaced the ASCII diagram in "How it works" with a Mermaid `sequenceDiagram`, which GitHub renders natively. Its participants are the test program, `POST /messages`, the bounded queue, the single writer loop, the SQLite message log, `RocketRegistry` and a dashboard. It shows:
  - enqueueing, including the 503 branch when the queue stays full or the service is shutting down
  - the per-message apply loop, with the rejection note
  - the single commit transaction
  - success (publish, then complete, then 204) against a storage error (reload, then 503)
  - the lock-free read path

**Verification:** rendered the diagram with the Mermaid CLI (`npx @mermaid-js/mermaid-cli`, a one-off into `artifacts/mermaid/`, nothing added to the repo). It rendered without errors, and the image was inspected. Not committed yet, together with the rest of Phase 5.

---

## 2026-10-01 · 30. A component flowchart in the README

**Prompt**
> please add a flowchart above the sequence diagram showing how the individual components of the solution relate to each other

**Actions**
- Added a Mermaid `flowchart TB` above the sequence diagram in "How it works":
  - one box per project (Api, Application, Domain, Storage.Sqlite, Verification tooling), each holding its main components
  - the external clients (the test program, a dashboard)
  - the SQLite file
  - the planned Postgres store
  - solid arrows for calls and data flow, and dashed lines for the storage interface's implementations and the verification tooling
- Added a sentence before each diagram saying what it shows.
- **First render:** the verification tooling and the SQLite store ended up at the top, because Mermaid places an arrow's source above its target. Reversed those arrows ("implemented by", "checked by", "read by").
- **Second render:** the flow reads top to bottom (clients, then Api, Application, then Domain and storage, then the database, with verification underneath).

**Verification:** both renders were made with the Mermaid CLI into `artifacts/mermaid/`, with nothing added to the repo, and the images were inspected. Not committed yet, together with the rest of Phase 5.

---

## 2026-10-01 · 31. Commit Phase 5

**Prompt**
> commit it

**Actions**
- Committed Phase 5 on `main`: `README.md` (with the component flowchart and the sequence diagram), the `scripts/probe.sh` port check and forced kill, the doc updates, and diary entries 28–31.
- Still open: whether to do Phase 6 (the Postgres store).

---

## 2026-10-01 · 32. Phase 6: Postgres store

**Prompt**
> go ahead with phase 6

**Actions** (test-first)
1. **Docker.** Docker Desktop wasn't running (no engine pipe), so I started it, telling the developer, and stopped it again at the end.
2. **Stub and tests.** Added `src/Rockets.Storage.Postgres` (Npgsql 10.0.3) as a stub, and `PostgresMessageStoreTests`, which runs the existing contract suite against a Testcontainers `postgres:18-alpine` (Testcontainers.PostgreSql 4.15.0). It uses one container per class and a fresh database per test, and skips the tests with the reason when Docker isn't available. Red: 10 of 10 failed against the stub.
3. **Implementation:**
   - `PostgresMessageStore`, structured like the SQLite store
   - `message_time` as round-trip `TEXT`, `payload_json` as `TEXT` (not `jsonb`), `received_at` as `timestamptz` (in UTC)
   - reads ordered with `COLLATE "C"`
   
   Green: 23 of 23 storage tests (10 Postgres, 13 SQLite).
4. **Wiring:** `Storage:Provider=Postgres` with `Storage:ConnectionString` in the composition root, which logs the host and database but no password. Added `docker-compose.yml` (Postgres 18 with a health check; the data is under `/var/lib/postgresql`, as the 18 images expect).
5. **Oracle:** `verify` now also accepts a Postgres connection string as its database argument.
6. **End to end on Postgres:**
   - `docker compose up`, the Release service with Provider=Postgres, and the default 100k run: exit 0 in 118.3 s, 100,000 stored, oracle PASS
   - after a restart: 100,000 messages replayed, answering after 1.1 s, oracle PASS
   - `docker compose down -v` afterwards
7. **The skip path:**
   - Pointing `DOCKER_HOST` at a dead address didn't simulate a missing Docker, because Testcontainers fell back to Docker Desktop's pipe and ran the tests anyway.
   - Stopped Docker Desktop with `docker desktop stop`: 13 passed and 10 were skipped, with the reason "Postgres contract tests need Docker, which is not available".
   - The whole `dotnet test` exits 0, with 318 total: 308 passed and 10 skipped.
8. **README:**
   - the intro, and a "Running on Postgres" section
   - the test counts, and the storage rows in the decision and verification tables
   - an e2e row for Postgres, the limitations (one instance, Postgres being slower) and the scaling step
   - the project layout
   - the flowchart: `PostgresMessageStore` in its own box with a Postgres database, read by the oracle
   
   The flowchart was rendered twice with the Mermaid CLI and inspected.

**Regression check:** `pwsh ./scripts/e2e.ps1 -Scenario quick` on SQLite still passes, since the oracle's database code and the composition root changed.

**Updated docs:** ticked Phase 6 in the plan (with the extra checks), added the Phase 6 section to the decision log, and updated `CLAUDE.md`. Not committed yet, so the phase can be reviewed first.

---

## 2026-10-01 · 33. Commit Phase 6

**Prompt**
> commit it

**Actions**
- Committed Phase 6 on `main`: the Postgres store, its Testcontainers contract tests, `docker-compose.yml`, the Postgres provider in the composition root, the oracle reading Postgres, the README and doc updates, and diary entries 32–33. Every phase in the implementation plan (0–6) is now done.

---

## 2026-10-02 · 34. Standalone executable

**Prompt**
> There needs to be an easier way for Lunar to run the program instead of using the dotnet SDK. Make a standalone executable available

**Findings**
- Tried a self-contained, single-file, compressed `dotnet publish` of `Rockets.Api` for `win-x64` and `linux-x64` (into the scratchpad, not the repo). Both build without warnings and need no .NET on the target machine, but each executable is about 52 MB.
- `appsettings.json` is published next to the executable rather than inside it. Without it the service would fall back to port 5000, so the file has to travel with the executable, or the defaults have to be built in.
- `vendor/rockets` covers six platforms (macOS, Linux and Windows on amd64/arm64, plus windows_386).

**Decisions raised with the developer**
- How Lunar gets the executables (a publish script producing zips for a release or the submission, committing them in the repo, or a Docker image instead), and which platforms to build. The developer wanted to clarify the questions before answering, so nothing is built yet.
- **Outcome:** in the next prompt, the developer decided the current deployment is fine and asked for a clean-start option instead (entry 35). No executable was built or committed.

---

## 2026-10-02 · 35. Clean database on start

**Prompt**
> The current deployment is fine. But there needs to be an option for starting the program with a clean database. Right now the user needs to delete the database file.

**Decisions** (small enough to decide and report; the reasons are in the decision log under "After Phase 6")
- Added the option `--Storage:ResetOnStart=true`, spelled like the existing `Storage:` settings. It also works as the environment variable `Storage__ResetOnStart`.
- Added `IMessageStore.ClearAsync`, which deletes rows in one transaction instead of deleting the file, so it works the same for SQLite and Postgres.
- The service clears the log at startup, before the replay. It is not an HTTP endpoint, which would race with the writer and be destructive without authentication.

**Actions**
- **Test first:** three new store contract tests (clear removes messages and rejections and returns the count; a cleared store takes the same messages again without reporting them as duplicates; clearing an empty store deletes 0), plus an API test that restarts with the option, posts again, and restarts without it. They all failed against stubs.
- **Implemented:** `ClearAsync` in the SQLite and Postgres stores and the in-memory test double, and the reset in `IngestionHostedService`, which logs a warning with the number of messages deleted.
- **Updated docs:** the README (a "Starting clean" note in the quick start, and the test and mutation counts), `CLAUDE.md` and the decision log.

**Verification**
- `dotnet test`: 325 tests pass.
- With Docker Desktop started for the run, all 29 storage contract tests pass on SQLite and Postgres. Docker Desktop was stopped again afterwards.
- Four planted bugs were each caught.
- A smoke test of the Release service on a temporary database showed one rocket, then none after `--Storage:ResetOnStart=true` (the warning reported 1 deleted message), then still none after a plain restart.
- Not committed yet.

---

## 2026-10-02 · 36. Clarify the clean-start note in the README

**Prompt**
> The "Starting clean" note in the readme is unclear. Please clarify

**Actions**
- Replaced the one-line bullet in the quick start with its own subsection, "Starting with a clean database", placed before "Running on Postgres". It covers:
  - why you would want a clean start: the data stays between runs, so `GET /rockets` mixes old and new rockets
  - the full command to copy, with `--` already in place, instead of a note to add it after `--`
  - what happens, in order: everything is deleted before any requests are accepted, a warning is logged, and the service starts with no rockets
  - that the option applies only to the start it is given on, and that it works for SQLite and Postgres
- The wording "Leave it off on later restarts, or they start clean too" was confusing, so it is now "Start the service without it to keep the data as usual".
- Only documentation changed. Not committed yet.

---

## 2026-10-02 · 37. Swap two README sections

**Prompt**
> Switch the "Running on Postgres" and "Starting with a clean database" sections

**Actions**
- Swapped the two subsections in the README's quick start, so "Running on Postgres" now comes before "Starting with a clean database". Their text is unchanged.
- Not committed yet.

---

## 2026-10-02 · 38. Commit the clean-start option

**Prompt**
> commit it

**Actions**
- Committed on `main`: `--Storage:ResetOnStart=true` and `IMessageStore.ClearAsync` (both stores), the new contract and API tests, the README section "Starting with a clean database", the decision log and `CLAUDE.md` updates, and diary entries 34–38.
