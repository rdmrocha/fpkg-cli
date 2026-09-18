internal static class ReleaseFolder
{
    /// <summary>Walks up looking for the folder that holds LibProsperoPkg.dll.</summary>
    internal static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "LibProsperoPkg.dll")))
                return dir.FullName;
        throw new InvalidOperationException("LibProsperoPkg.dll not found above " + AppContext.BaseDirectory);
    }
}
