using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace OodlePatcher;

/// <summary>
/// Rewrites LibProsperoPkg's ProsperoReducedKrakenEncoder to call PprPfsKrakenTool's Oodle
/// backend instead of libScePubTools.dll.
///
/// The body below was the whole of a top-level-statement Program.cs. It is a callable entry
/// point now so that `fpkg patch` can invoke it IN-PROCESS: the distribution zip carries no
/// sources and no .NET SDK, so shelling out to `dotnet run --project tools/OodlePatcher` is
/// not available to it. Nothing about the patch itself changed - every assertion, its
/// ordering and every message are as they were.
/// </summary>
public static class OodlePatcherProgram
{
    /// <summary>
    /// usage: oodle-patcher &lt;LibProsperoPkg.dll&gt; [PprPfsKrakenTool.dll].
    /// Returns the process exit code: 0 patched, 1 failed, 2 bad usage.
    /// </summary>
    public static int Run(string[] args)
    {
        const string EncoderType = "LibProsperoPkg.PFS.Compression.Oodle.ProsperoReducedKrakenEncoder";
        const string BlockType = "LibProsperoPkg.PFS.Compression.Oodle.EncodedBlock";
        const string PatcherVersion = "1";

        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("usage: oodle-patcher <LibProsperoPkg.dll> [PprPfsKrakenTool.dll]");
            return 2;
        }

