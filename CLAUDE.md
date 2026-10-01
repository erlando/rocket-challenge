# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

The repository is empty apart from a .NET `.gitignore`, so the solution is expected to be written in C#/.NET. No projects, build commands or tests exist yet. Update this file with the real build, test and run commands, and the architecture, once they exist.

## What this repo is for

This is a solution to Lunar's backend engineer challenge "Rockets". The challenge brief and the test program live outside the repo, in `../lunar-backend-engineer-challenge/`. `README.md` there is the source of truth for requirements.

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

The test program is in `../lunar-backend-engineer-challenge/windows_amd64/rockets.exe` (other OS/arch builds are next to it):

```
../lunar-backend-engineer-challenge/windows_amd64/rockets.exe launch "http://localhost:8088/messages" --message-delay=500ms --concurrency-level=1
```

Lunar will grade the solution by running the test program **with its default flag values**, so the service must listen on `http://localhost:8088/messages`. Use `rockets.exe launch --help` to see the flags, such as higher concurrency for stress testing.

## Assessment context

Grading looks at design trade-offs (explained), maintainability, concurrency and persistence choices, error handling, and verification through automated tests. The way AI was used is also assessed explicitly: delegation should be deliberate, with human ownership of the design. So work in small, reviewable increments. Raise design decisions such as storage, ordering and buffering strategy, or the API shape with the user instead of deciding them silently. Back changes with tests.

## Dev diary

For every user prompt, append an entry to `docs/devdiary.md` in the same turn as the work. Each entry has a numbered heading with the date and a title, the prompt quoted verbatim, and a summary of findings and actions, including decisions raised with the user and any verification. Follow the format of the existing entries.
