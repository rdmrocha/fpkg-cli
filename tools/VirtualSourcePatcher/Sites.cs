using dnlib.DotNet;

namespace VirtualSourcePatcher;

internal static class Sites
{
    internal static void Expect(bool ok, string what)
    {
        if (!ok)
            throw new InvalidOperationException(
                $"shape check failed: {what}. Upstream IL has changed; re-derive the patch " +
                "against the new release before shipping it.");
    }

    internal static TypeDef Type(ModuleDefMD module, string fullName) =>
        module.GetTypes().FirstOrDefault(t => t.FullName == fullName)
        ?? throw new InvalidOperationException($"type not found: {fullName}");

    /// <summary>
    /// Finds a method by shape rather than by name. Compiler-generated local functions are
    /// named like &lt;BuildInnerTree&gt;g__IsSelf|39_34 and the ordinal after the pipe moves
    /// between releases, so only the part before it is matched.
    /// </summary>
    internal static MethodDef Method(TypeDef type, string namePrefix, string returnType, int paramCount)
    {
        var found = type.Methods.Where(m =>
            m.HasBody &&
            m.MethodSig.RetType.FullName == returnType &&
            m.MethodSig.Params.Count == paramCount &&
            (m.Name.String == namePrefix ||
             (m.Name.String.Contains("g__" + namePrefix + "|", StringComparison.Ordinal)))).ToList();

        Expect(found.Count == 1,
            $"{type.Name}.{namePrefix} -> {returnType}/{paramCount} matched {found.Count} methods, expected 1");
        return found[0];
    }

    /// <summary>Imports the FpkgVirtualSource entry points into the target module.</summary>
    internal sealed class Shim
    {
        internal Shim(ModuleDefMD target, string shimPath)
        {
            var shim = ModuleDefMD.Load(shimPath);
            var vs = Type(shim, "FpkgVirtualSource.VirtualSource");
            var importer = new Importer(target);
            IsVirtual  = importer.Import(Method(vs, "IsVirtual", "System.Boolean", 1));
            Open       = importer.Import(Method(vs, "Open", "System.IO.Stream", 1));
            OpenOrFile = importer.Import(Method(vs, "OpenOrFile", "System.IO.Stream", 1));
            Enumerate  = importer.Import(Method(vs, "Enumerate",
                "System.Collections.Generic.IEnumerable`1<FpkgVirtualSource.VirtualEntry>", 1));

            var overlay = Type(shim, "FpkgVirtualSource.TreeOverlay");
            TreeOverlay = importer.Import(Method(overlay, "Populate", "System.Boolean", 2));
        }

        internal IMethod IsVirtual { get; }
        internal IMethod Open { get; }
        internal IMethod OpenOrFile { get; }
        internal IMethod Enumerate { get; }
        internal IMethod TreeOverlay { get; }
    }
}
