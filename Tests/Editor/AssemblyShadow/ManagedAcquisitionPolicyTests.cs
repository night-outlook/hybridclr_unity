using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HybridCLR.AssemblyShadow.CodeGen;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class ManagedAcquisitionPolicyTests
    {
        [Test] public void ByteLoadsAndBothTypeEnumeratorsCannotBeApprovedByMethodProse()
        {
            foreach (string operation in new[] { "Load", "GetTypes", "GetExportedTypes" })
            using (var fixture = Fixture.Create(operation))
            {
                fixture.Policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency { consumer = "Consumer", provider = "Image",
                    callSite = "Fixture.Host::Run", kind = "approved reflection", evidence = "Only Image is intended by this method" } };
                var result = fixture.Validate();
                StringAssert.Contains("UnboundedManagedAcquisition", result.ToString());
                var definition = fixture.Scan(); var evidence = definition.managedAcquisitions.First();
                Assert.AreEqual(1, evidence.operationIndex); Assert.AreEqual(64, evidence.methodHash.Length);
                StringAssert.Contains("Fixture.Host::Run(", evidence.methodSignature);
                StringAssert.Contains("System.Reflection.Assembly::" + operation, evidence.operationSignature);
                Assert.IsFalse(evidence.verified);
            }
        }

        [Test] public void BareAssemblyHandlesAreRecordedWithoutClaimingNativeOrTypeSafety()
        {
            using (var fixture = Fixture.Create("GetAssemblies"))
            {
                var acquisition = fixture.Scan().managedAcquisitions.Single();
                Assert.AreEqual("AppDomain.GetAssemblies", acquisition.kind);
                Assert.IsFalse(acquisition.requiresContract); Assert.IsFalse(acquisition.verified);
                Assert.IsTrue(fixture.Validate().IsValid);
            }
        }

        [Test] public void AcquisitionDelegatePointersCannotAvoidOperationEvidence()
        {
            using (var fixture = Fixture.Create("GetTypes"))
            {
                var method = fixture.Set.GetModule("Consumer").GetTypes().SelectMany(type => type.Methods).Single();
                method.Body.Instructions[1].OpCode = OpCodes.Ldvirtftn;
                var acquisition = fixture.Scan().managedAcquisitions.Single();
                Assert.AreEqual("IndirectAcquisition", acquisition.kind); Assert.IsTrue(acquisition.requiresContract);
                StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate().ToString());
            }
        }

        [Test] public void FixedImageRequiresBoundBytesAndCurrentOrdinaryHotUpdateSemantics()
        {
            using (var fixture = Fixture.Create("Load"))
            {
                fixture.Configure("FixedAssemblyBytes"); fixture.Transform();
                StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate().ToString());
                StringAssert.Contains("FixedImageEvidenceMissing", fixture.Validate(fixture.Configuration).ToString());
                var images = fixture.Images();
                Assert.IsTrue(fixture.Validate(fixture.Configuration, images).IsValid, fixture.Validate(fixture.Configuration, images).ToString());
                images[fixture.Configuration.sites[0].imagePath] = fixture.ConsumerBytes;
                StringAssert.Contains("FixedImageHashMismatch", fixture.Validate(fixture.Configuration, images).ToString());
                images = fixture.Images();
                fixture.Set.GetModule("Image").GetTypes().Single(type => type.FullName == "Fixture.Payload").Fields.Add(new FieldDefUser("Changed", new FieldSig(fixture.Set.GetModule("Image").CorLibTypes.Int32), dnlib.DotNet.FieldAttributes.Public));
                StringAssert.Contains("FixedImageSemanticMismatch", fixture.Validate(fixture.Configuration, images).ToString());
            }
        }

        [Test] public void FixedImageCannotPromoteShadowOrRuntimeProvidersToOrdinaryHotUpdate()
        {
            using (var fixture = Fixture.Create("Load"))
            {
                fixture.Configure("FixedAssemblyBytes"); fixture.Transform();
                fixture.Set.Get("Image").classification = AssemblyClassification.Runtime;
                StringAssert.Contains("InvalidFixedImageProvider", fixture.Validate(fixture.Configuration, fixture.Images()).ToString());
                fixture.Set.Get("Image").classification = AssemblyClassification.NormalHotUpdate; fixture.Set.Get("Image").isShadowCapable = true;
                StringAssert.Contains("InvalidFixedImageProvider", fixture.Validate(fixture.Configuration, fixture.Images()).ToString());
            }
        }

        [Test] public void FixedGuardProvenanceResolvesFollowingAssemblyGetTypeWithoutAProseWaiver()
        {
            using (var fixture = Fixture.Create("Load", true))
            {
                fixture.Configure("FixedAssemblyBytes"); fixture.Transform();
                var result = fixture.Validate(fixture.Configuration, fixture.Images());
                Assert.IsTrue(result.IsValid, result.ToString());
                // A caller-supplied declaration alone still cannot establish the receiver.
                StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate().ToString());
            }
        }

        [Test] public void RealLoaderCanonicalKeysRetainVerifiedFixedGuardAndReceiverProvenance()
        {
            using (var fixture = Fixture.Create("Load", true))
            {
                fixture.Configure("FixedAssemblyBytes"); fixture.Transform(); fixture.LoadThroughRealLoader();
                CollectionAssert.Contains(fixture.Set.Assemblies.Keys, "consumer");
                Assert.AreEqual("Consumer", fixture.Configuration.sites[0].assembly);
                var result = fixture.Validate(fixture.Configuration, fixture.Images());
                Assert.IsTrue(result.IsValid, result.ToString());
                StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate().ToString());
            }
        }

        [Test] public void ModuleTypeAndTokenAcquisitionsRejectDirectAndIndirectCalls()
        {
            foreach (string name in new[] { "GetTypes", "GetType", "FindTypes", "ResolveType", "ResolveMember", "ResolveMethod", "ResolveField",
                "GetMethod", "GetMethods", "GetField", "GetFields", "GetCustomAttributes", "GetCustomAttributesData", "get_CustomAttributes" })
            foreach (bool indirect in new[] { false, true })
            using (var fixture = Fixture.Create("GetTypes"))
            {
                fixture.ReplaceWithMetadataCall(typeof(System.Reflection.Module), name, indirect);
                var acquisition = fixture.Scan().managedAcquisitions.Single();
                Assert.AreEqual(indirect ? "IndirectAcquisition" : "Module." + name, acquisition.kind);
                Assert.IsTrue(acquisition.requiresContract); Assert.IsFalse(acquisition.verified);
                fixture.Policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency { consumer = "Consumer", provider = "Image",
                    callSite = "Fixture.Host::Run", kind = "intended module", evidence = "Caller claims this module is safe" } };
                StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate().ToString());
            }
        }

        [Test] public void ModuleHandleTokenResolversCannotBypassModuleAcquisitionChecks()
        {
            foreach (string name in new[] { "ResolveTypeHandle", "ResolveMethodHandle", "ResolveFieldHandle",
                "GetRuntimeTypeHandleFromMetadataToken", "GetRuntimeMethodHandleFromMetadataToken", "GetRuntimeFieldHandleFromMetadataToken" })
            foreach (bool indirect in new[] { false, true })
            using (var fixture = Fixture.Create("GetTypes"))
            {
                fixture.ReplaceWithMetadataCall(typeof(ModuleHandle), name, indirect);
                StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate().ToString());
                Assert.IsTrue(fixture.Scan().managedAcquisitions.Single().requiresContract);
            }
        }

        [Test] public void ModuleIdentityAndRawMetadataQueriesDoNotClaimTypeAcquisition()
        {
            foreach (string name in new[] { "get_Name", "get_ModuleVersionId", "ResolveString", "ResolveSignature" })
            using (var fixture = Fixture.Create("GetTypes"))
            {
                fixture.ReplaceWithMetadataCall(typeof(System.Reflection.Module), name, false);
                Assert.IsEmpty(fixture.Scan().managedAcquisitions);
                Assert.IsTrue(fixture.Validate().IsValid, fixture.Validate().ToString());
            }
        }

        [Test] public void RealLoaderAssemblyToModuleEnumerationChainFailsAtTypeAcquisition()
        {
            using (var fixture = Fixture.Create("GetAssemblies"))
            {
                var module = fixture.Set.GetModule("Consumer"); var method = module.GetTypes().SelectMany(type => type.Methods).Single();
                var importer = new Importer(module); var il = method.Body.Instructions; il.RemoveAt(il.Count - 1);
                il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Ldelem_Ref));
                il.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(typeof(Assembly).GetMethod("GetModules", Type.EmptyTypes))));
                il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Ldelem_Ref));
                il.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(typeof(System.Reflection.Module).GetMethod("GetTypes", Type.EmptyTypes))));
                il.Add(Instruction.Create(OpCodes.Pop)); il.Add(Instruction.Create(OpCodes.Ret)); method.MethodSig.RetType = module.CorLibTypes.Void;
                fixture.SaveConsumer(); fixture.LoadThroughRealLoader();
                var evidence = fixture.Scan().managedAcquisitions;
                Assert.AreEqual(3, evidence.Length);
                Assert.IsFalse(evidence.Single(item => item.kind == "AppDomain.GetAssemblies").requiresContract);
                Assert.IsFalse(evidence.Single(item => item.kind == "Assembly.GetModules").requiresContract);
                Assert.IsTrue(evidence.Single(item => item.kind == "Module.GetTypes").requiresContract);
                StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate().ToString());
            }
        }

        [Test] public void FiniteAnchorsRequirePhysicalIdentityAndCannotIncludeCandidatesBootstrapOrHotUpdate()
        {
            foreach (string operation in new[] { "GetAssemblies", "GetTypes" })
            using (var fixture = Fixture.Create(operation))
            {
                fixture.Configure(operation == "GetAssemblies" ? "FiniteAssemblyList" : "FiniteAssemblyTypes"); fixture.Transform();
                fixture.Set.Get("Image").classification = AssemblyClassification.Runtime;
                Assert.IsTrue(fixture.Validate(fixture.Configuration).IsValid, fixture.Validate(fixture.Configuration).ToString());
                fixture.Set.Get("Image").isShadowCapable = true;
                StringAssert.Contains("InvalidFiniteAcquisitionProvider", fixture.Validate(fixture.Configuration).ToString());
                fixture.Set.Get("Image").isShadowCapable = false; fixture.Set.Get("Image").isBootstrap = true;
                StringAssert.Contains("InvalidFiniteAcquisitionProvider", fixture.Validate(fixture.Configuration).ToString());
                fixture.Set.Get("Image").isBootstrap = false; fixture.Set.Get("Image").classification = AssemblyClassification.NormalHotUpdate;
                StringAssert.Contains("InvalidFiniteAcquisitionProvider", fixture.Validate(fixture.Configuration).ToString());
                fixture.Set.Get("Image").classification = AssemblyClassification.Runtime; fixture.Set.GetModule("Image").Assembly.Version = new Version(9, 0, 0, 0);
                StringAssert.Contains("InvalidFiniteAcquisitionProvider", fixture.Validate(fixture.Configuration).ToString());
            }
        }

        [Test] public void ByteGuardTamperingAndConfigurationMismatchDoNotProduceVerifiedEvidence()
        {
            using (var fixture = Fixture.Create("Load"))
            {
                fixture.Configure("FixedAssemblyBytes"); fixture.Transform();
                fixture.Policy.reflectionBindingConfigurationHash = new string('0', 64);
                StringAssert.Contains("AcquisitionConfigurationMismatch", fixture.Validate(fixture.Configuration, fixture.Images()).ToString());
                fixture.Policy.reflectionBindingConfigurationHash = fixture.Configuration.ComputeHash();
                var guard = ReflectionBindingTransformer.Verify(fixture.Set.GetModule("Consumer"), fixture.Configuration).Single().GuardMethod;
                guard.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
                var result = fixture.Validate(fixture.Configuration, fixture.Images());
                StringAssert.Contains("GuardTemplateMismatch", result.ToString()); StringAssert.Contains("UnboundedManagedAcquisition", result.ToString());
            }
        }

        private sealed class Fixture : IDisposable
        {
            internal byte[] ConsumerBytes, ImageBytes;
            internal CompiledAssemblySet Set;
            internal ShadowPolicyConfiguration Policy = new ShadowPolicyConfiguration();
            internal ReflectionBindingConfiguration Configuration;
            private string loaderRoot;
            internal Dictionary<string, byte[]> Images() { return new Dictionary<string, byte[]> { { Configuration.sites[0].imagePath, ImageBytes } }; }
            internal ShadowPolicyValidationResult Validate(ReflectionBindingConfiguration configuration = null, IReadOnlyDictionary<string, byte[]> images = null)
            { return ShadowAssemblyPolicyValidator.ValidateCompiled(Set, Policy, DateTime.UtcNow, configuration, images); }
            internal AssemblyPolicyDefinition Scan()
            {
                var definition = new AssemblyPolicyDefinition { name = "Consumer" };
                var scanner = typeof(ShadowAssemblyPolicyValidator).Assembly.GetType("HybridCLR.Editor.AssemblyShadow.ReflectionDependencyScanner");
                scanner.GetMethod("Scan", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { Set.Modules, "Consumer", definition });
                return definition;
            }
            internal void Configure(string kind)
            {
                var method = Set.GetModule("Consumer").GetTypes().SelectMany(type => type.Methods).Single();
                Configuration = new ReflectionBindingConfiguration { schemaVersion = 2, transformerVersion = 2, sites = new[] { new ReflectionBindingSite
                {
                    id = "test.managed", assembly = "Consumer", typeName = "Fixture.Host", methodSignature = ReflectionBindingFingerprint.MethodSignature(method),
                    originalMethodHash = ReflectionBindingFingerprint.Compute(method), operationIndex = 1, reason = "Precise operation fixture", kind = kind,
                    allowedTypes = kind == "FixedAssemblyBytes" ? new string[0] : new[] { "Fixture.Payload, " + Set.GetModule("Image").Assembly.FullName },
                    imagePath = kind == "FixedAssemblyBytes" ? "Images/Image.dll.bytes" : null,
                    imageSha256 = kind == "FixedAssemblyBytes" ? ShadowHash.Bytes(ImageBytes) : null,
                    providerAssemblyIdentity = kind == "FixedAssemblyBytes" ? Set.GetModule("Image").Assembly.FullName : null,
                } } };
                Policy.reflectionBindingConfigurationHash = Configuration.ComputeHash();
                Policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency { consumer = "Consumer", provider = "Image", kind = "guarded image", evidence = "Hash-bound managed input" } };
            }
            internal void Transform()
            {
                ConsumerBytes = ReflectionBindingTransformer.Transform(ConsumerBytes, null, Configuration).PeData;
                Set.Dispose(); Set = CreateSet(ConsumerBytes, ImageBytes);
            }
            internal void SaveConsumer()
            { using (var stream = new MemoryStream()) { Set.GetModule("Consumer").Write(stream); ConsumerBytes = stream.ToArray(); } }
            internal void LoadThroughRealLoader()
            {
                loaderRoot = Path.Combine(Path.GetTempPath(), "AssemblyShadow-AcquisitionLoader-" + Guid.NewGuid().ToString("N"));
                string assemblies = Path.Combine(loaderRoot, "Assemblies"), references = Path.Combine(loaderRoot, "References");
                Directory.CreateDirectory(assemblies); Directory.CreateDirectory(references);
                File.WriteAllBytes(Path.Combine(assemblies, "Consumer.dll"), ConsumerBytes); File.WriteAllBytes(Path.Combine(assemblies, "Image.dll"), ImageBytes);
                File.Copy(typeof(object).Assembly.Location, Path.Combine(references, "mscorlib.dll"));
                var capabilities = new[] { new AssemblyCapability { name = "Consumer" }, new AssemblyCapability { name = "Image", classification = AssemblyClassification.NormalHotUpdate } };
                Set.Dispose(); Set = DnlibAssemblyLoader.Load(assemblies, new[] { references }, capabilities);
            }
            internal void ReplaceWithMetadataCall(Type owner, string name, bool indirect)
            {
                var module = Set.GetModule("Consumer"); var method = module.GetTypes().SelectMany(type => type.Methods).Single();
                var reflection = owner.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(candidate => candidate.Name == name).OrderBy(candidate => candidate.GetParameters().Length).First();
                var target = new Importer(module).Import(reflection); var il = method.Body.Instructions; il.Clear();
                method.MethodSig = MethodSig.CreateStatic(module.CorLibTypes.Void, owner.IsValueType ? (TypeSig)new ValueTypeSig(target.DeclaringType) : new ClassSig(target.DeclaringType));
                if (indirect) il.Add(Instruction.Create(OpCodes.Ldftn, target));
                else
                {
                    il.Add(owner.IsValueType ? Instruction.Create(OpCodes.Ldarga_S, method.Parameters[0]) : Instruction.Create(OpCodes.Ldarg_0));
                    foreach (var parameter in target.MethodSig.Params)
                        il.Add(parameter.ElementType == ElementType.I4 || parameter.ElementType == ElementType.Boolean ? Instruction.Create(OpCodes.Ldc_I4_0) : Instruction.Create(OpCodes.Ldnull));
                    il.Add(Instruction.Create(owner.IsValueType ? OpCodes.Call : OpCodes.Callvirt, target));
                }
                il.Add(Instruction.Create(OpCodes.Pop)); il.Add(Instruction.Create(OpCodes.Ret));
            }
            internal static Fixture Create(string operation, bool getTypeAfterLoad = false)
            {
                var result = new Fixture();
                using (var module = NewModule("Image")) using (var stream = new MemoryStream())
                { module.Types.Add(new TypeDefUser("Fixture", "Payload", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public }); module.Write(stream); result.ImageBytes = stream.ToArray(); }
                using (var module = NewModule("Consumer")) using (var stream = new MemoryStream())
                {
                    var host = new TypeDefUser("Fixture", "Host", module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(host);
                    var assembly = new TypeRefUser(module, "System.Reflection", "Assembly", module.CorLibTypes.AssemblyRef);
                    var domain = new TypeRefUser(module, "System", "AppDomain", module.CorLibTypes.AssemblyRef);
                    var type = new TypeRefUser(module, "System", "Type", module.CorLibTypes.AssemblyRef);
                    TypeSig parameter = operation == "Load" ? (TypeSig)new SZArraySig(module.CorLibTypes.Byte) : operation == "GetAssemblies" ? new ClassSig(domain) : new ClassSig(assembly);
                    TypeSig returns = operation == "Load" ? (TypeSig)new ClassSig(assembly) : operation == "GetAssemblies" ? new SZArraySig(new ClassSig(assembly)) : new SZArraySig(new ClassSig(type));
                    var lookup = new MemberRefUser(module, operation, operation == "Load" ? MethodSig.CreateStatic(returns, parameter) : MethodSig.CreateInstance(returns), operation == "GetAssemblies" ? domain : assembly);
                    var method = new MethodDefUser("Run", MethodSig.CreateStatic(getTypeAfterLoad ? new ClassSig(type) : returns, parameter), dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() }; host.Methods.Add(method);
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); method.Body.Instructions.Add(Instruction.Create(operation == "Load" ? OpCodes.Call : OpCodes.Callvirt, lookup));
                    if (getTypeAfterLoad) { method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Fixture.Payload")); method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, new MemberRefUser(module, "GetType", MethodSig.CreateInstance(new ClassSig(type), module.CorLibTypes.String), assembly))); }
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); module.Write(stream); result.ConsumerBytes = stream.ToArray();
                }
                result.Set = CreateSet(result.ConsumerBytes, result.ImageBytes); return result;
            }
            private static ModuleDefUser NewModule(string name)
            {
                var module = new ModuleDefUser(name + ".dll", new Guid("55cde2bb-ae67-4f54-8601-fdf9c2907d13"), new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.FullName))) { Kind = ModuleKind.Dll };
                new AssemblyDefUser(name, new Version(1, 0, 0, 0)).Modules.Add(module); return module;
            }
            private static CompiledAssemblySet CreateSet(byte[] consumer, byte[] image)
            {
                var modules = new Dictionary<string, ModuleDefMD>(StringComparer.OrdinalIgnoreCase) { { "Consumer", ModuleDefMD.Load(consumer) }, { "Image", ModuleDefMD.Load(image) }, { "mscorlib", ModuleDefMD.Load(typeof(object).Assembly.Location) } };
                var descriptors = new Dictionary<string, AssemblyDescriptor>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in modules.Where(pair => pair.Key != "mscorlib")) descriptors.Add(pair.Key, new AssemblyDescriptor { name = pair.Key,
                    classification = pair.Key == "Image" ? AssemblyClassification.NormalHotUpdate : AssemblyClassification.Runtime,
                    references = pair.Value.GetAssemblyRefs().Select(reference => reference.Name.String).ToArray() });
                var constructor = typeof(CompiledAssemblySet).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
                return (CompiledAssemblySet)constructor.Invoke(new object[] { descriptors, modules, new Resolver(new AssemblyResolver()), new string[0] });
            }
            public void Dispose() { Set.Dispose(); if (loaderRoot != null && Directory.Exists(loaderRoot)) Directory.Delete(loaderRoot, true); }
        }
    }
}
