using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace FpkgVirtualSource;

/// <summary>Process-wide handle to container map. Written by the CLI, read by patched IL.</summary>
public static class ContainerRegistry
{
    private static readonly ConcurrentDictionary<string, Container> Open = new(StringComparer.Ordinal);

    public static string Register(Container container)
    {
        ArgumentNullException.ThrowIfNull(container);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            if (Open.TryAdd(handle, container)) return handle;
        }
        throw new InvalidOperationException("could not allocate a container handle");
    }

    public static Container Get(string handle) =>
        Open.TryGetValue(handle, out var c)
            ? c
            : throw new InvalidOperationException(
                $"no source container is registered for handle '{handle}'. " +
                "The virtual-source patch resolves paths through a container opened by the CLI.");

    public static void Release(string handle)
    {
        if (Open.TryRemove(handle, out var c)) c.Dispose();
    }
}
