using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    [TestFixture]
    public sealed class AssemblyIdentityTests
    {
        [Test]
        public void FullIdentityMismatchRejectsStrongNamedV2AgainstUnsignedV1WithBothPaths()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteConsumer(reference => { reference.Version = new Version(2, 0, 0, 0); reference.PublicKeyOrToken = new PublicKeyToken("0011223344556677"); });
                var error = Assert.Throws<ShadowBuildException>(() => fixture.Load());
                Assert.AreEqual("AssemblyIdentityMismatch", error.Code);
                StringAssert.Contains(fixture.ConsumerPath, error.Message);
                StringAssert.Contains(fixture.ProviderPath, error.Message);
                StringAssert.Contains("Version=2.0.0.0", error.Message);
                StringAssert.Contains("PublicKeyToken=0011223344556677", error.Message);
                StringAssert.Contains("Version=1.0.0.0", error.Message);
                StringAssert.Contains("PublicKeyToken=null", error.Message);
            }
        }

        [Test]
        public void VersionCultureTokenAndContentTypeAreMandatoryWithoutProof()
        {
            foreach (Action<AssemblyRef> mutate in IdentityChanges())
                using (var fixture = new Fixture())
                {
                    fixture.WriteConsumer(mutate);
                    Assert.AreEqual("AssemblyIdentityMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Load()).Code);
                    Assert.AreEqual("AssemblyIdentityMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Load(null, false)).Code,
                        "The diagnostic missing-reference switch cannot waive a present incompatible identity.");
                }
        }

        [Test]
        public void PublicKeyAndPublicKeyTokenRepresentationsMatchByNormalizedToken()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteProvider(module => module.Assembly.PublicKey = new PublicKey(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
                fixture.WriteConsumer(reference =>
                {
                    using (var provider = ModuleDefMD.Load(fixture.ProviderPath)) reference.PublicKeyOrToken = provider.Assembly.PublicKeyToken;
                });
                using (var set = fixture.Load()) Assert.IsNotNull(set.ResolveType(set.GetModule("Consumer").GetTypeRefs().Single()));
            }
        }

        [Test]
        public void VerifiedCatalogBytesPermitOnlyVersionUnificationAndProofIsReadOnly()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteConsumer(reference => reference.Version = new Version(2, 0, 0, 0));
                VerifiedTargetFrameworkReferences proof = fixture.Proof();
                Assert.AreEqual(1, proof.Providers.Count);
                Assert.IsTrue(((ICollection<string>)proof.Providers).IsReadOnly);
                Assert.IsEmpty(typeof(VerifiedTargetFrameworkReferences).GetConstructors());
                Assert.AreEqual(proof.ProvenanceHash, fixture.Proof().ProvenanceHash);
                using (var set = fixture.Load(proof)) Assert.IsNotNull(set.ResolveType(set.GetModule("Consumer").GetTypeRefs().Single()));
                foreach (Action<AssemblyRef> mutate in IdentityChanges().Skip(1))
                {
                    fixture.WriteConsumer(mutate);
                    Assert.AreEqual("AssemblyIdentityMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Load(proof)).Code);
                }
            }
        }

        [Test]
        public void SameNameAndIdentityWithChangedBytesCannotReuseFrameworkProof()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteConsumer(reference => reference.Version = new Version(2, 0, 0, 0));
                var proof = fixture.Proof();
                fixture.WriteProvider(module => module.Types.Add(new TypeDefUser("Fixture", "Injected")));
                Assert.AreEqual("AssemblyIdentityMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Load(proof)).Code);
            }
        }

        [Test]
        public void PrimarySnapshotCopyNeverReceivesFrameworkVersionUnification()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteConsumer(reference => reference.Version = new Version(2, 0, 0, 0));
                var proof = fixture.Proof();
                File.Copy(fixture.ProviderPath, Path.Combine(fixture.Assemblies, "Provider.dll"));
                Assert.AreEqual("AssemblyIdentityMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Load(proof)).Code);
            }
        }

        [Test]
        public void SuppliedIncompatibleFacadeDestinationIsNeverDeferredAsMissing()
        {
            using (var fixture = new Fixture())
            {
                fixture.Write("Facade", fixture.References, module =>
                {
                    var marker = new TypeRefUser(module, "System.Runtime.CompilerServices", "ReferenceAssemblyAttribute", new AssemblyRefUser("Provider", new Version(1, 0, 0, 0)));
                    module.Assembly.CustomAttributes.Add(new CustomAttribute(new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void), marker)));
                    module.ExportedTypes.Add(new ExportedTypeUser(module, 0, "Fixture", "Payload", dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.Forwarder,
                        new AssemblyRefUser("Provider", new Version(2, 0, 0, 0))));
                });
                var error = Assert.Throws<ShadowBuildException>(() => fixture.Load());
                Assert.AreEqual("AssemblyIdentityMismatch", error.Code);
                StringAssert.Contains(Path.Combine(fixture.References, "Facade.dll"), error.Message);
            }
        }

        [Test]
        public void ExternalNestedTypeSpecsAndTypeDefsCannotBypassIdentityRejection()
        {
            using (var fixture = new Fixture())
            using (var set = fixture.Load())
            using (var external = new ModuleDefUser("External"))
            {
                var correct = new TypeRefUser(external, "Fixture", "Payload", new AssemblyRefUser("Provider", new Version(1, 0, 0, 0)));
                var wrong = new TypeRefUser(external, "Fixture", "Payload", new AssemblyRefUser("Provider", new Version(2, 0, 0, 0)));
                Assert.IsNotNull(set.ResolveType(correct));
                Assert.IsNull(set.ResolveType(wrong));
                Assert.IsNull(set.ResolveType(new TypeRefUser(set.GetModule("Provider"), "Fixture", "Payload", wrong.DefinitionAssembly.ToAssemblyRef())),
                    "A requester module that also defines the type must not bypass the requested assembly identity.");
                Assert.IsNotNull(set.ResolveType(new TypeRefUser(external, "", "Nested", correct)));
                Assert.IsNull(set.ResolveType(new TypeRefUser(external, "", "Nested", wrong)));
                Assert.IsNotNull(set.ResolveType(new TypeSpecUser(new SZArraySig(new ClassSig(correct)))));
                Assert.IsNull(set.ResolveType(new TypeSpecUser(new SZArraySig(new ClassSig(wrong)))));
                var generic = new TypeRefUser(external, "Fixture", "Generic`1", correct.DefinitionAssembly.ToAssemblyRef());
                var wrongGeneric = new TypeRefUser(external, "Fixture", "Generic`1", wrong.DefinitionAssembly.ToAssemblyRef());
                Assert.IsNotNull(set.ResolveType(new TypeSpecUser(new GenericInstSig(new ClassSig(generic), external.CorLibTypes.Int32))));
                Assert.IsNull(set.ResolveType(new TypeSpecUser(new GenericInstSig(new ClassSig(wrongGeneric), external.CorLibTypes.Int32))));
                new AssemblyDefUser("Provider", new Version(2, 0, 0, 0)).Modules.Add(external);
                var foreignDefinition = new TypeDefUser("Fixture", "Payload"); external.Types.Add(foreignDefinition);
                Assert.IsNull(set.ResolveType(foreignDefinition));
            }
        }

        [Test]
        public void RecordedSourcePathCannotGrantFrameworkProvenance()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteConsumer(reference => reference.Version = new Version(2, 0, 0, 0));
                var receipt = fixture.Receipt();
                receipt.references[0].sourcePath = fixture.CatalogPath;
                string anchor = fixture.Write("Anchor", fixture.SystemDirectory, module => { });
                var proof = fixture.Proof(receipt, compilerReferences: new[] { anchor });
                Assert.IsEmpty(proof.Providers);
                Assert.AreEqual("AssemblyIdentityMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Load(proof)).Code);
            }
        }

        [Test]
        public void ForgedReferenceAssemblyAttributeCannotGrantFrameworkProvenance()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteProvider(module =>
                {
                    var attribute = new TypeDefUser("System.Runtime.CompilerServices", "ReferenceAssemblyAttribute");
                    var ctor = new MethodDefUser(".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.Runtime,
                        dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.SpecialName | dnlib.DotNet.MethodAttributes.RTSpecialName);
                    attribute.Methods.Add(ctor); module.Types.Add(attribute); module.Assembly.CustomAttributes.Add(new CustomAttribute(ctor));
                });
                fixture.WriteConsumer(reference => reference.Version = new Version(2, 0, 0, 0));
                Assert.AreEqual("AssemblyIdentityMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Load()).Code);
                Assert.AreEqual("FrameworkReferenceMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Proof()).Code);
            }
        }

        [Test]
        public void FrameworkNamePrefixCannotGrantVersionUnification()
        {
            using (var fixture = new Fixture())
            {
                fixture.Write("System.Untrusted", fixture.References, module => module.Types.Add(new TypeDefUser("Fixture", "Payload")));
                fixture.Write("Consumer", fixture.Assemblies, module => module.Types.Add(new TypeDefUser("Fixture", "Consumer",
                    new TypeRefUser(module, "Fixture", "Payload", new AssemblyRefUser("System.Untrusted", new Version(2, 0, 0, 0))))));
                Assert.AreEqual("AssemblyIdentityMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Load()).Code);
            }
        }

        [Test]
        public void CatalogMustBeInstalledAndAnActualCompilerReference()
        {
            using (var fixture = new Fixture())
            {
                Assert.AreEqual("FrameworkCatalogOutsideEditor", Assert.Throws<ShadowBuildException>(() =>
                    fixture.Proof(systemDirectories: new[] { fixture.References })).Code);
                Assert.AreEqual("FrameworkCatalogUnavailable", Assert.Throws<ShadowBuildException>(() =>
                    fixture.Proof(compilerReferences: new[] { fixture.ProviderPath })).Code);
                Assert.AreEqual("FrameworkCatalogUnavailable", Assert.Throws<ShadowBuildException>(() =>
                    fixture.Proof(compilerReferences: new string[0])).Code);
            }
        }

        [Test]
        public void RuntimeProfileInsideEditorCannotGrantCompilerFrameworkProvenance()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteConsumer(reference => reference.Version = new Version(2, 0, 0, 0));
                string runtimeDirectory = Path.Combine(fixture.Contents, "RuntimeProfile");
                Directory.CreateDirectory(runtimeDirectory);
                string runtimeReference = Path.Combine(runtimeDirectory, "Provider.dll");
                File.Copy(fixture.ProviderPath, runtimeReference);
                string anchor = fixture.Write("Anchor", fixture.SystemDirectory, module => { });
                var receipt = fixture.Receipt(); receipt.references[0].sourcePath = runtimeReference;
                var proof = fixture.Proof(receipt, compilerReferences: new[] { runtimeReference, anchor });
                Assert.IsEmpty(proof.Providers);
                Assert.AreEqual("AssemblyIdentityMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Load(proof)).Code);
            }
        }

        [Test]
        public void ChangedCatalogOrCapturedReferenceBytesRejectProof()
        {
            using (var fixture = new Fixture())
            {
                var receipt = fixture.Receipt();
                fixture.WriteProvider(module => module.Types.Add(new TypeDefUser("Fixture", "Changed")));
                Assert.AreEqual("FrameworkReferenceHashMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Proof(receipt)).Code);
                Assert.AreEqual("FrameworkReferenceMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Proof()).Code);
            }
            using (var fixture = new Fixture())
            {
                fixture.Write("Provider", fixture.SystemDirectory, module => module.Types.Add(new TypeDefUser("Fixture", "ChangedCatalog")));
                Assert.AreEqual("FrameworkReferenceMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Proof()).Code);
            }
        }

        [Test]
        public void DuplicateCatalogIdentitiesWithDifferentBytesRejectProof()
        {
            using (var fixture = new Fixture())
            {
                string other = Path.Combine(fixture.Contents, "OtherSystemDirectory");
                string conflicting = fixture.Write("Provider", other, module => module.Types.Add(new TypeDefUser("Fixture", "Other")));
                Assert.AreEqual("FrameworkCatalogAmbiguous", Assert.Throws<ShadowBuildException>(() => fixture.Proof(
                    systemDirectories: new[] { fixture.SystemDirectory, other }, compilerReferences: new[] { fixture.CatalogPath, conflicting })).Code);
            }
        }

        [Test]
        public void SnapshotTargetPinsHashAndReferenceRoleAreCheckedBeforeProof()
        {
            using (var fixture = new Fixture())
            {
                foreach (Action<AssemblySnapshotReceipt> alter in new Action<AssemblySnapshotReceipt>[]
                {
                    r => r.unityVersion = "other", r => r.target = "StandaloneWindows64", r => r.architecture = "x64",
                    r => r.sourcePins.unityVersion = "other", r => r.sourcePins.target = "StandaloneWindows64", r => r.sourcePins.architecture = "x64",
                })
                {
                    var receipt = fixture.Receipt(); alter(receipt);
                    Assert.AreEqual("FrameworkTargetMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Proof(receipt)).Code);
                }
                var alteredHash = fixture.Receipt(); alteredHash.snapshotHash = new string('0', 64);
                Assert.AreEqual("FrameworkSnapshotHashMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Proof(alteredHash)).Code);
                var moved = fixture.Receipt(); moved.references[0].path = "Assemblies/Provider.dll"; moved.snapshotHash = AssemblySnapshot.ComputeHash(moved);
                Assert.AreEqual("FrameworkReferenceRoleMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Proof(moved)).Code);
            }
        }

        private static IEnumerable<Action<AssemblyRef>> IdentityChanges()
        {
            yield return reference => reference.Version = new Version(2, 0, 0, 0);
            yield return reference => reference.Culture = "fr-FR";
            yield return reference => reference.PublicKeyOrToken = new PublicKeyToken("0011223344556677");
            yield return reference => reference.ContentType = AssemblyAttributes.ContentType_WindowsRuntime;
        }

        private sealed class Fixture : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "AssemblyIdentityTests-" + Guid.NewGuid().ToString("N"));
            public string Assemblies { get { return Path.Combine(Root, "Assemblies"); } }
            public string References { get { return Path.Combine(Root, "References"); } }
            public string Contents { get { return Path.Combine(Root, "Editor", "Contents"); } }
            public string SystemDirectory { get { return Path.Combine(Contents, "SystemReferences"); } }
            public string ProviderPath { get { return Path.Combine(References, "Provider.dll"); } }
            public string ConsumerPath { get { return Path.Combine(Assemblies, "Consumer.dll"); } }
            public string CatalogPath { get { return Path.Combine(SystemDirectory, "Provider.dll"); } }
            public Fixture()
            {
                WriteProvider(); WriteConsumer(); Directory.CreateDirectory(SystemDirectory); File.Copy(ProviderPath, CatalogPath);
            }
            public void WriteProvider(Action<ModuleDefUser> mutate = null)
            {
                Write("Provider", References, module =>
                {
                    var type = new TypeDefUser("Fixture", "Payload") { Attributes = dnlib.DotNet.TypeAttributes.Public };
                    type.NestedTypes.Add(new TypeDefUser("", "Nested") { Attributes = dnlib.DotNet.TypeAttributes.NestedPublic });
                    module.Types.Add(type);
                    var generic = new TypeDefUser("Fixture", "Generic`1") { Attributes = dnlib.DotNet.TypeAttributes.Public };
                    generic.GenericParameters.Add(new GenericParamUser(0, GenericParamAttributes.NonVariant, "T")); module.Types.Add(generic);
                    if (mutate != null) mutate(module);
                });
            }
            public void WriteConsumer(Action<AssemblyRef> mutate = null)
            {
                Write("Consumer", Assemblies, module =>
                {
                    var reference = new AssemblyRefUser("Provider", new Version(1, 0, 0, 0)); if (mutate != null) mutate(reference);
                    reference.HasPublicKey = reference.PublicKeyOrToken is PublicKey && !PublicKeyBase.IsNullOrEmpty2(reference.PublicKeyOrToken);
                    module.Types.Add(new TypeDefUser("Fixture", "Consumer", new TypeRefUser(module, "Fixture", "Payload", reference)));
                });
            }
            public string Write(string name, string directory, Action<ModuleDefUser> fill)
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, name + ".dll");
                using (var module = new ModuleDefUser(name + ".dll") { Kind = ModuleKind.Dll, Mvid = Guid.NewGuid() })
                { new AssemblyDefUser(name, new Version(1, 0, 0, 0)).Modules.Add(module); fill(module); module.Write(path); }
                return path;
            }
            public CompiledAssemblySet Load(VerifiedTargetFrameworkReferences proof = null, bool rejectMissing = true)
            { return DnlibAssemblyLoader.Load(Assemblies, new[] { References }, null, rejectMissing, proof); }
            public AssemblySnapshotReceipt Receipt()
            {
                var pins = new ShadowRepositoryPin { revision = new string('1', 40) };
                var receipt = new AssemblySnapshotReceipt
                {
                    unityVersion = "fixture", target = "StandaloneOSX", architecture = "arm64", kind = "CompilePlayerScripts",
                    sourcePins = new ShadowSourcePins { unityVersion = "fixture", target = "StandaloneOSX", architecture = "arm64", hybridclr = pins, hybridclrUnity = pins, il2cppPlus = pins },
                    assemblies = new[] { new SnapshotFile { name = "Consumer", path = "Assemblies/Consumer.dll", sha256 = ShadowHash.File(ConsumerPath) } },
                    references = new[] { new SnapshotFile { name = "Provider", path = "References/Provider.dll", sha256 = ShadowHash.File(ProviderPath), sourcePath = "untrusted" } },
                };
                receipt.snapshotHash = AssemblySnapshot.ComputeHash(receipt); return receipt;
            }
            public VerifiedTargetFrameworkReferences Proof(AssemblySnapshotReceipt receipt = null, string[] systemDirectories = null, string[] compilerReferences = null)
            {
                // Invoke the deterministic core with synthetic authority inputs. The public
                // production entry point always obtains those inputs from Unity itself.
                var method = typeof(TargetFrameworkReferenceVerifier).GetMethod("VerifyAgainstCatalog", BindingFlags.NonPublic | BindingFlags.Static);
                try
                {
                    return (VerifiedTargetFrameworkReferences)method.Invoke(null, new object[] { Root, receipt ?? Receipt(), "fixture", "StandaloneOSX", "arm64", "fixture-api", Contents,
                        systemDirectories ?? new[] { SystemDirectory }, compilerReferences ?? new[] { CatalogPath } });
                }
                catch (TargetInvocationException error) { throw error.InnerException; }
            }
            public void Dispose() { Directory.Delete(Root, true); }
        }
    }
}
