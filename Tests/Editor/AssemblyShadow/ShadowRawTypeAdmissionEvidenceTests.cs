using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HybridCLR.AssemblyShadow.CodeGen;
using NUnit.Framework;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class ShadowRawTypeAdmissionEvidenceTests
    {
        [Test] public void RawProofSchemaIsSeparateAndExact()
        {
            CollectionAssert.AreEquivalent(new[] { "schemaVersion", "policy", "phase", "configurationSha256", "configurationHash", "unityVersion", "target", "architecture", "buildGuid", "linkedPlayerReceiptHash", "profileHash", "sites" },
                typeof(RawTypeAdmissionProofReceipt).GetFields().Select(field => field.Name));
            CollectionAssert.AreEquivalent(new[] { "id", "consumerAssemblyIdentity", "consumerPath", "consumerSha256", "providerAssemblyIdentity", "providerPath", "providerSha256", "providerInventoryHash", "declaringType", "methodSignature", "operationIndex", "operationSignature", "kind", "typeName", "throwOnError", "ignoreCase", "compiledMethodHash", "linkedMethodHash", "compiledConsumerSha256" },
                typeof(RawTypeAdmissionProofSite).GetFields().Select(field => field.Name));
            Assert.IsFalse(typeof(AssemblySnapshotReceipt).GetFields().Any(field => field.Name.StartsWith("rawType", StringComparison.Ordinal)));
        }

        [Test] public void ReservedRawControlsCannotBeSuppliedAndAreVerifiedBeforeRemoval()
        {
            string control = RawTypeAdmissionDefines.Prefix + new string('a', 64);
            Assert.Throws<ShadowBuildException>(() => ShadowRawTypeAdmissionEvidence.CompilationDefines(new[] { control }));
            CollectionAssert.AreEqual(new[] { "UNITY_EDITOR", "USER_FEATURE" },
                ShadowRawTypeAdmissionEvidence.UserDefines(new[] { "UNITY_EDITOR", control, "USER_FEATURE" }));
            Assert.Throws<ReflectionBindingException>(() => ShadowRawTypeAdmissionEvidence.UserDefines(new[] { "UNITY_EDITOR", RawTypeAdmissionDefines.Prefix + "bad" }));
        }

        [Test] public void ControlledCompiledProofIsDerivedFromActualBytesAndCopiedExactly()
        {
            using (var fixture = new Fixture())
            {
                Assert.AreEqual(fixture.Configuration.ComputeHash(), ShadowRawTypeAdmissionEvidence.ReadAndVerify(fixture.Root, fixture.Receipt, false).ComputeHash());
                string destination = Path.Combine(fixture.Root, "Copy"); Directory.CreateDirectory(destination);
                foreach (var file in AssemblySnapshot.AllFiles(fixture.Receipt))
                {
                    string path = Path.Combine(destination, file.path); Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.Copy(Path.Combine(fixture.Root, file.path), path);
                }
                ShadowRawTypeAdmissionEvidence.Copy(fixture.Root, destination, fixture.Receipt);
                foreach (string relative in new[] { ShadowRawTypeAdmissionEvidence.ConfigurationPath, ShadowRawTypeAdmissionEvidence.CompiledProofPath })
                    CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(fixture.Root, relative)), File.ReadAllBytes(Path.Combine(destination, relative)));
                Assert.AreEqual(fixture.Configuration.ComputeHash(), ShadowRawTypeAdmissionEvidence.ReadAndVerify(destination, fixture.Receipt, false).ComputeHash());
            }
        }

        [Test] public void MissingUncontrolledAndExtraEvidenceFailClosed()
        {
            using (var fixture = new Fixture())
            {
                string control = fixture.Receipt.extraScriptingDefines[0];
                fixture.Receipt.extraScriptingDefines = new string[0];
                Assert.Throws<ShadowBuildException>(() => ShadowRawTypeAdmissionEvidence.ReadAndVerify(fixture.Root, fixture.Receipt, false));
                fixture.Receipt.extraScriptingDefines = new[] { control };
                string extra = Path.Combine(fixture.Root, ShadowRawTypeAdmissionEvidence.LinkedProofPath);
                File.WriteAllText(extra, "{}");
                Assert.Throws<ShadowBuildException>(() => ShadowRawTypeAdmissionEvidence.ReadAndVerify(fixture.Root, fixture.Receipt, false));
                File.Delete(extra);
                File.Delete(Path.Combine(fixture.Root, ShadowRawTypeAdmissionEvidence.CompiledProofPath));
                Assert.Throws<ShadowBuildException>(() => ShadowRawTypeAdmissionEvidence.ReadAndVerify(fixture.Root, fixture.Receipt, false));
            }
        }

        [Test] public void ChangedConfigInputOrDerivedProofCannotBeAcceptedAsAReceiptClaim()
        {
            foreach (string mutation in new[] { "configuration", "input", "field", "unknown", "missing", "whitespace", "linked-claim" })
            using (var fixture = new Fixture())
            {
                string path = Path.Combine(fixture.Root, ShadowRawTypeAdmissionEvidence.CompiledProofPath);
                string proof = File.ReadAllText(path);
                if (mutation == "configuration") File.AppendAllText(Path.Combine(fixture.Root, ShadowRawTypeAdmissionEvidence.ConfigurationPath), " ");
                else if (mutation == "input") File.AppendAllText(Path.Combine(fixture.Root, fixture.Receipt.assemblies[0].path), "x");
                else if (mutation == "field") File.WriteAllText(path, proof.Replace("\"Compiled\"", "\"Claimed\""));
                else if (mutation == "unknown") File.WriteAllText(path, proof.Insert(proof.LastIndexOf('}'), ",\"unexpected\":true"));
                else if (mutation == "missing") File.WriteAllText(path, proof.Replace("\"ignoreCase\"", "\"missingIgnoreCase\""));
                else if (mutation == "whitespace") File.WriteAllText(path, proof + "\n");
                else File.WriteAllText(path, proof.Replace("\"buildGuid\": \"\"", "\"buildGuid\": \"forged-player\""));
                Assert.Throws<ShadowBuildException>(() => ShadowRawTypeAdmissionEvidence.ReadAndVerify(fixture.Root, fixture.Receipt, false), mutation);
            }
        }

        [Test] public void HistoricalSnapshotWithoutControlRequiresNoRawEvidence()
        {
            using (var fixture = new Fixture())
            {
                string empty = Path.Combine(fixture.Root, "Historical"); Directory.CreateDirectory(empty);
                Assert.IsNull(ShadowRawTypeAdmissionEvidence.ReadAndVerify(empty, new AssemblySnapshotReceipt(), false));
                Assert.Throws<ShadowBuildException>(() => ShadowRawTypeAdmissionEvidence.ReadAndVerify(empty, fixture.Receipt, false));
            }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Root = Path.Combine(Path.GetTempPath(), "ShadowRawEvidence-" + Guid.NewGuid().ToString("N"));
            internal readonly AssemblySnapshotReceipt Receipt;
            internal readonly RawTypeAdmissionConfiguration Configuration;
            internal Fixture()
            {
                Directory.CreateDirectory(Path.Combine(Root, "Assemblies")); Directory.CreateDirectory(Path.Combine(Root, "References"));
                using (var provider = Module("Candidate"))
                {
                    provider.Types.Add(new TypeDefUser("Fixture", "Payload", provider.CorLibTypes.Object.TypeDefOrRef));
                    provider.Write(Path.Combine(Root, "Assemblies/Candidate.dll"));
                }
                using (var consumer = Module("Consumer"))
                {
                    var importer = new Importer(consumer);
                    var host = new TypeDefUser("Fixture", "Host", consumer.CorLibTypes.Object.TypeDefOrRef); consumer.Types.Add(host);
                    var method = new MethodDefUser("Run", MethodSig.CreateStatic(importer.Import(typeof(Type[])).ToTypeSig()),
                        dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() };
                    host.Methods.Add(method);
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Candidate"));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, importer.Import(typeof(Assembly).GetMethod("Load", new[] { typeof(string) }))));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(typeof(Assembly).GetMethod("GetTypes", Type.EmptyTypes))));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    consumer.Write(Path.Combine(Root, "Assemblies/Consumer.dll"));
                }
                File.Copy(typeof(object).Assembly.Location, Path.Combine(Root, "References/mscorlib.dll"));
                using (var consumer = ModuleDefMD.Load(Path.Combine(Root, "Assemblies/Consumer.dll")))
                using (var provider = ModuleDefMD.Load(Path.Combine(Root, "Assemblies/Candidate.dll")))
                {
                    var method = consumer.GetTypes().SelectMany(type => type.Methods).Single();
                    Configuration = new RawTypeAdmissionConfiguration { sites = new[] { new RawTypeAdmissionSite
                    {
                        id = "raw", consumerAssembly = "Consumer", declaringType = "Fixture.Host", methodSignature = method.FullName,
                        methodHash = ReflectionBindingFingerprint.Compute(method), operationIndex = 2,
                        operationSignature = ((IMethod)method.Body.Instructions[2].Operand).FullName,
                        providerAssemblyIdentity = provider.Assembly.FullName, typeName = "", reason = "Exact raw metadata fixture", throwOnError = false, ignoreCase = false,
                    } } };
                }
                byte[] config;
                using (var stream = new MemoryStream())
                { new DataContractJsonSerializer(typeof(RawTypeAdmissionConfiguration)).WriteObject(stream, Configuration); config = stream.ToArray(); }
                Receipt = new AssemblySnapshotReceipt
                {
                    kind = "CompilePlayerScripts", unityVersion = "fixture-unity", target = "StandaloneOSX", architecture = "arm64",
                    assemblies = new[] { FileEntry("Candidate", "Assemblies"), FileEntry("Consumer", "Assemblies") },
                    references = new[] { FileEntry("mscorlib", "References") }, extraScriptingDefines = new[] { RawTypeAdmissionDefines.Create(config) },
                };
                Directory.CreateDirectory(Path.Combine(Root, ShadowRawTypeAdmissionEvidence.DirectoryName));
                File.WriteAllBytes(Path.Combine(Root, ShadowRawTypeAdmissionEvidence.ConfigurationPath), config);
                var derive = typeof(ShadowRawTypeAdmissionEvidence).GetMethod("Derive", BindingFlags.Static | BindingFlags.NonPublic);
                var proof = (RawTypeAdmissionProofReceipt)derive.Invoke(null, new object[] { Root, Receipt, Configuration, ShadowHash.Bytes(config), false, null });
                File.WriteAllText(Path.Combine(Root, ShadowRawTypeAdmissionEvidence.CompiledProofPath), JsonUtility.ToJson(proof, true), new UTF8Encoding(false));
            }
            private SnapshotFile FileEntry(string name, string directory)
            { string relative = directory + "/" + name + ".dll"; return new SnapshotFile { name = name, path = relative, sha256 = ShadowHash.File(Path.Combine(Root, relative)) }; }
            private static ModuleDefUser Module(string name)
            {
                var module = new ModuleDefUser(name + ".dll", Guid.NewGuid(), new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.FullName))) { Kind = ModuleKind.Dll };
                new AssemblyDefUser(name, new Version(1, 0, 0, 0)).Modules.Add(module); return module;
            }
            public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        }
    }
}
