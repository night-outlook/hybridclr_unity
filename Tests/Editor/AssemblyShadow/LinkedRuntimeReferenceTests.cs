using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using NUnit.Framework;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class LinkedRuntimeReferenceTests
    {
        [Test] public void RemovedRawReferenceNeedsProofAndNeverMutatesDescriptors()
        {
            using (var fixture = new Fixture())
            using (var set = fixture.Load())
            {
                string[] references = set.Get("Consumer").references;
                StringAssert.Contains("RuntimeReferencesFilteredAssembly", Validate(set, fixture.Policy).ToString());
                var proof = fixture.Proof(set);
                CollectionAssert.AreEqual(new[] { "consumer -> provider" }, proof.RemovedReferences);
                Assert.IsTrue(((ICollection<string>)proof.RemovedReferences).IsReadOnly);
                Assert.IsEmpty(typeof(VerifiedLinkedRuntimeReferences).GetConstructors());
                Assert.IsTrue(Validate(set, fixture.Policy, proof).IsValid);
                Assert.AreSame(references, set.Get("Consumer").references);
                CollectionAssert.AreEqual(new[] { "provider" }, references);
                Assert.AreEqual(proof.ProvenanceHash, fixture.Proof(set).ProvenanceHash);
                var definitions = new[] {
                    new AssemblyPolicyDefinition { name = "Consumer", references = references },
                    new AssemblyPolicyDefinition { name = "Provider", classification = AssemblyClassification.BuildFiltered, entersPlayer = false },
                    new AssemblyPolicyDefinition { name = "Protected", isShadowCapable = true },
                };
                StringAssert.Contains("RuntimeReferencesFilteredAssembly", ShadowAssemblyPolicyValidator.ValidateDefinitions(definitions, fixture.Policy, DateTime.UtcNow).ToString());
            }
        }

        [Test] public void MissingPlayerEvidenceCannotCreateProof()
        {
            using (var fixture = new Fixture())
            using (var set = fixture.Load())
            {
                Assert.AreEqual("LinkedRuntimeEvidenceMissing", Assert.Throws<ShadowBuildException>(() =>
                    VerifiedLinkedRuntimeReferences.Verify(fixture.Snapshot, null, set, fixture.Policy)).Code);
                File.Delete(Path.Combine(fixture.Snapshot, AssemblySnapshot.ReceiptName));
                Assert.AreEqual("SnapshotReceiptMissing", Assert.Throws<ShadowBuildException>(() => fixture.Proof(set)).Code);
            }
        }

        [Test] public void RetainedLinkedReferenceCannotBeWaived()
        {
            using (var fixture = new Fixture(retainLinkedReference: true))
            using (var set = fixture.Load())
            {
                var proof = fixture.Proof(set); Assert.IsEmpty(proof.RemovedReferences);
                StringAssert.Contains("RuntimeReferencesFilteredAssembly", Validate(set, fixture.Policy, proof).ToString());
            }
        }

        [Test] public void CallbackOnlyRemovalCannotBeWaived()
        {
            using (var fixture = new Fixture(callbackFiltered: true))
            using (var set = fixture.Load())
            {
                var proof = fixture.Proof(set); Assert.IsEmpty(proof.RemovedReferences);
                StringAssert.Contains("RuntimeReferencesFilteredAssembly", Validate(set, fixture.Policy, proof).ToString());
            }
        }

        [Test] public void ChangedCurrentIlOrIdentityCannotUseFrozenLink()
        {
            foreach (bool identity in new[] { false, true })
                using (var fixture = new Fixture())
                {
                    fixture.WriteModule(fixture.Current, "Consumer", true, identity ? 2 : 1, addNop: !identity);
                    using (var set = fixture.Load())
                    {
                        var proof = fixture.Proof(set); Assert.IsEmpty(proof.RemovedReferences);
                        StringAssert.Contains("RuntimeReferencesFilteredAssembly", Validate(set, fixture.Policy, proof).ToString());
                    }
                }
        }

        [Test] public void RecompiledFixedAotWithOnlyDifferentMvidCanUseFrozenLink()
        {
            using (var fixture = new Fixture())
            {
                string frozenHash = fixture.Receipt.assemblies.Single(f => f.name == "Consumer").sha256;
                fixture.WriteModule(fixture.Current, "Consumer", true);
                using (var set = fixture.Load())
                {
                    Assert.AreNotEqual(frozenHash, set.Get("Consumer").sha256);
                    var proof = fixture.Proof(set);
                    CollectionAssert.AreEqual(new[] { "consumer -> provider" }, proof.RemovedReferences);
                    Assert.IsTrue(Validate(set, fixture.Policy, proof).IsValid);
                }
            }
        }

        [Test] public void ChangedCurrentReferencesCannotUseFrozenLink()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteModule(fixture.Current, "Consumer", false);
                using (var set = fixture.Load()) Assert.IsEmpty(fixture.Proof(set).RemovedReferences);
            }
        }

        [Test] public void InMemoryIdentityAndIlChangesCannotMintProof()
        {
            foreach (bool identity in new[] { false, true })
                using (var fixture = new Fixture())
                using (var set = fixture.Load())
                {
                    if (identity) set.GetModule("Consumer").Assembly.Version = new Version(2, 0, 0, 0);
                    else set.GetModule("Consumer").Types.Single(t => t.Name == "Payload").Methods[0].Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
                    Assert.IsEmpty(fixture.Proof(set).RemovedReferences);
                }
        }

        [Test] public void CandidateNormalAndBootstrapConsumersRemainIneligible()
        {
            foreach (string role in new[] { "candidate", "normal", "bootstrap" })
                using (var fixture = new Fixture(consumerRole: role))
                using (var set = fixture.Load()) Assert.IsEmpty(fixture.Proof(set).RemovedReferences, role);
        }

        [Test] public void ProtectedCandidateDemotionOrBootstrapRoleSwapCannotGrantEligibility()
        {
            foreach (bool bootstrapSwap in new[] { false, true })
                using (var fixture = new Fixture(consumerRole: "candidate"))
                using (var set = fixture.Load())
                {
                    var role = fixture.Policy.assemblies.Single(c => c.name == "Consumer");
                    role.isShadowCapable = false; role.isBootstrap = bootstrapSwap;
                    set.Get("Consumer").isShadowCapable = false; set.Get("Consumer").isBootstrap = bootstrapSwap;
                    Assert.IsEmpty(fixture.Proof(set).RemovedReferences, "Authenticated protected union must survive current-role changes.");
                }
        }

        [Test] public void NormalHotUpdateProviderCannotBecomeACompilerOnlyException()
        {
            using (var fixture = new Fixture(normalProvider: true))
            using (var set = fixture.Load()) Assert.IsEmpty(fixture.Proof(set).RemovedReferences);
        }

        [Test] public void WrongExpectedSnapshotNativeGuidAndMutableReceiptReject()
        {
            using (var fixture = new Fixture())
            using (var other = new Fixture())
            using (var set = fixture.Load())
            {
                Assert.AreEqual("LinkedRuntimeSnapshotMismatch", Assert.Throws<ShadowBuildException>(() =>
                    VerifiedLinkedRuntimeReferences.Verify(fixture.Snapshot, other.Receipt, set, fixture.Policy)).Code);
                fixture.Receipt.buildGuid = Guid.NewGuid().ToString("N");
                Assert.AreEqual("LinkedRuntimeSnapshotMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Proof(set)).Code);
                fixture.Receipt = AssemblySnapshot.ReadAndVerify(fixture.Snapshot, true);
                fixture.Receipt.nativeLibrarySha256 = new string('a', 64);
                Assert.AreEqual("LinkedRuntimeSnapshotMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Proof(set)).Code);
            }
        }

        [Test] public void TamperedLinkedOrNativeBytesRejectFactory()
        {
            using (var fixture = new Fixture())
            using (var set = fixture.Load())
            {
                File.AppendAllText(fixture.LinkedConsumer, "tampered");
                Assert.AreEqual("LinkedFileHashMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Proof(set)).Code);
            }
            using (var fixture = new Fixture())
            using (var set = fixture.Load())
            {
                File.AppendAllText(fixture.Receipt.nativeLibraryPath, "tampered");
                Assert.AreEqual("LinkedRuntimeNativeMismatch", Assert.Throws<ShadowBuildException>(() => fixture.Proof(set)).Code);
            }
        }

        [Test] public void ProofCannotBeReusedForAnotherSetOrMutatedRolesMetadataAndEvidence()
        {
            using (var fixture = new Fixture())
            using (var set = fixture.Load())
            using (var another = fixture.Load())
                StringAssert.Contains("InvalidLinkedRuntimeReferenceProof", Validate(another, fixture.Policy, fixture.Proof(set)).ToString());
            foreach (string mutation in new[] { "identity", "il", "consumerRole", "providerRole", "descriptor", "references", "currentFile", "linked", "receipt", "native" })
                using (var fixture = new Fixture())
                using (var set = fixture.Load())
                {
                    var proof = fixture.Proof(set);
                    switch (mutation)
                    {
                        case "identity": set.GetModule("Consumer").Assembly.Version = new Version(2, 0); break;
                        case "il": set.GetModule("Consumer").Types.Single(t => t.Name == "Payload").Methods[0].Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop)); break;
                        case "consumerRole": fixture.Policy.assemblies.Single(c => c.name == "Consumer").isShadowCapable = true; break;
                        case "providerRole": fixture.Policy.assemblies.Single(c => c.name == "Provider").classification = AssemblyClassification.Runtime; break;
                        case "descriptor": set.Get("Provider").isBootstrap = true; break;
                        case "references": set.Get("Consumer").references = new string[0]; break;
                        case "currentFile": fixture.WriteModule(fixture.Current, "Consumer", true); break;
                        case "linked": File.AppendAllText(fixture.LinkedConsumer, "tampered"); break;
                        case "receipt": File.AppendAllText(Path.Combine(fixture.Snapshot, AssemblySnapshot.ReceiptName), " "); break;
                        case "native": File.AppendAllText(fixture.Receipt.nativeLibraryPath, "tampered"); break;
                    }
                    StringAssert.Contains("InvalidLinkedRuntimeReferenceProof", Validate(set, fixture.Policy, proof).ToString(), mutation);
                }
        }

        [Test] public void DeclaredAndReflectionDependenciesAreNeverWaived()
        {
            using (var fixture = new Fixture())
            using (var set = fixture.Load())
            {
                var proof = fixture.Proof(set);
                fixture.Policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency {
                    consumer = "Consumer", provider = "Provider", kind = "Runtime", evidence = "explicit contract" } };
                StringAssert.Contains("RuntimeReferencesFilteredAssembly", Validate(set, fixture.Policy, proof).ToString());
            }
            using (var fixture = new Fixture(reflection: true))
            using (var set = fixture.Load())
            {
                var proof = fixture.Proof(set); Assert.AreEqual(1, proof.RemovedReferences.Count);
                StringAssert.Contains("RuntimeReferencesFilteredAssembly", Validate(set, fixture.Policy, proof).ToString());
            }
        }

        private static ShadowPolicyValidationResult Validate(CompiledAssemblySet set, ShadowPolicyConfiguration policy, VerifiedLinkedRuntimeReferences proof = null)
        { return ShadowAssemblyPolicyValidator.ValidateCompiled(set, policy, DateTime.UtcNow, linkedRuntimeReferences: proof); }

        private sealed class Fixture : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "LinkedRuntimeReferenceTests-" + Guid.NewGuid().ToString("N"));
            public string Snapshot { get { return Path.Combine(Root, "Snapshot"); } }
            public string Current { get { return Path.Combine(Root, "Current"); } }
            public string LinkedConsumer { get { return Path.Combine(Snapshot, "LinkedPlayer/Assemblies/consumer.dll"); } }
            public AssemblySnapshotReceipt Receipt;
            public ShadowPolicyConfiguration Policy;
            private readonly bool reflect;

            public Fixture(bool retainLinkedReference = false, bool callbackFiltered = false, string consumerRole = "runtime", bool normalProvider = false, bool reflection = false)
            {
                reflect = reflection;
                var source = new ShadowPolicyConfiguration { assemblies = new[] {
                    new AssemblyCapability { name = "Consumer", isShadowCapable = consumerRole == "candidate", isBootstrap = consumerRole == "bootstrap",
                        classification = consumerRole == "normal" ? AssemblyClassification.NormalHotUpdate : AssemblyClassification.Runtime },
                    new AssemblyCapability { name = "Provider", classification = normalProvider ? AssemblyClassification.NormalHotUpdate : AssemblyClassification.Runtime },
                    new AssemblyCapability { name = "Protected", isShadowCapable = true },
                } };
                foreach (string name in new[] { "Consumer", "Provider", "Protected" }) WriteModule(Current, name, name == "Consumer");
                Directory.CreateDirectory(Path.Combine(Snapshot, "References"));
                var inputs = new List<SnapshotFile>(); var filtered = new List<SnapshotFile>();
                foreach (string name in new[] { "Consumer", "Provider", "Protected" })
                {
                    string relative = "Assemblies/" + (callbackFiltered && name == "Provider" ? "Filtered/" : "") + name + ".dll";
                    string destination = Path.Combine(Snapshot, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(Path.Combine(Current, name + ".dll"), destination);
                    var file = new SnapshotFile { name = name, path = relative, sha256 = ShadowHash.File(destination) };
                    if (callbackFiltered && name == "Provider") filtered.Add(file); else inputs.Add(file);
                }
                string native = Path.Combine(Root, "Native.bin"); File.WriteAllText(native, "synthetic native Player evidence");
                var pin = new ShadowRepositoryPin { revision = new string('1', 40), url = "fixture" };
                Receipt = new AssemblySnapshotReceipt {
                    kind = "PlayerBuildInputs", unityVersion = "fixture", target = "StandaloneOSX", architecture = "arm64", buildGuid = Guid.NewGuid().ToString("N"),
                    playerBuildSucceeded = true, playerBuildFilterCaptured = true, nativeLibraryPath = native, nativeLibrarySha256 = ShadowHash.File(native),
                    sourcePins = new ShadowSourcePins { unityVersion = "fixture", target = "StandaloneOSX", architecture = "arm64", hybridclr = pin, hybridclrUnity = pin, il2cppPlus = pin },
                    assemblies = inputs.ToArray(), references = new SnapshotFile[0], filteredAssemblies = filtered.ToArray(),
                    filteredAssemblyCapabilities = callbackFiltered ? new[] { source.assemblies[1] } : new AssemblyCapability[0],
                    normalHotUpdateAssemblies = source.assemblies.Where(c => c.classification == AssemblyClassification.NormalHotUpdate).Select(c => c.name).ToArray(),
                    linkerExcludedAssemblies = callbackFiltered ? new string[0] : new[] { "provider" },
                    linkerExcludedAssemblyCapabilities = callbackFiltered ? new AssemblyCapability[0] : new[] { source.assemblies[1] },
                };
                string linkedDirectory = Path.Combine(Snapshot, "LinkedPlayer/Assemblies");
                WriteModule(linkedDirectory, "Consumer", retainLinkedReference, canonicalFile: true);
                WriteModule(linkedDirectory, "Protected", false, canonicalFile: true);
                Receipt.linkedPlayerReceipt = new LinkedPlayerReceipt { buildGuid = Receipt.buildGuid, nativeLibrarySha256 = Receipt.nativeLibrarySha256,
                    target = Receipt.target, architecture = Receipt.architecture, sourceDirectory = Path.GetFullPath(linkedDirectory),
                    protectedAssemblies = source.assemblies.Where(c => c.isShadowCapable || c.isBootstrap).Select(c => AssemblyIdentityUtil.CanonicalName(c.name)).OrderBy(n => n).ToArray(),
                    assemblies = Directory.GetFiles(linkedDirectory, "*.dll").Select(path => {
                        using (var module = ModuleDefMD.Load(path)) return new LinkedPlayerFile { name = AssemblyIdentityUtil.CanonicalName(module.Assembly.Name.String),
                            path = "Assemblies/" + Path.GetFileName(path), sha256 = ShadowHash.File(path), mvid = module.Mvid.ToString() }; }).OrderBy(f => f.name).ToArray(),
                };
                Receipt.linkedPlayerReceiptHash = ShadowLinkedPlayerEvidence.ComputeHash(Receipt.linkedPlayerReceipt);
                Receipt.snapshotHash = AssemblySnapshot.ComputeHash(Receipt);
                File.WriteAllText(Path.Combine(Snapshot, "LinkedPlayer/" + ShadowLinkedPlayerEvidence.ReceiptName), JsonUtility.ToJson(Receipt.linkedPlayerReceipt));
                File.WriteAllText(Path.Combine(Snapshot, AssemblySnapshot.ReceiptName), JsonUtility.ToJson(Receipt));
                Receipt = AssemblySnapshot.ReadAndVerify(Snapshot, true);
                Policy = ShadowFilteredInputPolicy.Apply(source, Receipt);
            }

            public void WriteModule(string directory, string name, bool providerReference, int version = 1, bool canonicalFile = false, bool addNop = false)
            {
                Directory.CreateDirectory(directory);
                using (var module = new ModuleDefUser(name + ".dll") { Kind = ModuleKind.Dll, Mvid = Guid.NewGuid() })
                {
                    new AssemblyDefUser(name, new Version(version, 0, 0, 0)).Modules.Add(module);
                    var type = new TypeDefUser("Fixture", "Payload"); module.Types.Add(type);
                    var method = new MethodDefUser("Run", MethodSig.CreateStatic(module.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static);
                    method.Body = new CilBody(); type.Methods.Add(method);
                    if (providerReference)
                    {
                        var scope = new AssemblyRefUser("Provider", new Version(1, 0, 0, 0));
                        if (reflect)
                        {
                            var systemType = new TypeRefUser(module, "System", "Type", scope);
                            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Fixture.Payload, Provider"));
                            method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new MemberRefUser(module, "GetType", MethodSig.CreateStatic(new ClassSig(systemType), module.CorLibTypes.String), systemType)));
                            method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                        }
                        else method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new MemberRefUser(module, "Run", MethodSig.CreateStatic(module.CorLibTypes.Void), new TypeRefUser(module, "Fixture", "Payload", scope))));
                    }
                    if (addNop) method.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    module.Write(Path.Combine(directory, (canonicalFile ? name.ToLowerInvariant() : name) + ".dll"));
                }
            }
            public CompiledAssemblySet Load() { return DnlibAssemblyLoader.Load(Current, new[] { Path.Combine(Snapshot, "References") }, Policy.assemblies); }
            public VerifiedLinkedRuntimeReferences Proof(CompiledAssemblySet set) { return VerifiedLinkedRuntimeReferences.Verify(Snapshot, Receipt, set, Policy); }
            public void Dispose() { Directory.Delete(Root, true); }
        }
    }
}
