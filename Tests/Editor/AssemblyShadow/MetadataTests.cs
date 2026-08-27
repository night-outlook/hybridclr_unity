using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = false)]
    internal sealed class SemanticFixtureAttribute : Attribute
    {
        public SemanticFixtureAttribute(string value) { Value = value; }
        public string Value { get; private set; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 8, Pack = 2)]
    internal struct LayoutFixture
    {
        [FieldOffset(0)] public int Number;
        [FieldOffset(4)] public short Short;
    }

    internal static class SemanticFixture
    {
        [SemanticFixture("stable")]
        public static int BranchAndException(int value)
        {
            try
            {
                if (value > 1)
                    return value + 3;
                throw new InvalidOperationException();
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }

        public static T Generic<T>(T value) where T : class
        {
            return value;
        }

        public static string GenericCall()
        {
            return Generic("method-spec");
        }
    }

    internal sealed class PropertyFixture
    {
        public int Value { get; set; }
        public int Other() { return 1; }
    }

    [TestFixture]
    public sealed class MetadataTests
    {
        [Test]
        public void CanonicalNamesAndNestedGenericTypeKeysAreStable()
        {
            Assert.AreEqual("assemblya", AssemblyIdentityUtil.CanonicalName("/tmp/AssemblyA.dll"));
            Assert.AreEqual("assemblya", AssemblyIdentityUtil.CanonicalName("ASSEMBLYA.DLL"));
            Assert.AreEqual("assemblya.contracts", AssemblyIdentityUtil.CanonicalName("AssemblyA.Contracts"));
            Assert.AreEqual("assemblya.implementation.internal", AssemblyIdentityUtil.CanonicalName("C:\\patch\\AssemblyA.Implementation.Internal.DLL"));
            Assert.AreNotEqual(AssemblyIdentityUtil.CanonicalName("AssemblyA.Contracts"), AssemblyIdentityUtil.CanonicalName("AssemblyA.Implementation.Internal"));
            using (ModuleDefMD module = ModuleDefMD.Load(File.ReadAllBytes(typeof(MetadataTests).Assembly.Location)))
            {
                TypeDef type = module.GetTypes().First(t => t.Name == "SemanticFixture");
                Assert.That(AssemblyIdentityUtil.TypeKey(type), Does.Contain(":SemanticFixture"));
            }
        }

        [Test]
        public void SemanticReportIncludesVersionedSectionsAndExcludesMvid()
        {
            string path = typeof(MetadataTests).Assembly.Location;
            using (ModuleDefMD first = ModuleDefMD.Load(File.ReadAllBytes(path)))
            using (ModuleDefMD second = ModuleDefMD.Load(File.ReadAllBytes(path)))
            {
                string before = AssemblySemanticHasher.Compute(first).semanticHash;
                first.Mvid = Guid.NewGuid();
                string after = AssemblySemanticHasher.Compute(first).semanticHash;
                Assert.AreEqual(before, after, "MVID is not semantic input.");
                SemanticHashReport report = AssemblySemanticHasher.Compute(second);
                Assert.AreEqual(SemanticHashSchema.Version, report.schema);
                Assert.IsNotEmpty(report.sections.identity);
                Assert.IsNotEmpty(report.sections.types);
                Assert.IsNotEmpty(report.sections.methods);
                Assert.IsNotEmpty(report.sections.attributes);
            }
        }

        [Test]
        public void SemanticMethodBodyAndAttributesAffectHashAndCanBeConfigured()
        {
            string path = typeof(MetadataTests).Assembly.Location;
            using (ModuleDefMD baseline = ModuleDefMD.Load(File.ReadAllBytes(path)))
            using (ModuleDefMD changed = ModuleDefMD.Load(File.ReadAllBytes(path)))
            {
                string baselineHash = AssemblySemanticHasher.Compute(baseline).semanticHash;
                MethodDef method = changed.GetTypes().First(t => t.Name == "SemanticFixture").Methods.First(m => m.Name == "BranchAndException");
                Instruction constant = method.Body.Instructions.First(i => i.IsLdcI4());
                constant.OpCode = OpCodes.Ldc_I4;
                constant.Operand = 99;
                Assert.AreNotEqual(baselineHash, AssemblySemanticHasher.Compute(changed).semanticHash);

                string defaultHash = AssemblySemanticHasher.Compute(baseline).semanticHash;
                string ignoredHash = AssemblySemanticHasher.Compute(baseline, new SemanticHashOptions { IgnoredAttributeNames = new[] { typeof(SemanticFixtureAttribute).FullName } }).semanticHash;
                Assert.AreNotEqual(defaultHash, ignoredHash);
            }
        }

        [Test]
        public void SemanticCanonicalizationCapturesAttributesMethodSpecsLayoutAndAccessors()
        {
            string path = typeof(MetadataTests).Assembly.Location;
            using (ModuleDefMD baseline = ModuleDefMD.Load(File.ReadAllBytes(path)))
            using (ModuleDefMD changed = ModuleDefMD.Load(File.ReadAllBytes(path)))
            {
                string baselineHash = AssemblySemanticHasher.Compute(baseline).semanticHash;
                TypeDef fixture = changed.GetTypes().First(t => t.Name == "SemanticFixture");
                MethodDef attributed = fixture.Methods.First(m => m.Name == "BranchAndException");
                CustomAttribute attribute = attributed.CustomAttributes.First(a => a.TypeFullName.EndsWith("SemanticFixtureAttribute", StringComparison.Ordinal));
                CAArgument argument = attribute.ConstructorArguments[0];
                argument.Value = "changed";
                attribute.ConstructorArguments[0] = argument;
                Assert.AreNotEqual(baselineHash, AssemblySemanticHasher.Compute(changed).semanticHash);
            }

            using (ModuleDefMD baseline = ModuleDefMD.Load(File.ReadAllBytes(path)))
            using (ModuleDefMD changed = ModuleDefMD.Load(File.ReadAllBytes(path)))
            {
                TypeDef changedFixture = changed.GetTypes().First(t => t.Name == "SemanticFixture");
                MethodSpec changedSpec = (MethodSpec)changedFixture.Methods.First(m => m.Name == "GenericCall").Body.Instructions.First(i => i.Operand is MethodSpec).Operand;
                string baselineHash = AssemblySemanticHasher.Compute(baseline).semanticHash;
                changedSpec.GenericInstMethodSig.GenericArguments[0] = changed.CorLibTypes.Int32;
                Assert.AreNotEqual(baselineHash, AssemblySemanticHasher.Compute(changed).semanticHash);
                FieldDef field = changed.GetTypes().First(t => t.Name == "LayoutFixture").Fields.First(f => f.Name == "Number");
                field.FieldOffset = 2;
                Assert.AreNotEqual(baselineHash, AssemblySemanticHasher.Compute(changed).semanticHash);
            }

            using (ModuleDefMD baseline = ModuleDefMD.Load(File.ReadAllBytes(path)))
            using (ModuleDefMD changed = ModuleDefMD.Load(File.ReadAllBytes(path)))
            {
                string baselineHash = AssemblySemanticHasher.Compute(baseline).semanticHash;
                PropertyDef property = changed.GetTypes().First(t => t.Name == "PropertyFixture").Properties.First(p => p.Name == "Value");
                property.GetMethod = property.DeclaringType.Methods.First(m => m.Name == "Other");
                Assert.AreNotEqual(baselineHash, AssemblySemanticHasher.Compute(changed).semanticHash);
            }

            using (ModuleDefMD baseline = ModuleDefMD.Load(File.ReadAllBytes(path)))
            using (ModuleDefMD changed = ModuleDefMD.Load(File.ReadAllBytes(path)))
            {
                string baselineHash = AssemblySemanticHasher.Compute(baseline).semanticHash;
                GenericParam parameter = changed.GetTypes().First(t => t.Name == "SemanticFixture").Methods.First(m => m.Name == "Generic").GenericParameters[0];
                parameter.Flags |= GenericParamAttributes.NotNullableValueTypeConstraint;
                Assert.AreNotEqual(baselineHash, AssemblySemanticHasher.Compute(changed).semanticHash);
            }
        }

        [Test]
        public void TypeKeysRetainAssemblyIdentityForSameLogicalType()
        {
            ModuleDefUser module = new ModuleDefUser("type-key-test");
            TypeRef first = new TypeRefUser(module, "Same", "Type", new AssemblyRefUser("Provider.One"));
            TypeRef second = new TypeRefUser(module, "Same", "Type", new AssemblyRefUser("Provider.Two"));
            Assert.AreNotEqual(AssemblyIdentityUtil.TypeKey(first), AssemblyIdentityUtil.TypeKey(second));
        }

        [Test]
        public void LoaderRejectsDuplicateSnapshotSimpleNames()
        {
            string source = typeof(MetadataTests).Assembly.Location;
            string root = TemporaryDirectory();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "one"));
                Directory.CreateDirectory(Path.Combine(root, "two"));
                string fileName = Path.GetFileName(source);
                File.Copy(source, Path.Combine(root, "one", fileName));
                File.Copy(source, Path.Combine(root, "two", fileName));
                ShadowBuildException exception = Assert.Throws<ShadowBuildException>(() => DnlibAssemblyLoader.Load(root, null, null, false));
                Assert.AreEqual("DuplicateAssembly", exception.Code);
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void LoaderReportsUnresolvedReferencesWithRequesterPath()
        {
            string source = typeof(MetadataTests).Assembly.Location;
            string root = TemporaryDirectory();
            try
            {
                string fileName = Path.GetFileName(source);
                File.Copy(source, Path.Combine(root, fileName));
                ShadowBuildException exception = Assert.Throws<ShadowBuildException>(() => DnlibAssemblyLoader.Load(root, null, null, true));
                Assert.AreEqual("UnresolvedAssemblyReference", exception.Code);
                StringAssert.Contains(Path.GetFullPath(Path.Combine(root, fileName)), exception.Message);
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void LoaderDefersOnlyUnusedReferenceFacadeTargetsAndResolvesAvailableForwarders()
        {
            using (var fixture = new FacadeFixture())
            using (CompiledAssemblySet set = fixture.Load())
            {
                string expected = "fixture.facade -> " + AssemblyIdentityUtil.AssemblyReferenceKey(new AssemblyRefUser("Missing.Provider", new Version(1, 0, 0, 0)));
                CollectionAssert.AreEqual(new[] { expected }, set.DeferredFacadeReferences);
                Assert.IsTrue(((System.Collections.Generic.ICollection<string>)set.DeferredFacadeReferences).IsReadOnly);
                Assert.IsFalse(set.DeferredFacadeReferences.Any(s => s.Contains(fixture.Root)));
                TypeRef present = set.GetModule("Consumer").GetTypeRefs().Single(t => t.Name == "Present");
                Assert.AreEqual("Fixture.Core", set.ResolveType(present).Module.Assembly.Name.String);
            }
        }

        [Test]
        public void LoaderRejectsMissingDirectSnapshotDependencyEvenWhenAFacadeAlsoForwardsToIt()
        {
            using (var fixture = new FacadeFixture())
            {
                fixture.WriteConsumer("Missing.Provider", "Optional");
                ShadowBuildException error = Assert.Throws<ShadowBuildException>(() => fixture.Load());
                Assert.AreEqual("UnresolvedAssemblyReference", error.Code);
                StringAssert.Contains(fixture.ConsumerPath, error.Message);
                StringAssert.Contains("missing.provider", error.Message);
            }
        }

        [Test]
        public void LoaderRejectsMissingDirectDependencyFromAnOrdinaryReferenceModule()
        {
            using (var fixture = new FacadeFixture())
            {
                fixture.WriteAssembly(fixture.References, "Ordinary", module => module.Types.Add(new TypeDefUser("Fixture", "BusinessType",
                    new TypeRefUser(module, "Fixture", "Optional", new AssemblyRefUser("Missing.Provider")))));
                ShadowBuildException error = Assert.Throws<ShadowBuildException>(() => fixture.Load());
                Assert.AreEqual("UnresolvedAssemblyReference", error.Code);
                StringAssert.Contains(Path.Combine(fixture.References, "Ordinary.dll"), error.Message);
            }
        }

        [Test]
        public void LoaderExcludesOnlyReceiptDerivedBuildFilteredConsumersFromForwardedTypeRequirements()
        {
            using (var fixture = new FacadeFixture())
            {
                fixture.WriteConsumer("Fixture.Facade", "Optional");
                Assert.AreEqual("UnresolvedForwardedType", Assert.Throws<ShadowBuildException>(() => fixture.Load()).Code);
                var runtime = new[] { new AssemblyCapability { name = "Consumer", classification = AssemblyClassification.Runtime } };
                Assert.AreEqual("UnresolvedForwardedType", Assert.Throws<ShadowBuildException>(() => fixture.Load(runtime)).Code);
                string originalHash = ShadowHash.File(fixture.ConsumerPath);
                ShadowPolicyConfiguration derived = fixture.DeriveFilteredPolicy(AssemblyClassification.Runtime);
                Assert.AreEqual(AssemblyClassification.BuildFiltered, derived.assemblies.Single(a => a.name == "Consumer").classification);
                using (CompiledAssemblySet set = fixture.Load(derived.assemblies))
                {
                    Assert.AreEqual(originalHash, set.Get("Consumer").sha256);
                    Assert.AreEqual(AssemblyClassification.BuildFiltered, set.Get("Consumer").classification);
                    Assert.AreEqual(1, set.DeferredFacadeReferences.Count);
                    Assert.IsNull(set.ResolveType(set.GetModule("Consumer").GetTypeRefs().Single(t => t.Name == "Optional")));
                }
            }
        }

        [Test]
        public void LoaderDoesNotExemptReceiptDerivedNormalHotUpdateConsumers()
        {
            using (var fixture = new FacadeFixture())
            {
                fixture.WriteConsumer("Fixture.Facade", "Optional");
                ShadowPolicyConfiguration derived = fixture.DeriveFilteredPolicy(AssemblyClassification.NormalHotUpdate);
                Assert.AreEqual(AssemblyClassification.NormalHotUpdate, derived.assemblies.Single(a => a.name == "Consumer").classification);
                ShadowBuildException error = Assert.Throws<ShadowBuildException>(() => fixture.Load(derived.assemblies));
                Assert.AreEqual("UnresolvedForwardedType", error.Code);
                StringAssert.Contains(fixture.ConsumerPath, error.Message);
            }
        }

        [Test]
        public void LoaderStillRequiresDirectReferencesFromBuildFilteredSnapshots()
        {
            using (var fixture = new FacadeFixture())
            {
                fixture.WriteConsumer("Missing.Provider", "Optional");
                ShadowPolicyConfiguration derived = fixture.DeriveFilteredPolicy(AssemblyClassification.Runtime);
                Assert.AreEqual("UnresolvedAssemblyReference", Assert.Throws<ShadowBuildException>(() => fixture.Load(derived.assemblies)).Code);
            }
        }

        [Test]
        public void DiagnosticOnlyLoadDoesNotClaimUnverifiedFacadeDeferrals()
        {
            using (var fixture = new FacadeFixture())
            {
                fixture.WriteConsumer("Fixture.Facade", "Optional");
                using (CompiledAssemblySet set = DnlibAssemblyLoader.Load(fixture.Snapshot, new[] { fixture.References }, null, false))
                {
                    Assert.IsEmpty(set.DeferredFacadeReferences);
                    Assert.IsNull(set.ResolveType(set.GetModule("Consumer").GetTypeRefs().Single(t => t.Name == "Optional")));
                }
            }
        }

        [Test]
        public void LoaderRejectsRequiredForwardedTypeWithActualSnapshotRequester()
        {
            using (var fixture = new FacadeFixture())
            {
                fixture.WriteConsumer("Fixture.Facade", "Optional");
                ShadowBuildException error = Assert.Throws<ShadowBuildException>(() => fixture.Load());
                Assert.AreEqual("UnresolvedForwardedType", error.Code);
                StringAssert.Contains(fixture.ConsumerPath, error.Message);
                StringAssert.Contains("fixture.facade:Fixture:Optional", error.Message);
            }
        }

        [Test]
        public void LoaderRejectsMissingTypeEvenWhenForwardingTargetAssemblyIsPresent()
        {
            using (var fixture = new FacadeFixture())
            {
                fixture.WriteAssembly(fixture.References, "Missing.Provider", module => { });
                fixture.WriteConsumer("Fixture.Facade", "Optional");
                ShadowBuildException error = Assert.Throws<ShadowBuildException>(() => fixture.Load());
                Assert.AreEqual("UnresolvedForwardedType", error.Code);
                StringAssert.Contains(fixture.ConsumerPath, error.Message);
            }
        }

        [Test]
        public void LoaderRejectsFacadeThatDefinesAnOrdinaryType()
        {
            using (var fixture = new FacadeFixture())
            {
                fixture.WriteFacade(module => module.Types.Add(new TypeDefUser("Fixture", "BusinessType")));
                ShadowBuildException error = Assert.Throws<ShadowBuildException>(() => fixture.Load());
                Assert.AreEqual("UnresolvedAssemblyReference", error.Code);
                StringAssert.Contains(fixture.FacadePath, error.Message);
            }
        }

        [Test]
        public void LoaderRejectsFacadeWithGlobalExecutableCode()
        {
            using (var fixture = new FacadeFixture())
            {
                fixture.WriteFacade(module =>
                {
                    var method = new MethodDefUser("Run", MethodSig.CreateStatic(module.CorLibTypes.Void),
                        MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() };
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    module.GlobalType.Methods.Add(method);
                });
                Assert.AreEqual("UnresolvedAssemblyReference", Assert.Throws<ShadowBuildException>(() => fixture.Load()).Code);
            }
        }

        [Test]
        public void LoaderRequiresExplicitReferenceAssemblyMetadataForFacadeDeferral()
        {
            using (var fixture = new FacadeFixture())
            {
                fixture.WriteFacade(module => module.Assembly.CustomAttributes.Clear());
                Assert.AreEqual("UnresolvedAssemblyReference", Assert.Throws<ShadowBuildException>(() => fixture.Load()).Code);
            }
        }

        [Test]
        public void LoaderNeverDefersAPrimarySnapshotFacade()
        {
            using (var fixture = new FacadeFixture())
            {
                string primary = Path.Combine(fixture.Snapshot, "Fixture.Facade.dll");
                File.Copy(fixture.FacadePath, primary);
                ShadowBuildException error = Assert.Throws<ShadowBuildException>(() => fixture.Load());
                Assert.AreEqual("UnresolvedAssemblyReference", error.Code);
                StringAssert.Contains(primary, error.Message);
            }
        }

        [Test]
        public void LoaderNeverDefersFacadeTargetsAlsoUsedByAnAttributeTypeRef()
        {
            using (var fixture = new FacadeFixture())
            {
                fixture.WriteFacade(module =>
                {
                    var target = (AssemblyRef)module.ExportedTypes[1].Implementation;
                    var attributeType = new TypeRefUser(module, "Fixture", "MissingAttribute", target);
                    module.Assembly.CustomAttributes.Add(new CustomAttribute(new MemberRefUser(module, ".ctor",
                        MethodSig.CreateInstance(module.CorLibTypes.Void), attributeType)));
                });
                ShadowBuildException error = Assert.Throws<ShadowBuildException>(() => fixture.Load());
                Assert.AreEqual("UnresolvedAssemblyReference", error.Code);
                StringAssert.Contains(fixture.FacadePath, error.Message);
            }
        }

        [Test]
        public void LoaderNeverDefersFacadeTargetsAlsoUsedByLinkedResources()
        {
            using (var fixture = new FacadeFixture())
            {
                fixture.WriteFacade(module => module.Resources.Add(new AssemblyLinkedResource("RequiredResource",
                    (AssemblyRef)module.ExportedTypes[1].Implementation, ManifestResourceAttributes.Public)));
                Assert.AreEqual("UnresolvedAssemblyReference", Assert.Throws<ShadowBuildException>(() => fixture.Load()).Code);
            }
        }

        [Test]
        public void LoaderValidatesForwardingChainsFromOrdinarySnapshotProviders()
        {
            using (var fixture = new FacadeFixture())
            {
                fixture.WriteAssembly(fixture.References, "Bridge", module =>
                {
                    module.Types.Add(new TypeDefUser("Fixture", "OrdinaryType"));
                    module.ExportedTypes.Add(new ExportedTypeUser(module, 0, "Fixture", "Optional",
                        TypeAttributes.Public | TypeAttributes.Forwarder, new AssemblyRefUser("Fixture.Facade")));
                });
                fixture.WriteConsumer("Bridge", "Optional");
                ShadowBuildException error = Assert.Throws<ShadowBuildException>(() => fixture.Load());
                Assert.AreEqual("UnresolvedForwardedType", error.Code);
                StringAssert.Contains(fixture.ConsumerPath, error.Message);
                StringAssert.Contains("bridge:Fixture:Optional", error.Message);
            }
        }

        [Test]
        public void LoaderResolvesNestedForwardersWithoutRequiringUnusedDestinations()
        {
            using (var fixture = new FacadeFixture())
            {
                fixture.WriteCore(module => module.Types.Single(t => t.Name == "Present").NestedTypes.Add(
                    new TypeDefUser(string.Empty, "Nested") { Attributes = TypeAttributes.NestedPublic }));
                fixture.WriteFacade(module => module.ExportedTypes.Add(new ExportedTypeUser(module, 0, string.Empty,
                    "Nested", TypeAttributes.NestedPublic, module.ExportedTypes[0])));
                fixture.WriteAssembly(fixture.Snapshot, "Consumer", module =>
                {
                    var outer = new TypeRefUser(module, "Fixture", "Present", new AssemblyRefUser("Fixture.Facade"));
                    module.Types.Add(new TypeDefUser("Fixture", "ConsumerType", new TypeRefUser(module, string.Empty, "Nested", outer)));
                });
                using (CompiledAssemblySet set = fixture.Load())
                {
                    TypeRef nested = set.GetModule("Consumer").GetTypeRefs().Single(t => t.Name == "Nested");
                    Assert.AreEqual("Fixture.Present/Nested", set.ResolveType(nested).FullName);
                    Assert.AreEqual(1, set.DeferredFacadeReferences.Count);
                }
            }
        }

        [Test]
        public void ResolveTypeNeverUsesCallerResolverOrHostCoreLibraryForAbsentTargets()
        {
            using (var fixture = new FacadeFixture())
            using (ModuleDefMD hostCore = ModuleDefMD.Load(File.ReadAllBytes(typeof(object).Assembly.Location)))
            using (var foreign = new ModuleDefUser("Foreign"))
            {
                fixture.WriteFacade(module =>
                {
                    module.ExportedTypes[1].TypeNamespace = "System";
                    module.ExportedTypes[1].TypeName = "Object";
                    module.ExportedTypes[1].Implementation = new AssemblyRefUser(hostCore.Assembly.Name);
                });
                var externalResolver = new RecordingAssemblyResolver(hostCore.Assembly);
                foreign.Context = new ModuleContext(externalResolver);
                using (CompiledAssemblySet set = fixture.Load())
                {
                    Assert.IsFalse(set.Modules.ContainsKey(AssemblyIdentityUtil.CanonicalName(hostCore.Assembly.Name)));
                    Assert.IsNull(set.ResolveType(new TypeRefUser(foreign, "System", "Object", new AssemblyRefUser(hostCore.Assembly.Name))));
                    Assert.IsNull(set.ResolveType(new TypeRefUser(foreign, "System", "Object", new AssemblyRefUser("Fixture.Facade"))));
                    Assert.AreEqual(0, externalResolver.Calls);
                }
                fixture.WriteConsumer("Fixture.Facade", "Object", "System");
                Assert.AreEqual("UnresolvedForwardedType", Assert.Throws<ShadowBuildException>(() => fixture.Load()).Code);
            }
        }

        private sealed class RecordingAssemblyResolver : IAssemblyResolver
        {
            private readonly AssemblyDef _assembly;
            public int Calls;
            public RecordingAssemblyResolver(AssemblyDef assembly) { _assembly = assembly; }
            public AssemblyDef Resolve(IAssembly assembly, ModuleDef sourceModule) { ++Calls; return _assembly; }
        }

        private sealed class FacadeFixture : IDisposable
        {
            public readonly string Root = TemporaryDirectory();
            public string Snapshot { get { return Path.Combine(Root, "Snapshot"); } }
            public string References { get { return Path.Combine(Root, "References"); } }
            public string ConsumerPath { get { return Path.Combine(Snapshot, "Consumer.dll"); } }
            public string FacadePath { get { return Path.Combine(References, "Fixture.Facade.dll"); } }

            public FacadeFixture()
            {
                WriteCore();
                WriteFacade();
                WriteConsumer();
            }

            public CompiledAssemblySet Load(AssemblyCapability[] capabilities = null)
            {
                return DnlibAssemblyLoader.Load(Snapshot, new[] { References }, capabilities);
            }

            public ShadowPolicyConfiguration DeriveFilteredPolicy(AssemblyClassification sourceRole)
            {
                // Synthetic receipt contract for a unit test, not Player-build proof.
                // Derivation goes through the production evidence/hash/role checks.
                const string playerName = "Fixture.Player";
                WriteAssembly(Snapshot, playerName, module => module.Types.Add(new TypeDefUser("Fixture", "PlayerEntry")));
                string playerPath = Path.Combine(Snapshot, playerName + ".dll");
                string linkedOutput = Path.Combine(Root, "SyntheticLinkedOutput");
                Directory.CreateDirectory(linkedOutput);
                string linkedPath = Path.Combine(linkedOutput, "fixture.player.dll");
                File.Copy(playerPath, linkedPath);
                string linkedMvid;
                using (ModuleDefMD linkedModule = ModuleDefMD.Load(linkedPath))
                    linkedMvid = linkedModule.Mvid.Value.ToString("D");
                var source = new ShadowPolicyConfiguration { assemblies = new[]
                    { new AssemblyCapability { name = "Consumer", classification = sourceRole },
                      new AssemblyCapability { name = playerName, isShadowCapable = true, capabilityDeclared = true } } };
                var receipt = new AssemblySnapshotReceipt
                {
                    kind = "PlayerBuildInputs", playerBuildSucceeded = true, playerBuildFilterCaptured = true,
                    buildGuid = "fixture", nativeLibrarySha256 = ShadowHash.Text("fixture-native"),
                    target = "StandaloneOSX", architecture = "arm64",
                    sourcePins = new ShadowSourcePins { unityVersion = "fixture", target = "StandaloneOSX", architecture = "arm64",
                        hybridclr = new ShadowRepositoryPin { revision = new string('1', 40) },
                        hybridclrUnity = new ShadowRepositoryPin { revision = new string('2', 40) },
                        il2cppPlus = new ShadowRepositoryPin { revision = new string('3', 40) } },
                    assemblies = new[] { new SnapshotFile { name = playerName, path = "Assemblies/" + playerName + ".dll", sha256 = ShadowHash.File(playerPath) } },
                    references = new SnapshotFile[0],
                    filteredAssemblies = new[] { new SnapshotFile { name = "Consumer", path = "Assemblies/Filtered/Consumer.dll", sha256 = ShadowHash.File(ConsumerPath) } },
                    filteredAssemblyCapabilities = source.assemblies.Where(item => item.name == "Consumer").ToArray(),
                    normalHotUpdateAssemblies = sourceRole == AssemblyClassification.NormalHotUpdate ? new[] { "Consumer" } : new string[0],
                };
                receipt.linkedPlayerReceipt = new LinkedPlayerReceipt
                {
                    buildGuid = receipt.buildGuid, nativeLibrarySha256 = receipt.nativeLibrarySha256,
                    target = receipt.target, architecture = receipt.architecture, sourceDirectory = linkedOutput,
                    protectedAssemblies = new[] { "fixture.player" },
                    assemblies = new[] { new LinkedPlayerFile { name = "fixture.player", path = "Assemblies/fixture.player.dll",
                        sha256 = ShadowHash.File(linkedPath), mvid = linkedMvid } },
                };
                receipt.linkedPlayerReceiptHash = ShadowLinkedPlayerEvidence.ComputeHash(receipt.linkedPlayerReceipt);
                receipt.snapshotHash = AssemblySnapshot.ComputeHash(receipt);
                return ShadowFilteredInputPolicy.Apply(source, receipt);
            }

            public void WriteCore(Action<ModuleDefUser> customize = null)
            {
                WriteAssembly(References, "Fixture.Core", module =>
                {
                    module.Types.Add(new TypeDefUser("Fixture", "Present") { Attributes = TypeAttributes.Public });
                    var attribute = new TypeDefUser("System.Runtime.CompilerServices", "ReferenceAssemblyAttribute") { Attributes = TypeAttributes.Public };
                    attribute.Methods.Add(new MethodDefUser(".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void),
                        MethodImplAttributes.Runtime, MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName));
                    module.Types.Add(attribute);
                    if (customize != null) customize(module);
                });
            }

            public void WriteFacade(Action<ModuleDefUser> customize = null)
            {
                WriteAssembly(References, "Fixture.Facade", module =>
                {
                    var core = new AssemblyRefUser("Fixture.Core", new Version(1, 0, 0, 0));
                    var attribute = new TypeRefUser(module, "System.Runtime.CompilerServices", "ReferenceAssemblyAttribute", core);
                    module.Assembly.CustomAttributes.Add(new CustomAttribute(new MemberRefUser(module, ".ctor",
                        MethodSig.CreateInstance(module.CorLibTypes.Void), attribute)));
                    module.ExportedTypes.Add(new ExportedTypeUser(module, 0, "Fixture", "Present", TypeAttributes.Public | TypeAttributes.Forwarder, core));
                    module.ExportedTypes.Add(new ExportedTypeUser(module, 0, "Fixture", "Optional", TypeAttributes.Public | TypeAttributes.Forwarder,
                        new AssemblyRefUser("Missing.Provider", new Version(1, 0, 0, 0))));
                    if (customize != null) customize(module);
                });
            }

            public void WriteConsumer(string provider = "Fixture.Facade", string typeName = "Present", string typeNamespace = "Fixture")
            {
                WriteAssembly(Snapshot, "Consumer", module => module.Types.Add(new TypeDefUser("Fixture", "ConsumerType",
                    new TypeRefUser(module, typeNamespace, typeName, new AssemblyRefUser(provider, new Version(1, 0, 0, 0))))));
            }

            public void WriteAssembly(string directory, string name, Action<ModuleDefUser> populate)
            {
                Directory.CreateDirectory(directory);
                using (var module = new ModuleDefUser(name + ".dll") { Kind = ModuleKind.Dll, Mvid = Guid.NewGuid() })
                {
                    new AssemblyDefUser(name, new Version(1, 0, 0, 0)).Modules.Add(module);
                    populate(module);
                    module.Write(Path.Combine(directory, name + ".dll"));
                }
            }

            public void Dispose() { Directory.Delete(Root, true); }
        }

        private static string TemporaryDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "AssemblyShadowTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
