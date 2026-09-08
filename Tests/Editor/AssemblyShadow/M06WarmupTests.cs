using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using NUnit.Framework;
using UnityEngine;
using MethodAttributes = dnlib.DotNet.MethodAttributes;
using TypeAttributes = dnlib.DotNet.TypeAttributes;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class M06WarmupTests
    {
        [Test] public void ActualEmittedBodiesAndClosedGenericMethodsValidateWithoutExecution()
        {
            using (var fixture = new Fixture())
            {
                var plan = fixture.Plan();
                var result = ShadowWarmupValidator.ValidateAndClone(plan, fixture.Inputs, new[] { "Warmup" });
                Assert.AreEqual(1, result.types.Length); Assert.AreEqual(2, result.methods.Length);
                Assert.AreEqual("System.String", result.methods[1].genericArguments[0].type);
                Assert.AreNotSame(plan, result); Assert.AreNotSame(plan.methods[1].genericArguments[0], result.methods[1].genericArguments[0]);
                plan.methods[1].genericArguments[0].type = "tampered";
                Assert.AreEqual("System.String", result.methods[1].genericArguments[0].type);
                Assert.IsTrue(fixture.Inputs.GetModule("Warmup").Find("Fixture.Entry", false).Methods.Single(method => method.Name == "Run").HasBody);
            }
        }

        [Test] public void EmptyNullDuplicateAndOutOfClosureTargetsFail()
        {
            using (var fixture = new Fixture())
            {
                Assert.Throws<ShadowBuildException>(() => fixture.Validate(null));
                Assert.Throws<ShadowBuildException>(() => fixture.Validate(new ShadowWarmupPlan()));
                var plan = fixture.Plan(); plan.types = null; Assert.Throws<ShadowBuildException>(() => fixture.Validate(plan));
                plan = fixture.Plan(); plan.types = new[] { plan.types[0], plan.types[0] }; AssertCode("WarmupDuplicate", () => fixture.Validate(plan));
                plan = fixture.Plan(); plan.methods = new[] { plan.methods[0], plan.methods[0] }; AssertCode("WarmupDuplicate", () => fixture.Validate(plan));
                plan = fixture.Plan(); plan.types[0].assembly = "mscorlib"; AssertCode("WarmupClosure", () => fixture.Validate(plan));
                plan = fixture.Plan(); plan.types[0].type = "Fixture.Missing"; AssertCode("WarmupTypeResolution", () => fixture.Validate(plan));
                plan = fixture.Plan(); plan.methods[0].assembly = "mscorlib"; AssertCode("WarmupClosure", () => fixture.Validate(plan));
            }
        }

        [Test] public void MethodIdentityIncludesStaticnessReturnParametersAndFullAssemblyIdentity()
        {
            using (var fixture = new Fixture())
            {
                foreach (Action<ShadowWarmupMethodEntry> mutate in new Action<ShadowWarmupMethodEntry>[] {
                    method => method.isStatic = false, method => method.name = "Missing", method => method.returnType.type = "System.String",
                    method => method.returnType.assembly = "mscorlib", method => method.parameterTypes[0].type = "System.String",
                    method => method.parameterTypes[0].assembly = fixture.CoreIdentity.Replace("Version=4.0.0.0", "Version=9.0.0.0") })
                {
                    var plan = fixture.Plan(); mutate(plan.methods[0]); Assert.Throws<ShadowBuildException>(() => fixture.Validate(plan));
                }
            }
        }

        [Test] public void MissingBodyConstructorsAbstractAndNativeMethodsAreRejected()
        {
            foreach (string mutation in new[] { "bodyless", "abstract", "pinvoke", "internalcall", "runtime", "vararg", "duplicate" })
            using (var fixture = new Fixture((module, type) => {
                var method = type.Methods.Single(item => item.Name == "Run");
                if (mutation == "bodyless") method.Body = null;
                if (mutation == "abstract") { method.Body = null; method.IsAbstract = true; }
                if (mutation == "pinvoke") method.IsPinvokeImpl = true;
                if (mutation == "internalcall") method.IsInternalCall = true;
                if (mutation == "runtime") { method.Body = null; method.ImplAttributes = dnlib.DotNet.MethodImplAttributes.Runtime; }
                if (mutation == "vararg") method.MethodSig.CallingConvention = CallingConvention.VarArg;
                if (mutation == "duplicate") type.Methods.Add(Fixture.Body("Run", MethodSig.CreateStatic(module.CorLibTypes.Int32, module.CorLibTypes.Int32), OpCodes.Ldarg_0));
            })) Assert.Throws<ShadowBuildException>(() => fixture.Validate(fixture.Plan()), mutation);
            using (var fixture = new Fixture())
            foreach (string name in new[] { ".ctor", ".cctor" })
            {
                var plan = fixture.Plan(); plan.methods[0].name = name;
                AssertCode("WarmupConstructor", () => fixture.Validate(plan));
            }
        }

        [Test] public void NongenericTypeListAndGenericArgumentIdentityAreClosedAndExact()
        {
            using (var fixture = new Fixture((module, type) => {
                var generic = new TypeDefUser("Fixture", "Open`1", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public };
                generic.GenericParameters.Add(new GenericParamUser(0, GenericParamAttributes.NonVariant, "T"));
                generic.Methods.Add(Fixture.Body("Run", MethodSig.CreateStatic(module.CorLibTypes.Void), OpCodes.Nop)); module.Types.Add(generic);
            }))
            {
                var plan = fixture.Plan(); plan.types[0].type = "Fixture.Open`1"; AssertCode("WarmupTypeUnsupported", () => fixture.Validate(plan));
                foreach (string type in new[] { "System.Void", "System.Int32&", "System.Int32*", "System.String[]", "System.Collections.Generic.List`1" })
                {
                    plan = fixture.Plan(); plan.methods[1].genericArguments[0].type = type;
                    Assert.Throws<ShadowBuildException>(() => fixture.Validate(plan), type);
                }
                plan = fixture.Plan(); plan.methods[1].genericArguments[0].assembly = "mscorlib"; AssertCode("WarmupIdentity", () => fixture.Validate(plan));
                plan = fixture.Plan(); plan.methods[1].genericArity = 2; AssertCode("WarmupPlan", () => fixture.Validate(plan));
            }
        }

        [Test] public void GenericSpecialConstraintsAreValidatedAgainstCapturedTypes()
        {
            foreach (GenericParamAttributes flags in new[] { GenericParamAttributes.ReferenceTypeConstraint, GenericParamAttributes.NotNullableValueTypeConstraint,
                GenericParamAttributes.DefaultConstructorConstraint })
            using (var fixture = new Fixture((module, type) => type.Methods.Single(method => method.Name == "Echo").GenericParameters[0].Flags = flags))
            {
                bool needsValue = flags != GenericParamAttributes.ReferenceTypeConstraint;
                var valid = fixture.Plan(needsValue ? "System.Int32" : "System.String"); fixture.Validate(valid);
                var invalid = fixture.Plan(needsValue ? "System.String" : "System.Int32");
                AssertCode("WarmupGenericConstraint", () => fixture.Validate(invalid));
            }
        }

        [Test] public void GenericDeclaredInterfaceConstraintsMustActuallyBeImplemented()
        {
            using (var fixture = new Fixture((module, type) => {
                var contract = new TypeDefUser("Fixture", "IContract") { Attributes = TypeAttributes.Interface | TypeAttributes.Abstract | TypeAttributes.Public };
                var argument = new TypeDefUser("Fixture", "Argument", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public };
                argument.Interfaces.Add(new InterfaceImplUser(contract)); module.Types.Add(contract); module.Types.Add(argument);
                type.Methods.Single(method => method.Name == "Echo").GenericParameters[0].GenericParamConstraints.Add(new GenericParamConstraintUser(contract));
            }))
            {
                var plan = fixture.Plan(); var identity = new ShadowWarmupTypeIdentity { assembly = fixture.Inputs.GetModule("Warmup").Assembly.FullName, type = "Fixture.Argument" };
                plan.methods[1].genericArguments[0] = identity; plan.methods[1].returnType = identity; plan.methods[1].parameterTypes[0] = identity;
                fixture.Validate(plan);
                AssertCode("WarmupGenericConstraint", () => fixture.Validate(fixture.Plan("System.Object")));
            }
        }

        [Test] public void ArraysAndByrefSignaturesRetainTheirExactShape()
        {
            using (var fixture = new Fixture((module, type) => type.Methods.Add(Fixture.Body("Names",
                MethodSig.CreateStatic(new SZArraySig(module.CorLibTypes.String), new ByRefSig(module.CorLibTypes.Int32)), OpCodes.Ldnull))))
            {
                var plan = fixture.Plan(); plan.methods = new[] { new ShadowWarmupMethodEntry { assembly = "Warmup", declaringType = "Fixture.Entry", name = "Names", isStatic = true,
                    returnType = fixture.Identity("System.String[]"), parameterTypes = new[] { fixture.Identity("System.Int32&") } } };
                fixture.Validate(plan); plan.methods[0].parameterTypes[0].type = "System.Int32";
                AssertCode("WarmupMethodResolution", () => fixture.Validate(plan));
            }
        }

        [Test] public void UnsupportedPointerModifierOpenAndSizedSignaturesFailClosed()
        {
            foreach (string shape in new[] { "pointer", "modifier", "open", "sized", "lowerbound", "nested-byref" })
            using (var fixture = new Fixture((module, type) => {
                TypeSig signature = module.CorLibTypes.Int32;
                if (shape == "pointer") signature = new PtrSig(signature);
                if (shape == "modifier") signature = new CModReqdSig(module.CorLibTypes.Object.TypeDefOrRef, signature);
                if (shape == "open") signature = new GenericVar(0);
                if (shape == "nested-byref") signature = new ByRefSig(new ByRefSig(signature));
                if (shape == "sized" || shape == "lowerbound")
                {
                    var array = new ArraySig(signature, 2);
                    if (shape == "sized") array.Sizes.Add(3); else array.LowerBounds.Add(1);
                    signature = array;
                }
                type.Methods.Single(method => method.Name == "Run").MethodSig.Params[0] = signature;
            })) AssertCode("WarmupSignature", () => fixture.Validate(fixture.Plan()));
        }

        [Test] public void UnsupportedConstructedConstraintsAndVarianceAreNotSilentlyIgnored()
        {
            using (var fixture = new Fixture((module, type) => {
                var generic = new TypeDefUser("Fixture", "IContract`1") { Attributes = TypeAttributes.Interface | TypeAttributes.Abstract | TypeAttributes.Public };
                generic.GenericParameters.Add(new GenericParamUser(0, GenericParamAttributes.NonVariant, "T")); module.Types.Add(generic);
                type.Methods.Single(method => method.Name == "Echo").GenericParameters[0].GenericParamConstraints.Add(
                    new GenericParamConstraintUser(new TypeSpecUser(new GenericInstSig(new ClassSig(generic), module.CorLibTypes.String))));
            })) AssertCode("WarmupConstraintUnsupported", () => fixture.Validate(fixture.Plan()));
            using (var fixture = new Fixture((module, type) =>
                type.Methods.Single(method => method.Name == "Echo").GenericParameters[0].Flags = GenericParamAttributes.Covariant))
                AssertCode("WarmupConstraintUnsupported", () => fixture.Validate(fixture.Plan()));
        }

        [Test] public void VersionTwoRoundTripRechecksActualDllBytesAndNeverMutatesArtifact()
        {
            using (var fixture = new Fixture())
            {
                string path = fixture.Artifact(fixture.Plan()); byte[] before = File.ReadAllBytes(path);
                var read = ShadowWarmupManifestReader.ReadAndVerify(path, fixture.Inputs);
                Assert.AreEqual(2, read.SchemaVersion); Assert.AreEqual(1, read.BaseManifest.schemaVersion); Assert.AreEqual(2, read.Warmup.methods.Length);
                CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(path), "DLLs", "Warmup.dll"), "changed");
                AssertCode("WarmupClosureBytes", () => ShadowWarmupManifestReader.ReadAndVerify(path, fixture.Inputs));
            }
        }

        [Test] public void SchemaTwoLegacyExtensionsAndUnsignedDllSizesRemainStrict()
        {
            using (var fixture = new Fixture())
            {
                string path = fixture.Artifact(fixture.Plan());
                string current = File.ReadAllText(path);
                string historical = current.Replace(",\"nativeBudgetCapabilityVersion\":0,\"metadataEncodingProfile\":null,\"metadataCapacityReport\":null", "")
                    .Replace(",\"dllSize\":0", "");
                Assert.AreNotEqual(current, historical);
                fixture.Rewrite(path, historical);
                Assert.AreEqual(0, ShadowWarmupManifestReader.ReadAndVerify(path, fixture.Inputs).BaseManifest.nativeBudgetCapabilityVersion);
                Assert.AreEqual(historical, File.ReadAllText(path));
                long length = new FileInfo(Path.Combine(Path.GetDirectoryName(path), "DLLs/Warmup.dll")).Length;
                fixture.Rewrite(path, current.Replace("\"dllSize\":0", "\"dllSize\":" + length));
                Assert.AreEqual((ulong)length, ShadowWarmupManifestReader.ReadAndVerify(path, fixture.Inputs).BaseManifest.closure[0].dllSize);
                foreach (string invalid in new[] { "-1", "true", "\"1\"", "18446744073709551615", "18446744073709551616" })
                {
                    fixture.Rewrite(path, current.Replace("\"dllSize\":0", "\"dllSize\":" + invalid));
                    Assert.Throws<ShadowBuildException>(() => ShadowWarmupManifestReader.ReadAndVerify(path, fixture.Inputs));
                }
                fixture.Rewrite(path, current.Replace("\"nativeBudgetCapabilityVersion\":0", "\"nativeBudgetCapabilityVersion\":1"));
                AssertCode("WarmupManifestSchema", () => ShadowWarmupManifestReader.ReadAndVerify(path, fixture.Inputs));
                fixture.Rewrite(path, current.Replace(",\"metadataCapacityReport\":null", ""));
                AssertCode("WarmupManifestSchema", () => ShadowWarmupManifestReader.ReadAndVerify(path, fixture.Inputs));
            }
        }

        [Test] public void RehashedMissingFalseZeroUnknownDuplicateAndNullSchemaTwoFieldsFail()
        {
            using (var fixture = new Fixture())
            {
                string path = fixture.Artifact(fixture.Plan()), original = File.ReadAllText(path);
                foreach (Func<string, string> change in new Func<string, string>[] {
                    json => json.Replace("\"schemaVersion\":2", "\"schemaVersion\":3"),
                    json => json.Replace("\"schemaVersion\":2", "\"schemaVersion\":2,\"schemaVersion\":2"),
                    json => json.Replace("\"isStatic\":true,", ""), json => json.Replace("\"genericArity\":0,", ""),
                    json => json.Replace("\"isStatic\":true", "\"isStatic\":1"), json => json.Replace("\"genericArity\":0", "\"genericArity\":false"),
                    json => json.Replace("\"genericArity\":0", "\"genericArity\":2147483648"),
                    json => json.Insert(1, "\"unknown\":0,"), json => json.Replace("\"methods\":[", "\"unknownWarmup\":0,\"methods\":["),
                    json => "{\"schemaVersion\":2,\"patch\":null,\"warmup\":null}" })
                {
                    fixture.Rewrite(path, change(original)); Assert.Throws<ShadowBuildException>(() => ShadowWarmupManifestReader.ReadAndVerify(path, fixture.Inputs));
                }
                fixture.Rewrite(path, original.Replace("\"name\":\"Run\"", "\"name\":\"Missing\""));
                AssertCode("WarmupMethodResolution", () => ShadowWarmupManifestReader.ReadAndVerify(path, fixture.Inputs));
            }
        }

        [Test] public void ManifestHashAndPdbBindingsCannotBeRepairedByChangingOnlyJsonClaims()
        {
            using (var fixture = new Fixture())
            {
                string path = fixture.Artifact(fixture.Plan()); File.AppendAllText(path, " ");
                AssertCode("ManifestHashMismatch", () => ShadowWarmupManifestReader.ReadAndVerify(path, fixture.Inputs));
                fixture.Rewrite(path, File.ReadAllText(path).Replace("\"pdb\":\"\"", "\"pdb\":\"DLLs/Warmup.pdb\""));
                AssertCode("WarmupClosureBytes", () => ShadowWarmupManifestReader.ReadAndVerify(path, fixture.Inputs));
            }
        }

        [Test, Category("RequiresUnityJson")]
        public void LegacyWireSelectionIsByteIdenticalAndVersionOneStillReads()
        {
            using (var fixture = new Fixture())
            {
                var manifest = fixture.BaseManifest();
                string expected = JsonUtility.ToJson(manifest, true);
                var selector = typeof(ShadowPatchManifestBuilder).GetMethod("SelectWireManifest", BindingFlags.NonPublic | BindingFlags.Static);
                object selected = selector.Invoke(null, new object[] { manifest, null });
                Assert.AreSame(manifest, selected);
                CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(JsonUtility.ToJson(selected, true)));
                Assert.IsFalse(expected.Contains("warmup")); Assert.IsFalse(expected.Contains("\"patch\":"));
                string path = fixture.Artifact(fixture.Plan()); fixture.Rewrite(path, expected);
                var read = ShadowWarmupManifestReader.ReadAndVerify(path, fixture.Inputs);
                Assert.AreEqual(1, read.SchemaVersion); Assert.IsNull(read.Warmup); Assert.AreEqual(manifest.patchId, read.BaseManifest.patchId);
            }
        }

        [Test, Category("RequiresUnityJson")]
        public void ActualUnitySchemaTwoSerializationRoundTripsWithoutMissingDefaults()
        {
            using (var fixture = new Fixture())
            {
                var verified = ShadowWarmupValidator.ValidateAndClone(fixture.Plan(), fixture.Inputs, new[] { "Warmup" });
                var selector = typeof(ShadowPatchManifestBuilder).GetMethod("SelectWireManifest", BindingFlags.NonPublic | BindingFlags.Static);
                var envelope = (ShadowPatchManifestV2)selector.Invoke(null, new object[] { fixture.BaseManifest(), verified });
                string path = fixture.Artifact(fixture.Plan()); fixture.Rewrite(path, JsonUtility.ToJson(envelope, true));
                var read = ShadowWarmupManifestReader.ReadAndVerify(path, fixture.Inputs);
                Assert.AreEqual(2, read.SchemaVersion); Assert.AreEqual(0, read.Warmup.methods[0].genericArity);
                Assert.IsTrue(read.Warmup.methods[0].isStatic); Assert.AreEqual(0, read.Warmup.methods[0].genericArguments.Length);
            }
        }

        [Test] public void LegacyDtoFieldInventoryAndEarlyBuilderValidationRemainSealed()
        {
            const string fields = "schemaVersion semanticHashSchema patchId baselineBuildId baselineManifestSha256 unityVersion target architecture sourcePins runtimeAbiHash compileSnapshotHash reflectionBindingConfigurationSha256 reflectionBindingConfigurationHash reflectionBindings bootstrapAbiHash baselineResourceAbiHash resourceAbiHash resourceChangeLevel dllOnly resourceBundlesRequired resourceChangeReasons changedRoots loadOrder closure dependencyGraph deferredFacadeReferences unsigned signatureAlgorithm nativeBudgetCapabilityVersion metadataEncodingProfile metadataCapacityReport";
            CollectionAssert.AreEqual(fields.Split(' '), typeof(ShadowPatchManifest).GetFields().Select(field => field.Name).ToArray());
            AssertCode("InvalidPatchRequest", () => ShadowPatchManifestBuilder.Build(null));
            AssertCode("InvalidPatchRequest", () => ShadowPatchManifestBuilder.BuildWithWarmup(null, new ShadowWarmupPlan()));
            AssertCode("WarmupPlan", () => ShadowPatchManifestBuilder.BuildWithWarmup(null, null));
        }

        private static void AssertCode(string code, TestDelegate action) { Assert.AreEqual(code, Assert.Throws<ShadowBuildException>(action).Code); }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Root = Path.Combine(Path.GetTempPath(), "M06WarmupTests-" + Guid.NewGuid().ToString("N"));
            internal readonly CompiledAssemblySet Inputs;
            internal string CoreIdentity { get { return Inputs.GetModule("mscorlib").Assembly.FullName; } }
            internal Fixture(Action<ModuleDefUser, TypeDef> customize = null)
            {
                string source = Path.Combine(Root, "Inputs"), references = Path.Combine(Root, "References");
                Directory.CreateDirectory(source); Directory.CreateDirectory(references);
                File.Copy(typeof(object).Assembly.Location, Path.Combine(references, "mscorlib.dll"));
                using (var module = new ModuleDefUser("Warmup.dll", Guid.NewGuid(), new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.FullName))) { Kind = ModuleKind.Dll })
                {
                    new AssemblyDefUser("Warmup", new Version(1, 0, 0, 0)).Modules.Add(module);
                    var type = new TypeDefUser("Fixture", "Entry", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public }; module.Types.Add(type);
                    type.Methods.Add(Body("Run", MethodSig.CreateStatic(module.CorLibTypes.Int32, module.CorLibTypes.Int32), OpCodes.Ldarg_0));
                    var generic = Body("Echo", MethodSig.CreateStaticGeneric(1, new GenericMVar(0), new GenericMVar(0)), OpCodes.Ldarg_0);
                    generic.GenericParameters.Add(new GenericParamUser(0, GenericParamAttributes.NonVariant, "T")); type.Methods.Add(generic);
                    if (customize != null) customize(module, type);
                    module.Write(Path.Combine(source, "Warmup.dll"));
                }
                Inputs = DnlibAssemblyLoader.Load(source, new[] { references }, new[] { new AssemblyCapability { name = "Warmup", isShadowCapable = true, capabilityDeclared = true } });
            }
            internal static MethodDef Body(string name, MethodSig signature, OpCode instruction)
            {
                var method = new MethodDefUser(name, signature, dnlib.DotNet.MethodImplAttributes.IL | dnlib.DotNet.MethodImplAttributes.Managed,
                    MethodAttributes.Public | (signature.HasThis ? 0 : MethodAttributes.Static)) { Body = new CilBody() };
                method.Body.Instructions.Add(Instruction.Create(instruction)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); return method;
            }
            internal ShadowWarmupTypeIdentity Identity(string name) { return new ShadowWarmupTypeIdentity { assembly = CoreIdentity, type = name }; }
            internal ShadowWarmupPlan Plan(string argument = "System.String")
            {
                return new ShadowWarmupPlan { types = new[] { new ShadowWarmupTypeEntry { assembly = "Warmup", type = "Fixture.Entry" } }, methods = new[] {
                    new ShadowWarmupMethodEntry { assembly = "Warmup", declaringType = "Fixture.Entry", name = "Run", isStatic = true,
                        returnType = Identity("System.Int32"), parameterTypes = new[] { Identity("System.Int32") } },
                    new ShadowWarmupMethodEntry { assembly = "Warmup", declaringType = "Fixture.Entry", name = "Echo", isStatic = true, genericArity = 1,
                        genericArguments = new[] { Identity(argument) }, returnType = Identity(argument), parameterTypes = new[] { Identity(argument) } } } };
            }
            internal void Validate(ShadowWarmupPlan plan) { ShadowWarmupValidator.ValidateAndClone(plan, Inputs, new[] { "Warmup" }); }
            internal ShadowPatchManifest BaseManifest()
            {
                var descriptor = Inputs.Get("Warmup");
                var manifest = new ShadowPatchManifest { patchId = "P01", sourcePins = new ShadowSourcePins { hybridclr = new ShadowRepositoryPin(),
                    hybridclrUnity = new ShadowRepositoryPin(), il2cppPlus = new ShadowRepositoryPin(), demo = new ShadowRepositoryPin() },
                    closure = new[] { new ShadowPatchAssembly { name = "Warmup", dll = "DLLs/Warmup.dll", sha256 = descriptor.sha256, mvid = descriptor.mvid,
                        baselineMvid = descriptor.mvid, references = descriptor.references } }, loadOrder = new[] { "Warmup" } };
                FillDefaults(manifest); return manifest;
            }
            internal string Artifact(ShadowWarmupPlan plan)
            {
                string root = Path.Combine(Root, "Artifact-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root, "DLLs"));
                File.Copy(Path.Combine(Root, "Inputs", "Warmup.dll"), Path.Combine(root, "DLLs", "Warmup.dll"));
                string path = Path.Combine(root, "patch-manifest.json"); Rewrite(path, Json(new ShadowPatchManifestV2 { patch = BaseManifest(), warmup = plan })); return path;
            }
            internal void Rewrite(string path, string json)
            { File.WriteAllText(path, json, new UTF8Encoding(false)); File.WriteAllText(Path.Combine(Path.GetDirectoryName(path), "manifest.sha256"), ShadowHash.File(path) + "\n"); }
            public void Dispose() { Inputs.Dispose(); Directory.Delete(Root, true); }
        }

        private static void FillDefaults(object value)
        {
            foreach (FieldInfo field in value.GetType().GetFields())
            {
                object current = field.GetValue(value);
                if (field.FieldType == typeof(string)) { if (current == null) field.SetValue(value, ""); }
                else if (field.FieldType.IsArray)
                { if (current == null) field.SetValue(value, Array.CreateInstance(field.FieldType.GetElementType(), 0)); else foreach (object child in (Array)current) if (!(child is string)) FillDefaults(child); }
                else if (current != null && !field.FieldType.IsPrimitive) FillDefaults(current);
            }
        }

        // Independent test JSON writer; the separate Unity-only regression
        // checks the production serializer's unchanged legacy bytes.
        private static string Json(object value)
        {
            if (value == null) return "null";
            if (value is string) return "\"" + ((string)value).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is int || value is uint || value is ulong)
                return ((IFormattable)value).ToString(null, System.Globalization.CultureInfo.InvariantCulture);
            if (value is Array) return "[" + string.Join(",", ((Array)value).Cast<object>().Select(Json).ToArray()) + "]";
            return "{" + string.Join(",", value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Select(field => Json(field.Name) + ":" + Json(field.GetValue(value))).ToArray()) + "}";
        }
    }
}
