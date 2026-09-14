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

                Sites.Expect(ctorRef.MethodSig.Params.Count == 6,
                    $"expected the 6-argument FileStream constructor, got {ctorRef.MethodSig.Params.Count}");
                Sites.Expect(ctorRef.MethodSig.Params[0].FullName == "System.String",
                    "the FileStream constructor's first argument is not the path");
                Sites.Expect(i >= 5, "not enough instructions before newobj FileStream");

                // Nothing may jump into the middle of the argument run, and no exception-handler
                // boundary may sit on it; either would make the disposal below break control flow.
                ExpectNotTargeted(method, body, i - 5, 6,
                    "the newobj FileStream argument run");

                for (int back = 1; back <= 5; back++)
                {
                    var arg = body[i - back];
                    Sites.Expect(IsSingleValuePush(arg.OpCode),
                        $"argument {back} before newobj FileStream is {arg.OpCode}, which is not a " +
                        "simple single-value push; re-derive this rewrite against the current IL");
                    arg.OpCode = OpCodes.Nop; arg.Operand = null;
                }

                // Mutated in place rather than replaced, so nothing that referenced this
                // instruction object (a branch, a handler bound) is left pointing at an orphan.
                body[i].OpCode = OpCodes.Call;
                body[i].Operand = shim.OpenOrFile;
                WidenFileStreamLocal(method, body, i);
                redirected++;
            }
            method.Body.UpdateInstructionOffsets();
        }
        Sites.Expect(redirected == 1,
            $"expected exactly one FileStream construction in FSFile to redirect, found {redirected}");
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
