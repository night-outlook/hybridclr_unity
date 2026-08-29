using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HybridCLR.Editor.AOT;
using HybridCLR.Editor.Commands;
using HybridCLR.Editor.Meta;
using HybridCLR.Editor.MethodBridge;
using NUnit.Framework;
using UnityEditor;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    // All filesystem inputs here are freshly emitted fixture bytes. These tests
    // exercise generator algorithms, not successful Player/snapshot acceptance.
    public sealed class M06GeneratorTests
    {
        [Test] public void OptimizedInventoryIsRealImmutableAndDeterministic()
        {
            using (var fixture = new Fixture())
            {
                var first = fixture.Generator("first.cpp"); var inventory = first.Prepare();
                Assert.AreSame(inventory, first.Prepare());
                Assert.IsNotEmpty(inventory.ManagedToNative); Assert.IsNotEmpty(inventory.NativeToManaged);
                Assert.IsNotEmpty(inventory.AdjustThunks); Assert.AreEqual(1, inventory.ReversePInvoke.Count);
                Assert.AreEqual(2, inventory.ReversePInvoke[0].Capacity); Assert.IsNotEmpty(inventory.Calli);
                Assert.IsFalse(inventory.NativePointerDispatchHasMethodInfo);
                Assert.Throws<NotSupportedException>(() => ((IList<GeneratedBridgeSignature>)inventory.ManagedToNative).Clear());
                first.Generate(); var second = fixture.Generator("second.cpp"); second.Generate();
                CollectionAssert.AreEqual(File.ReadAllBytes(fixture.PathOf("first.cpp")), File.ReadAllBytes(fixture.PathOf("second.cpp")));
                CollectionAssert.AreEqual(inventory.ManagedToNative.Select(item => item.Abi), second.Prepare().ManagedToNative.Select(item => item.Abi));
            }
        }

        [Test] public void GeneratorOrderingIncludesAssemblyIdentityForEqualDisplaySignatures()
        {
            using (var fixture = new EqualDisplaySignatureFixture())
            {
                Assert.AreEqual(fixture.First.ToString(), fixture.Second.ToString());
                var forward = fixture.Generator("forward.cpp", fixture.First, fixture.Second); forward.Generate();
                var reverse = fixture.Generator("reverse.cpp", fixture.Second, fixture.First); reverse.Generate();
                CollectionAssert.AreEqual(File.ReadAllBytes(fixture.PathOf("forward.cpp")), File.ReadAllBytes(fixture.PathOf("reverse.cpp")));
                CollectionAssert.AreEqual(forward.Prepare().StructMappings.Select(item => item.Key), reverse.Prepare().StructMappings.Select(item => item.Key));
            }
        }

        [Test] public void ActualGeneratedMethodEntriesGuardBeforeDispatchButCalliHasNoInventedMethod()
        {
            using (var fixture = new Fixture())
            {
                fixture.Generator("guard.cpp").Generate(); string code = File.ReadAllText(fixture.PathOf("guard.cpp"));
                foreach (string kind in new[] { "ManagedToNative", "NativeToManaged", "AdjustThunk", "ReversePInvoke" })
                    StringAssert.Contains("Generated." + kind, code);
                AssertEntry(code, "static void __M2N_", "Generated.ManagedToNative", "method->methodPointerCallByInterp");
                AssertEntry(code, "static int32_t __N2M_", "Generated.NativeToManaged", "Interpreter::Execute");
                AssertEntry(code, "__N2M_AdjustorThunk_", "Generated.AdjustThunk", "__arg0 += sizeof(Il2CppObject)");
                AssertEntry(code, "__ReversePInvokeMethod_0(", "Generated.ReversePInvoke", "method->methodPointerCallByInterp");
                int reverseStart = code.IndexOf("__ReversePInvokeMethod_0(", StringComparison.Ordinal);
                string reverse = code.Substring(reverseStart, code.IndexOf("\n}", reverseStart, StringComparison.Ordinal) - reverseStart);
                StringAssert.Contains("if (!il2cpp::vm::AssemblyShadow::AssertMethodIsActive", reverse);
                StringAssert.Contains("il2cpp::utils::Runtime::Abort();", reverse);
                StringAssert.DoesNotContain("RequireActiveMethod", reverse);
                int start = code.IndexOf("static void __M2NF_", StringComparison.Ordinal);
                Assert.GreaterOrEqual(start, 0); string calli = code.Substring(start, code.IndexOf("\n}", start, StringComparison.Ordinal) - start);
                StringAssert.DoesNotContain("RequireActiveMethod", calli); StringAssert.DoesNotContain("const MethodInfo*", calli);
                StringAssert.Contains("#if HYBRIDCLR_ENABLE_ASSEMBLY_SHADOW", code);
            }
        }

        [Test] public void StructuralAbiCoverageIgnoresLocalIdsButRequiresCapacityAndLayout()
        {
            using (var fixture = new Fixture())
            {
                var inventory = fixture.Generator("unused.cpp").Prepare(); var actual = inventory.ManagedToNative[0];
                var selected = new[] { Entry("s9", actual.Abi, 3) };
                Assert.DoesNotThrow(() => RequireEntries(selected, new[] { Entry("s0", actual.Abi, 2) }));
                Assert.Throws<ShadowBuildException>(() => RequireEntries(selected, new[] { Entry("s0", actual.Abi, 4) }));
                Assert.Throws<ShadowBuildException>(() => RequireEntries(selected, new[] { Entry("s9", actual.Abi + "different-layout", 1) }));
                Assert.Throws<ShadowBuildException>(() => RequireEntries(selected, new[] { Entry("s9", actual.Abi, 0) }));
                Assert.DoesNotThrow(() => RequireEntries(new[] { null, Entry("first", actual.Abi, 1), Entry("second", actual.Abi, 5) },
                    new[] { Entry("required", actual.Abi, 4) }));
                Assert.Throws<ShadowBuildException>(() => RequireEntries(new[] { Entry("first", actual.Abi, 1), Entry("second", actual.Abi, 5) },
                    new[] { Entry("required", actual.Abi, 6) }));
            }
        }

        [Test] public void RuntimeStructMappingsRetainActualNamesAndOptimizerLayout()
        {
            using (var fixture = new Fixture())
            {
                var structure = new TypeDefUser("Fixture", "Payload", new TypeRefUser(fixture.Module, "System", "ValueType", fixture.Module.CorLibTypes.AssemblyRef))
                    { Attributes = dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.SequentialLayout | dnlib.DotNet.TypeAttributes.Sealed };
                structure.Fields.Add(new FieldDefUser("value", new FieldSig(fixture.Module.CorLibTypes.Int32), dnlib.DotNet.FieldAttributes.Public));
                fixture.Module.Types.Add(structure);
                fixture.Methods.Add(new GenericMethod(fixture.AddMethod("WithPayload", MethodSig.CreateStatic(structure.ToTypeSig(), structure.ToTypeSig())), null, null));
                var generator = fixture.Generator("struct.cpp"); var inventory = generator.Prepare(); generator.Generate();
                var mapping = inventory.StructMappings.Single(item => item.Key.EndsWith(":Fixture.Payload", StringComparison.Ordinal));
                Assert.IsNotEmpty(mapping.Abi); StringAssert.Contains(mapping.Key, File.ReadAllText(fixture.PathOf("struct.cpp")));
                Assert.IsTrue(inventory.ManagedToNative.Any(item => item.Abi.Contains(mapping.Abi)));
            }
        }

        [Test] public void EmptyOrdinaryOnlyCollectionDoesNotRequireAnInventedMetadataMethod()
        {
            using (var fixture = new Fixture())
            {
                var generator = new Generator(new Generator.Options { TemplateCode = Fixture.Template, OutputFile = fixture.PathOf("empty.cpp"),
                    GenericMethods = new GenericMethod[0], ReversePInvokeMethods = new List<RawMonoPInvokeCallbackMethodInfo>(),
                    CalliMethodSignatures = new CallNativeMethodSignatureInfo[0], Log = _ => { } });
                Assert.IsEmpty(generator.Prepare().ManagedToNative); generator.Generate(); Assert.IsTrue(File.Exists(fixture.PathOf("empty.cpp")));
            }
        }

        [Test] public void ByteBoundResolverRejectsMutationAndNeverUsesOptionalAmbientFallback()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteModule("fixture.dll"); var image = fixture.Image("fixture.dll");
                using (var resolver = Resolver(fixture.Root, image))
                {
                    Assert.AreEqual(fixture.PathOf("fixture.dll"), resolver.ResolveAssembly("FIXTURE.dll", true));
                    Assert.Throws<ShadowBuildException>(() => resolver.ResolveAssembly("netstandard", false));
                    image.sha256 = "caller-mutated-copy";
                    Assert.DoesNotThrow(() => resolver.ResolveAssembly("Fixture", true));
                    File.AppendAllText(fixture.PathOf("fixture.dll"), "mutation");
                    Assert.Throws<ShadowBuildException>(() => resolver.ResolveAssembly("Fixture", true));
                }
            }
        }

        [Test] public void SupplementaryMetadataNamesResolveFacadeTypesToPhysicalStrippedProviders()
        {
            string root = Path.Combine(Path.GetTempPath(), "M06AotProviders-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                WriteProviderModule(root, "netstandard.dll", "netstandard", "System", "FacadeGeneric`1");
                WriteProviderModule(root, "mscorlib.dll", "mscorlib", "System", "FacadeGeneric`1");
                var facade = ProviderImage(root, "netstandard.dll", "Reference");
                var physical = ProviderImage(root, "mscorlib.dll", "StrippedAot");
                using (var resolver = Resolver(root, facade, physical))
                {
                    resolver.Bind(new AssemblyCache(resolver));
                    var method = typeof(GenerationAssemblyResolver).GetMethod("ResolveStrippedImplementationAssemblyNames", BindingFlags.Instance | BindingFlags.NonPublic);
                    CollectionAssert.AreEqual(new[] { "mscorlib.dll" },
                        (string[])Invoke(method, resolver, (object)new[] { "System.FacadeGeneric`1", "System.FacadeGeneric`1" }));
                    Assert.AreEqual("GenerationAotProvider", Assert.Throws<ShadowBuildException>(() =>
                        Invoke(method, resolver, (object)new[] { "System.Missing`1" })).Code);
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test] public void GenericReferenceWriterUsesExplicitPhysicalAssemblyNames()
        {
            string root = Path.Combine(Path.GetTempPath(), "M06AotWriter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string generated = Path.Combine(root, "AOTGenericReferences.cs");
                new GenericReferenceWriter().Write(new List<GenericClass>(), new List<GenericMethod>(), generated, new[] { "mscorlib.dll" });
                string code = File.ReadAllText(generated);
                StringAssert.Contains("\"mscorlib.dll\"", code);
                StringAssert.DoesNotContain("\"netstandard.dll\"", code);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test] public void CapturedIdentityMvidAndPdbBytesCannotBeChanged()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteModule("fixture.dll"); File.WriteAllBytes(fixture.PathOf("fixture.pdb"), new byte[] { 1, 2, 3 });
                var image = fixture.Image("fixture.dll", "fixture.pdb");
                VerifyImages(fixture.Root, image); File.WriteAllBytes(fixture.PathOf("fixture.pdb"), new byte[] { 1, 2, 4 });
                Assert.Throws<ShadowBuildException>(() => VerifyImages(fixture.Root, image));
                File.WriteAllBytes(fixture.PathOf("fixture.pdb"), new byte[] { 1, 2, 3 });
                image.mvid = Guid.NewGuid().ToString(); Assert.Throws<ShadowBuildException>(() => VerifyImages(fixture.Root, image));
                image = fixture.Image("fixture.dll", "fixture.pdb"); image.assemblyIdentity = "Other, Version=1.0.0.0";
                Assert.Throws<ShadowBuildException>(() => VerifyImages(fixture.Root, image));
            }
        }

        [Test] public void ActualCaseInsensitiveNameCollisionAndPathEscapeReject()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteModule("first.dll"); fixture.Module.Assembly.Name = "FIXTURE"; fixture.WriteModule("second.dll");
                Assert.Throws<ShadowBuildException>(() => VerifyImages(fixture.Root, fixture.Image("first.dll"), fixture.Image("second.dll")));
                var image = fixture.Image("first.dll"); image.path = "../outside.dll";
                Assert.Throws<ShadowBuildException>(() => VerifyImages(fixture.Root, image));
            }
        }

        [Test] public void ExactDnlibResolverRejectsWrongFullIdentityEvenWithMatchingSimpleName()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteModule("fixture.dll"); using (var resolver = Resolver(fixture.Root, fixture.Image("fixture.dll")))
                {
                    var cache = new AssemblyCache(resolver); cache.LoadModule("Fixture", false); resolver.Bind(cache);
                    Assert.AreEqual(fixture.Module.Assembly.FullName, cache.ModCtx.AssemblyResolver.Resolve(new AssemblyRefUser(fixture.Module.Assembly), null).FullName);
                    var wrong = new AssemblyRefUser("Fixture", new Version(9, 0, 0, 0));
                    Assert.Throws<ShadowBuildException>(() => cache.ModCtx.AssemblyResolver.Resolve(wrong, null));
                }
            }
        }

        [Test] public void PlanHashBindsClosureOrderRolesCatalogOrdinaryPdbAndSourceContext()
        {
            var receipt = PlanReceipt(); string expected = ShadowGenerationPlan.ComputeHash(receipt);
            var changes = new Action<ShadowGenerationPlanReceipt>[] {
                value => value.target = "Other", value => value.architecture = "x64", value => value.snapshotHash = "other",
                value => value.explicitRoots[0] = "b", value => value.closure[0] = "b", value => value.loadOrder[0] = "b",
                value => value.ordinaryAssemblies[0] = "other", value => value.catalog[0].sha256 = "other",
                value => value.catalog[0].pdbSha256 = "other", value => value.catalog[0].path = "Other/a.dll",
                value => value.images[0].role = "Reference", value => value.sourcePins.architecture = "x64" };
            foreach (var change in changes) { var copy = Clone(receipt); change(copy); Assert.AreNotEqual(expected, ShadowGenerationPlan.ComputeHash(copy)); }
            receipt.planHash = "not-part-of-own-hash"; Assert.AreEqual(expected, ShadowGenerationPlan.ComputeHash(receipt));
        }

        [Test] public void PlanReaderRejectsTargetAndSchemaBeforeTouchingSnapshot()
        {
            using (var fixture = new Fixture())
            {
                var receipt = PlanReceipt(); receipt.planHash = ShadowGenerationPlan.ComputeHash(receipt);
                Write(fixture.PathOf(ShadowGenerationPlan.ReceiptName), receipt);
                Assert.AreEqual("GenerationTarget", Assert.Throws<ShadowBuildException>(() => ShadowGenerationPlan.ReadAndVerify(fixture.Root, BuildTarget.StandaloneOSX, "wrong")).Code);
                receipt.purpose = "Deployable"; Write(fixture.PathOf(ShadowGenerationPlan.ReceiptName), receipt);
                Assert.AreEqual("GenerationSchema", Assert.Throws<ShadowBuildException>(() => ShadowGenerationPlan.ReadAndVerify(fixture.Root, BuildTarget.StandaloneOSX, "arm64")).Code);
            }
        }

        [Test] public void SnapshotCatalogNormalizesAbsentPdbFields()
        {
            var file = new SnapshotFile { name = "Fixture", path = "References/Fixture.dll", sourcePath = "/source/Fixture.dll",
                sha256 = "dll-hash", pdbPath = string.Empty, pdbSha256 = string.Empty };
            var image = new GenerationImage { name = "Fixture", role = "Reference", path = "Snapshot/References/Fixture.dll",
                sourcePath = file.sourcePath, sha256 = file.sha256, pdbPath = null, pdbSha256 = null };
            MethodInfo matches = typeof(ShadowGenerationPlan).GetMethod("MatchesSnapshotImage", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsTrue((bool)matches.Invoke(null, new object[] { image, file, false }));
            image.pdbSha256 = string.Empty;
            Assert.IsFalse((bool)matches.Invoke(null, new object[] { image, file, false }));
        }

        [Test] public void CanonicalWireRejectsUnknownDuplicateAndMissingFields()
        {
            using (var fixture = new Fixture())
            {
                string path = fixture.PathOf(ShadowGenerationPlan.ReceiptName); var receipt = PlanReceipt(); Write(path, receipt);
                string json = File.ReadAllText(path);
                foreach (string malformed in new[] { json.Insert(1, "\"unknown\":1,"), json.Insert(1, "\"schemaVersion\":1,"), json.Replace("\"purpose\":\"NotDeployable\",", "") })
                {
                    File.WriteAllText(path, malformed);
                    Assert.Catch<Exception>(() => ReadGeneric(typeof(ShadowGenerationPlanReceipt), path));
                }
            }
        }

        [Test] public void AotAndOutputHashesBindExcludedBytesAndOptimizedProof()
        {
            var aot = new ShadowGenerationAotReceipt { planHash = "plan", excludedSelectedNames = new[] { "a" }, images = PlanReceipt().images };
            string hash = ShadowGenerationAotInputs.ComputeHash(aot); aot.images[0].sha256 = "changed-excluded-baseline";
            Assert.AreNotEqual(hash, ShadowGenerationAotInputs.ComputeHash(aot));
            var output = new ShadowGenerationOutputReceipt { stage = "MethodBridge", planHash = "plan", aotInventoryHash = "aot",
                managedToNative = new[] { Entry("s0", "layout", 1) }, structMappings = new[] { Entry("A:S", "layout", 1) } };
            hash = ShadowGenerationOutput.ComputeHash(output); output.managedToNative[0].capacity++;
            Assert.AreNotEqual(hash, ShadowGenerationOutput.ComputeHash(output)); hash = ShadowGenerationOutput.ComputeHash(output);
            output.structMappings[0].key = "Other:S"; Assert.AreNotEqual(hash, ShadowGenerationOutput.ComputeHash(output));
            output.outputHash = "stored-self-hash"; ShadowGenerationOutput.ComputeHash(output);
            Assert.AreEqual("stored-self-hash", output.outputHash, "Hashing must restore the self-excluded field even when it is already populated.");
        }

        [Test] public void ActualCompiledReferencesSelectOnlyReverseClosureAndRejectFixedConsumers()
        {
            using (var fixture = new Fixture())
            {
                string inputs = fixture.PathOf("inputs"); Directory.CreateDirectory(inputs);
                WriteGraphModule(inputs, "A", null); WriteGraphModule(inputs, "B", "A"); WriteGraphModule(inputs, "Unselected", null);
                var capabilities = new[] { "A", "B", "Unselected" }.Select(name => new AssemblyCapability {
                    name = name, classification = AssemblyClassification.Runtime, isShadowCapable = true, capabilityDeclared = true }).ToArray();
                using (var set = DnlibAssemblyLoader.Load(inputs, new string[0], capabilities))
                {
                    CollectionAssert.Contains(set.Get("B").references, "a");
                    var graph = new AssemblyReferenceGraph(set.Assemblies.Values);
                    CollectionAssert.AreEqual(new[] { "A", "B" }, graph.ReverseClosure(new[] { "A" }));
                    CollectionAssert.AreEqual(new[] { "A", "B" }, graph.LoadOrder(graph.ReverseClosure(new[] { "A" })));
                    Assert.IsEmpty(graph.ReverseClosure(new string[0]));
                }
                capabilities[1].isShadowCapable = false;
                using (var set = DnlibAssemblyLoader.Load(inputs, new string[0], capabilities))
                    Assert.Throws<ShadowBuildException>(() => new AssemblyReferenceGraph(set.Assemblies.Values).ReverseClosure(new[] { "A" }));
                capabilities[1].classification = AssemblyClassification.NormalHotUpdate;
                using (var set = DnlibAssemblyLoader.Load(inputs, new string[0], capabilities))
                {
                    Assert.Throws<ShadowBuildException>(() => new AssemblyReferenceGraph(set.Assemblies.Values).ReverseClosure(new[] { "A" }));
                    var ordinary = typeof(ShadowGenerationPlan).GetMethod("RequireOrdinaryCoverage", BindingFlags.NonPublic | BindingFlags.Static);
                    Assert.DoesNotThrow(() => Invoke(ordinary, null, set, new[] { "b" }));
                    Assert.Throws<ShadowBuildException>(() => Invoke(ordinary, null, set, new string[0]));
                    Assert.Throws<ShadowBuildException>(() => Invoke(ordinary, null, set, new[] { "a", "b" }));
                }
            }
        }

        [Test] public void ExplicitRootsRejectPathDisplayIdentityAndCanonicalCollisions()
        {
            var names = typeof(ShadowGenerationPlan).GetMethod("Names", BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var invalid in new[] { new[] { "dir/A.dll" }, new[] { "A, Version=1.0.0.0" }, new[] { "A", "a.dll" }, new[] { "" } })
                Assert.Throws<ShadowBuildException>(() => Invoke(names, null, (object)invalid));
            CollectionAssert.AreEqual(new[] { "a", "b" }, (string[])Invoke(names, null, (object)new[] { "B.dll", "A" }));
        }

        [Test] public void StrippedSelectedCollisionRequiresExactExplicitExclusionSet()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteModule("fixture.dll"); var images = new[] { fixture.Image("fixture.dll") };
                var check = typeof(ShadowGenerationAotInputs).GetMethod("RequireExclusions", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.DoesNotThrow(() => Invoke(check, null, images, new[] { "Fixture", "Ordinary" }, new[] { "fixture" }));
                foreach (var invalid in new[] { new string[0], new[] { "fixture", "ordinary" }, new[] { "fixture", "fixture" }, new[] { "Fixture" } })
                    Assert.Throws<ShadowBuildException>(() => Invoke(check, null, images, new[] { "Fixture" }, invalid));
                Assert.DoesNotThrow(() => Invoke(check, null, images, new[] { "Ordinary" }, new string[0]));
            }
        }

        [Test] public void EqualAbiWithoutExactRuntimeStructNameDoesNotProveCoverage()
        {
            var check = typeof(ShadowGenerationOutput).GetMethod("RequireStructMappings", BindingFlags.NonPublic | BindingFlags.Static);
            var required = new[] { Entry("A:Payload", "same-layout", 1) };
            Assert.DoesNotThrow(() => Invoke(check, null, required, required));
            Assert.Throws<ShadowBuildException>(() => Invoke(check, null, new[] { Entry("Other:Payload", "same-layout", 1) }, required));
            Assert.Throws<ShadowBuildException>(() => Invoke(check, null, new[] { Entry("A:Payload", "changed-layout", 1) }, required));
        }

        private static void WriteGraphModule(string root, string name, string provider)
        {
            using (var module = new ModuleDefUser(name + ".dll") { Kind = ModuleKind.Dll })
            {
                new AssemblyDefUser(name, new Version(1, 0, 0, 0)).Modules.Add(module);
                var type = new TypeDefUser("Graph", "Node", null) { Attributes = dnlib.DotNet.TypeAttributes.Public }; module.Types.Add(type);
                if (provider != null) type.Fields.Add(new FieldDefUser("dependency", new FieldSig(new ClassSig(new TypeRefUser(module, "Graph", "Node",
                    new AssemblyRefUser(provider, new Version(1, 0, 0, 0))))), dnlib.DotNet.FieldAttributes.Public));
                module.Write(Path.Combine(root, name + ".dll"));
            }
        }
        private static void WriteProviderModule(string root, string moduleName, string assemblyName, string typeNamespace, string typeName)
        {
            using (var module = new ModuleDefUser(moduleName) { Kind = ModuleKind.Dll })
            {
                new AssemblyDefUser(assemblyName, new Version(1, 0, 0, 0)).Modules.Add(module);
                module.Types.Add(new TypeDefUser(typeNamespace, typeName, null) { Attributes = dnlib.DotNet.TypeAttributes.Public });
                module.Write(Path.Combine(root, moduleName));
            }
        }
        private static GenerationImage ProviderImage(string root, string relative, string role)
        { return (GenerationImage)Invoke(Io.GetMethod("Image", BindingFlags.NonPublic | BindingFlags.Static), null, root, relative, null, Path.Combine(root, relative), role); }

        [Test] public void LegacyMenuSignaturesRemainAlongsideExplicitPlanOverloads()
        {
            foreach (var pair in new[] { Tuple.Create(typeof(LinkGeneratorCommand), "GenerateLinkXml"),
                Tuple.Create(typeof(MethodBridgeGeneratorCommand), "GenerateMethodBridgeAndReversePInvokeWrapper"),
                Tuple.Create(typeof(AOTReferenceGeneratorCommand), "GenerateAOTGenericReference") })
            {
                Assert.NotNull(pair.Item1.GetMethod(pair.Item2, new[] { typeof(BuildTarget) }));
                Assert.IsTrue(pair.Item1.GetMethods().Any(method => method.Name == pair.Item2 && method.GetParameters().Length > 0 && method.GetParameters()[0].ParameterType == typeof(VerifiedGenerationPlan)));
            }
            var provider = new ShadowRuntimeAssemblyInputProvider(BuildTarget.StandaloneOSX, new[] { "Ordinary" }, new[] { "B", "A" });
            CollectionAssert.AreEquivalent(new[] { "A", "B", "Ordinary" }, provider.GetAssemblies(BuildTarget.StandaloneOSX, RuntimeAssemblyInputKind.Link));
            Assert.Throws<ShadowBuildException>(() => provider.GetAssemblies(BuildTarget.StandaloneWindows64, RuntimeAssemblyInputKind.Link));
        }

        private static void AssertEntry(string code, string entry, string guard, string dispatch)
        {
            int start = code.IndexOf(entry, StringComparison.Ordinal); Assert.GreaterOrEqual(start, 0, entry);
            int end = code.IndexOf("\n}", start, StringComparison.Ordinal); string body = code.Substring(start, end - start);
            int check = body.IndexOf(guard, StringComparison.Ordinal), call = body.IndexOf(dispatch, StringComparison.Ordinal);
            Assert.GreaterOrEqual(check, 0, guard); Assert.Greater(call, check, dispatch);
        }
        private static GenerationAbiEntry Entry(string key, string abi, int capacity) { return new GenerationAbiEntry { key = key, abi = abi, capacity = capacity }; }
        private static void RequireEntries(GenerationAbiEntry[] selected, GenerationAbiEntry[] required)
        { Invoke(typeof(ShadowGenerationOutput).GetMethod("RequireEntries", BindingFlags.NonPublic | BindingFlags.Static), null, selected, required, "test"); }
        private static GenerationAssemblyResolver Resolver(string root, params GenerationImage[] images)
        { return (GenerationAssemblyResolver)Activator.CreateInstance(typeof(GenerationAssemblyResolver), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { root, images }, null); }
        private static void VerifyImages(string root, params GenerationImage[] images)
        { Invoke(Io.GetMethod("VerifyImages", BindingFlags.NonPublic | BindingFlags.Static), null, root, images); }
        private static Type Io { get { return typeof(ShadowGenerationPlan).Assembly.GetType("HybridCLR.Editor.AssemblyShadow.GenerationIO", true); } }
        private static object ReadGeneric(Type type, string path)
        { return Invoke(Io.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(type), null, path); }
        private static object Invoke(MethodInfo method, object target, params object[] args)
        { try { return method.Invoke(target, args); } catch (TargetInvocationException error) { throw error.InnerException; } }
        private static void Write<T>(string path, T value) { using (var stream = File.Create(path)) new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); }
        private static T Clone<T>(T value)
        { using (var stream = new MemoryStream()) { var serializer = new DataContractJsonSerializer(typeof(T)); serializer.WriteObject(stream, value); stream.Position = 0; return (T)serializer.ReadObject(stream); } }
        private static ShadowGenerationPlanReceipt PlanReceipt()
        {
            return new ShadowGenerationPlanReceipt { target = "StandaloneOSX", architecture = "arm64", snapshotHash = "snapshot", explicitRoots = new[] { "a" },
                closure = new[] { "a" }, loadOrder = new[] { "a" }, ordinaryAssemblies = new[] { "ordinary" }, sourcePins = new ShadowSourcePins { architecture = "arm64" },
                images = new[] { new GenerationImage { name = "A", role = "Shadow", sha256 = "a" } },
                catalog = new[] { new GenerationImage { name = "A", path = "Snapshot/Assemblies/A.dll", sha256 = "a", pdbSha256 = "pdb" } } };
        }
        private sealed class Fixture : IDisposable
        {
            internal const string Template = "//!!!{{CODE\n\n//!!!}}CODE\n";
            internal readonly string Root = Path.Combine(Path.GetTempPath(), "M06GeneratorTests-" + Guid.NewGuid().ToString("N"));
            internal readonly ModuleDefUser Module;
            internal readonly List<GenericMethod> Methods = new List<GenericMethod>();
            private readonly ModuleDefMD core;
            private readonly TypeDef host;
            private readonly MethodDef callback;
            internal Fixture()
            {
                Directory.CreateDirectory(Root);
                Module = new ModuleDefUser("Fixture.dll") { Kind = ModuleKind.Dll }; new AssemblyDefUser("Fixture", new Version(1, 0, 0, 0)).Modules.Add(Module);
                var context = ModuleDef.CreateModuleContext(); Module.Context = context;
                var resolver = (AssemblyResolver)context.AssemblyResolver; resolver.UseGAC = false;
                // Explicit local fixture dependency, never an ambient production resolver.
                core = ModuleDefMD.Load(typeof(object).Assembly.Location, context); resolver.AddToCache(core);
                host = new TypeDefUser("Fixture", "Host", Module.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public };
                Module.Types.Add(host);
                callback = AddMethod("Callback", MethodSig.CreateStatic(Module.CorLibTypes.Int32, Module.CorLibTypes.Int32));
                Methods.Add(new GenericMethod(callback, null, null));
                var iface = new TypeDefUser("Fixture", "ICall", null) { Attributes = dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.Interface | dnlib.DotNet.TypeAttributes.Abstract };
                Module.Types.Add(iface); var method = new MethodDefUser("Invoke", MethodSig.CreateInstance(Module.CorLibTypes.Int32, Module.CorLibTypes.Int32),
                    dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Abstract | dnlib.DotNet.MethodAttributes.Virtual | dnlib.DotNet.MethodAttributes.NewSlot);
                iface.Methods.Add(method); Methods.Add(new GenericMethod(method, null, null));
            }
            internal MethodDef AddMethod(string name, MethodSig signature)
            {
                var method = new MethodDefUser(name, signature, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() };
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); host.Methods.Add(method); return method;
            }
            internal Generator Generator(string output)
            {
                return new Generator(new Generator.Options { TemplateCode = Template, OutputFile = PathOf(output), GenericMethods = Methods,
                    ReversePInvokeMethods = new List<RawMonoPInvokeCallbackMethodInfo> { new RawMonoPInvokeCallbackMethodInfo { Method = callback }, new RawMonoPInvokeCallbackMethodInfo { Method = callback } },
                    CalliMethodSignatures = new[] { new CallNativeMethodSignatureInfo { MethodSig = MethodSig.CreateStatic(Module.CorLibTypes.Int32, Module.CorLibTypes.Int32), Callvention = dnlib.DotNet.CallingConvention.C } }, Log = _ => { } });
            }
            internal string PathOf(string relative) { return Path.Combine(Root, relative); }
            internal void WriteModule(string relative) { Module.Write(PathOf(relative)); }
            internal GenerationImage Image(string relative, string pdb = null)
            { return (GenerationImage)Invoke(Io.GetMethod("Image", BindingFlags.NonPublic | BindingFlags.Static), null, Root, relative, pdb, PathOf(relative), "Compiler"); }
            public void Dispose() { Module.Dispose(); core.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        }

        private sealed class EqualDisplaySignatureFixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetTempPath(), "M06EqualDisplay-" + Guid.NewGuid().ToString("N"));
            private readonly ModuleDefUser firstModule;
            private readonly ModuleDefUser secondModule;
            internal readonly GenericMethod First;
            internal readonly GenericMethod Second;

            internal EqualDisplaySignatureFixture()
            {
                Directory.CreateDirectory(root);
                First = Create("First", 1, out firstModule);
                Second = Create("Second", 2, out secondModule);
            }

            private static GenericMethod Create(string assemblyName, int fieldCount, out ModuleDefUser module)
            {
                module = new ModuleDefUser(assemblyName + ".dll") { Kind = ModuleKind.Dll };
                new AssemblyDefUser(assemblyName, new Version(1, 0, 0, 0)).Modules.Add(module);
                var payload = new TypeDefUser("Fixture", "Payload", new TypeRefUser(module, "System", "ValueType", module.CorLibTypes.AssemblyRef))
                    { Attributes = dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.SequentialLayout | dnlib.DotNet.TypeAttributes.Sealed };
                for (int i = 0; i < fieldCount; ++i)
                    payload.Fields.Add(new FieldDefUser("value" + i, new FieldSig(module.CorLibTypes.Int32), dnlib.DotNet.FieldAttributes.Public));
                module.Types.Add(payload);
                var host = new TypeDefUser("Fixture", "Host", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public };
                module.Types.Add(host);
                var method = new MethodDefUser("Same", MethodSig.CreateStatic(payload.ToTypeSig()),
                    dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() };
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); host.Methods.Add(method);
                return new GenericMethod(method, null, null);
            }

            internal Generator Generator(string output, params GenericMethod[] methods)
            {
                return new Generator(new Generator.Options { TemplateCode = Fixture.Template, OutputFile = PathOf(output), GenericMethods = methods,
                    ReversePInvokeMethods = new List<RawMonoPInvokeCallbackMethodInfo>(), CalliMethodSignatures = new CallNativeMethodSignatureInfo[0], Log = _ => { } });
            }

            internal string PathOf(string relative) { return Path.Combine(root, relative); }
            public void Dispose() { firstModule.Dispose(); secondModule.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