        string source = Path.GetFullPath(args[0]);
        string backendPath = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetDirectoryName(source)!, "PprPfsKrakenTool.dll");

        foreach (var p in new[] { source, backendPath })
            if (!File.Exists(p)) { Console.Error.WriteLine($"error: not found: {p}"); return 1; }

        string sourceSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));

        ModuleDefMD? mod = null;
        ModuleDefMD? backend = null;
        try
        {
            mod = ModuleDefMD.Load(source);
            backend = ModuleDefMD.Load(backendPath);

            static TypeDef Find(ModuleDefMD m, string fullName) =>
                m.GetTypes().FirstOrDefault(t => t.FullName == fullName)
                ?? throw new InvalidOperationException($"type not found: {fullName} in {m.Location}");

            var encoder = Find(mod, EncoderType);
            var block = Find(mod, BlockType);
            var backendType = Find(backend, "PprPfsKrakenTool.OodleBackend");

            // The EncodedBlock constructor the original Backend.Encode used: (byte[], bool, int, int).
            var blockCtor = block.FindConstructors().FirstOrDefault(c =>
                    c.MethodSig.Params.Count == 4 &&
                    c.MethodSig.Params[0].FullName == "System.Byte[]" &&
                    c.MethodSig.Params[1].FullName == "System.Boolean" &&
                    c.MethodSig.Params[2].FullName == "System.Int32" &&
                    c.MethodSig.Params[3].FullName == "System.Int32")
                ?? throw new InvalidOperationException(
                    "EncodedBlock(byte[], bool, int, int) not found; upstream changed its shape");

            static MethodDef Method(TypeDef t, string name, int paramCount) =>
                t.Methods.FirstOrDefault(m => m.Name == name && m.MethodSig.Params.Count == paramCount)
                ?? throw new InvalidOperationException($"method not found: {t.Name}.{name}/{paramCount}");

            var isAvail = Method(encoder, "IsAvailableAt", 1);
            var describe = Method(encoder, "DescribeAvailability", 1);
            var encode = Method(encoder, "EncodeBlock", 3);

            // Refuse anything whose shape is not what the rewrites assume.
            static void Expect(bool ok, string what)
            {
                if (!ok) throw new InvalidOperationException("unexpected shape: " + what);
            }

            Expect(isAvail.IsStatic && isAvail.MethodSig.RetType.FullName == "System.Boolean" &&
                   isAvail.MethodSig.Params[0].FullName == "System.String",
                "IsAvailableAt(string) -> bool");
            Expect(describe.IsStatic && describe.MethodSig.RetType.FullName == "System.String" &&
                   describe.MethodSig.Params[0].FullName == "System.String",
                "DescribeAvailability(string) -> string");
            Expect(encode.IsStatic &&
                   encode.MethodSig.RetType.FullName == "System.Nullable`1<" + BlockType + ">" &&
                   encode.MethodSig.Params[0].FullName == "System.ReadOnlySpan`1<System.Byte>" &&
                   encode.MethodSig.Params[1].FullName == "System.Int32" &&
                   encode.MethodSig.Params[2].FullName == "System.String",
                "EncodeBlock(ReadOnlySpan<byte>, int, string) -> EncodedBlock?");

            var importer = new Importer(mod);
            var mIsAvail = importer.Import(Method(backendType, "IsAvailable", 1));
            var mDescribe = importer.Import(Method(backendType, "Describe", 1));
            var mTryEncode = importer.Import(Method(backendType, "TryEncodeBlock", 6));

            static void Forward(MethodDef target, IMethod callee)
            {
                var body = new CilBody();
                for (int i = 0; i < target.MethodSig.Params.Count; i++)
                    body.Instructions.Add(OpCodes.Ldarg.ToInstruction(target.Parameters[i]));
                body.Instructions.Add(OpCodes.Call.ToInstruction(callee));
                body.Instructions.Add(OpCodes.Ret.ToInstruction());
                body.OptimizeMacros();
                target.Body = body;
            }

            Forward(isAvail, mIsAvail);
            Forward(describe, mDescribe);

            // EncodeBlock(ReadOnlySpan<byte> data, int level, string? path):
            //   if (!TryEncodeBlock(data, level, out p, out mc, out f, out bf)) return null;
            //   return new EncodedBlock(p, mc, f, bf);
            {
                // System.Nullable`1<EncodedBlock>. GetTypeRef returns a TypeRef already owned by this
                // module, so no Importer round-trip is needed; a ValueTypeSig wraps it as the
                // ClassOrValueTypeSig that GenericInstSig requires.
                var nullableRef = mod.CorLibTypes.GetTypeRef("System", "Nullable`1");
                var nullableBlock = new GenericInstSig(new ValueTypeSig(nullableRef), block.ToTypeSig());
                var nullableSpec = nullableBlock.ToTypeDefOrRef();

                // Nullable<T>::.ctor(!0). The parameter is the *type's* generic parameter, hence
                // GenericVar, not GenericMVar.
                var nullableCtor = new MemberRefUser(mod, ".ctor",
                    MethodSig.CreateInstance(mod.CorLibTypes.Void, new GenericVar(0)),
                    nullableSpec);

                var body = new CilBody { InitLocals = true };
                var lPayload = new Local(new SZArraySig(mod.CorLibTypes.Byte));
                var lMulti = new Local(mod.CorLibTypes.Boolean);
                var lFirst = new Local(mod.CorLibTypes.Int32);
                var lFlags = new Local(mod.CorLibTypes.Int32);
                var lResult = new Local(nullableBlock);
                foreach (var l in new[] { lPayload, lMulti, lFirst, lFlags, lResult })
                    body.Variables.Add(l);

                var fail = OpCodes.Ldloca.ToInstruction(lResult);

                body.Instructions.Add(OpCodes.Ldarg.ToInstruction(encode.Parameters[0]));
                body.Instructions.Add(OpCodes.Ldarg.ToInstruction(encode.Parameters[1]));
                body.Instructions.Add(OpCodes.Ldloca.ToInstruction(lPayload));
                body.Instructions.Add(OpCodes.Ldloca.ToInstruction(lMulti));
                body.Instructions.Add(OpCodes.Ldloca.ToInstruction(lFirst));
                body.Instructions.Add(OpCodes.Ldloca.ToInstruction(lFlags));
                body.Instructions.Add(OpCodes.Call.ToInstruction(mTryEncode));
                body.Instructions.Add(OpCodes.Brfalse.ToInstruction(fail));

                body.Instructions.Add(OpCodes.Ldloc.ToInstruction(lPayload));
                body.Instructions.Add(OpCodes.Ldloc.ToInstruction(lMulti));
                body.Instructions.Add(OpCodes.Ldloc.ToInstruction(lFirst));
                body.Instructions.Add(OpCodes.Ldloc.ToInstruction(lFlags));
                body.Instructions.Add(OpCodes.Newobj.ToInstruction(blockCtor));
                body.Instructions.Add(OpCodes.Newobj.ToInstruction(nullableCtor));
                body.Instructions.Add(OpCodes.Ret.ToInstruction());

                body.Instructions.Add(fail);                                   // ldloca lResult
                body.Instructions.Add(OpCodes.Initobj.ToInstruction(nullableSpec));
                body.Instructions.Add(OpCodes.Ldloc.ToInstruction(lResult));
                body.Instructions.Add(OpCodes.Ret.ToInstruction());

                body.OptimizeMacros();
                body.OptimizeBranches();
                encode.Body = body;
            }

            string dir = Path.GetDirectoryName(source)!;
            string outDll = Path.Combine(dir, "LibProsperoPkg.oodle.dll");
            if (string.Equals(Path.GetFullPath(outDll), source, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("refusing to overwrite the source assembly");

            mod.Write(outDll);

            var stamp = new
            {
                sourceSha256 = sourceSha,
                patcherVersion = PatcherVersion,
                patchedAtUtc = DateTime.UtcNow.ToString("O"),
                backend = Path.GetFileName(backendPath),
                methods = new[] { isAvail.FullName, describe.FullName, encode.FullName },
            };
            File.WriteAllText(Path.Combine(dir, "LibProsperoPkg.oodle.stamp"),
                JsonSerializer.Serialize(stamp, new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine($"patched {Path.GetFileName(outDll)}  source {sourceSha[..16]}…");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
        finally
        {
            mod?.Dispose();
            backend?.Dispose();
        }
    }
}
