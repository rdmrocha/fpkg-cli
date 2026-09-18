using Xunit;

// These tests redirect Console.Out to capture a command's stage output, and Console.Out is process
// wide: with xUnit's default per-class parallelism another class's command run writes into the
// capturing writer and the assertions read a mix of two runs. It is a real race that only shows up
// sometimes — it surfaced the moment PackageRegions.Load stopped reading the payload and the runs
// got short enough to overlap. The suite is I/O bound on one ~660 MB package anyway, so there is
// little parallelism to give up.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
