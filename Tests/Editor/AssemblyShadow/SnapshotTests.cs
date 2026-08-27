using System;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class SnapshotTests
    {
        private string root;
        [SetUp] public void SetUp() { root = Path.Combine(Path.GetTempPath(), "AssemblyShadowSnapshotTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
        [TearDown] public void TearDown() { if (Directory.Exists(root)) Directory.Delete(root, true); }

        [Test] public void DuplicateReferenceIdentitiesAreRejectedRatherThanFirstWins()
        {
            string input = Module("input", "Input");
            string first = Module("first", "Reference");
            string second = Module("second", "Reference");
            Assert.AreEqual("DuplicateAssembly", Assert.Throws<ShadowBuildException>(() => Capture(new[] { input }, new[] { first, second })).Code);
        }

        [Test] public void ActualCompilerInputOverridesItsReferenceCopy()
        {
            string input = Module("input", "Input");
            string reference = Module("reference", "Input");
            var receipt = Capture(new[] { input }, new[] { reference });
            Assert.AreEqual(1, receipt.assemblies.Length);
            Assert.IsEmpty(receipt.references);
            Assert.AreEqual(ShadowHash.File(input), receipt.assemblies[0].sha256);
            AssemblySnapshot.ReadAndVerify(Path.Combine(root, "snapshot"), false);
        }

        [Test] public void CompilerSnapshotNullLinkedReceiptRoundTripsAsAbsence()
        {
            var captured = Capture(new[] { Module("input", "Input") }, new string[0]);
            Assert.IsNull(captured.linkedPlayerReceipt);
            var roundTrip = JsonUtility.FromJson<AssemblySnapshotReceipt>(File.ReadAllText(Path.Combine(root, "snapshot", AssemblySnapshot.ReceiptName)));
            Assert.IsTrue(ShadowLinkedPlayerEvidence.IsAbsent(roundTrip), "Unity's serialized null/default representation has no linked claims.");
            var verified = AssemblySnapshot.ReadAndVerify(Path.Combine(root, "snapshot"), false);
            Assert.IsNull(verified.linkedPlayerReceipt, "The verified API normalizes semantic absence.");
            Assert.AreEqual(captured.snapshotHash, verified.snapshotHash);
        }

        [Test] public void LinkedAbsenceAcceptsNullAndClaimlessZeroOrConstructorDefaults()
        {
            var receipt = new AssemblySnapshotReceipt();
            Assert.IsTrue(ShadowLinkedPlayerEvidence.IsAbsent(receipt));
            foreach (int schema in new[] { 0, 1 })
            {
                receipt.linkedPlayerReceipt = new LinkedPlayerReceipt { schemaVersion = schema };
                Assert.IsTrue(ShadowLinkedPlayerEvidence.IsAbsent(receipt));
                receipt.linkedPlayerReceipt.protectedAssemblies = null; receipt.linkedPlayerReceipt.assemblies = null;
                receipt.linkerExcludedAssemblies = null; receipt.linkerExcludedAssemblyCapabilities = null;
                Assert.IsTrue(ShadowLinkedPlayerEvidence.IsAbsent(receipt));
            }
        }

        [Test] public void LinkedAbsenceRejectsEveryIndividualClaimIncludingMalformedRows()
        {
            foreach (var field in typeof(LinkedPlayerReceipt).GetFields().Where(field => field.FieldType == typeof(string)))
            {
                var receipt = new AssemblySnapshotReceipt { linkedPlayerReceipt = new LinkedPlayerReceipt() };
                field.SetValue(receipt.linkedPlayerReceipt, " ");
                Assert.IsFalse(ShadowLinkedPlayerEvidence.IsAbsent(receipt), field.Name + " must not be silently discarded.");
            }
            var claims = new Action<AssemblySnapshotReceipt>[]
            {
                receipt => receipt.linkedPlayerReceipt.schemaVersion = 2,
                receipt => receipt.linkedPlayerReceipt.schemaVersion = -1,
                receipt => receipt.linkedPlayerReceipt.protectedAssemblies = new string[] { null },
                receipt => receipt.linkedPlayerReceipt.assemblies = new LinkedPlayerFile[] { null },
                receipt => receipt.linkedPlayerReceiptHash = " ",
                receipt => receipt.linkerExcludedAssemblies = new string[] { null },
                receipt => receipt.linkerExcludedAssemblyCapabilities = new AssemblyCapability[] { null },
            };
            foreach (var claim in claims)
            {
                var receipt = new AssemblySnapshotReceipt { linkedPlayerReceipt = new LinkedPlayerReceipt() };
                claim(receipt); Assert.IsFalse(ShadowLinkedPlayerEvidence.IsAbsent(receipt));
            }
            Assert.IsFalse(ShadowLinkedPlayerEvidence.IsAbsent(null));
        }

        [Test] public void CompilerSnapshotRejectsPartialLinkedClaimsEvenAfterRehash()
        {
            var receipt = Capture(new[] { Module("input", "Input") }, new string[0]);
            receipt.linkedPlayerReceipt = new LinkedPlayerReceipt { buildGuid = "partial-claim" };
            receipt.snapshotHash = AssemblySnapshot.ComputeHash(receipt);
            File.WriteAllText(Path.Combine(root, "snapshot", AssemblySnapshot.ReceiptName), JsonUtility.ToJson(receipt));
            Assert.AreEqual("UnexpectedLinkedEvidence", Assert.Throws<ShadowBuildException>(() => AssemblySnapshot.ReadAndVerify(Path.Combine(root, "snapshot"), false)).Code);
        }

        [Test] public void CompilerSnapshotCannotHideLinkedTreeBehindAnEmptyReceipt()
        {
            Capture(new[] { Module("input", "Input") }, new string[0]);
            Directory.CreateDirectory(Path.Combine(root, "snapshot", ShadowLinkedPlayerEvidence.DirectoryName));
            Assert.AreEqual("UnexpectedLinkedEvidence", Assert.Throws<ShadowBuildException>(() => AssemblySnapshot.ReadAndVerify(Path.Combine(root, "snapshot"), false)).Code);
        }

        [Test] public void CompilerSnapshotRejectsCompleteLinkedEvidenceEvenAfterRehash()
        {
            var receipt = LinkedSnapshot(); receipt.kind = "CompilePlayerScripts"; receipt.snapshotHash = AssemblySnapshot.ComputeHash(receipt);
            File.WriteAllText(Path.Combine(root, "snapshot", AssemblySnapshot.ReceiptName), JsonUtility.ToJson(receipt));
            Assert.AreEqual("UnexpectedLinkedEvidence", Assert.Throws<ShadowBuildException>(() => AssemblySnapshot.ReadAndVerify(Path.Combine(root, "snapshot"), false)).Code);
        }

        [Test] public void DuplicateActualCompilerInputsAreRejected()
        {
            string first = Module("first", "Input");
            string second = Module("second", "Input");
            Assert.AreEqual("DuplicateAssembly", Assert.Throws<ShadowBuildException>(() => Capture(new[] { first, second }, new string[0])).Code);
        }

        [Test] public void UnlistedDllCannotBeInjectedIntoSnapshot()
        {
            string input = Module("input", "Input");
            Capture(new[] { input }, new string[0]);
            File.Copy(input, Path.Combine(root, "snapshot/Assemblies/Injected.dll"));
            Assert.AreEqual("SnapshotSetMismatch", Assert.Throws<ShadowBuildException>(() => AssemblySnapshot.ReadAndVerify(Path.Combine(root, "snapshot"), false)).Code);
        }

        [Test] public void CompileOutputCannotBeUsedAsSuccessfulPlayerEvidence()
        {
            Capture(new[] { Module("input", "Input") }, new string[0]);
            Assert.AreEqual("NotPlayerSnapshot", Assert.Throws<ShadowBuildException>(() => AssemblySnapshot.ReadAndVerify(Path.Combine(root, "snapshot"), true)).Code);
        }

        [Test] public void CapturedInputCannotBeReclassifiedAsAReferenceByMovingReceiptRows()
        {
            var receipt = Capture(new[] { Module("input", "Input") }, new string[0]);
            receipt.references = receipt.assemblies; receipt.assemblies = new SnapshotFile[0];
            File.WriteAllText(Path.Combine(root, "snapshot", AssemblySnapshot.ReceiptName), JsonUtility.ToJson(receipt));
            Assert.AreEqual("SnapshotSectionMismatch", Assert.Throws<ShadowBuildException>(() => AssemblySnapshot.ReadAndVerify(Path.Combine(root, "snapshot"), false)).Code);
        }

        [Test] public void PlayerBuildProvenanceCannotBeRelabelledWithNewDemoSources()
        {
            var runtime = new ShadowRepositoryPin { revision = new string('1', 40), url = "runtime" };
            var captured = new ShadowSourcePins { hybridclr = runtime, hybridclrUnity = runtime, il2cppPlus = runtime,
                demo = new ShadowRepositoryPin { revision = new string('2', 40), url = "demo" } };
            var current = new ShadowSourcePins { hybridclr = runtime, hybridclrUnity = runtime, il2cppPlus = runtime,
                demo = new ShadowRepositoryPin { revision = new string('3', 40), url = "demo" } };
            Assert.DoesNotThrow(() => ShadowSourcePins.RequireCompatible(captured, current), "A patch may use changed business sources.");
            Assert.AreEqual("BuildSourceProvenanceMismatch", Assert.Throws<ShadowBuildException>(() => ShadowSourcePins.RequireSameBuildSources(captured, current)).Code);
        }

        [Test] public void LinkedProofPreservesDifferentStrippedBytesAndCopiesIndependently()
        {
            var receipt = LinkedSnapshot();
            Assert.AreNotEqual(receipt.assemblies[0].sha256, receipt.linkedPlayerReceipt.assemblies.Single(f => f.name == "input").sha256);
            Assert.AreEqual(2, AssemblySnapshot.AllFiles(receipt).Count(), "Linked DLLs must not become duplicate resolver inputs.");
            AssemblySnapshot.ReadAndVerify(Path.Combine(root, "snapshot"), true);
            ShadowLinkedPlayerEvidence.Copy(Path.Combine(root, "snapshot"), Path.Combine(root, "copy"), receipt);
            ShadowLinkedPlayerEvidence.ReadAndVerify(Path.Combine(root, "copy"), receipt);
            Assert.AreEqual("LinkedEvidenceExists", Assert.Throws<ShadowBuildException>(() =>
                ShadowLinkedPlayerEvidence.Copy(Path.Combine(root, "snapshot"), Path.Combine(root, "copy"), receipt)).Code);
        }

        [Test] public void LinkedDllAndPdbTamperingAreRejected()
        {
            var receipt = LinkedSnapshot();
            string directory = Path.Combine(root, "snapshot/LinkedPlayer");
            var file = receipt.linkedPlayerReceipt.assemblies.Single(f => f.name == "input");
            string dll = Path.Combine(directory, file.path), pdb = Path.Combine(directory, file.pdbPath);
            byte[] original = File.ReadAllBytes(dll);
            File.WriteAllBytes(dll, new byte[] { 1, 2, 3 });
            Assert.AreEqual("LinkedFileHashMismatch", Assert.Throws<ShadowBuildException>(() => AssemblySnapshot.ReadAndVerify(Path.Combine(root, "snapshot"), true)).Code);
            File.WriteAllBytes(dll, original); File.WriteAllBytes(pdb, new byte[] { 9 });
            Assert.AreEqual("LinkedPdbHashMismatch", Assert.Throws<ShadowBuildException>(() => AssemblySnapshot.ReadAndVerify(Path.Combine(root, "snapshot"), true)).Code);
        }

        [Test] public void LinkedProofRejectsMissingReceiptAndInjectedFiles()
        {
            var receipt = LinkedSnapshot();
            string directory = Path.Combine(root, "snapshot/LinkedPlayer");
            string receiptPath = Path.Combine(directory, ShadowLinkedPlayerEvidence.ReceiptName);
            string contents = File.ReadAllText(receiptPath); File.Delete(receiptPath);
            Assert.AreEqual("LinkedReceiptMissing", Assert.Throws<ShadowBuildException>(() => AssemblySnapshot.ReadAndVerify(Path.Combine(root, "snapshot"), true)).Code);
            File.WriteAllText(receiptPath, contents); File.WriteAllText(Path.Combine(directory, "unlisted.txt"), "injected");
            Assert.AreEqual("LinkedFileSetMismatch", Assert.Throws<ShadowBuildException>(() => ShadowLinkedPlayerEvidence.ReadAndVerify(Path.Combine(root, "snapshot"), receipt)).Code);
        }

        [Test] public void LinkedProofRejectsMvidRelabellingAndMissingProtectedAssembly()
        {
            var receipt = LinkedSnapshot();
            receipt.linkedPlayerReceipt.assemblies[0].mvid = Guid.NewGuid().ToString("D");
            receipt.linkedPlayerReceiptHash = ShadowLinkedPlayerEvidence.ComputeHash(receipt.linkedPlayerReceipt);
            string path = Path.Combine(root, "snapshot/LinkedPlayer/" + ShadowLinkedPlayerEvidence.ReceiptName);
            File.WriteAllText(path, JsonUtility.ToJson(receipt.linkedPlayerReceipt));
            Assert.AreEqual("LinkedFileIdentityMismatch", Assert.Throws<ShadowBuildException>(() => ShadowLinkedPlayerEvidence.ReadAndVerify(Path.Combine(root, "snapshot"), receipt)).Code);
            receipt.linkedPlayerReceipt.protectedAssemblies = new[] { "MissingCandidate" };
            receipt.linkedPlayerReceiptHash = ShadowLinkedPlayerEvidence.ComputeHash(receipt.linkedPlayerReceipt);
            Assert.AreEqual("LinkedCandidateMissing", Assert.Throws<ShadowBuildException>(() => ShadowLinkedPlayerEvidence.ValidateReceipt(receipt)).Code);
        }

        [Test] public void SuccessfulPlayerWithoutLinkedEvidenceIsRejected()
        {
            var receipt = Capture(new[] { Module("input", "Input") }, new string[0]);
            receipt.kind = "PlayerBuildInputs"; receipt.playerBuildSucceeded = true; receipt.playerBuildFilterCaptured = true;
            receipt.buildGuid = Guid.NewGuid().ToString("N"); receipt.nativeLibrarySha256 = ShadowHash.Text("native");
            receipt.snapshotHash = AssemblySnapshot.ComputeHash(receipt);
            File.WriteAllText(Path.Combine(root, "snapshot", AssemblySnapshot.ReceiptName), JsonUtility.ToJson(receipt));
            Assert.AreEqual("LinkedEvidenceMissing", Assert.Throws<ShadowBuildException>(() => AssemblySnapshot.ReadAndVerify(Path.Combine(root, "snapshot"), true)).Code);
        }

        [Test] public void CaptureRejectsCandidateActuallyRemovedByLinker()
        {
            var receipt = Capture(new[] { Module("input", "Input"), Module("input", "Other") }, new string[0]);
            string linked = Module("linked", "Other");
            receipt.kind = "PlayerBuildInputs"; receipt.playerBuildSucceeded = true; receipt.playerBuildFilterCaptured = true;
            receipt.buildGuid = Guid.NewGuid().ToString("N"); receipt.nativeLibrarySha256 = ShadowHash.Text("native");
            Assert.AreEqual("LinkedCandidateMissing", Assert.Throws<ShadowBuildException>(() => ShadowLinkedPlayerEvidence.Capture(
                Path.Combine(root, "snapshot"), Path.GetDirectoryName(linked), receipt, new[] { "Input" },
                new[] { new AssemblyCapability { name = "Input", isShadowCapable = true }, new AssemblyCapability { name = "Other" } })).Code);
        }

        private AssemblySnapshotReceipt LinkedSnapshot()
        {
            var receipt = Capture(new[] { Module("input", "Input"), Module("input", "Unused") }, new string[0]);
            string linked = Module("linked", "Input");
            File.WriteAllBytes(Path.ChangeExtension(linked, ".pdb"), new byte[] { 1, 2, 3 });
            receipt.kind = "PlayerBuildInputs"; receipt.playerBuildSucceeded = true; receipt.playerBuildFilterCaptured = true;
            receipt.buildGuid = Guid.NewGuid().ToString("N"); receipt.nativeLibrarySha256 = ShadowHash.Text("native");
            ShadowLinkedPlayerEvidence.Capture(Path.Combine(root, "snapshot"), Path.GetDirectoryName(linked), receipt, new[] { "Input" }, new[]
            { new AssemblyCapability { name = "Input", isShadowCapable = true }, new AssemblyCapability { name = "Unused" } });
            receipt.snapshotHash = AssemblySnapshot.ComputeHash(receipt);
            File.WriteAllText(Path.Combine(root, "snapshot", AssemblySnapshot.ReceiptName), JsonUtility.ToJson(receipt));
            return receipt;
        }

        private AssemblySnapshotReceipt Capture(string[] inputs, string[] references)
        {
            var pin = new ShadowRepositoryPin { revision = new string('1', 40), url = "fixture" };
            var pins = new ShadowSourcePins { unityVersion = Application.unityVersion, target = "StandaloneOSX", architecture = "arm64", hybridclr = pin, il2cppPlus = pin, hybridclrUnity = pin, demo = pin };
            return AssemblySnapshot.Capture(Path.Combine(root, "snapshot"), inputs, references, "CompilePlayerScripts", BuildTarget.StandaloneOSX, "arm64", pins, new string[0]);
        }

        private string Module(string directory, string name)
        {
            string path = Path.Combine(root, directory, name + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var module = new ModuleDefUser(name + ".dll") { Kind = ModuleKind.Dll, Mvid = Guid.NewGuid() })
            {
                var assembly = new AssemblyDefUser(name, new Version(1, 0));
                assembly.Modules.Add(module);
                module.Write(path);
            }
            return path;
        }
    }
}
