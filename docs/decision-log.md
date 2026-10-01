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
