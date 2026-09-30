using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    // Shared by the real Unity Editor NUnit adapter and the host compiler suite.
    // Every test reopens independently written DLL bytes; none creates MethodInfo
    // stand-ins or claims to have executed the native resolver.
    public static class R03EvolutionContractCases
    {
        private sealed class Case
        {
            public string Id;
            public Action<ModuleDef, TypeDef> Baseline;
            public Action<ModuleDef, TypeDef> Target;
            public Action<ModuleDefMD, ModuleDefMD, byte[], byte[]> Verify;
        }

        public static string[] CaseIds { get { return Cases().Select(c => c.Id).ToArray(); } }

        public static void Run(string id, Action<string, byte[]> capture = null)
        {
            var item = Cases().Single(c => c.Id == id);
            byte[] before = Build(item.Baseline), after = Build(item.Target);
            if (capture != null) { capture("baseline.dll", before); capture("target.dll", after); }
            using (var baseline = ModuleDefMD.Load(before))
            using (var target = ModuleDefMD.Load(after))
                item.Verify(baseline, target, before, after);
        }

        private static IEnumerable<Case> Cases()
        {
            yield return Layout("L01-private-reference-append", null, (m, t) => Field(t, "extra", m.CorLibTypes.Object), false);
            yield return Layout("L02-private-string-append", null, (m, t) => Field(t, "extra", m.CorLibTypes.String), false);
            yield return Layout("L03-private-primitive-append-needs-native", null, (m, t) => Field(t, "extra", m.CorLibTypes.Int64), true, true);
            yield return Layout("L04-public-primitive-append", null, (m, t) => Field(t, "extra", m.CorLibTypes.Int32, FieldAttributes.Public), false);
            yield return Layout("L05-value-type-growth", Value, (m, t) => { Value(m, t); Field(t, "extra", m.CorLibTypes.Int32); }, false);
            yield return Layout("L06-generic-definition-growth", GenericType, (m, t) => { GenericType(m, t); Field(t, "extra", m.CorLibTypes.Int32); }, false);
            yield return Layout("L07-instance-field-removal", null, (m, t) => t.Fields.Clear(), false);
            yield return Layout("L08-instance-field-type-change", null, (m, t) => t.Fields[0].FieldType = m.CorLibTypes.Int64, false);
            yield return Layout("L09-instance-field-order-change", (m, t) => Field(t, "second", m.CorLibTypes.Int64),
                (m, t) => { var old = t.Fields[0]; t.Fields.Clear(); Field(t, "second", m.CorLibTypes.Int64); t.Fields.Add(old); }, false);
            yield return Layout("L10-static-field-is-not-instance-layout", null,
                (m, t) => Field(t, "cache", m.CorLibTypes.Object, FieldAttributes.Private | FieldAttributes.Static), true, false);
            yield return Layout("L11-existing-field-visibility-is-not-storage", null, (m, t) => t.Fields[0].Attributes = FieldAttributes.Public, true, false);
            yield return Layout("L12-interface-addition", null, Interface, false);
            yield return Layout("L13-explicit-offset-change", (m, t) => Explicit(t, 0), (m, t) => Explicit(t, 8), false);
            yield return Layout("L14-packing-needs-native", (m, t) => t.PackingSize = 4, (m, t) => t.PackingSize = 8, true, true);
            yield return Layout("L15-class-size-needs-native", null, (m, t) => t.ClassSize = 32, true, true);
            yield return Layout("L16-layout-kind-change", null, (m, t) => Explicit(t, 0), false);
            yield return Layout("L17-type-constraint-change", GenericType,
                (m, t) => { GenericType(m, t); t.GenericParameters[0].Flags |= GenericParamAttributes.ReferenceTypeConstraint; }, false);
            yield return Layout("L18-method-only-change-needs-allocation-proof", null, InsertVirtual, true, false);
            yield return new Case { Id = "L19-new-type-no-baseline-proof", Target = (m, t) => m.Types.Add(new TypeDefUser("R03", "Added", m.CorLibTypes.Object.TypeDefOrRef)),
                Verify = (a, b, ab, bb) => { var r = NativeLayoutAdmissionValidator.Analyze(ab, bb); Require(r.editorAccepted, "Added type not a layout rewrite"); Require(r.types.Count(x => x.decision == "NoBaselineCounterpart") == 1, "Exact added type"); Require(!r.pureInterpreterExpansionEnabled && !r.nativeProofExecuted, "No expansion or native proof inferred"); } };
            yield return new Case { Id = "L20-analysis-does-not-load-assembly-or-cctor", Target = ThrowingCctor,
                Verify = (a, b, ab, bb) => { var names = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.FullName).ToArray(); var r = NativeLayoutAdmissionValidator.Analyze(ab, bb); Require(r.editorAccepted && !r.nativeProofExecuted, "Cctor not executed"); Require(names.SequenceEqual(AppDomain.CurrentDomain.GetAssemblies().Select(x => x.FullName)), "Metadata analysis did not load fixture assembly"); } };

            yield return Method("M01-token-and-virtual-order-not-identity", null, InsertVirtual, true, true);
            yield return Method("M02-no-inline-not-identity-or-contract", null, (m, t) => Keep(t).ImplAttributes |= MethodImplAttributes.NoInlining, true, true);
            yield return Method("M03-visibility-separate-compatibility", null, (m, t) => Keep(t).Attributes = (Keep(t).Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Private, true, false);
            yield return Method("M04-dispatch-separate-compatibility", null, (m, t) => Keep(t).Attributes &= ~(MethodAttributes.Virtual | MethodAttributes.NewSlot), true, false);
            yield return Method("M05-static-calling-shape", null, (m, t) => { var k = Keep(t); k.Attributes = MethodAttributes.Public | MethodAttributes.Static; k.MethodSig = MethodSig.CreateStatic(m.CorLibTypes.Int32); }, false, false);
            yield return Method("M06-byref-signature", Parameter, (m, t) => { Parameter(m, t); Keep(t).MethodSig.Params[0] = new ByRefSig(m.CorLibTypes.Int32); }, false, false);
            yield return Method("M07-required-modifier-identity", Parameter, (m, t) => { Parameter(m, t); Keep(t).MethodSig.Params[0] = new CModReqdSig(new TypeRefUser(m, "System.Runtime.CompilerServices", "IsVolatile", m.CorLibTypes.AssemblyRef), m.CorLibTypes.Int32); }, false, false);
            yield return Method("M08-method-constraint-separate-compatibility", GenericMethod,
                (m, t) => { GenericMethod(m, t); Keep(t).GenericParameters[0].Flags |= GenericParamAttributes.ReferenceTypeConstraint; }, true, false);
            yield return Method("M09-parameter-name-not-contract", Parameter,
                (m, t) => { Parameter(m, t); Keep(t).ParamDefs.Add(new ParamDefUser("named", 1, (ParamAttributes)0)); }, true, true);
            yield return Method("M10-parameter-attribute-separate-compatibility", Parameter,
                (m, t) => { Parameter(m, t); Keep(t).ParamDefs.Add(new ParamDefUser("named", 1, ParamAttributes.In)); }, true, false);
            yield return Method("M11-generic-composite-local-numbering", CompositeGenericMethod,
                (m, t) => { CompositeGenericMethod(m, t); InsertVirtual(m, t); }, true, true);
            yield return Method("M12-return-type-is-signature", null,
                (m, t) => { Keep(t).MethodSig.RetType = m.CorLibTypes.Int64; Keep(t).Body.Instructions.Insert(1, Instruction.Create(OpCodes.Conv_I8)); }, false, false);
            yield return new Case { Id = "M13-missing-method-rejected", Target = (m, t) => t.Methods.Remove(Keep(t)),
                Verify = (a, b, ab, bb) => Expect("LogicalMethodNotFound", () => LogicalMethodIdentity.ResolveCompatible(Keep(Node(a)), Node(b))) };
            yield return Method("M14-declaring-type-constraints", GenericType,
                (m, t) => { GenericType(m, t); t.GenericParameters[0].Flags |= GenericParamAttributes.ReferenceTypeConstraint; }, true, false);
            yield return Method("M15-optimization-hints-independent", null,
                (m, t) => Keep(t).ImplAttributes |= MethodImplAttributes.NoOptimization | MethodImplAttributes.AggressiveInlining | (MethodImplAttributes)0x0200, true, true);
        }

        private static Case Layout(string id, Action<ModuleDef, TypeDef> baseline, Action<ModuleDef, TypeDef> target, bool accepted, bool? changed = null)
        {
            return new Case { Id = id, Baseline = baseline, Target = target, Verify = (a, b, ab, bb) =>
            {
                var report = NativeLayoutAdmissionValidator.Analyze(ab, bb);
                Require(report.editorAccepted == accepted, id + " admission");
                Require(!report.nativeProofExecuted && !report.pureInterpreterExpansionEnabled && report.allocationProofStillRequired, "Editor cannot issue native certificates");
                Require(report.baselineDllSha256 == ShadowHash.Bytes(ab) && report.targetDllSha256 == ShadowHash.Bytes(bb), "Exact byte identity");
                if (!accepted) Expect("NativeLayoutIncompatible", report.RequireEditorAdmission);
                else
                {
                    report.RequireEditorAdmission();
                    var row = report.types.Single(x => x.typeKey == AssemblyIdentityUtil.TypeKey(Node(b)));
                    Require(row.decision == "NeedsNativeProof", "Positive screen is not native compatibility");
                    if (changed.HasValue) Require(row.metadataChanged == changed.Value && row.prePublicationNativeProofRequired == changed.Value, "Correct proof phase");
                }
            } };
        }

        private static Case Method(string id, Action<ModuleDef, TypeDef> baseline, Action<ModuleDef, TypeDef> target, bool equal, bool compatible)
        {
            return new Case { Id = id, Baseline = baseline, Target = target, Verify = (a, b, ab, bb) =>
            {
                var oldMethod = Keep(Node(a)); var newMethod = Keep(Node(b));
                Require((LogicalMethodIdentity.Key(oldMethod) == LogicalMethodIdentity.Key(newMethod)) == equal, id + " logical key");
                Require((LogicalMethodIdentity.CompatibilityDifferences(oldMethod, newMethod).Length == 0) == compatible, id + " separate compatibility");
                if (compatible) Require(object.ReferenceEquals(LogicalMethodIdentity.ResolveCompatible(oldMethod, Node(b)), newMethod), "Resolved actual reopened method definition");
                else Expect(equal ? "MethodCompatibility" : "LogicalMethodNotFound", () => LogicalMethodIdentity.ResolveCompatible(oldMethod, Node(b)));
                if (id == "M01-token-and-virtual-order-not-identity" || id == "M11-generic-composite-local-numbering")
                    Require(oldMethod.MDToken.Raw != newMethod.MDToken.Raw, "The real method row moved");
            } };
        }

        private static byte[] Build(Action<ModuleDef, TypeDef> edit)
        {
            using (var module = new ModuleDefUser("R03Contract.dll"))
            {
                module.Kind = ModuleKind.Dll; module.RuntimeVersion = "v4.0.30319";
                new AssemblyDefUser("R03Contract", new Version(1, 0, 0, 0)).Modules.Add(module);
                var type = new TypeDefUser("R03", "Node", module.CorLibTypes.Object.TypeDefOrRef)
                    { Attributes = TypeAttributes.Public | TypeAttributes.BeforeFieldInit };
                module.Types.Add(type); Field(type, "stable", module.CorLibTypes.Int32);
                var ctor = new MethodDefUser(".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void), MethodImplAttributes.IL,
                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.HideBySig);
                ctor.Body = new CilBody(); ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void), module.CorLibTypes.Object.TypeDefOrRef)));
                ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); type.Methods.Add(ctor);
                type.Methods.Add(IntMethod(module, "Keep", false));
                if (edit != null) edit(module, type);
                using (var bytes = new MemoryStream()) { module.Write(bytes); return bytes.ToArray(); }
            }
        }
        private static MethodDef IntMethod(ModuleDef module, string name, bool isStatic)
        {
            var method = new MethodDefUser(name, isStatic ? MethodSig.CreateStatic(module.CorLibTypes.Int32) : MethodSig.CreateInstance(module.CorLibTypes.Int32),
                MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.HideBySig | (isStatic ? MethodAttributes.Static : MethodAttributes.Virtual | MethodAttributes.NewSlot));
            method.Body = new CilBody(); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 41)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); return method;
        }
        private static void Field(TypeDef type, string name, TypeSig signature, FieldAttributes attributes = FieldAttributes.Private)
        { type.Fields.Add(new FieldDefUser(name, new FieldSig(signature), attributes)); }
        private static TypeDef Node(ModuleDef module) { return module.GetTypes().Single(t => t.Namespace == "R03" && t.Name.String.StartsWith("Node", StringComparison.Ordinal)); }
        private static MethodDef Keep(TypeDef type) { return type.Methods.Single(m => m.Name == "Keep"); }
        private static void InsertVirtual(ModuleDef module, TypeDef type) { type.Methods.Insert(1, IntMethod(module, "Before", false)); }
        private static void Parameter(ModuleDef module, TypeDef type) { Keep(type).MethodSig.Params.Add(module.CorLibTypes.Int32); }
        private static void GenericType(ModuleDef module, TypeDef type)
        { type.Name = "Node`1"; type.GenericParameters.Add(new GenericParamUser(0, (GenericParamAttributes)0, "T")); }
        private static void GenericMethod(ModuleDef module, TypeDef type)
        { var method = Keep(type); method.GenericParameters.Add(new GenericParamUser(0, (GenericParamAttributes)0, "T")); method.MethodSig.GenParamCount = 1; method.MethodSig.CallingConvention |= CallingConvention.Generic; }
        private static void CompositeGenericMethod(ModuleDef module, TypeDef type)
        {
            GenericMethod(module, type);
            var list = new ClassSig(new TypeRefUser(module, "System.Collections.Generic", "List`1", module.CorLibTypes.AssemblyRef));
            Keep(type).MethodSig.Params.Add(new GenericInstSig(list, new SZArraySig(new GenericMVar(0))));
        }
        private static void Value(ModuleDef module, TypeDef type)
        {
            type.BaseType = new TypeRefUser(module, "System", "ValueType", module.CorLibTypes.AssemblyRef);
            type.Attributes = TypeAttributes.Public | TypeAttributes.SequentialLayout | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit;
            type.Methods.Clear(); type.Methods.Add(IntMethod(module, "Keep", true));
        }
        private static void Interface(ModuleDef module, TypeDef type)
        {
            var marker = new TypeDefUser("R03", "IMarker", null) { Attributes = TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract };
            module.Types.Add(marker); type.Interfaces.Add(new InterfaceImplUser(marker));
        }
        private static void Explicit(TypeDef type, uint offset)
        { type.Attributes = (type.Attributes & ~TypeAttributes.LayoutMask) | TypeAttributes.ExplicitLayout; type.Fields[0].FieldOffset = offset; type.ClassSize = 32; }
        private static void ThrowingCctor(ModuleDef module, TypeDef type)
        {
            var method = new MethodDefUser(".cctor", MethodSig.CreateStatic(module.CorLibTypes.Void), MethodImplAttributes.IL,
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            method.Body = new CilBody(); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Throw)); type.Methods.Add(method);
        }
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static void Expect(string code, Action action)
        {
            try { action(); } catch (ShadowBuildException e) { Require(e.Code == code, "Expected " + code + ", got " + e.Code); return; }
            throw new InvalidOperationException("Expected rejection: " + code);
        }
    }
}
