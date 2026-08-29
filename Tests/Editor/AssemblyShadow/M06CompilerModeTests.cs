using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using NUnit.Framework;
using UnityEditor.Build.Player;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class M06CompilerModeTests
    {
        [Test] public void BothExplicitModesBindExactSnapshotAndActualOption()
        {
            foreach (bool development in new[] { true, false })
            using (var fixture = new Fixture())
            {
                fixture.Capture(development); var read = fixture.Verify(development);
                Assert.AreEqual(development, read.developmentBuild);
                Assert.AreEqual((int)(development ? ScriptCompilationOptions.DevelopmentBuild : ScriptCompilationOptions.None), read.compilerOptions);
                Assert.AreEqual(ShadowHash.File(fixture.SnapshotPath), read.snapshotReceiptSha256);
                Assert.Throws<ShadowBuildException>(() => fixture.Verify(!development));
                Assert.Throws<IOException>(() => fixture.Capture(development));
            }
        }
        [Test] public void MissingModeCannotRelabelLegacyCompileAsRelease()
        {
            using (var fixture = new Fixture())
            {
                Assert.Throws<ShadowBuildException>(() => fixture.Verify(false));
                Assert.Throws<ShadowBuildException>(() => fixture.Verify(true));
                Assert.IsFalse(File.Exists(fixture.ModePath));
            }
        }
        [Test] public void SnapshotReceiptAndDefinesTamperingFails()
        {
            using (var fixture = new Fixture())
            {
                fixture.Capture(false); File.AppendAllText(fixture.SnapshotPath, " ");
                Assert.Throws<ShadowBuildException>(() => fixture.Verify(false));
            }
            using (var fixture = new Fixture())
            {
                fixture.Capture(true); fixture.Snapshot.extraScriptingDefines = new[] { "RELABELED" };
                Assert.Throws<ShadowBuildException>(() => fixture.Verify(true));
            }
        }
        [Test] public void MissingFalseZeroDuplicateUnknownAndWrongModeFieldsFail()
        {
            using (var fixture = new Fixture())
            {
                fixture.Capture(false); string original = File.ReadAllText(fixture.ModePath);
                foreach (Func<string, string> mutate in new Func<string, string>[] {
                    json => json.Replace("\"developmentBuild\":false,", ""),
                    json => json.Replace("\"compilerOptions\":0,", ""),
                    json => json.Replace("\"developmentBuild\":false", "\"developmentBuild\":true"),
                    json => json.Replace("\"compilerOptions\":0", "\"compilerOptions\":2147483648"),
                    json => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
                    json => json.Insert(1, "\"unknown\":0,"),
                    json => json.Replace("\"compilerOptions\":0", "\"compilerOptions\":0,\"compilerOptions\":0") })
                {
                    string changed = mutate(original); Assert.AreNotEqual(original, changed, "Mutation must reach the actual serialized field.");
                    File.WriteAllText(fixture.ModePath, changed, new UTF8Encoding(false)); Assert.Throws<ShadowBuildException>(() => fixture.Verify(false));
                }
            }
        }
        [Test] public void CaptureRejectsOptionBooleanMismatchBeforeWriting()
        {
            using (var fixture = new Fixture())
            {
                Assert.Throws<ShadowBuildException>(() => Invoke("Capture", fixture.Root, fixture.Snapshot, ScriptCompilationOptions.None, true));
                Assert.IsFalse(File.Exists(fixture.ModePath));
            }
        }
        [Test] public void LegacyEntryPointsRetainDevelopmentWithoutModeCompanion()
        {
            using (var module = ModuleDefMD.Load(typeof(AssemblySnapshot).Assembly.Location))
            {
                var compile = module.Find(typeof(AssemblySnapshot).FullName, false).Methods.Single(method => method.Name == "Compile" && method.MethodSig.Params.Count == 6);
                var call = compile.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Call && ((IMethod)instruction.Operand).Name == "CompileCore");
                int index = compile.Body.Instructions.IndexOf(call);
                Assert.AreEqual(OpCodes.Ldc_I4_1, compile.Body.Instructions[index - 2].OpCode);
                Assert.AreEqual(OpCodes.Ldc_I4_0, compile.Body.Instructions[index - 1].OpCode);
                var begin = module.Find(typeof(ShadowPlayerInputCapture).FullName, false).Methods.Single(method => method.Name == "Begin" && method.MethodSig.Params.Count == 7);
                var delegateCall = begin.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Call && ((IMethod)instruction.Operand).Name == "Begin");
                Assert.AreEqual(OpCodes.Ldc_I4_1, begin.Body.Instructions[begin.Body.Instructions.IndexOf(delegateCall) - 1].OpCode);
            }
            Assert.IsFalse(typeof(AssemblySnapshotReceipt).GetFields().Any(field => field.Name == "developmentBuild" || field.Name == "compilerOptions"));
        }
        private static object Invoke(string name, params object[] arguments)
        {
            try { return typeof(ShadowCompilerModeEvidence).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        private sealed class Fixture : IDisposable
        {
            internal readonly string Root = Path.Combine(Path.GetTempPath(), "M06CompilerMode-" + Guid.NewGuid().ToString("N"));
            internal readonly AssemblySnapshotReceipt Snapshot = new AssemblySnapshotReceipt { kind = "CompilePlayerScripts", unityVersion = "2022.3.62f2",
                target = "StandaloneOSX", architecture = "arm64", snapshotHash = "verified-snapshot-hash", extraScriptingDefines = new[] { "ASSEMBLY_SHADOW_M06" } };
            internal string SnapshotPath { get { return Path.Combine(Root, AssemblySnapshot.ReceiptName); } }
            internal string ModePath { get { return Path.Combine(Root, ShadowCompilerModeEvidence.ReceiptName); } }
            internal Fixture() { Directory.CreateDirectory(Root); File.WriteAllText(SnapshotPath, "exact captured receipt bytes", new UTF8Encoding(false)); }
            internal void Capture(bool development) { Invoke("Capture", Root, Snapshot, development ? ScriptCompilationOptions.DevelopmentBuild : ScriptCompilationOptions.None, development); }
            internal ShadowCompilerModeReceipt Verify(bool development) { return (ShadowCompilerModeReceipt)Invoke("Verify", Root, Snapshot, development); }
            public void Dispose() { Directory.Delete(Root, true); }
        }
    }
}
