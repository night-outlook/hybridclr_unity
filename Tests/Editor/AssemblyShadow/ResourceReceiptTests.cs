using System;
using System.IO;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class ResourceReceiptTests
    {
        [Test]
        public void VerifiedReceiptIsPortableAndDoesNotReinspectLiveAssets()
        {
            using (var fixture = new ReceiptFixture())
            {
                Assert.IsNotNull(fixture.Read());
                string moved = fixture.Root + "-relocated";
                Directory.Move(fixture.Root, moved); fixture.Root = moved;
                var receipt = fixture.Read();
                Assert.AreEqual(fixture.Receipt.resourceAbiHash, receipt.Receipt.resourceAbiHash);
                ShadowResourceBaseline.RequirePlayerAbi(receipt, new ResourceAbiDescriptor());
            }
        }

        [Test]
        public void NewPlayerSerializedAbiCannotBePairedWithOldBundleReceipt()
        {
            using (var fixture = new ReceiptFixture())
            {
                var verified = fixture.Read();
                Assert.AreEqual("ResourcePlayerAbiMismatch", Assert.Throws<ShadowBuildException>(() => ShadowResourceBaseline.RequirePlayerAbi(verified, ChangedAbi())).Code);
            }
        }

        [Test]
        public void RelabelingOldBundlesWithCurrentAbiFailsCompilerProvenanceEvenAfterRehash()
        {
            using (var fixture = new ReceiptFixture())
            {
                fixture.Json(fixture.Receipt.resourceAbiPath, ChangedAbi());
                fixture.Receipt.resourceAbiHash = ResourceAbiHasher.Compute(ChangedAbi());
                fixture.Receipt.resourceAbiFileSha256 = ShadowHash.File(Path.Combine(fixture.Root, fixture.Receipt.resourceAbiPath));
                fixture.Seal();
                fixture.Reject("ResourceCompilerAbiMismatch");
            }
        }

        [TestCase("Bundles/fixture.bundle", "ResourceBundleHashMismatch")]
        [TestCase("Sources/Assets/Fixture.prefab", "ResourceSourceHashMismatch")]
        [TestCase("Sources/Assets/Fixture.prefab.meta", "ResourceSourceHashMismatch")]
        [TestCase("resource-script-index.json", "ResourceIndexHashMismatch")]
        [TestCase("resource-abi.json", "ResourceAbiHashMismatch")]
        public void TamperedFrozenArtifactsAreRejected(string path, string expected)
        {
            using (var fixture = new ReceiptFixture())
            {
                File.AppendAllText(Path.Combine(fixture.Root, path), "tampered");
                fixture.Reject(expected);
            }
        }

        [Test]
        public void IndexMustComeFromFrozenSourcesEvenWhenItsChecksumIsUpdated()
        {
            using (var fixture = new ReceiptFixture())
            {
                var index = new ResourceScriptIndex { bundles = new[] { "fixture.bundle" }, entries = new[] {
                    new ResourceScriptIndexEntry { typeKey = "invented:Test:Type", assetPaths = new[] { "Assets/Fixture.prefab" }, assetGuids = new[] { ReceiptFixture.Guid }, bundleNames = new[] { "fixture.bundle" } }
                } };
                fixture.Json(fixture.Receipt.resourceIndexPath, index);
                fixture.Receipt.resourceIndexHash = ShadowHash.File(Path.Combine(fixture.Root, fixture.Receipt.resourceIndexPath));
                fixture.Seal();
                fixture.Reject("ResourceIndexProvenanceMismatch");
            }
        }

        [Test]
        public void FrozenAssetGuidMustAgreeWithItsMetaEvenWhenRehashed()
        {
            using (var fixture = new ReceiptFixture())
            {
                var source = fixture.Receipt.sources.Single();
                File.WriteAllText(Path.Combine(fixture.Root, source.metaSnapshotPath), "guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n");
                source.metaSha256 = ShadowHash.File(Path.Combine(fixture.Root, source.metaSnapshotPath));
                fixture.Receipt.sourceSetHash = ShadowResourceBaseline.ComputeSourceSetHash(fixture.Receipt.sources);
                fixture.Seal();
                fixture.Reject("ResourceSourceGuidMismatch");
            }
        }

        [Test]
        public void MissingBuildProvenanceCannotBeReplacedByAnAbiAndBundleHash()
        {
            using (var fixture = new ReceiptFixture())
            {
                fixture.Receipt.provenance = "FreezeCurrentAbiWithExistingBundles"; fixture.Seal();
                fixture.Reject("ResourceProvenanceMissing");
            }
        }

        [Test]
        public void PatchDefineCompilationCannotEstablishANewResourceBaseline()
        {
            using (var fixture = new ReceiptFixture())
            {
                string snapshotRoot = Path.Combine(fixture.Root, "CompilerInputs");
                var input = AssemblySnapshot.ReadAndVerify(snapshotRoot, false);
                input.extraScriptingDefines = new[] { "ASSEMBLY_SHADOW_P05" };
                input.snapshotHash = AssemblySnapshot.ComputeHash(input);
                fixture.Json("CompilerInputs/" + AssemblySnapshot.ReceiptName, input);
                fixture.Receipt.compilerSnapshotHash = input.snapshotHash; fixture.Seal();
                fixture.Reject("ResourceCompilerMismatch");
            }
        }

        [Test]
        public void ResourceMetadataCannotBeReplacedEvenWhenItsReceiptIsRehashed()
        {
            using (var fixture = new ReceiptFixture())
            {
                var metadata = fixture.Receipt.metadataAssemblies.Single();
                string path = Path.Combine(fixture.Root, metadata.path);
                using (var module = ModuleDefMD.Load(File.ReadAllBytes(path)))
                {
                    module.Mvid = System.Guid.NewGuid();
                    module.Write(path);
                }
                metadata.sha256 = ShadowHash.File(path);
                fixture.Seal();
                fixture.Reject("ResourceCompilerMismatch");
            }
        }

        [Test]
        public void BuiltinEngineSkinIsProvenByBackingBytesAndEngineModulesWithoutSkippingObjects()
        {
            using (var fixture = new BuiltinFixture())
            {
                fixture.Verify();
                Assert.AreEqual(AssetDatabase.LoadAllAssetsAtPath(fixture.Source.path).Length, fixture.Proof.objects.Length);
                Assert.That(fixture.Proof.objects.Any(o => o.typeName == "UnityEngine.GUISkin"));
                Assert.That(fixture.Proof.objects.Any(o => o.localId == 10001 && o.typeName == "UnityEngine.Texture2D"));
                Assert.AreEqual(ShadowHash.File(Path.Combine(EditorApplication.applicationContentsPath, "Resources/unity default resources")), fixture.Proof.backingSha256);
                Assert.That(fixture.Proof.modules.Any(m => m.assemblyName == "UnityEngine.IMGUIModule"));
                string relocated = fixture.Root + "-relocated";
                Directory.Move(fixture.Root, relocated); fixture.Root = relocated;
                fixture.Verify();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BuiltinBackingAndDefiningModuleBytesAreRehashed(bool module)
        {
            using (var fixture = new BuiltinFixture())
            {
                string path = Path.Combine(fixture.Root, module ? fixture.Proof.modules[0].path : fixture.Proof.backingPath);
                File.SetAttributes(path, System.IO.FileAttributes.Normal); File.AppendAllText(path, "changed");
                Assert.AreEqual("ResourceBuiltinProofMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Verify()).Code);
            }
        }

        [TestCase("missing-id", "ResourceBuiltinIdentityMismatch")]
        [TestCase("duplicate-id", "ResourceBuiltinIdentityMismatch")]
        [TestCase("foreign-guid", "ResourceBuiltinIdentityMismatch")]
        [TestCase("nonpersistent", "ResourceBuiltinIdentityMismatch")]
        [TestCase("foreign-type", "ResourceBuiltinTypeUnproven")]
        [TestCase("unknown-type", "ResourceBuiltinTypeUnproven")]
        [TestCase("monobehaviour", "ResourceBuiltinTypeUnproven")]
        public void RehashedBuiltinProofStillRequiresIdentityAndEngineTypeEvidence(string mutation, string expected)
        {
            using (var fixture = new BuiltinFixture())
            {
                var item = fixture.Proof.objects[0];
                switch (mutation)
                {
                    case "missing-id": item.localId = 0; break;
                    case "duplicate-id": item.localId = fixture.Proof.objects[1].localId; break;
                    case "foreign-guid": item.guid = new string('f', 32); break;
                    case "nonpersistent": item.persistent = false; break;
                    case "foreign-type": item.assemblyName = typeof(ResourceReceiptTests).Assembly.GetName().Name; item.typeName = typeof(AuthoredScriptableObject).FullName; break;
                    case "unknown-type": item.typeName = "UnityEngine.NotARealEngineType"; break;
                    case "monobehaviour": item.assemblyName = "UnityEngine.CoreModule"; item.typeName = "UnityEngine.MonoBehaviour"; break;
                }
                fixture.Reseal();
                Assert.AreEqual(expected, Assert.Throws<ShadowBuildException>(() => fixture.Verify()).Code);
            }
        }

        [Test]
        public void ProjectScriptableObjectTypeCannotBeClassifiedAsEngineOwned()
        {
            var method = typeof(ShadowResourceBaseline).GetMethod("RequireEngineModule", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, new object[] { typeof(AuthoredScriptableObject) }));
            Assert.IsInstanceOf<ShadowBuildException>(exception.InnerException);
            Assert.AreEqual("ResourceBuiltinTypeUnproven", ((ShadowBuildException)exception.InnerException).Code);
        }

        private sealed class AuthoredScriptableObject : ScriptableObject { }

        private sealed class BuiltinFixture : IDisposable
        {
            public string Root;
            public ShadowResourceSource Source;
            public ShadowBuiltinResourceProof Proof;
            public BuiltinFixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "ShadowBuiltinReceiptTest-" + System.Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
                try
                {
                    Source = ShadowResourceBaseline.CaptureBuiltinSource("Library/unity default resources", Root);
                    Proof = JsonUtility.FromJson<ShadowBuiltinResourceProof>(File.ReadAllText(Path.Combine(Root, Source.snapshotPath)));
                }
                catch { Dispose(); throw; }
            }
            public void Verify() { ShadowResourceBaseline.VerifyBuiltinSource(Root, Source); }
            public void Reseal()
            {
                string path = Path.Combine(Root, Source.snapshotPath);
                File.WriteAllText(path, JsonUtility.ToJson(Proof, true), new UTF8Encoding(false)); Source.sha256 = ShadowHash.File(path);
            }
            public void Dispose()
            {
                if (!Directory.Exists(Root)) return;
                foreach (string path in Directory.GetFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, System.IO.FileAttributes.Normal);
                Directory.Delete(Root, true);
            }
        }

        private static ResourceAbiDescriptor ChangedAbi()
        {
            return new ResourceAbiDescriptor(new[] { new ResourceAbiTypeDescriptor { typeKey = "fixture:Test:Component", assembly = "fixture", @namespace = "Test", type = "Component",
                fields = new[] { new ResourceAbiFieldDescriptor { declaringType = "fixture:Test:Component", name = "newSerializedField", type = "System.Int32", flags = "public;" } }
            } }, new string[0]);
        }

        private sealed class ReceiptFixture : IDisposable
        {
            public const string Guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            private readonly string parent;
            public string Root;
            public ShadowResourceBaselineReceipt Receipt;
            public ReceiptFixture()
            {
                parent = Path.Combine(Path.GetTempPath(), "ShadowResourceReceiptTest-" + System.Guid.NewGuid().ToString("N"));
                Root = Path.Combine(parent, "Receipt"); Directory.CreateDirectory(parent); Directory.CreateDirectory(Root);
                string dll = Path.Combine(parent, "Fixture.dll");
                using (var module = new ModuleDefUser("Fixture.dll") { Kind = ModuleKind.Dll })
                { var assembly = new AssemblyDefUser("Fixture", new Version(1, 0)); assembly.Modules.Add(module); module.Write(dll); }
                var pin = new ShadowRepositoryPin { revision = new string('a', 40), url = "fixture" };
                var pins = new ShadowSourcePins { unityVersion = Application.unityVersion, target = BuildTarget.StandaloneOSX.ToString(), architecture = "arm64", hybridclr = pin, hybridclrUnity = pin, il2cppPlus = pin, demo = pin };
                var input = AssemblySnapshot.Capture(Path.Combine(Root, "CompilerInputs"), new[] { dll }, new string[0], "CompilePlayerScripts", BuildTarget.StandaloneOSX, "arm64", pins, new string[0]);
                Directory.CreateDirectory(Path.Combine(Root, "ResourceAssemblies"));
                File.Copy(dll, Path.Combine(Root, "ResourceAssemblies/Fixture.dll"));
                string sourcePath = "Sources/Assets/Fixture.prefab";
                Write(sourcePath, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1 &1\nGameObject:\n  m_Name: Fixture\n");
                Write(sourcePath + ".meta", "fileFormatVersion: 2\nguid: " + Guid + "\n");
                Write("Bundles/fixture.bundle", "opaque frozen bundle fixture bytes");
                var source = new ShadowResourceSource { path = "Assets/Fixture.prefab", snapshotPath = sourcePath, sha256 = ShadowHash.File(Path.Combine(Root, sourcePath)),
                    metaSnapshotPath = sourcePath + ".meta", metaSha256 = ShadowHash.File(Path.Combine(Root, sourcePath + ".meta")), guid = Guid, dependencies = new[] { "Assets/Fixture.prefab" } };
                var abi = new ResourceAbiDescriptor(); var index = new ResourceScriptIndex { bundles = new[] { "fixture.bundle" } };
                Json("resource-abi.json", abi); Json("resource-script-index.json", index);
                Receipt = new ShadowResourceBaselineReceipt { provenance = ShadowResourceBaseline.FreshBuildProvenance, unityVersion = Application.unityVersion,
                    target = BuildTarget.StandaloneOSX.ToString(), architecture = "arm64", compilerSnapshotHash = input.snapshotHash,
                    candidateAssemblies = new[] { "Fixture" }, metadataAssemblies = new[] { new ShadowResourceProofFile { path = "ResourceAssemblies/Fixture.dll", sha256 = input.assemblies.Single().sha256 } },
                    buildMap = new ShadowResourceBuildMap { bundleDirectory = "Bundles", bundles = new[] { new ShadowBundleDefinition { name = "fixture.bundle", assets = new[] { "Assets/Fixture.prefab" } } } },
                    bundles = new[] { new ShadowBundleArtifact { name = "fixture.bundle", assets = new[] { "Assets/Fixture.prefab" }, sha256 = ShadowHash.File(Path.Combine(Root, "Bundles/fixture.bundle")) } },
                    sources = new[] { source }, scripts = new ShadowResourceScript[0], sourceSetHash = ShadowResourceBaseline.ComputeSourceSetHash(new[] { source }),
                    dependencies = new ShadowDependencyConfiguration(), resourceAbiHash = ResourceAbiHasher.Compute(abi), resourceAbiFileSha256 = ShadowHash.File(Path.Combine(Root, "resource-abi.json")),
                    resourceIndexHash = ShadowHash.File(Path.Combine(Root, "resource-script-index.json")) };
                Seal();
            }
            public VerifiedShadowResourceBaseline Read() { return ShadowResourceBaseline.ReadAndVerify(Root, BuildTarget.StandaloneOSX, "arm64"); }
            public void Reject(string expected) { Assert.AreEqual(expected, Assert.Throws<ShadowBuildException>(() => Read()).Code); }
            public void Seal() { Json(ShadowResourceBaseline.ReceiptName, Receipt); Write("manifest.sha256", ShadowHash.File(Path.Combine(Root, ShadowResourceBaseline.ReceiptName)) + "\n"); }
            public void Json(string path, object value) { Write(path, JsonUtility.ToJson(value, true)); }
            private void Write(string path, string contents) { string destination = Path.Combine(Root, path); Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.WriteAllText(destination, contents, new UTF8Encoding(false)); }
            public void Dispose() { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
        }
    }
}
