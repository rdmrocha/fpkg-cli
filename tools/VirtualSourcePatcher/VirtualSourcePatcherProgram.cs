using System.Security.Cryptography;
using dnlib.DotNet;

namespace VirtualSourcePatcher;

/// <summary>
/// Rewrites the nine LibProsperoPkg IL sites that make <c>ffpfsc:&lt;handle&gt;:/…</c> paths
/// resolve through FpkgVirtualSource, so a container can be used as a build source.
///
/// The body below was the whole of a top-level-statement Program.cs. It is a callable entry
/// point now so that `fpkg patch` can invoke it IN-PROCESS: the distribution zip carries no
/// sources and no .NET SDK, so shelling out to `dotnet run --project` is not available to
/// it. Nothing about the patch itself changed - every assertion, its ordering and every
/// message are as they were.
/// </summary>
public static class VirtualSourcePatcherProgram
{
    /// <summary>
    /// usage: virtual-source-patcher &lt;LibProsperoPkg.dll&gt; &lt;output.dll&gt;, plus the two
    /// read-only <c>--dump</c> modes. Returns the process exit code.
    /// </summary>
    public static int Run(string[] args)
    {
        if (args.Length == 3 && args[0] == "--dump")
        {
            return Dump(Path.GetFullPath(args[1]), args[2]);
        }

        if (args.Length == 3 && args[0] == "--dump-type")
        {
            return DumpType(Path.GetFullPath(args[1]), args[2]);
        }

        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: virtual-source-patcher <LibProsperoPkg.dll> <output.dll>");
            Console.Error.WriteLine("       virtual-source-patcher --dump <LibProsperoPkg.dll> <Type.Method>");
            Console.Error.WriteLine("       virtual-source-patcher --dump-type <LibProsperoPkg.dll> <Type>");
            return 2;
        }

