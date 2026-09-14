namespace FpkgVirtualSource.Tests;

/// <summary>
/// Persists a <see cref="TestVolume"/> to a temp file and opens it as a real
/// <see cref="Container"/>, for tests that need the full Container/VirtualSource wiring
/// (header sniffing, <see cref="Container.Files"/>, <see cref="Container.FindAppRoot"/>)
/// rather than a bare in-memory <see cref="ExfatReader"/>.
///
/// The temp file is deleted right after <see cref="Container.Open"/> returns: on this
/// platform an already-open file descriptor keeps reading fine after its directory entry is
/// removed, so no on-disk fixture is left behind for the caller to clean up.
/// </summary>
internal static class TestContainer
{
    internal static Container Open(TestVolume volume)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".exfat");
        File.WriteAllBytes(path, volume.Build().Bytes);
        try { return Container.Open(path); }
        finally { File.Delete(path); }
    }
}
