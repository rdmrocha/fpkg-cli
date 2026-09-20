using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace VirtualSourcePatcher;

internal static class Rewrites
{
    /// <summary>
    /// Site 1. Prepends to ProsperoPs5InnerFile.OpenRead():
    ///   if (VirtualSource.IsVirtual(SourcePath)) return VirtualSource.Open(SourcePath);
    /// Inert for folder sources: IsVirtual is false for every path that is not ffpfsc:-schemed
    /// (null included), so control falls through to the original first instruction.
    /// </summary>
    internal static void PrependVirtualOpen(MethodDef openRead, MethodDef sourcePathGetter, Sites.Shim shim)
    {
        var body = openRead.Body;
        Sites.Expect(body.Instructions.Count > 0, "OpenRead has no body");
        Sites.Expect(openRead.MethodSig.RetType.FullName == "System.IO.Stream",
            $"OpenRead returns {openRead.MethodSig.RetType.FullName}, expected System.IO.Stream");
        Sites.Expect(!openRead.IsStatic, "OpenRead is static; the prologue assumes an instance method");
        Sites.Expect(!sourcePathGetter.IsStatic && !sourcePathGetter.IsVirtual &&
                     sourcePathGetter.MethodSig.Params.Count == 0 &&
                     sourcePathGetter.MethodSig.RetType.FullName == "System.String",
            "get_SourcePath is not a non-virtual instance string property getter; " +
            "the prologue's `call` would dispatch incorrectly");

        var original = body.Instructions[0];

        var prologue = new[]
        {
            OpCodes.Ldarg_0.ToInstruction(),
            OpCodes.Call.ToInstruction(sourcePathGetter),
            OpCodes.Call.ToInstruction(shim.IsVirtual),
            OpCodes.Brfalse.ToInstruction(original),
            OpCodes.Ldarg_0.ToInstruction(),
            OpCodes.Call.ToInstruction(sourcePathGetter),
            OpCodes.Call.ToInstruction(shim.Open),
            OpCodes.Ret.ToInstruction(),
        };
        for (int i = 0; i < prologue.Length; i++) body.Instructions.Insert(i, prologue[i]);
        body.UpdateInstructionOffsets();
    }

    /// <summary>
    /// Site 2. In FSFile(string, long): leave virtual paths untouched by Path.GetFullPath, and
    /// open through the shim so the Write delegate streams from the container.
    /// Inert for folder sources: the guard branches only when IsVirtual is true, and
    /// VirtualSource.OpenOrFile falls back to a plain FileStream for real paths.
    /// </summary>
    internal static void VirtualiseFsFileCtor(MethodDef ctor, TypeDef fsFile, Sites.Shim shim)
    {
        GuardGetFullPath(ctor, shim);
        RedirectFileStream(fsFile, shim);
        ctor.Body.UpdateInstructionOffsets();
    }

