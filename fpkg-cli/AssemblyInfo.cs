// The repair-playgo split/splice logic lives in `internal` types (Fpkg.Cli.RepairPlayGo) so it
// stays out of the CLI's public surface. The test project needs to see them directly rather
// than going through the CLI's argument parsing, hence this visibility grant. Note the test
// ASSEMBLY name is "fpkg.Tests" (from fpkg-cli.Tests/fpkg.Tests.csproj's AssemblyName), not the
// "fpkg-cli.Tests" directory name.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("fpkg.Tests")]
