using Rockets.Capture;

// Developer tool for observing what the rockets test program actually sends,
// and for checking the service end to end against a capture (the oracle: expect + verify).
//   serve   [--Capture:Path=<file>] [--Probe:FirstAttemptStatus=<code>] [--Probe:FirstAttemptDelayMs=<ms>] [--Urls=<url>]
//   analyze <capture.ndjson>
//   compare <a.ndjson> <b.ndjson>
//   expect  <capture.ndjson> <expected.json>
//   verify  <expected.json> <service url> <SQLite database path | Postgres connection string>
return args.FirstOrDefault() switch
{
    "serve" => await CaptureServer.RunAsync(args[1..]),
    "analyze" when args.Length == 2 => CaptureAnalyzer.Run(args[1]),
    "compare" when args.Length == 3 => CaptureComparer.Run(args[1], args[2]),
    "expect" when args.Length == 3 => Oracle.RunExpect(args[1], args[2]),
    "verify" when args.Length == 4 => await Oracle.RunVerifyAsync(args[1], args[2], args[3]),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        Usage:
          serve   [--Capture:Path=<file>] [--Probe:FirstAttemptStatus=<code>] [--Probe:FirstAttemptDelayMs=<ms>] [--Urls=<url>]
          analyze <capture.ndjson>
          compare <a.ndjson> <b.ndjson>
          expect  <capture.ndjson> <expected.json>
          verify  <expected.json> <service url> <SQLite database path | Postgres connection string>
        """);
    return 2;
}
