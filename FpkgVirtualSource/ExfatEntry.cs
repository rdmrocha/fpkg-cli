namespace FpkgVirtualSource;

/// <summary>One file or directory parsed from an exFAT directory.</summary>
public sealed class ExfatEntry
{
    public required string Name { get; init; }
    /// <summary>POSIX-style path relative to the volume root, no leading slash.</summary>
    public required string RelativePath { get; init; }
    public required bool IsDirectory { get; init; }
    /// <summary>First cluster of the entry's data; 0 when empty.</summary>
    public required int FirstCluster { get; init; }
    public required long Length { get; init; }
    /// <summary>True when the allocation is contiguous and the FAT need not be consulted.</summary>
    public required bool NoFatChain { get; init; }
    public List<ExfatEntry> Children { get; } = [];
}