        string source = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        if (!File.Exists(source)) { Console.Error.WriteLine($"error: not found: {source}"); return 1; }

        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));

        ModuleDefMD? module = null;
        try
        {
            module = ModuleDefMD.Load(source);

            // Resolving these up front means an upstream shape change fails before anything is rewritten.
            var builder = Sites.Type(module, "LibProsperoPkg.PKG.ProsperoPkgBuilder");
            var innerFile = Sites.Type(module, "LibProsperoPkg.PFS.ProsperoPs5InnerFile");
            var fsFile = Sites.Type(module, "LibProsperoPkg.PFS.FSFile");
            var napsMeta = Sites.Type(module, "LibProsperoPkg.PKG.ProsperoNapsMeta");
            var plaintextReader = napsMeta.NestedTypes.SingleOrDefault(t => t.Name == "PlaintextBlockReader")
                ?? throw new InvalidOperationException("ProsperoNapsMeta.PlaintextBlockReader not found");

            var openRead = Sites.Method(innerFile, "OpenRead", "System.IO.Stream", 0);
            var populate = Sites.Method(builder, "Populate", "System.Void", 8);
            var isLooseElf = Sites.Method(builder, "IsLooseElf", "System.Boolean", 1);
            var isSelf = Sites.Method(builder, "IsSelf", "System.Boolean", 1);
            var sceVersion = Sites.Method(builder, "GetApplicationSceVersion", "System.Byte[]", 1);
            // Reached only when an SDK override is set. Both halves open the ORIGINAL executable
            // by SourcePath - the probe to find the .sceversion offset, the Write delegate to copy
            // the prefix - so a container source needs both virtualised.
            var sdkSelf = Sites.Method(builder, "BuildSdkOverriddenSelf", "LibProsperoPkg.PFS.FSFile", 2);
            var sdkSelfCopy = builder.NestedTypes
                .SelectMany(t => t.Methods)
                .Where(m => m.HasBody && m.MethodSig.Params.Count == 1 &&
                            m.MethodSig.Params[0].FullName == "System.IO.Stream" &&
                            m.DeclaringType.Fields.Any(f => f.Name == "sourcePath") &&
                            m.DeclaringType.Fields.Any(f => f.Name == "patchOffset") &&
                            m.DeclaringType.Fields.Any(f => f.Name == "patch"))
                .ToList();
            Sites.Expect(sdkSelfCopy.Count == 1,
                "expected exactly one (sourcePath, patchOffset, patch) closure method taking a " +
                $"Stream - the SDK-patch copy delegate - found {sdkSelfCopy.Count}");

            var fsFileCtor = fsFile.FindConstructors().SingleOrDefault(c =>
                c.MethodSig.Params.Count == 2 &&
                c.MethodSig.Params[0].FullName == "System.String" &&
                c.MethodSig.Params[1].FullName == "System.Int64")
                ?? throw new InvalidOperationException("FSFile(string, long) not found");

            Console.WriteLine($"source {sha[..12]}…");
            foreach (var (name, m) in new (string, MethodDef)[]
                     {
                         ("OpenRead", openRead), ("Populate", populate), ("IsLooseElf", isLooseElf),
                         ("IsSelf", isSelf), ("GetApplicationSceVersion", sceVersion), ("FSFile..ctor", fsFileCtor),
                         ("BuildSdkOverriddenSelf", sdkSelf), ("SDK-patch copy", sdkSelfCopy[0]),
                     })
                Console.WriteLine($"  located {name,-24} {m.FullName}");

            Console.WriteLine($"  located {"PlaintextBlockReader",-24} {plaintextReader.FullName}");

            string shimPath = Path.Combine(Path.GetDirectoryName(output)!, "FpkgVirtualSource.dll");
            Sites.Expect(File.Exists(shimPath), $"FpkgVirtualSource.dll must sit next to {output}");
            var shim = new Sites.Shim(module, shimPath);

            var sourcePathGetter = innerFile.Methods.Single(m => m.Name == "get_SourcePath");
            Rewrites.PrependVirtualOpen(openRead, sourcePathGetter, shim);
            Rewrites.VirtualiseFsFileCtor(fsFileCtor, fsFile, shim);
            Rewrites.PrependTreeOverlay(populate, shim.TreeOverlay);
            foreach (var probe in new[] { isLooseElf, isSelf, sceVersion, sdkSelf })
                Rewrites.VirtualiseProbe(probe, shim);
            Rewrites.WidenPlaintextBlockReader(plaintextReader, shim);
            Rewrites.VirtualiseSelfPatchCopy(sdkSelfCopy[0], shim);

            module.Write(output);
            Console.WriteLine($"wrote {output} (sites 1, 2, 3, 4, 5, 6, 7, 8, 9)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
        finally
        {
            module?.Dispose();
        }
    }

    // Read-only IL dump. Matches on "<DeclaringType>.<Name>" containing the pattern, so
    // compiler-generated names such as <BuildInnerTree>g__IsSelf|39_34 are still reachable.
    private static int Dump(string assembly, string pattern)
    {
        if (!File.Exists(assembly)) { Console.Error.WriteLine($"error: not found: {assembly}"); return 1; }
        using var mod = ModuleDefMD.Load(assembly);
        var matches = mod.GetTypes()
            .SelectMany(t => t.Methods)
            .Where(m => m.HasBody && ($"{m.DeclaringType.Name}.{m.Name}").Contains(pattern, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0) { Console.Error.WriteLine($"error: no method matched '{pattern}'"); return 1; }

        foreach (var m in matches)
        {
            Console.WriteLine($"=== {m.FullName}  (virtual={m.IsVirtual}, static={m.IsStatic})");
            foreach (var local in m.Body.Variables)
                Console.WriteLine($"  .local [{local.Index}] {local.Type.FullName}");
            foreach (var eh in m.Body.ExceptionHandlers)
                Console.WriteLine($"  .try {eh.HandlerType} try=IL_{eh.TryStart?.Offset:X4}..IL_{eh.TryEnd?.Offset:X4} " +
                                  $"handler=IL_{eh.HandlerStart?.Offset:X4}..IL_{eh.HandlerEnd?.Offset:X4}");
            var instructions = m.Body.Instructions;
            for (int i = 0; i < instructions.Count; i++)
            {
                var ins = instructions[i];
                Console.WriteLine($"  [{i,4}] IL_{ins.Offset:X4}  {ins.OpCode.Name,-12} " +
                                  $"{ins.Operand}  (pop={ins.OpCode.StackBehaviourPop}, push={ins.OpCode.StackBehaviourPush})");
            }
            Console.WriteLine();
        }
        return 0;
    }

    // Read-only dump of a whole type: every field with its type, then every method's IL.
    // Matches on the type's Name or FullName containing the pattern, so nested types such as
    // LibProsperoPkg.PKG.ProsperoNapsMeta/PlaintextBlockReader are reachable by short name.
    private static int DumpType(string assembly, string pattern)
    {
        if (!File.Exists(assembly)) { Console.Error.WriteLine($"error: not found: {assembly}"); return 1; }
        using var mod = ModuleDefMD.Load(assembly);
        var types = mod.GetTypes()
            .Where(t => t.Name.String.Contains(pattern, StringComparison.Ordinal) ||
                        t.FullName.Contains(pattern, StringComparison.Ordinal))
            .ToList();

        if (types.Count == 0) { Console.Error.WriteLine($"error: no type matched '{pattern}'"); return 1; }

        foreach (var t in types)
        {
            Console.WriteLine($"##### type {t.FullName}  (nested={t.IsNested}, sealed={t.IsSealed}, " +
                              $"base={t.BaseType?.FullName})");
            foreach (var f in t.Fields)
                Console.WriteLine($"  .field {(f.IsStatic ? "static " : "")}{f.FieldType.FullName} {f.Name}");
            foreach (var n in t.NestedTypes)
                Console.WriteLine($"  .nested {n.FullName}");
            Console.WriteLine();

            foreach (var m in t.Methods)
            {
                Console.WriteLine($"=== {m.FullName}  (virtual={m.IsVirtual}, static={m.IsStatic}, " +
                                  $"body={m.HasBody})");
                if (!m.HasBody) { Console.WriteLine(); continue; }
                foreach (var local in m.Body.Variables)
                    Console.WriteLine($"  .local [{local.Index}] {local.Type.FullName}");
                foreach (var eh in m.Body.ExceptionHandlers)
                    Console.WriteLine($"  .try {eh.HandlerType} try=IL_{eh.TryStart?.Offset:X4}..IL_{eh.TryEnd?.Offset:X4} " +
                                      $"handler=IL_{eh.HandlerStart?.Offset:X4}..IL_{eh.HandlerEnd?.Offset:X4}");
                var instructions = m.Body.Instructions;
                for (int i = 0; i < instructions.Count; i++)
                {
                    var ins = instructions[i];
                    Console.WriteLine($"  [{i,4}] IL_{ins.Offset:X4}  {ins.OpCode.Name,-12} " +
                                      $"{ins.Operand}  (pop={ins.OpCode.StackBehaviourPop}, push={ins.OpCode.StackBehaviourPush})");
                }
                Console.WriteLine();
            }
        }
        return 0;
    }
    }
