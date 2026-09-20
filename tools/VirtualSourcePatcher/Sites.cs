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

            // Site 10. WriteBlock branches on the decision, which takes the caller's own
            // `deduplicate` and can only ever turn true into false; EncodeBlock ANDs the
            // cache predicate into an existing conjunction.
            var filler = Type(shim, "FpkgVirtualSource.PlayGoFillerDedup");
            FillerDisableDedup = importer.Import(
                Method(filler, "ShouldDisableDedup", "System.Boolean", 2));
            FillerCacheAllowed = importer.Import(
                Method(filler, "CompressionCacheAllowed", "System.Boolean", 1));

            // Site 11. The physical-placement cursor is rounded up to a 64 KiB boundary before a
            // filler, and once more after the last one; the same exact-path allow-list decides.
            FillerBlockAlign = importer.Import(
                Method(filler, "ShouldBlockAlign", "System.Boolean", 1));

            // Sites 13 and 14. Both CNT-level parity corrections, both inert unless the CLI
            // switches them on. Adjust runs on every profile Resolve returns and can only ever
            // clear the encryption bit on five specific ids; SkipRightSprx gates a void method.
            var cnt = Type(shim, "FpkgVirtualSource.CntEntryPolicy");
            CntAdjustFlags1 = importer.Import(
                Method(cnt, "AdjustFlags1", "System.UInt32", 3));
            CntSkipRightSprx = importer.Import(
                Method(cnt, "ShouldSkipRightSprx", "System.Boolean", 0));

            // Site 15. The AFID candidate list is reordered in place right after BuildCore
            // finishes collecting it, so the fillers are placed last.
            var order = Type(shim, "FpkgVirtualSource.PlayGoFillerOrder");
            FillerMoveLast = importer.Import(
                Method(order, "MoveFillersLast", "System.Void", 1));
        }

        internal IMethod IsVirtual { get; }
        internal IMethod Open { get; }
        internal IMethod OpenOrFile { get; }
        internal IMethod Enumerate { get; }
        internal IMethod TreeOverlay { get; }
        internal IMethod FillerDisableDedup { get; }
        internal IMethod FillerCacheAllowed { get; }
        internal IMethod FillerBlockAlign { get; }
        internal IMethod CntAdjustFlags1 { get; }
        internal IMethod CntSkipRightSprx { get; }
        internal IMethod FillerMoveLast { get; }
    }
}
