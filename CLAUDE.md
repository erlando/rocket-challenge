# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status and commands

Work follows the phases in `docs/implementation-plan.md` (Phases 0–2 are done). Record decisions per phase in `docs/decision-log.md`. Update this file as the architecture takes shape.

Architecture so far:
- `src/Rockets.Domain` is pure code with no IO.
  - `Messages/MessageParser` validates an envelope and reads its payload according to `messageType`. Unknown types become `UnknownMessage`.
  - `RocketState.Apply` applies one message, and callers must apply messages in messageNumber order.
  - `RocketLedger` is the immutable ordering model for one rocket. It holds a checkpoint (the exact state for messages 1..N), the pending messages above N, and the current state (the checkpoint plus pending, applied in order).
  - Domain changes must keep `RocketLedgerPropertyTests` green. Those seeded shuffle-and-redelivery runs check the ledger after every step.
- `src/Rockets.Application/Storage/IMessageStore` is the storage seam: an append-only message log, which is the only stored state.
  - `CommitAsync(messages, rejections)` writes one atomic batch and reports duplicates, with a payload-hash mismatch flag.
  - Reads stream messages per channel in messageNumber order.
- `src/Rockets.Storage.Sqlite` is the only place with SQL. It uses WAL, with `synchronous=FULL` by default (the developer's choice; `NORMAL` is configurable).
- Every store must pass `tests/Rockets.Storage.Tests/MessageStoreContractTests`. A new store subclasses it.

- Build: `dotnet build` (.NET 10 SDK, warnings are errors, package versions in `Directory.Packages.props`)
- Test: `dotnet test`. xUnit v3 runs on Microsoft.Testing.Platform, which `global.json` opts into, so VSTest options don't apply. Shared test-project settings are in `tests/Directory.Build.props`.
- Run a single test: `dotnet test --project tests/<Project> --filter-method "*Name*"` (also `--filter-class`)
- Run the service: `dotnet run --project src/Rockets.Api`, which listens on http://localhost:8088.
- Store benchmark: `dotnet run -c Release --project tools/Rockets.StoreBenchmark`, which measures commit throughput per synchronous level and batch size, plus replay time.
- Capture and probe tool: `tools/Rockets.Capture`, with the commands `serve`, `analyze <file>` and `compare <a> <b>`.
  - `scripts/probe.sh <name> <timeout> [rockets args] -- [server args]` runs the test program against the capture server and prints the analysis.
  - Captures go in `artifacts/`, which is git-ignored.

The test program opens a new connection for every message. On Windows, two back-to-back 100k runs use up the client ports for about a minute (TIME_WAIT), so wait between large runs.

## What this repo is for

This is a solution to Lunar's backend engineer challenge "Rockets". The challenge brief is in `docs/CHALLENGE.md` and is the source of truth for requirements. It is an exact copy of the README that came with the challenge. The repo must stay self-contained: never reference files outside it.

The service consumes JSON messages that rockets POST to it and exposes rocket state through a REST API meant for a dashboard. The minimum requirements are:
- `POST /messages`: the ingestion endpoint the test program posts to.
- An endpoint that returns the current state of one rocket (type, speed, mission, status/explosion reason).
- An endpoint that lists all rockets, preferably with sorting.

### Message semantics (the core correctness problem)

- `metadata.channel` is unique per rocket and serves as the rocket ID.
- `metadata.messageNumber` gives the order within a channel. Messages arrive **out of order** and are delivered **at least once**, so duplicates happen.
- Any non-2xx response causes the message to be **redelivered**. Return 2xx only after the message has been accepted durably or idempotently. A duplicate must also get 2xx, otherwise it is redelivered forever.
- Message types and their payloads:
  - `RocketLaunched`: `{type, launchSpeed, mission}`. Sent once.
  - `RocketSpeedIncreased`: `{by}`
  - `RocketSpeedDecreased`: `{by}`
  - `RocketMissionChanged`: `{newMission}`
  - `RocketExploded`: `{reason}`. Sent once.
- Speed changes are deltas, so state depends on applying messages in `messageNumber` order without gaps or double-counting. A message may arrive before the `RocketLaunched` for its channel.

## Running the test program

The test program is vendored in `vendor/rockets/<os>_<arch>/` (Windows: `vendor/rockets/windows_amd64/rockets.exe`):

```
vendor/rockets/windows_amd64/rockets.exe launch "http://localhost:8088/messages" --message-delay=500ms --concurrency-level=1
```

Lunar will grade the solution by running the test program **with its default flag values**, so the service must listen on `http://localhost:8088/messages`. Use `rockets.exe launch --help` to see the flags, such as higher concurrency for stress testing.

## Assessment context

Grading looks at design trade-offs (explained), maintainability, concurrency and persistence choices, error handling, and verification through automated tests. The way AI was used is also assessed explicitly: delegation should be deliberate, with human ownership of the design. So work in small, reviewable increments. Raise design decisions such as storage, ordering and buffering strategy, or the API shape with the user instead of deciding them silently. Back changes with tests.

## Dev diary

For every user prompt, append an entry to `docs/devdiary.md` in the same turn as the work. Each entry has a numbered heading with the date and a title, the prompt quoted verbatim, and a summary of findings and actions, including decisions raised with the user and any verification. Follow the format of the existing entries.
