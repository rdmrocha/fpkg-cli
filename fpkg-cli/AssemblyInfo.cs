using System.Runtime.CompilerServices;

// InnerList and InnerOffsets are diagnostics, not a public API: the tests reach into them rather
// than driving the CLI through stdout, so a wrong OFFSET fails as a number, not as a text diff.
[assembly: InternalsVisibleTo("fpkg.Tests")]
