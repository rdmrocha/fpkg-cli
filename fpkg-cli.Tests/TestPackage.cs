internal static class TestPackage
{
    internal const string FileName = "Terminator.2D.NO.FATE.PPSA25872.v1.2.0000.pkg";

    /// <summary>
    /// The package's content id, as reported by <c>ProsperoPkgReader.Read(Path).Header.ContentId</c>.
    /// Hard-coded here rather than read live because nothing else in this project needs a live
    /// read; TestPackageTests.ReadsTheContentId asserts it back against the header, so a stale
    /// value here would fail loudly rather than drift silently.
    /// </summary>
    internal const string ContentId = "EP4060-PPSA25872_00-T2DNFMAINGAMEPS5";

    /// <summary>The folder holding LibProsperoPkg.dll, delegated to ReleaseFolder so the walk-up
    /// lives in one place in this project.</summary>
    internal static string RepoRoot { get; } = ReleaseFolder.Find();

    /// <summary>
    /// The package's expected path under RepoRoot, computed unconditionally whether or not the
    /// file actually exists there. Callers must guard with <see cref="Exists"/> (typically via
    /// <c>Skip.IfNot(TestPackage.Exists, ...)</c>) before relying on the file being present.
    /// </summary>
    internal static string Path => System.IO.Path.Combine(RepoRoot, FileName);

    internal static bool Exists => File.Exists(Path);
}
