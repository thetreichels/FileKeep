using Xunit;

// The suite leans on process-wide state — the USENETBACKUP_PASSPHRASE
// environment variable (set/restored per test class), statics like
// RestoreSession.IdleTimeout, and per-class temp trees. Under
// `dotnet test`'s default parallel-across-classes execution those
// leak between classes: a RestoreSession test asserting "no
// passphrase available" raced DashboardTests' constructor setting the
// env var process-wide and read another class's fixture value. The
// sandbox reflection runner is sequential and never showed it.
// Serial execution matches the suite's design assumptions (~30 s).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
