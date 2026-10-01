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