    /// <summary>
    /// Path.GetFullPath(origFileName) -> virtual ? origFileName : Path.GetFullPath(origFileName).
    /// </summary>
    private static void GuardGetFullPath(MethodDef ctor, Sites.Shim shim)
    {
        var instructions = ctor.Body.Instructions;

        int getFullPath = -1;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].OpCode == OpCodes.Call && instructions[i].Operand is IMethod m &&
                m.FullName.Contains("System.IO.Path::GetFullPath", StringComparison.Ordinal))
            {
                Sites.Expect(getFullPath < 0, "FSFile(string, long) calls Path.GetFullPath more than once");
                getFullPath = i;
            }
        }
        Sites.Expect(getFullPath >= 0, "FSFile(string, long) does not call Path.GetFullPath");

        // `dup` must duplicate exactly the path argument, so the instruction that produced it has
        // to be a simple push sitting immediately before the call.
        Sites.Expect(getFullPath >= 1, "nothing precedes Path.GetFullPath to duplicate");
        var pathPush = instructions[getFullPath - 1];
        Sites.Expect(IsSingleValuePush(pathPush.OpCode),
            $"the instruction before Path.GetFullPath is {pathPush.OpCode}, which is not a simple " +
            "single-value push; `dup` would copy the wrong stack slot");

        // Anything branching straight at the Path.GetFullPath call would skip the guard once the
        // three instructions are inserted ahead of it.
        ExpectNotTargeted(ctor, instructions, getFullPath, 1, "the Path.GetFullPath call");

        // The result must land straight in the SourcePath backing field; anything else means the
        // surrounding code changed and the branch target below would no longer be stack-consistent.
        Sites.Expect(getFullPath + 1 < instructions.Count, "Path.GetFullPath is the last instruction");
        var keepAsIs = instructions[getFullPath + 1];
        Sites.Expect(keepAsIs.OpCode == OpCodes.Stfld && keepAsIs.Operand is IField f &&
                     f.Name.String.Contains("SourcePath", StringComparison.Ordinal) &&
                     f.FieldSig.Type.FullName == "System.String",
            $"Path.GetFullPath's result is consumed by {keepAsIs.OpCode} " +
            $"{keepAsIs.Operand}, expected stfld of the SourcePath backing field");

        var guard = new[]
        {
            OpCodes.Dup.ToInstruction(),
            OpCodes.Call.ToInstruction(shim.IsVirtual),
            OpCodes.Brtrue.ToInstruction(keepAsIs),
        };
        for (int i = 0; i < guard.Length; i++) instructions.Insert(getFullPath + i, guard[i]);
    }

    /// <summary>
    /// The Write delegate constructs a FileStream over SourcePath. Redirect it to the shim.
    /// FileStream's six arguments are pushed BEFORE the newobj, so the five after the path are
    /// nopped in place — the same in-place technique this repo already used on ParseOptimal,
    /// which keeps the stack consistent without moving any instruction.
    /// </summary>
    private static void RedirectFileStream(TypeDef fsFile, Sites.Shim shim)
    {
        int redirected = 0;
        foreach (var method in fsFile.Methods.Concat(
                     fsFile.NestedTypes.SelectMany(n => n.Methods)).Where(m => m.HasBody))
        {
            var body = method.Body.Instructions;
            for (int i = 0; i < body.Count; i++)
            {
                if (body[i].OpCode != OpCodes.Newobj || body[i].Operand is not IMethod ctorRef) continue;
                if (ctorRef.DeclaringType.FullName != "System.IO.FileStream") continue;
                RedirectSixArgOpen(method, body, i, ctorRef, shim, "newobj FileStream");
                redirected++;
            }
            method.Body.UpdateInstructionOffsets();
        }
        Sites.Expect(redirected == 1,
            $"expected exactly one FileStream construction in FSFile to redirect, found {redirected}");
    }


    /// <summary>
    /// Site 13. Every value <c>ProsperoCntEntryPolicy.Resolve</c> returns is passed through
    /// <c>CntEntryPolicy.Adjust</c>, which clears the encryption bit on the five licence entries
    /// Sony stores in plaintext (measured: ids 1024, 1025, 1026, 8224, 8225 carry
    /// <c>Flags1 = 0</c> in the Windows-built package and <c>0x80000000</c> in ours, with
    /// identical sizes).
    ///
    /// <para><b>Why the return values and not the call sites.</b> <c>Resolve</c> has two callers
    /// (<c>LayOutEntries</c> and <c>CollectMediaEntries</c>) and both would need the id and the
    /// relative name pushed again to reach the shim. Patching the callee is one edit instead of
    /// two, and it cannot be bypassed by a third caller appearing upstream.</para>
    ///
    /// <para><b>Why the <c>ret</c> is mutated rather than preceded.</b> Inserting before a
    /// <c>ret</c> would leave any branch that targets that <c>ret</c> pointing at the original
    /// instruction, jumping straight over the inserted code. So the <c>ret</c> itself becomes the
    /// first inserted instruction and a fresh <c>ret</c> is appended after the call: every
    /// existing branch and handler bound still points at a live instruction, and it is now the
    /// head of the inserted run. Same technique this file already uses on the six-argument
    /// opens.</para>
    ///
    /// <para>Inert when the CLI has not set <c>PlaintextLicenseEntries</c>: <c>Adjust</c> returns
    /// its argument unchanged, so an unconfigured build resolves exactly as before.</para>
    /// </summary>
    internal static void PlaintextLicenseCntEntries(TypeDef policy, Sites.Shim shim)
    {
        var resolve = Sites.Method(policy, "Resolve",
            "LibProsperoPkg.PKG.ProsperoCntEntryProfile", 4);
        Sites.Expect(resolve.IsStatic, "ProsperoCntEntryPolicy.Resolve is not static; argument " +
                                       "indices 0 and 2 would not be id and relativeName");
        Sites.Expect(resolve.MethodSig.Params[0].FullName == "System.UInt32" &&
                     resolve.MethodSig.Params[2].FullName == "System.String",
            "Resolve's parameters are not (uint id, _, string relativeName, _); re-derive site 13");

        var profileType = resolve.MethodSig.RetType.ToTypeDefOrRef();
        var typeDef = profileType.ResolveTypeDef()
            ?? throw new InvalidOperationException("ProsperoCntEntryProfile could not be resolved");
        var getFlags1 = typeDef.FindMethod("get_Flags1")
            ?? throw new InvalidOperationException("ProsperoCntEntryProfile.get_Flags1 not found");
        var getFlags2 = typeDef.FindMethod("get_Flags2")
            ?? throw new InvalidOperationException("ProsperoCntEntryProfile.get_Flags2 not found");
        var getIncludeName = typeDef.FindMethod("get_IncludeName")
            ?? throw new InvalidOperationException("ProsperoCntEntryProfile.get_IncludeName not found");
        var ctor = typeDef.FindConstructors().SingleOrDefault(c =>
            c.MethodSig.Params.Count == 3 &&
            c.MethodSig.Params[0].FullName == "System.UInt32" &&
            c.MethodSig.Params[1].FullName == "System.UInt32" &&
            c.MethodSig.Params[2].FullName == "System.Boolean")
            ?? throw new InvalidOperationException(
                "ProsperoCntEntryProfile(uint, uint, bool) not found; re-derive site 13");

        // A record STRUCT, so its getters take a managed pointer as `this`: ldloca, never ldloc.
        // Getting this wrong does not throw InvalidProgramException, it segfaults the build --
        // measured. newobj on a value-type constructor is fine and leaves the value on the stack.
        Sites.Expect(typeDef.IsValueType,
            "ProsperoCntEntryProfile is no longer a value type; site 13's ldloca/newobj sequence " +
            "must be re-derived as ldloc/newobj before it is shipped");

        // The return signature, NOT profileType.ToTypeSig(): going through TypeDefOrRef produces a
        // CLASS signature for a value type, so the local would be a reference and every ldloca
        // would hand the getters a pointer to a garbage object.
        var slot = new Local(resolve.MethodSig.RetType);
        resolve.Body.Variables.Add(slot);

        var body = resolve.Body.Instructions;
        var rets = Enumerable.Range(0, body.Count)
                             .Where(i => body[i].OpCode == OpCodes.Ret)
                             .ToList();
        Sites.Expect(rets.Count > 0, "ProsperoCntEntryPolicy.Resolve has no ret");

        foreach (int at in Enumerable.Reverse(rets))
        {
            // The ret BECOMES the first inserted instruction, so branch targets stay live.
            body[at].OpCode = OpCodes.Stloc;
            body[at].Operand = slot;
            Instruction[] tail =
            [
                OpCodes.Ldloca.ToInstruction(slot),
                OpCodes.Call.ToInstruction(getFlags1),
                OpCodes.Ldarg_0.ToInstruction(),                  // id
                OpCodes.Ldarg_2.ToInstruction(),                  // relativeName
                OpCodes.Call.ToInstruction(shim.CntAdjustFlags1),
                OpCodes.Ldloca.ToInstruction(slot),
                OpCodes.Call.ToInstruction(getFlags2),
                OpCodes.Ldloca.ToInstruction(slot),
                OpCodes.Call.ToInstruction(getIncludeName),
                OpCodes.Newobj.ToInstruction(ctor),
                OpCodes.Ret.ToInstruction(),
            ];
            for (int k = 0; k < tail.Length; k++) body.Insert(at + 1 + k, tail[k]);
        }
        resolve.Body.UpdateInstructionOffsets();
    }

    /// <summary>
    /// Site 14. <c>ProsperoPkgBuilder.EnsureAboutRightSprx</c> returns immediately when the CLI
    /// asks for it.
    ///
    /// <para>Sony's package has no <c>sce_sys/about/right.sprx</c>: its folder-to-GP5 wrapper drops
    /// the whole <c>about/</c> directory from the package inputs and its packer writes only what
    /// the GP5 names. LibProsperoPkg instead synthesises the file from an embedded resource, so
    /// excluding it from the source tree does not remove it from the package — this method has to
    /// be stopped.</para>
    ///
    /// <para>The method returns void, so the guard is three instructions at the head. Nothing may
    /// branch to or handle at the original first instruction, which is checked rather than
    /// assumed.</para>
    /// </summary>
    internal static void SuppressAboutRightSprx(TypeDef builder, Sites.Shim shim)
    {
        var ensure = Sites.Method(builder, "EnsureAboutRightSprx", "System.Void", 1);
        var body = ensure.Body.Instructions;
        Sites.Expect(body.Count > 0, "EnsureAboutRightSprx has no body");
        ExpectNotTargeted(ensure, body, 0, 1, "the first instruction of EnsureAboutRightSprx");

        var first = body[0];
        body.Insert(0, OpCodes.Call.ToInstruction(shim.CntSkipRightSprx));
        body.Insert(1, OpCodes.Brfalse.ToInstruction(first));
        body.Insert(2, OpCodes.Ret.ToInstruction());
        ensure.Body.UpdateInstructionOffsets();
    }


    /// <summary>
    /// Site 15. After <c>BuildCore</c> has collected its AFID candidates, the staged PlayGo
    /// language fillers are moved to the end of that list, which is what decides placement.
    ///
    /// <para>Sony orders placement by chunk, so the fillers land last and its final language
    /// extent reaches the end of the mount image — 32 extents where we emit 33. See
    /// <c>PlayGoFillerOrder</c> for why this is done by reordering rather than through
    /// <c>PublisherAfidAssignments</c>.</para>
    ///
    /// <para><b>Locating the list.</b> The insertion point is the call to
    /// <c>CollectFilesEntryOrder(dir, excludedSubtree, outList, onFile)</c>, which is the last
    /// thing to append to it. The list is the THIRD argument, and the fourth is a delegate built
    /// by <c>ldftn</c> + <c>newobj</c>, so walking back four instructions from the call reaches
    /// the push that loaded it. That shape is asserted rather than assumed, and the local's type
    /// is checked to be a <c>List`1</c>.</para>
    ///
    /// <para><c>CollectFilesEntryOrder</c> is recursive, so its own <c>ret</c> is NOT the place to
    /// do this: it would fire once per directory, on a list that is still being built.</para>
    /// </summary>
    internal static void PlaceLanguageFillersLast(TypeDef assembler, Sites.Shim shim)
    {
        var collect = Sites.Method(assembler, "CollectFilesEntryOrder", "System.Void", 4);
        var build = Sites.Method(assembler, "BuildCore",
            "LibProsperoPkg.PFS.ProsperoPs5InnerImageResult", 4);
        var body = build.Body.Instructions;

        var calls = Enumerable.Range(0, body.Count)
            .Where(i => body[i].OpCode == OpCodes.Call && body[i].Operand is IMethod m &&
                        m.Name == collect.Name && m.ResolveMethodDef() == collect)
            .ToList();
        Sites.Expect(calls.Count == 1,
            $"BuildCore calls CollectFilesEntryOrder {calls.Count} times, expected 1");

        int at = calls[0];
        Sites.Expect(at >= 4, "not enough instructions before the CollectFilesEntryOrder call");
        Sites.Expect(body[at - 1].OpCode == OpCodes.Newobj && body[at - 2].OpCode == OpCodes.Ldftn,
            "the fourth argument to CollectFilesEntryOrder is not an ldftn/newobj delegate; " +
            "re-derive site 15 against the current IL");

        var push = body[at - 4];
        var list = push.GetLocal(build.Body.Variables);
        Sites.Expect(list is not null,
            $"the third argument to CollectFilesEntryOrder is pushed by {push.OpCode}, expected a " +
            "ldloc; re-derive site 15");
        Sites.Expect(list!.Type.FullName.StartsWith("System.Collections.Generic.List`1",
                                                    StringComparison.Ordinal),
            $"the third argument is a {list.Type.FullName}, expected a List`1; re-derive site 15");

        ExpectNotTargeted(build, body, at + 1, 1, "the instruction after CollectFilesEntryOrder");
        body.Insert(at + 1, OpCodes.Ldloc.ToInstruction(list));
        body.Insert(at + 2, OpCodes.Call.ToInstruction(shim.FillerMoveLast));
        build.Body.UpdateInstructionOffsets();
    }


    /// <summary>
    /// Site 16. <c>CollectFilesEntryOrder</c> sorts each directory's entries with
    /// <c>StringComparer.Ordinal</c>; Sony's folder-to-GP5 generator sorts them
    /// <c>key=lambda entry: entry.name.casefold()</c> — case-INSENSITIVELY. That single difference
    /// is the whole of the remaining placement divergence.
    ///
    /// <para><b>Measured.</b> Replaying the source tree with a case-insensitive name sort
    /// reproduces the regenerated the reference fixture oracle's physical order exactly, 52 of 52 positions.
    /// An ordinal sort reproduces 33 of 52, first diverging at index 2 where ordinal puts
    /// <c>Media/…</c> ('M' = 0x4D) ahead of <c>eboot.bin</c> ('e' = 0x65) and the oracle does
    /// not.</para>
    ///
    /// <para>One instruction: the operand of the <c>call StringComparer::get_Ordinal()</c> inside
    /// that method becomes <c>get_OrdinalIgnoreCase()</c>. Scoped to this method alone, so every
    /// other ordinal comparison in the assembly is untouched.</para>
    /// </summary>
    internal static void WalkDirectoriesCaseInsensitively(TypeDef assembler, ModuleDefMD module)
    {
        var collect = Sites.Method(assembler, "CollectFilesEntryOrder", "System.Void", 4);
        var comparer = module.CorLibTypes.GetTypeRef("System", "StringComparer");
        var importer = new Importer(module);
        var target = importer.Import(new MemberRefUser(module, "get_OrdinalIgnoreCase",
            MethodSig.CreateStatic(comparer.ToTypeSig()), comparer));

        int swapped = 0;
        foreach (var ins in collect.Body.Instructions)
        {
            if (ins.OpCode != OpCodes.Call || ins.Operand is not IMethod m) continue;
            if (m.Name != "get_Ordinal" || m.DeclaringType?.Name != "StringComparer") continue;
            ins.Operand = target;
            swapped++;
        }
        Sites.Expect(swapped == 1,
            $"CollectFilesEntryOrder has {swapped} StringComparer.Ordinal call(s), expected 1; " +
            "re-derive site 16 against the current IL");
        collect.Body.UpdateInstructionOffsets();
    }

    /// <summary>
    /// Site 9. The Write delegate g__BuildSdkOverriddenSelf hands to its replacement FSFile opens
    /// the ORIGINAL executable by path, to copy everything up to the .sceversion patch offset:
    ///
    ///     using FileStream source = BuildFileIO.Open(sourcePath, Open, Read, Read, 1 MiB, Sequential);
    ///     CopyPrefix(source, destination, patchOffset);
    ///
    /// For a container source that path is the "ffpfsc:&lt;handle&gt;:/..." scheme, so the open throws.
    /// It is reached only when an SDK override is set — ConvertLooseElfExecutables is a no-op
    /// without one — which is why it stayed invisible until --sdk-version gained a default.
    ///
    /// Six pushed arguments and a String first parameter, exactly like the FSFile constructor, so
    /// both go through the same rewrite. Inert for folder sources: OpenOrFile falls back to a
    /// plain FileStream for real paths.
    /// </summary>
    internal static void VirtualiseSelfPatchCopy(MethodDef lambda, Sites.Shim shim)
    {
        var body = lambda.Body.Instructions;

        int callIndex = -1;
        IMethod? opened = null;
        for (int i = 0; i < body.Count; i++)
        {
            if (body[i].OpCode != OpCodes.Call || body[i].Operand is not IMethod m) continue;
            if (m.FullName.IndexOf("BuildFileIO::Open", StringComparison.Ordinal) < 0) continue;
            Sites.Expect(callIndex < 0, $"{lambda.Name}: more than one BuildFileIO.Open call");
            callIndex = i;
            opened = m;
        }
        Sites.Expect(callIndex >= 0, $"{lambda.Name}: no BuildFileIO.Open call found");

        RedirectSixArgOpen(lambda, body, callIndex, opened!, shim, "BuildFileIO.Open");
        lambda.Body.UpdateInstructionOffsets();
    }

    /// <summary>
    /// Rewrites a six-argument "open this path" call — `newobj FileStream(...)` or
    /// `BuildFileIO.Open(...)`, which share a signature — into VirtualSource.OpenOrFile(string).
    /// The five arguments after the path are pushed BEFORE the call, so they are nopped in place
    /// rather than removed: mutating keeps every branch and handler bound pointing at a live
    /// instruction instead of an orphan.
    /// </summary>
    private static void RedirectSixArgOpen(
        MethodDef method, IList<Instruction> body, int callIndex, IMethod target,
        Sites.Shim shim, string what)
    {
        Sites.Expect(target.MethodSig.Params.Count == 6,
            $"{method.Name}: expected the 6-argument form of {what}, got {target.MethodSig.Params.Count}");
        Sites.Expect(target.MethodSig.Params[0].FullName == "System.String",
            $"{method.Name}: {what}'s first argument is not the path");
        // Only meaningful for `call`. A constructor's signature always has HasThis, but `newobj`
        // allocates the receiver itself rather than taking one off the stack, so the six pushed
        // arguments are the whole story there and the stack stays balanced either way.
        Sites.Expect(body[callIndex].OpCode == OpCodes.Newobj || !target.MethodSig.HasThis,
            $"{method.Name}: {what} is an instance method; the call site pushes a receiver that " +
            "the static VirtualSource.OpenOrFile would not consume");
        Sites.Expect(callIndex >= 5, $"{method.Name}: not enough instructions before {what}");

        // Nothing may jump into the middle of the argument run, and no exception-handler
        // boundary may sit on it; either would make the disposal below break control flow.
        ExpectNotTargeted(method, body, callIndex - 5, 6, $"the {what} argument run");

        for (int back = 1; back <= 5; back++)
        {
            var arg = body[callIndex - back];
            Sites.Expect(IsSingleValuePush(arg.OpCode),
                $"{method.Name}: argument {back} before {what} is {arg.OpCode}, which is not a " +
                "simple single-value push; re-derive this rewrite against the current IL");
            arg.OpCode = OpCodes.Nop; arg.Operand = null;
        }

        body[callIndex].OpCode = OpCodes.Call;
        body[callIndex].Operand = shim.OpenOrFile;
        WidenFileStreamLocal(method, body, callIndex);
    }

    /// <summary>
    /// The shim returns System.IO.Stream, but the slot that receives the widened call's result
    /// (via the stloc immediately after <paramref name="callIndex"/>) is declared FileStream.
    /// Leaving it would let the JIT devirtualise later calls against the wrong type, so the slot
    /// is widened — only after proving nothing in the method needs it to be a FileStream.
    ///
    /// The "address taken" guard defaults to whole-method scope: it fails if the method takes
    /// the address of ANY local via ldloca/ldloca_s. That is correct for a caller — such as the
    /// FSFile constructor — that never takes the address of an unrelated local. Passing
    /// <paramref name="scopeLdlocaTo"/> narrows that guard to only the widened local itself, for
    /// a caller — such as the three executable probes — whose body legitimately takes the
    /// address of OTHER locals (a stack CancellationToken, a stack Span&lt;byte&gt;) that have
    /// nothing to do with the FileStream slot; narrowing it is safe because the hazard ldloca
    /// guards against only depends on what happens to the widened local itself. Either way, the
    /// "no call to a FileStream-declared member" guard below stays whole-method scoped in both
    /// modes — it is conservative rather than a source of false positives here, so there was no
    /// need to narrow it too.
    /// </summary>
    private static void WidenFileStreamLocal(
        MethodDef method, IList<Instruction> body, int callIndex, Local? scopeLdlocaTo = null)
    {
        Sites.Expect(callIndex + 1 < body.Count, "the widened call is the last instruction");
        var store = body[callIndex + 1];
        var local = store.GetLocal(method.Body.Variables);
        Sites.Expect(local is not null,
            $"the instruction after the widened call is {store.OpCode}, expected a stloc; " +
            "re-derive this rewrite against the current IL");
        Sites.Expect(local!.Type.FullName == "System.IO.FileStream",
            $"the FileStream is stored in a {local.Type.FullName} slot, expected System.IO.FileStream");

        foreach (var ins in body)
        {
            bool isLdloca = ins.OpCode == OpCodes.Ldloca || ins.OpCode == OpCodes.Ldloca_S;
            bool addressesWidenedLocal = scopeLdlocaTo is null
                ? isLdloca
                : isLdloca && ReferenceEquals(ins.Operand, scopeLdlocaTo);
            Sites.Expect(!addressesWidenedLocal,
                "the FileStream local is addressed by ldloca; widening it is not safe");
            Sites.Expect(!(ins.Operand is IMethod m && m.DeclaringType?.FullName == "System.IO.FileStream"
                           && ins.OpCode != OpCodes.Newobj),
                $"the method calls FileStream-declared member {ins.Operand}; the local cannot be " +
                "widened to System.IO.Stream");
        }

        local.Type = method.Module.CorLibTypes.GetTypeRef("System.IO", "Stream").ToTypeSig();
    }

    /// <summary>
    /// Sites 5-7. The probes already branch on "is there a SourcePath", and when there is one
    /// they call OpenExecutableProbe to read a few leading bytes. Swapping that single call for
    /// VirtualSource.OpenOrFile makes virtual paths work and leaves real paths byte-identical:
    /// both return a Stream positioned at zero, and the probes only ever read forward from it.
    /// The call's operand is swapped in place - the instruction object is untouched otherwise -
    /// so no branch, switch case, or exception-handler bound referencing it is left dangling.
    /// </summary>
    internal static void VirtualiseProbe(MethodDef probe, Sites.Shim shim)
    {
        var body = probe.Body.Instructions;

        int callIndex = -1;
        IMethod? replaced = null;
        for (int i = 0; i < body.Count; i++)
        {
            if (body[i].OpCode != OpCodes.Call || body[i].Operand is not IMethod m) continue;
            if (!m.Name.String.Contains("OpenExecutableProbe", StringComparison.Ordinal)) continue;
            Sites.Expect(callIndex < 0, $"{probe.Name}: found more than one OpenExecutableProbe call");
            callIndex = i;
            replaced = m;
        }
        Sites.Expect(callIndex >= 0, $"{probe.Name}: no OpenExecutableProbe call found");

        // Swapping only the operand keeps whatever arguments the original call site pushed, so
        // the replaced method's signature must already match VirtualSource.OpenOrFile's
        // (string) -> Stream. Every other site asserts the shape of what it rewrites; this one
        // did not, and an upstream arity or return-type change would have produced invalid IL
        // that surfaces as an InvalidProgramException at build time rather than a loud patcher
        // failure. Static, because a `call` at an instance method would also consume a receiver.
        Sites.Expect(replaced!.MethodSig.Params.Count == 1 &&
                     replaced.MethodSig.Params[0].FullName == "System.String",
            $"{probe.Name}: OpenExecutableProbe takes " +
            $"({string.Join(", ", replaced.MethodSig.Params.Select(p => p.FullName))}), expected a " +
            "single System.String; swapping in VirtualSource.OpenOrFile(string) would leave the " +
            "stack unbalanced. Re-derive this rewrite against the current IL");
        Sites.Expect(replaced.MethodSig.RetType.FullName == "System.IO.Stream" ||
                     replaced.MethodSig.RetType.FullName == "System.IO.FileStream",
            $"{probe.Name}: OpenExecutableProbe returns {replaced.MethodSig.RetType.FullName}, " +
            "expected a FileStream/Stream; VirtualSource.OpenOrFile returns System.IO.Stream. " +
            "Re-derive this rewrite against the current IL");
        Sites.Expect(!replaced.MethodSig.HasThis,
            $"{probe.Name}: OpenExecutableProbe is an instance method; the call site pushes a " +
            "receiver that the static VirtualSource.OpenOrFile would not consume. Re-derive " +
            "this rewrite against the current IL");

        body[callIndex].Operand = shim.OpenOrFile;

        // The local held the concrete FileStream; widen it so a shim Stream fits without RyuJIT
        // devirtualising a later call against the wrong declared type. Unlike the FSFile
        // constructor, these probes legitimately take the address of OTHER locals (a stack
        // CancellationToken, a stack Span<byte>), so the ldloca guard is scoped to this specific
        // local rather than the whole method — see WidenFileStreamLocal's doc comment.
        Sites.Expect(callIndex + 1 < body.Count,
            $"{probe.Name}: the OpenExecutableProbe call is the last instruction");
        var probeLocal = body[callIndex + 1].GetLocal(probe.Body.Variables);
        WidenFileStreamLocal(probe, body, callIndex, probeLocal);

        probe.Body.UpdateInstructionOffsets();
    }

    /// <summary>
    /// Site 3. Prepends to Populate(node, path, ...):
    ///   if (TreeOverlay.Populate(node, path)) return;
    /// The staged sce_sys is still walked normally by the caller for the real directory.
    /// </summary>
    internal static void PrependTreeOverlay(MethodDef populate, IMethod overlay)
    {
        var body = populate.Body;
        Sites.Expect(body.Instructions.Count > 0, "Populate has no body");
        // MethodSig.Params excludes the implicit `this`, so an 8-parameter match alone cannot
        // tell a static method from an instance one with the same declared parameter count. If
        // Populate ever became an instance method, Ldarg_0 would silently become `this` and
        // every argument below would shift by one while every other shape check still passed.
        Sites.Expect(populate.IsStatic,
            $"{populate.FullName} is an instance method; the prologue's Ldarg_0/Ldarg_1 assume " +
            "a static method where those map to (node, path) rather than (this, node)");
        Sites.Expect(populate.MethodSig.Params.Count == 8,
            $"Populate takes {populate.MethodSig.Params.Count} parameters, expected 8");
        Sites.Expect(populate.MethodSig.Params[0].FullName == "LibProsperoPkg.PFS.FSDir",
            "Populate's first parameter is not FSDir");
        Sites.Expect(populate.MethodSig.Params[1].FullName == "System.String",
            "Populate's second parameter is not String");

        var original = body.Instructions[0];
        var prologue = new[]
        {
            OpCodes.Ldarg_0.ToInstruction(),
            OpCodes.Ldarg_1.ToInstruction(),
            OpCodes.Call.ToInstruction(overlay),
            OpCodes.Brfalse.ToInstruction(original),
            OpCodes.Ret.ToInstruction(),
        };
        for (int i = 0; i < prologue.Length; i++) body.Instructions.Insert(i, prologue[i]);
        body.UpdateInstructionOffsets();
    }

    /// <summary>
    /// Site 4. ProsperoNapsMeta.PlaintextBlockReader caches an open stream between calls in
    /// `private FileStream currentStream`. Widens that field to System.IO.Stream and routes its
    /// construction through the shim.
    ///
    /// Safe because every existing use of the field is already declared on Stream — the dump shows
    /// `call Stream::Dispose`, `callvirt Stream::set_Position` and `callvirt Stream::ReadExactly`,
    /// and nothing outside this type touches the field — so widening the declared type leaves all
    /// of them verifiable. Should a future release reach for a member declared on FileStream, the
    /// loop below retargets it to the Stream declaration only when that is provably meaning-
    /// preserving (the member exists on Stream AND the opcode already matches its virtuality), and
    /// otherwise fails loudly rather than emitting a dangling operand or a mis-dispatching call.
    ///
    /// Ordering: every shape assertion runs before the mutation it guards, and the field retype is
    /// deferred to the very end so that a failure anywhere leaves the field alone too. Two checks
    /// cannot be hoisted — `replacedCtor == 1` is a count, so it can only follow the counting — but
    /// Program.cs writes the module only after every site returns, so an abort never reaches disk.
    ///
    /// Byte-identical for folder sources: the original six constructor arguments are
    /// (path, Open, Read, Read, bufferSize 1, RandomAccess), which is exactly what
    /// VirtualSource.OpenOrFile passes to its own FileStream for a non-virtual path. This site
    /// feeds the NAPS plaintext integrity digests, so that equivalence is the whole point.
    /// </summary>
    internal static void WidenPlaintextBlockReader(TypeDef reader, Sites.Shim shim)
    {
        var field = reader.Fields.SingleOrDefault(f => f.Name == "currentStream")
            ?? throw new InvalidOperationException("PlaintextBlockReader.currentStream not found");
        Sites.Expect(field.FieldType.FullName == "System.IO.FileStream",
            $"currentStream is {field.FieldType.FullName}, expected System.IO.FileStream");

        var module = reader.Module;
        var streamRef = module.CorLibTypes.GetTypeRef("System.IO", "Stream");
        var importer = new Importer(module);

        int replacedCtor = 0, retargeted = 0;
        foreach (var method in reader.Methods.Where(m => m.HasBody))
        {
            var instructions = method.Body.Instructions;
            for (int i = 0; i < instructions.Count; i++)
            {
                var ins = instructions[i];

                if (ins.OpCode == OpCodes.Newobj && ins.Operand is IMethod ctor &&
                    ctor.DeclaringType?.FullName == "System.IO.FileStream")
                {
                    Sites.Expect(ctor.MethodSig.Params.Count == 6,
                        $"expected the 6-argument FileStream constructor, got {ctor.MethodSig.Params.Count}");
                    Sites.Expect(ctor.MethodSig.Params[0].FullName == "System.String",
                        "the FileStream constructor's first argument is not the path");
                    Sites.Expect(i >= 5, "not enough instructions before newobj FileStream");

                    // Nothing may jump into the middle of the argument run and no exception-handler
                    // boundary may sit on it; either would leave the stack unbalanced once the five
                    // trailing arguments become nops. Checked before any mutation.
                    ExpectNotTargeted(method, instructions, i - 5, 6,
                        "the newobj FileStream argument run in PlaintextBlockReader");

                    // The result must land straight in currentStream. Anything else would mean the
                    // Stream is flowing somewhere still typed FileStream. Checked before any
                    // mutation, so a shape change leaves the assembly untouched.
                    Sites.Expect(i + 1 < instructions.Count, "newobj FileStream is the last instruction");
                    var store = instructions[i + 1];
                    Sites.Expect(store.OpCode == OpCodes.Stfld &&
                                 store.Operand is IField sf && sf.Name == "currentStream",
                        $"the FileStream flows into {store.OpCode} {store.Operand}, expected " +
                        "stfld currentStream; re-derive this rewrite against the current IL");

                    // Every argument must be a simple single-value push before anything is nopped:
                    // asserting the whole run first means a partially rewritten body is impossible.
                    for (int back = 1; back <= 5; back++)
                        Sites.Expect(IsSingleValuePush(instructions[i - back].OpCode),
                            $"argument {back} before newobj FileStream is " +
                            $"{instructions[i - back].OpCode}, which is not a simple single-value " +
                            "push; re-derive this rewrite against the current IL");

                    // The five arguments after the path are constants pushed immediately before the
                    // newobj; nopping them in place is stack-neutral because OpenOrFile takes only
                    // the path, and it moves no instruction that a branch could reference.
                    for (int back = 1; back <= 5; back++)
                    {
                        instructions[i - back].OpCode = OpCodes.Nop;
                        instructions[i - back].Operand = null;
                    }

                    // Mutated in place rather than replaced, so nothing that referenced this
                    // instruction object (a branch, a handler bound) is left pointing at an orphan.
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = shim.OpenOrFile;
                    replacedCtor++;
                    continue;
                }

                if (ins.Operand is IMethod call && call.DeclaringType?.FullName == "System.IO.FileStream")
                {
                    // Unreachable against the current library — the compiler already emitted every
                    // currentStream use against the Stream declaration, so `retargeted` is 0. It
                    // exists as the loud-failure net for a future revision, which is exactly why it
                    // must not quietly do the wrong thing the first time it does fire. Swapping the
                    // operand only preserves semantics for a call whose opcode already matches the
                    // Stream declaration's virtuality; for anything else the patch genuinely has to
                    // be re-derived.
                    Sites.Expect(ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt,
                        $"PlaintextBlockReader references FileStream member {call} with " +
                        $"{ins.OpCode}; ldftn/ldtoken and friends bind a delegate target or a " +
                        "metadata token rather than a call site, so swapping the operand would " +
                        "change meaning. Re-derive this rewrite against the current library");

                    var onStream = streamRef.ResolveTypeDefThrow().FindMethod(call.Name, call.MethodSig);
                    Sites.Expect(onStream is not null,
                        $"PlaintextBlockReader calls {call}, which is declared only on FileStream; " +
                        "currentStream cannot be widened to System.IO.Stream without a different rewrite");

                    // A non-virtual `call` at a virtual/abstract Stream member is a base call: it
                    // skips the override that actually reads the container (and is invalid IL when
                    // the member is abstract). A `callvirt` at a non-virtual one is equally a shape
                    // mismatch. Either way the opcode, not just the operand, would need rewriting.
                    bool virtualOnStream = onStream!.IsVirtual || onStream.IsAbstract;
                    Sites.Expect(virtualOnStream
                            ? ins.OpCode == OpCodes.Callvirt
                            : ins.OpCode == OpCodes.Call,
                        $"PlaintextBlockReader uses {ins.OpCode} for {call}, but " +
                        $"System.IO.Stream::{onStream.Name} is " +
                        $"{(virtualOnStream ? "virtual/abstract and needs callvirt" : "non-virtual and needs call")}; " +
                        "retargeting the operand alone would dispatch incorrectly. Re-derive this " +
                        "rewrite against the current library");

                    ins.Operand = importer.Import(onStream);
                    retargeted++;
                }
            }
            method.Body.UpdateInstructionOffsets();
        }

        // A count can only be checked once the counting is done, so this one assertion necessarily
        // follows the IL mutation it validates. Nothing has reached disk at this point: Program.cs
        // calls module.Write only after every site returns, so throwing here leaves the on-disk
        // assembly untouched.
        Sites.Expect(replacedCtor == 1,
            $"expected exactly 1 FileStream construction in PlaintextBlockReader, found {replacedCtor}");

        // Last, so that every shape assertion above has already passed. ldfld/stfld in this module
        // carry the FieldDef itself as their operand, so retyping the definition retypes all four
        // accesses at once. Nothing outside the type reads the field.
        field.FieldType = streamRef.ToTypeSig();

        Console.WriteLine($"  site 4: widened currentStream, retargeted {retargeted} FileStream call(s)");
    }

    /// <summary>
    /// Site 10. Gives each PlayGo language filler its OWN stored copy, the way Sony's
    /// <c>img_create</c> does, instead of letting 31 byte-identical files collapse onto one.
    ///
    /// <para>
    /// <c>ProsperoPs5InnerImageAssembler.CompressToStream</c> already takes a
    /// <c>bool deduplicate</c> and its buffered call site already passes <c>false</c>, but that
    /// argument cannot be reused here: its branch in <c>WriteBlock</c> sets
    /// <c>onDiskOffset = -1</c>, and the STREAMING call site reads
    /// <c>blocks[0].OnDiskOffset</c> straight into <c>FileNode.OnDiskOffset</c>, so forcing it
    /// would give every filler an offset of -1. What the fillers need is the dedup MISS path —
    /// write at <c>destination.Position</c> and record that — just without the lookup and without
    /// the registration. So three narrow guards go in instead, all on one predicate:
    /// </para>
    /// <list type="number">
    /// <item><c>EncodeBlock</c>: skip the shared compression cache. Required, not cosmetic — a
    /// cache hit returns a COMPACTED entry whose <c>Data</c> is <c>Array.Empty</c> and whose size
    /// comes from <c>CachedStoredSize</c>, which the miss path then refuses with "Cached block has
    /// no canonical physical extent." A zero block cached by any earlier chunk-0 file would
    /// otherwise fail the build, non-deterministically with respect to the source content.</item>
    /// <item><c>WriteBlock</c>: jump straight to the miss path, so <c>TryReuse</c> is never
    /// consulted. Guarded by <c>deduplicate</c> as well, so the buffered call site keeps its
    /// existing <c>-1</c> behaviour exactly.</item>
    /// <item><c>WriteBlock</c>: skip <c>Remember</c>, so a filler's stored copy can never become
    /// the reuse target of an unrelated zero block and pull chunk-0 payload into a language
    /// chunk's extent.</item>
    /// </list>
    /// <para>
    /// The predicate is <c>PlayGoFillerDedup</c>'s EXACT allow-list, empty unless the CLI has just
    /// staged fillers, so this is inert for every other file in every other build — see the
    /// deduplication test that proves a normal file still shares one stored copy.
    /// </para>
    /// <para>
    /// Everything is located by shape: the closure by the five fields it must carry, the two local
    /// functions by the <c>&gt;g__Name|</c> prefix that survives an ordinal change, and each guard
    /// site by the field access and branch that it sits on. Every assertion runs before the first
    /// mutation of the method it guards.
    /// </para>
    /// </summary>
    internal static void BypassLanguageFillerDedup(TypeDef assembler, Sites.Shim shim)
    {
        var fileNode = assembler.NestedTypes.SingleOrDefault(t => t.Name == "FileNode")
            ?? throw new InvalidOperationException(
                "ProsperoPs5InnerImageAssembler.FileNode not found");
        var fullPath = fileNode.Fields.SingleOrDefault(f => f.Name == "FullPath")
            ?? throw new InvalidOperationException("FileNode.FullPath not found");
        Sites.Expect(!fullPath.IsStatic && fullPath.FieldSig.Type.FullName == "System.String",
            $"FileNode.FullPath is {(fullPath.IsStatic ? "static " : "")}" +
            $"{fullPath.FieldSig.Type.FullName}, expected an instance System.String; the guard " +
            "passes it to PlayGoFillerDedup(string)");

        // CompressToStream's closure. Named by shape rather than by the ordinal in
        // <>c__DisplayClass68_0, which moves between releases: it is the one nested display class
        // that carries every field the two local functions below read.
        string[] required = ["file", "deduplicator", "deduplicate", "destination", "allowPfsCompression"];
        var closures = assembler.NestedTypes.Where(t =>
            t.Name.String.Contains("c__DisplayClass", StringComparison.Ordinal) &&
            required.All(n => t.Fields.Any(f => f.Name == n))).ToList();
        Sites.Expect(closures.Count == 1,
            $"the CompressToStream closure — a display class with ({string.Join(", ", required)}) — " +
            $"matched {closures.Count} nested types, expected 1");
        var closure = closures[0];

        var fileField = closure.Fields.Single(f => f.Name == "file");
        Sites.Expect(fileField.FieldSig.Type.FullName == fileNode.FullName,
            $"the closure's `file` is {fileField.FieldSig.Type.FullName}, expected {fileNode.FullName}");
        foreach (string boolean in new[] { "deduplicate", "allowPfsCompression" })
            Sites.Expect(closure.Fields.Single(f => f.Name == boolean).FieldSig.Type.FullName ==
                         "System.Boolean",
                $"the closure's `{boolean}` is not System.Boolean");

        var encodeBlock = LocalFunction(closure, "EncodeBlock");
        var writeBlock = LocalFunction(closure, "WriteBlock");
        foreach (var m in new[] { encodeBlock, writeBlock })
            Sites.Expect(!m.IsStatic,
                $"{m.Name} is static; the guards' Ldarg_0 assumes the closure instance. " +
                "Re-derive this rewrite against the current IL");

        BypassCompressionCache(encodeBlock, closure, fileField, fullPath, shim);
        BypassBlockReuse(writeBlock, closure, fileField, fullPath, shim);
        Console.WriteLine($"  site 10: filler dedup bypass in {closure.Name}" +
                          $".{encodeBlock.Name} and .{writeBlock.Name}");
    }

    /// <summary>
    /// Site 11. Starts every PlayGo language filler on a 64 KiB boundary in the physical layout,
    /// so no two of them share a block and each language extent measures a whole 65,536 bytes —
    /// what Sony's own tool emits.
    ///
    /// <para>
    /// The placement cursor lives in <c>CreateAdaptiveDedupPlan</c>. Its loop opens with a
    /// cancellation check and then a <c>WholeBlockRaw</c> test; that branch already rounds the
    /// cursor up, but it also reserves <c>file.Length</c>, which for thirty-one 1 MiB fillers
    /// would cost 31 MB of image. This rewrite takes neither that branch nor its reservation: it
    /// only advances the cursor to the next boundary, so a filler still stores its ~16 compressed
    /// bytes and costs one block instead of one megabyte.
    /// </para>
    /// <para>
    /// Inserted at the head of the loop body, after the cancellation check and before the
    /// <c>WholeBlockRaw</c> test:
    /// <code>
    /// if (PlayGoFillerDedup.ShouldBlockAlign(file.FullPath) || wasFiller)
    ///     num = RoundUp(num, 65536L);
    /// wasFiller = PlayGoFillerDedup.ShouldBlockAlign(file.FullPath);
    /// </code>
    /// The <c>|| wasFiller</c> is what makes the LAST filler's extent come out at 65,536 too: it
    /// rounds up once more on the file that FOLLOWS the run, so that file starts on a boundary
    /// instead of sharing the final filler's block and swallowing the padding.
    /// </para>
    /// <para>
    /// <c>ShouldBlockAlign</c> is the same exact-path allow-list as
    /// <see cref="Sites.Shim.FillerDisableDedup"/>: empty unless the CLI has just staged fillers,
    /// so the guard is false for every file in every other build and the cursor is left untouched.
    /// </para>
    /// <para>
    /// REACHABILITY. This is the ADAPTIVE layout path, and most builds never take it.
    /// <c>SelectPhysicalLayout</c> only generates layout candidates when the maximum possible
    /// saving clears <c>_minimumLayoutSavingsBytes</c>/<c>_minimumLayoutSavingsPercent</c>;
    /// below that it logs "Inner layout candidates: skipped" and keeps the ORIGINAL placement,
    /// in which case <c>CreateAdaptiveDedupPlan</c> is never called at all. It is also reached as
    /// a repair path when every generated candidate is rejected on NAPS/U2C capacity. the reference fixture
    /// clears neither bar (maximum saving 393,216 vs a 1,048,576 minimum), so this rewrite is
    /// dormant there and <see cref="AlignLanguageFillerWrites"/> — site 12 — is what aligns the
    /// fillers in the original placement. Both exist on purpose: a title with a different size
    /// profile WILL cross the threshold, and the alignment has to hold on that path too. The two
    /// share <see cref="BlockAlignGuard"/>, so they are one mechanism applied to two cursors.
    /// </para>
    /// <para>
    /// Everything is located by shape — the method by name and signature, the loop head by the one
    /// <c>ldfld WholeBlockRaw</c> in it and the <c>RoundUp(num, 65536)</c> run that follows, the
    /// <c>file</c> and <c>num</c> locals by the instructions that load them. No compiler-generated
    /// ordinal is relied on. Every assertion runs before the first mutation.
    /// </para>
    /// </summary>
    internal static void AlignLanguageFillerBlocks(TypeDef assembler, Sites.Shim shim)
    {
        var fileNode = assembler.NestedTypes.SingleOrDefault(t => t.Name == "FileNode")
            ?? throw new InvalidOperationException(
                "ProsperoPs5InnerImageAssembler.FileNode not found");
        var fullPath = fileNode.Fields.SingleOrDefault(f => f.Name == "FullPath")
            ?? throw new InvalidOperationException("FileNode.FullPath not found");
        Sites.Expect(!fullPath.IsStatic && fullPath.FieldSig.Type.FullName == "System.String",
            $"FileNode.FullPath is {(fullPath.IsStatic ? "static " : "")}" +
            $"{fullPath.FieldSig.Type.FullName}, expected an instance System.String; the guard " +
            "passes it to PlayGoFillerDedup.ShouldBlockAlign(string)");

        var plan = assembler.NestedTypes.SingleOrDefault(t => t.Name == "AdaptiveDedupPlan")
            ?? throw new InvalidOperationException(
                "ProsperoPs5InnerImageAssembler.AdaptiveDedupPlan not found");
        var create = Sites.Method(assembler, "CreateAdaptiveDedupPlan", plan.FullName, 3);
        Sites.Expect(!create.IsStatic,
            "CreateAdaptiveDedupPlan is static; the loop's cancellation check assumes an " +
            "instance method. Re-derive this rewrite against the current IL");

        var body = create.Body.Instructions;

        // The one `ldfld WholeBlockRaw` in the method: the head of the raw-file branch, which sits
        // at the top of the placement loop.
        int raw = -1;
        for (int i = 0; i < body.Count; i++)
        {
            if (!LoadsField(body[i], fileNode, "WholeBlockRaw")) continue;
            Sites.Expect(raw < 0,
                "CreateAdaptiveDedupPlan: more than one `ldfld WholeBlockRaw`, so the head of " +
                "the placement loop is no longer unique. Re-derive this rewrite against the " +
                "current IL");
            raw = i;
        }
        Sites.Expect(raw >= 2 && raw + 6 < body.Count,
            "CreateAdaptiveDedupPlan: no `ldfld WholeBlockRaw` found, so the head of the " +
            "placement loop could not be located. Re-derive this rewrite against the current IL");

        // `file` — the local the WholeBlockRaw test is read off, loaded at an empty stack.
        var file = body[raw - 1].GetLocal(create.Body.Variables);
        Sites.Expect(file is not null && file.Type.FullName == fileNode.FullName,
            "CreateAdaptiveDedupPlan: `ldfld WholeBlockRaw` is not preceded by a load of a " +
            $"{fileNode.Name} local, so the guard's `file` is unknown. Re-derive this rewrite " +
            "against the current IL");
        Sites.Expect(body[raw - 2].Operand is IMethod cancel &&
                     cancel.Name == "ThrowIfCancellationRequested",
            "CreateAdaptiveDedupPlan: the load of `file` is not preceded by " +
            "ThrowIfCancellationRequested, so this is not the head of the placement loop. " +
            "Re-derive this rewrite against the current IL");

        Sites.Expect(body[raw + 1].OpCode == OpCodes.Brfalse ||
                     body[raw + 1].OpCode == OpCodes.Brfalse_S,
            "CreateAdaptiveDedupPlan: `ldfld WholeBlockRaw` is not followed by a brfalse. " +
            "Re-derive this rewrite against the current IL");

        // `num = RoundUp(num, 65536L)` — the raw branch's first statement. It names both the
        // placement cursor and the RoundUp overload the guard reuses.
        var num = body[raw + 2].GetLocal(create.Body.Variables);
        Sites.Expect(num is not null && num.Type.FullName == "System.Int64",
            "CreateAdaptiveDedupPlan: the WholeBlockRaw branch does not open by loading an " +
            "Int64 local, so the placement cursor is unknown. Re-derive this rewrite against " +
            "the current IL");
        Sites.Expect(body[raw + 3].OpCode == OpCodes.Ldc_I4 &&
                     body[raw + 3].Operand is int block && block == 65536,
            "CreateAdaptiveDedupPlan: the WholeBlockRaw branch does not round up to 65536, so " +
            "the block size is no longer what this rewrite aligns to. Re-derive this rewrite " +
            "against the current IL");
        Sites.Expect(body[raw + 4].OpCode == OpCodes.Conv_I8,
            "CreateAdaptiveDedupPlan: the 65536 literal is not widened with conv.i8. Re-derive " +
            "this rewrite against the current IL");
        Sites.Expect(body[raw + 5].Operand is IMethod roundUp &&
                     roundUp.Name == "RoundUp" &&
                     roundUp.MethodSig.RetType.FullName == "System.Int64" &&
                     roundUp.MethodSig.Params.Count == 2,
            "CreateAdaptiveDedupPlan: `RoundUp(num, 65536)` was not found at the head of the " +
            "WholeBlockRaw branch. Re-derive this rewrite against the current IL");
        Sites.Expect(ReferenceEquals(body[raw + 6].GetLocal(create.Body.Variables), num),
            "CreateAdaptiveDedupPlan: the RoundUp result is not stored back into the placement " +
            "cursor. Re-derive this rewrite against the current IL");
        var roundUpMethod = (IMethod)body[raw + 5].Operand;

        // Past the assertions above, both are known non-null.
        Local fileLocal = file!;
        Local cursor = num!;

        // The guard replaces the loop body's entry point, and the explicit `wasFiller = false`
        // replaces the method's; anything branching at either would skip or re-enter them.
        ExpectNotTargeted(create, body, raw - 1, 1, "the head of the placement loop body");
        ExpectNotTargeted(create, body, 0, 1, "the first instruction of CreateAdaptiveDedupPlan");

        var wasFiller = DeclareFillerFlag(create);
        var guard = BlockAlignGuard(fileLocal, cursor, wasFiller, fullPath, roundUpMethod, shim);

        // Back to front: inserting the initialiser first would move `raw`.
        for (int i = 0; i < guard.Length; i++) body.Insert(raw - 1 + i, guard[i]);
        InsertFillerFlagInitialiser(create, body, wasFiller);

        create.Body.UpdateInstructionOffsets();
        Console.WriteLine($"  site 11: 64 KiB filler alignment in {create.Name} " +
                          $"(cursor V_{cursor.Index}, flag V_{wasFiller.Index})");
    }

    /// <summary>
    /// Site 12. The same 64 KiB alignment as site 11, applied to the cursor that actually places
    /// every file in an ordinary build: <c>BuildCore</c>'s physical write loop.
    ///
    /// <para>
    /// Site 11 re-plans offsets in the adaptive layout path, which most builds never enter. The
    /// ORIGINAL placement — the one "Inner layout planning: 100% (selected original)" keeps — is
    /// produced here, as <c>BuildCore</c> walks the files in AFID order:
    /// <code>
    /// if (fileBackedData != null) {
    ///     if (f.WholeBlockRaw) fileBackedDataEnd = RoundUp(fileBackedDataEnd, 65536L);
    ///     f.OnDiskOffset = fileBackedDataEnd;
    ///     fileBackedData.Position = fileBackedDataEnd;   // &lt;- every write lands from here
    ///     … CompressToStream(f, fileBackedData, …) …
    ///     fileBackedDataEnd = fileBackedData.Position;
    /// </code>
    /// The guard goes in immediately after the <c>fileBackedData != null</c> test and BEFORE the
    /// stock <c>WholeBlockRaw</c> round-up, so it advances the cursor before
    /// <c>f.OnDiskOffset</c> is taken from it and before the stream is seeked. That ordering is
    /// the whole correctness argument: <c>OnDiskOffset</c>, and the <c>destination.Position</c>
    /// that <c>CompressToStream</c>'s <c>WriteBlock</c> later records per block, are both read
    /// AFTER the padding, so they are the aligned positions. Advancing the cursor after either
    /// would silently misplace every filler.
    /// </para>
    /// <para>
    /// PADDING CONTENT. Nothing is written for the padding. The cursor is moved forward and the
    /// stream is seeked to it, so the skipped span is the stream's own gap — zeros, on every
    /// backing store this assembler writes to — and it belongs to no file: no inode references
    /// it, no compression block covers it, and it is not in any PlayGo extent. This is the stock
    /// mechanism, not a new one: the <c>WholeBlockRaw</c> branch one instruction below does
    /// exactly the same thing for keystones and executables.
    /// </para>
    /// <para>
    /// The cost is one 64 KiB block per filler — about 2 MB for thirty-one of them — because the
    /// filler still stores only its few compressed bytes. Taking the <c>WholeBlockRaw</c> branch
    /// instead would reserve <c>f.Length</c> and cost 31 MB; this rewrite does not touch it.
    /// </para>
    /// <para>
    /// Everything after the fillers shifts by the padding. That is intended and is not repaired
    /// here: the outer-PFS digests, the NAPS plan, the PlayGo CRC table and the SI are all
    /// derived from the finished image further down the pipeline, so they follow on their own.
    /// <c>verify --full</c> is the check that they did.
    /// </para>
    /// <para>
    /// Inert when nothing is staged: <c>ShouldBlockAlign</c> is the same exact-path allow-list as
    /// site 10's, empty unless the CLI has just staged zero fillers. With it empty the guard is
    /// false for every file, <c>wasFiller</c> never becomes true, the cursor is never advanced and
    /// the image is byte-for-byte what it was before the patch.
    /// </para>
    /// <para>
    /// The <c>fileBackedData == null</c> arm — the buffered path — is deliberately left alone: it
    /// has no physical cursor to align, and which arm is taken is fixed for the whole loop.
    /// </para>
    /// <para>
    /// THE LAYOUT OPTIMISER WILL UNDO THIS unless it is switched off. The padding is slack, and
    /// reclaiming slack is exactly what <c>SelectPhysicalLayout</c>'s candidates are for. On
    /// the reference fixture the untouched build has maximum possible savings of 393,216 bytes against a
    /// 1,048,576 minimum, so the planner logs "Inner layout candidates: skipped" and keeps the
    /// original placement. Adding ~2 MB of alignment padding pushes the savings OVER that
    /// threshold, the planner then builds candidates, selects "alignment", repacks ~558 MB and
    /// compacts the image right back — fillers re-packed into one block, 64-byte extents, and a
    /// package slightly SMALLER than the baseline. The alignment only survives with the two
    /// candidate generators disabled, which the CLI exposes as <c>--no-coalescing</c> and
    /// <c>--no-relocation-align</c> (both are ON by default in fpkg, though the library defaults
    /// them to false). Disabling them costs nothing on a build that was already below the
    /// threshold: with this rewrite absent, the flags produce a byte-identical package.
    /// </para>
    /// </summary>
    internal static void AlignLanguageFillerWrites(TypeDef assembler, Sites.Shim shim)
    {
        var fileNode = assembler.NestedTypes.SingleOrDefault(t => t.Name == "FileNode")
            ?? throw new InvalidOperationException(
                "ProsperoPs5InnerImageAssembler.FileNode not found");
        var fullPath = fileNode.Fields.SingleOrDefault(f => f.Name == "FullPath")
            ?? throw new InvalidOperationException("FileNode.FullPath not found");
        Sites.Expect(!fullPath.IsStatic && fullPath.FieldSig.Type.FullName == "System.String",
            $"FileNode.FullPath is {(fullPath.IsStatic ? "static " : "")}" +
            $"{fullPath.FieldSig.Type.FullName}, expected an instance System.String; the guard " +
            "passes it to PlayGoFillerDedup.ShouldBlockAlign(string)");

        var build = Sites.Method(assembler, "BuildCore",
            "LibProsperoPkg.PFS.ProsperoPs5InnerImageResult", 4);
        Sites.Expect(!build.IsStatic,
            "BuildCore is static; re-derive this rewrite against the current IL");

        var body = build.Body.Instructions;
        var locals = build.Body.Variables;

        // The placement run, matched whole because `ldfld WholeBlockRaw; brfalse; RoundUp` occurs
        // more than once in BuildCore. Only the placement copy is followed by
        // `f.OnDiskOffset = cursor; stream.Position = cursor` — the trailing copy stores into
        // OnDiskData instead, and the buffered arm has no seek at all.
        //
        //   [i+0] ldloc  f              [i+7]  stloc  cursor
        //   [i+1] ldfld  WholeBlockRaw  [i+8]  ldloc  f
        //   [i+2] brfalse                [i+9]  ldloc  cursor
        //   [i+3] ldloc  cursor          [i+10] stfld  FileNode::OnDiskOffset
        //   [i+4] ldc.i4 65536           [i+11] ldloc  stream
        //   [i+5] conv.i8                [i+12] ldloc  cursor
        //   [i+6] call   RoundUp         [i+13] callvirt Stream::set_Position
        int at = -1;
        Local? file = null, cursor = null;
        IMethod? roundUp = null;
        for (int i = 0; i + 13 < body.Count; i++)
        {
            if (!LoadsField(body[i + 1], fileNode, "WholeBlockRaw")) continue;
            if (body[i + 2].OpCode != OpCodes.Brfalse &&
                body[i + 2].OpCode != OpCodes.Brfalse_S) continue;

            var f = body[i].GetLocal(locals);
            var c = body[i + 3].GetLocal(locals);
            if (f is null || c is null) continue;
            if (f.Type.FullName != fileNode.FullName || c.Type.FullName != "System.Int64") continue;

            if (body[i + 4].OpCode != OpCodes.Ldc_I4 ||
                body[i + 4].Operand is not int size || size != 65536) continue;
            if (body[i + 5].OpCode != OpCodes.Conv_I8) continue;
            if (body[i + 6].Operand is not IMethod r || r.Name != "RoundUp" ||
                r.MethodSig.RetType.FullName != "System.Int64" ||
                r.MethodSig.Params.Count != 2) continue;
            if (!ReferenceEquals(body[i + 7].GetLocal(locals), c)) continue;

            // The tail that makes this the PLACEMENT run rather than a bookkeeping round-up.
            if (!ReferenceEquals(body[i + 8].GetLocal(locals), f)) continue;
            if (!ReferenceEquals(body[i + 9].GetLocal(locals), c)) continue;
            if (body[i + 10].OpCode != OpCodes.Stfld ||
                body[i + 10].Operand is not IField off || off.Name != "OnDiskOffset" ||
                off.DeclaringType?.FullName != fileNode.FullName) continue;
            var stream = body[i + 11].GetLocal(locals);
            if (stream is null || stream.Type.FullName != "System.IO.Stream") continue;
            if (!ReferenceEquals(body[i + 12].GetLocal(locals), c)) continue;
            if (body[i + 13].Operand is not IMethod seek || seek.Name != "set_Position") continue;

            Sites.Expect(at < 0,
                "BuildCore: more than one physical-placement run (`RoundUp` then `OnDiskOffset` " +
                "then `Stream.Position`), so the write cursor is no longer unique. Re-derive " +
                "this rewrite against the current IL");
            at = i;
            file = f;
            cursor = c;
            roundUp = r;
        }
        Sites.Expect(at >= 0,
            "BuildCore: the physical-placement run (`RoundUp` then `OnDiskOffset` then " +
            "`Stream.Position`) was not found, so the write cursor could not be located. " +
            "Re-derive this rewrite against the current IL");

        // The guard takes over the entry to the placement run, and the initialiser the entry to
        // the method; a branch at either would skip or re-enter them.
        ExpectNotTargeted(build, body, at, 1, "the head of BuildCore's placement run");
        ExpectNotTargeted(build, body, 0, 1, "the first instruction of BuildCore");

        var wasFiller = DeclareFillerFlag(build);
        var guard = BlockAlignGuard(file!, cursor!, wasFiller, fullPath, roundUp!, shim);

        // Back to front: inserting the initialiser first would move `at`.
        for (int i = 0; i < guard.Length; i++) body.Insert(at + i, guard[i]);
        InsertFillerFlagInitialiser(build, body, wasFiller);

        build.Body.UpdateInstructionOffsets();
        Console.WriteLine($"  site 12: 64 KiB filler alignment in {build.Name} " +
                          $"(cursor V_{cursor!.Index}, flag V_{wasFiller.Index})");
    }

    /// <summary>
    /// The one guard both alignment sites insert, over whichever cursor the site owns:
    /// <code>
    /// if (PlayGoFillerDedup.ShouldBlockAlign(file.FullPath) || wasFiller)
    ///     cursor = RoundUp(cursor, 65536L);
    /// wasFiller = PlayGoFillerDedup.ShouldBlockAlign(file.FullPath);
    /// </code>
    /// The <c>|| wasFiller</c> is what makes the LAST filler's extent come out a whole block: it
    /// rounds up once more on the file that FOLLOWS the run, so that file starts on a boundary
    /// instead of sharing the final filler's block and swallowing the padding. Emitted fresh at
    /// each site, because an Instruction object may appear in a body only once.
    /// </summary>
    private static Instruction[] BlockAlignGuard(
        Local file, Local cursor, Local wasFiller, FieldDef fullPath, IMethod roundUp,
        Sites.Shim shim)
    {
        // Two branch targets inside the guard: the round-up, and the tail that records the flag.
        var alignHere = OpCodes.Ldloc.ToInstruction(cursor);
        var recordFlag = OpCodes.Ldloc.ToInstruction(file);
        return
        [
            // if (ShouldBlockAlign(file.FullPath)) goto alignHere;
            OpCodes.Ldloc.ToInstruction(file),
            OpCodes.Ldfld.ToInstruction(fullPath),
            OpCodes.Call.ToInstruction(shim.FillerBlockAlign),
            OpCodes.Brtrue.ToInstruction(alignHere),
            // if (!wasFiller) goto recordFlag;
            OpCodes.Ldloc.ToInstruction(wasFiller),
            OpCodes.Brfalse.ToInstruction(recordFlag),
            // cursor = RoundUp(cursor, 65536L);
            alignHere,
            OpCodes.Ldc_I4.ToInstruction(65536),
            OpCodes.Conv_I8.ToInstruction(),
            OpCodes.Call.ToInstruction(roundUp),
            OpCodes.Stloc.ToInstruction(cursor),
            // wasFiller = ShouldBlockAlign(file.FullPath);
            recordFlag,
            OpCodes.Ldfld.ToInstruction(fullPath),
            OpCodes.Call.ToInstruction(shim.FillerBlockAlign),
            OpCodes.Stloc.ToInstruction(wasFiller),
        ];
    }

    /// <summary>Adds the guard's edge-detection flag to a method's locals.</summary>
    private static Local DeclareFillerFlag(MethodDef method)
    {
        var flag = new Local(method.Module.CorLibTypes.Boolean);
        method.Body.Variables.Add(flag);
        return flag;
    }

    /// <summary>
    /// Prepends <c>wasFiller = false;</c> — written out rather than left to <c>.locals init</c>,
    /// so the guard does not depend on the flag the body happens to carry.
    /// </summary>
    private static void InsertFillerFlagInitialiser(
        MethodDef method, IList<Instruction> body, Local flag)
    {
        body.Insert(0, OpCodes.Ldc_I4_0.ToInstruction());
        body.Insert(1, OpCodes.Stloc.ToInstruction(flag));
    }

    /// <summary>
    /// Finds one of CompressToStream's local functions. Roslyn emits them as
    /// <c>&lt;CompressToStream&gt;g__EncodeBlock|68_1</c>; the ordinal after the pipe moves between
    /// releases, so only the part before it is matched — trap #7.
    /// </summary>
    private static MethodDef LocalFunction(TypeDef closure, string name)
    {
        var found = closure.Methods.Where(m =>
            m.HasBody && m.Name.String.Contains("g__" + name + "|", StringComparison.Ordinal)).ToList();
        Sites.Expect(found.Count == 1,
            $"{closure.Name}.{name} matched {found.Count} local functions, expected 1");
        return found[0];
    }

    /// <summary>
    /// The instructions that leave <c>CompressionCacheAllowed(file.FullPath)</c> on the stack.
    /// Emitted fresh at each site, because an Instruction object may appear in a body only once.
    /// </summary>
    private static Instruction[] CacheAllowedTest(FieldDef file, FieldDef fullPath, Sites.Shim shim) =>
    [
        OpCodes.Ldarg_0.ToInstruction(),
        OpCodes.Ldfld.ToInstruction(file),
        OpCodes.Ldfld.ToInstruction(fullPath),
        OpCodes.Call.ToInstruction(shim.FillerCacheAllowed),
    ];

    /// <summary>
    /// The instructions that leave <c>ShouldDisableDedup(file.FullPath, deduplicate)</c> on the
    /// stack. The caller's own <c>deduplicate</c> is passed in rather than being second-guessed:
    /// the shim is the single place that decides, and it can only ever turn true into false.
    /// </summary>
    private static Instruction[] DisableDedupTest(
        FieldDef file, FieldDef fullPath, FieldDef deduplicate, Sites.Shim shim) =>
    [
        OpCodes.Ldarg_0.ToInstruction(),
        OpCodes.Ldfld.ToInstruction(file),
        OpCodes.Ldfld.ToInstruction(fullPath),
        OpCodes.Ldarg_0.ToInstruction(),
        OpCodes.Ldfld.ToInstruction(deduplicate),
        OpCodes.Call.ToInstruction(shim.FillerDisableDedup),
    ];

    private static bool LoadsField(Instruction ins, TypeDef declaring, string name) =>
        ins.OpCode == OpCodes.Ldfld && ins.Operand is IField f && f.Name == name &&
        f.DeclaringType?.FullName == declaring.FullName;

    private static bool IsBrTrue(Instruction ins) =>
        ins.OpCode == OpCodes.Brtrue || ins.OpCode == OpCodes.Brtrue_S;

    /// <summary>
    /// <c>if (!CompressKernelBlocks || !allowPfsCompression)</c> takes the uncached path; the
    /// second test is an <c>ldfld allowPfsCompression</c> followed by the <c>brtrue</c> that jumps
    /// INTO the cache. ANDing the predicate onto that bool turns it into
    /// <c>allowPfsCompression &amp;&amp; dedupAllowed</c>, which is stack-neutral and leaves the
    /// branch instruction itself — and therefore every reference to it — untouched.
    /// </summary>
    private static void BypassCompressionCache(
        MethodDef encodeBlock, TypeDef closure, FieldDef file, FieldDef fullPath, Sites.Shim shim)
    {
        var body = encodeBlock.Body.Instructions;

        int at = -1;
        for (int i = 0; i + 1 < body.Count; i++)
        {
            if (!LoadsField(body[i], closure, "allowPfsCompression") || !IsBrTrue(body[i + 1])) continue;
            Sites.Expect(at < 0,
                $"{encodeBlock.Name}: more than one `ldfld allowPfsCompression; brtrue` — the " +
                "compression-cache test is no longer unique. Re-derive this rewrite against the " +
                "current IL");
            at = i;
        }
        Sites.Expect(at >= 0,
            $"{encodeBlock.Name}: no `ldfld allowPfsCompression; brtrue` found, so the " +
            "compression-cache test could not be located. Re-derive this rewrite against the " +
            "current IL");

        // Anything branching straight at the brtrue would arrive after the inserted guard and
        // leave the `and` a value short.
        ExpectNotTargeted(encodeBlock, body, at + 1, 1, "the allowPfsCompression cache test");

        var guard = CacheAllowedTest(file, fullPath, shim)
            .Append(OpCodes.And.ToInstruction()).ToArray();
        for (int i = 0; i < guard.Length; i++) body.Insert(at + 1 + i, guard[i]);
        encodeBlock.Body.UpdateInstructionOffsets();
    }

    /// <summary>
    /// Two guards in WriteBlock, applied back to front so the first one's insertion cannot move
    /// the second one's index. Both assertions run before either mutation.
    /// </summary>
    private static void BypassBlockReuse(
        MethodDef writeBlock, TypeDef closure, FieldDef file, FieldDef fullPath, Sites.Shim shim)
    {
        var body = writeBlock.Body.Instructions;

        int tryReuse = Sole(writeBlock, body, "TryReuse");
        int remember = Sole(writeBlock, body, "Remember");
        Sites.Expect(tryReuse + 1 < body.Count && IsBrTrue(body[tryReuse + 1]),
            $"{writeBlock.Name}: TryReuse is not followed by a brtrue, so the miss path could not " +
            "be located. Re-derive this rewrite against the current IL");

        // Where control has to land when the guard fires: the first instruction of the dedup MISS
        // path, reached with an empty stack, which writes at destination.Position and records it.
        var missPath = body[tryReuse + 2];
        var afterRemember = body[remember + 1];

        // `if (!deduplicate)` — the only `ldfld deduplicate` whose branch is a brtrue; the second
        // one, guarding the cache Compact, is a brfalse.
        int dedupTest = -1;
        for (int i = 0; i + 1 < body.Count; i++)
        {
            if (!LoadsField(body[i], closure, "deduplicate") || !IsBrTrue(body[i + 1])) continue;
            Sites.Expect(dedupTest < 0,
                $"{writeBlock.Name}: more than one `ldfld deduplicate; brtrue`. Re-derive this " +
                "rewrite against the current IL");
            dedupTest = i;
        }
        Sites.Expect(dedupTest >= 1,
            $"{writeBlock.Name}: no `ldfld deduplicate; brtrue` found. Re-derive this rewrite " +
            "against the current IL");
        Sites.Expect(body[dedupTest - 1].OpCode == OpCodes.Ldarg_0,
            $"{writeBlock.Name}: `ldfld deduplicate` is not preceded by ldarg.0, so the guard " +
            "would not be inserted at an empty-stack point. Re-derive this rewrite against the " +
            "current IL");
        Sites.Expect(dedupTest - 1 < tryReuse && tryReuse < remember,
            $"{writeBlock.Name}: the deduplicate test, TryReuse and Remember are out of the " +
            "expected order. Re-derive this rewrite against the current IL");

        // Remember's six pushed arguments: ldarg.0, ldfld deduplicator, ldloc (chunk),
        // ldarga result, call get_Integrity, ldloc (offset). The guard goes at the head of that
        // run, which is the last empty-stack point before the call.
        int rememberArgs = remember - 6;
        Sites.Expect(rememberArgs > dedupTest &&
                     body[rememberArgs].OpCode == OpCodes.Ldarg_0 &&
                     LoadsField(body[rememberArgs + 1], closure, "deduplicator"),
            $"{writeBlock.Name}: Remember's argument run does not start with " +
            "`ldarg.0; ldfld deduplicator`, so the guard's insertion point is unknown. " +
            "Re-derive this rewrite against the current IL");

        ExpectNotTargeted(writeBlock, body, rememberArgs, 1, "the head of Remember's argument run");
        ExpectNotTargeted(writeBlock, body, dedupTest - 1, 1, "the head of the deduplicate test");

        var deduplicate = closure.Fields.Single(f => f.Name == "deduplicate");

        // Back to front: inserting at dedupTest-1 first would invalidate rememberArgs.
        // `if (ShouldDisableDedup(file.FullPath, deduplicate)) goto afterRemember;`
        var skipRemember = DisableDedupTest(file, fullPath, deduplicate, shim)
            .Append(OpCodes.Brtrue.ToInstruction(afterRemember)).ToArray();
        for (int i = 0; i < skipRemember.Length; i++) body.Insert(rememberArgs + i, skipRemember[i]);

        // `if (ShouldDisableDedup(file.FullPath, deduplicate)) goto missPath;` — placed before the
        // stock `if (!deduplicate)` test and handed that same flag, so a call site that asked for
        // no de-duplication keeps the stock branch and its -1 sentinel, bit for bit.
        var toMissPath = DisableDedupTest(file, fullPath, deduplicate, shim)
            .Append(OpCodes.Brtrue.ToInstruction(missPath)).ToArray();
        for (int i = 0; i < toMissPath.Length; i++) body.Insert(dedupTest - 1 + i, toMissPath[i]);

        writeBlock.Body.UpdateInstructionOffsets();
    }

    /// <summary>The index of the one call to <paramref name="name"/> on the deduplicator.</summary>
    private static int Sole(MethodDef method, IList<Instruction> body, string name)
    {
        int found = -1;
        for (int i = 0; i < body.Count; i++)
        {
            if (body[i].Operand is not IMethod m || m.Name != name) continue;
            if (m.DeclaringType?.Name.String.Contains("InnerBlockDeduplicator",
                    StringComparison.Ordinal) != true) continue;
            Sites.Expect(found < 0,
                $"{method.Name}: more than one InnerBlockDeduplicator.{name} call. Re-derive " +
                "this rewrite against the current IL");
            found = i;
        }
        Sites.Expect(found >= 0,
            $"{method.Name}: no InnerBlockDeduplicator.{name} call found. Re-derive this rewrite " +
            "against the current IL");
        return found;
    }

    /// <summary>
    /// True for instructions that consume nothing and leave exactly one value on the stack.
    /// Pop0 alone is not enough: nop, br and endfinally are Pop0/Push0, and nopping one of those
    /// in an argument run would leave the stack a value short — invalid IL that only surfaces as
    /// an InvalidProgramException at runtime.
    /// </summary>
    private static bool IsSingleValuePush(OpCode opCode) =>
        opCode.StackBehaviourPop == StackBehaviour.Pop0 &&
        opCode.StackBehaviourPush is StackBehaviour.Push1 or StackBehaviour.Pushi
            or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8
            or StackBehaviour.Pushref;

    /// <summary>
    /// Asserts that no branch, switch case, or exception-handler boundary in <paramref name="method"/>
    /// points at any instruction in the window [start, start + count).
    /// </summary>
    private static void ExpectNotTargeted(
        MethodDef method, IList<Instruction> body, int start, int count, string what)
    {
        var window = new HashSet<Instruction>();
        for (int i = start; i < start + count; i++) window.Add(body[i]);

        foreach (var ins in body)
        {
            if (ins.Operand is Instruction target)
                Sites.Expect(!window.Contains(target),
                    $"{what} is the target of a {ins.OpCode} branch; re-derive this rewrite " +
                    "against the current IL");
            else if (ins.Operand is Instruction[] targets)
                foreach (var t in targets)
                    Sites.Expect(!window.Contains(t),
                        $"{what} is a case of a {ins.OpCode}; re-derive this rewrite " +
                        "against the current IL");
        }

        foreach (var eh in method.Body.ExceptionHandlers)
            foreach (var (name, bound) in new (string, Instruction?)[]
                     {
                         ("TryStart", eh.TryStart), ("TryEnd", eh.TryEnd),
                         ("HandlerStart", eh.HandlerStart), ("HandlerEnd", eh.HandlerEnd),
                         ("FilterStart", eh.FilterStart),
                     })
                Sites.Expect(bound is null || !window.Contains(bound),
                    $"{what} is an exception-handler {name} boundary; re-derive this rewrite " +
                    "against the current IL");
    }
}
