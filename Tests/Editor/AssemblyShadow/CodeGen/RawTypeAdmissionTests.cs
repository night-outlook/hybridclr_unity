using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HybridCLR.AssemblyShadow.CodeGen;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class RawTypeAdmissionTests
    {
        [Test] public void FiveRawApisRemainPresentAndProduceByteBoundImmutableProofs()
        {
            using (var fixture = new Fixture())
            {
                var proofs = fixture.Verify(); Assert.AreEqual(5, proofs.Length);
                foreach (var proof in proofs)
                {
                    Assert.AreSame(fixture.Method(proof.Method.Name), proof.Method);
                    Assert.AreEqual(fixture.Config.ComputeHash(), proof.ConfigurationHash);
                    Assert.AreEqual(Hash(fixture.ConsumerBytes), proof.ConsumerSha256);
                    Assert.AreEqual(Hash(fixture.ProviderBytes), proof.ProviderSha256);
                    Assert.AreEqual(64, proof.ProviderInventoryHash.Length);
                    Assert.AreEqual(Code.Callvirt, proof.Method.Body.Instructions[proof.OperationIndex].OpCode.Code);
                    Assert.AreEqual(proof.OperationSignature, ((IMethod)proof.Method.Body.Instructions[proof.OperationIndex].Operand).FullName);
                    Assert.IsTrue(proof.Matches(proof.Method, proof.OperationIndex)); Assert.IsTrue(proof.Matches(proof.Method, proof.ReceiverLoadIndex));
                    Assert.IsFalse(proof.Matches(proof.Method, 0));
                }
                Assert.IsEmpty(typeof(VerifiedRawTypeAdmission).GetConstructors());
                Assert.IsTrue(typeof(VerifiedRawTypeAdmission).GetProperties().All(value => value.GetSetMethod() == null));
                CollectionAssert.AreEqual(proofs.Select(value => value.ProviderInventoryHash), fixture.Verify().Select(value => value.ProviderInventoryHash));
            }
        }

        [Test] public void StrictJsonRequiresEveryFieldAndRejectsUnknownDuplicatesAndWrongTypes()
        {
            using (var fixture = new Fixture())
            {
                fixture.Config.sites = new[] { fixture.Config.sites[0] };
                string json = Json(fixture.Config);
                Assert.AreEqual(fixture.Config.ComputeHash(), RawTypeAdmissionConfiguration.Parse(Encoding.UTF8.GetBytes(json)).ComputeHash());
                foreach (string key in new[] { "schemaVersion", "policy", "sites", "id", "consumerAssembly", "declaringType", "methodSignature", "methodHash", "operationIndex", "operationSignature", "providerAssemblyIdentity", "typeName", "throwOnError", "ignoreCase", "reason" })
                {
                    string renamed = json.Replace("\"" + key + "\":", "\"unknown_" + key + "\":");
                    Assert.Throws<ReflectionBindingException>(() => RawTypeAdmissionConfiguration.Parse(Encoding.UTF8.GetBytes(renamed)), key);
                }
                foreach (string bad in new[] { json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
                    json.Replace("\"throwOnError\":false", "\"throwOnError\":0"), json.Replace("\"ignoreCase\":false", "\"ignoreCase\":null"),
                    json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1.0"), json.Replace("\"schemaVersion\":1", "\"schemaVersion\":01"),
                    json.Replace("\"operationIndex\":2", "\"operationIndex\":2147483648"), json + "{}" })
                    Assert.Throws<ReflectionBindingException>(() => RawTypeAdmissionConfiguration.Parse(Encoding.UTF8.GetBytes(bad)), bad);
            }
        }

        [Test] public void CanonicalHashIsIndependentOfJsonFormattingAndSiteOrderButBindsAllFields()
        {
            using (var fixture = new Fixture())
            {
                string expected = fixture.Config.ComputeHash(); var reordered = RawTypeAdmissionConfiguration.Parse(Encoding.UTF8.GetBytes(Json(fixture.Config)));
                Array.Reverse(reordered.sites); Assert.AreEqual(expected, reordered.ComputeHash());
                reordered.sites[0].reason += " changed"; Assert.AreNotEqual(expected, reordered.ComputeHash());
                byte[] raw = Encoding.UTF8.GetBytes(Json(fixture.Config)); byte[] pretty = Encoding.UTF8.GetBytes("\n" + Json(fixture.Config));
                Assert.AreEqual(expected, RawTypeAdmissionConfiguration.Parse(pretty).ComputeHash());
                Assert.AreNotEqual(RawTypeAdmissionDefines.Create(raw), RawTypeAdmissionDefines.Create(pretty));
            }
        }

        [Test] public void RawControlNeverDisappearsInEditorAndConflictsFailClosed()
        {
            string control = RawTypeAdmissionDefines.Create(new byte[] { 1, 2, 3 }), hash;
            Assert.IsTrue(RawTypeAdmissionDefines.TryGetEnabledHash(new[] { control, control, "UNITY_EDITOR" }, out hash));
            Assert.AreEqual(control.Substring(RawTypeAdmissionDefines.Prefix.Length), hash);
            Assert.IsFalse(RawTypeAdmissionDefines.TryGetEnabledHash(new[] { "UNITY_EDITOR" }, out hash));
            Assert.Throws<ReflectionBindingException>(() => RawTypeAdmissionDefines.TryGetEnabledHash(new[] { control, RawTypeAdmissionDefines.Prefix + new string('0', 64) }, out hash));
            Assert.Throws<ReflectionBindingException>(() => RawTypeAdmissionDefines.TryGetEnabledHash(new[] { "UNITY_EDITOR", RawTypeAdmissionDefines.Prefix + "bad" }, out hash));
        }

        [Test] public void MissingStaleDuplicateOrAdditionalOperationsAreRejected()
        {
            foreach (string change in new[] { "hash", "index", "operation", "duplicate", "missing", "reason" })
            using (var fixture = new Fixture())
            {
                if (change == "hash") fixture.Config.sites[0].methodHash = new string('0', 64);
                if (change == "index") fixture.Config.sites[0].operationIndex++;
                if (change == "operation") fixture.Config.sites[0].operationSignature = "System.Type[] System.Reflection.Assembly::GetExportedTypes()";
                if (change == "duplicate") fixture.Config.sites = fixture.Config.sites.Concat(new[] { fixture.Config.sites[0] }).ToArray();
                if (change == "missing") fixture.Config.sites[0].methodSignature += "Missing";
                if (change == "reason") fixture.Config.sites[0].reason = "";
                Assert.Throws<ReflectionBindingException>(() => fixture.Verify(), change);
            }
            using (var fixture = new Fixture(module =>
            {
                var method = module.GetTypes().SelectMany(type => type.Methods).First(); var il = method.Body.Instructions;
                il.Insert(3, Instruction.Create(OpCodes.Pop)); il.Insert(4, Instruction.Create(OpCodes.Ldstr, "Candidate"));
                il.Insert(5, Instruction.Create(OpCodes.Call, (IMethod)il[1].Operand)); il.Insert(6, Instruction.Create(OpCodes.Callvirt, (IMethod)il[2].Operand));
            })) Assert.Throws<ReflectionBindingException>(() => fixture.Verify());
        }

        [Test] public void ReceiverAliasesMutationsAndAlternateEntriesCannotBeApprovedByARehashedConfiguration()
        {
            foreach (string change in new[] { "alias", "dup", "unknown", "branch", "switch", "eh", "indirect", "callback", "extra-call" })
            using (var fixture = new Fixture(module =>
            {
                var method = module.GetTypes().SelectMany(type => type.Methods).First(); var il = method.Body.Instructions;
                if (change == "alias") { var local = new Local(new Importer(module).Import(typeof(Assembly)).ToTypeSig()); method.Body.Variables.Add(local); il.Insert(2, Instruction.Create(OpCodes.Stloc, local)); il.Insert(3, Instruction.Create(OpCodes.Ldloc, local)); }
                if (change == "dup") { il.Insert(2, Instruction.Create(OpCodes.Dup)); il.Insert(3, Instruction.Create(OpCodes.Pop)); }
                if (change == "unknown") il[0] = Instruction.Create(OpCodes.Ldarg_0);
                if (change == "branch") il.Insert(0, Instruction.Create(OpCodes.Br, il[1]));
                if (change == "switch") { var target = il[2]; il.Insert(0, Instruction.Create(OpCodes.Ldc_I4_0)); il.Insert(1, Instruction.Create(OpCodes.Switch, new[] { target })); }
                if (change == "eh") method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = il[0], TryEnd = il[2], HandlerStart = il[2], HandlerEnd = il[3] });
                if (change == "indirect") il[2].OpCode = OpCodes.Ldftn;
                if (change == "callback" || change == "extra-call")
                { il.Insert(0, Instruction.Create(OpCodes.Ldc_I4_M1)); il.Insert(1, Instruction.Create(OpCodes.Call, new Importer(module).Import(typeof(Math).GetMethod("Abs", new[] { typeof(int) })))); il.Insert(2, Instruction.Create(OpCodes.Pop)); }
                method.Body.KeepOldMaxStack = true; method.Body.MaxStack = 16;
            })) Assert.Throws<ReflectionBindingException>(() => fixture.Verify(), change);
        }

        [Test] public void OptionalNopsAndBranchToLiteralStartAreSafe()
        {
            using (var fixture = new Fixture(module =>
            {
                foreach (var method in module.GetTypes().SelectMany(type => type.Methods))
                { var il = method.Body.Instructions; il.Insert(1, Instruction.Create(OpCodes.Nop)); il.Insert(0, Instruction.Create(OpCodes.Br, il[0])); }
            })) Assert.AreEqual(5, fixture.Verify().Length);
        }

        [Test] public void FiveFiniteSwitchArmsRequireAllTwentyFiveOperationIndices()
        {
            using (var fixture = new Fixture(module =>
            {
                foreach (var method in module.GetTypes().SelectMany(type => type.Methods))
                {
                    var original = method.Body.Instructions.ToArray(); method.Body.Instructions.Clear();
                    var il = method.Body.Instructions; var equal = new Importer(module).Import(typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) }));
                    for (int arm = 0; arm < 5; arm++)
                    {
                        var next = Instruction.Create(OpCodes.Nop);
                        il.Add(Instruction.Create(OpCodes.Ldarg_0)); il.Add(Instruction.Create(OpCodes.Ldstr, "arm" + arm));
                        il.Add(Instruction.Create(OpCodes.Call, equal)); il.Add(Instruction.Create(OpCodes.Brfalse, next));
                        foreach (var source in original) il.Add(new Instruction(source.OpCode, source.Operand));
                        il.Add(next);
                    }
                    il.Add(Instruction.Create(OpCodes.Ldstr, "assemblyName"));
                    il.Add(Instruction.Create(OpCodes.Newobj, new Importer(module).Import(typeof(ArgumentOutOfRangeException).GetConstructor(new[] { typeof(string) }))));
                    il.Add(Instruction.Create(OpCodes.Throw));
                }
            }))
            {
                fixture.Config = Fixture.Configuration(fixture.Modules["consumer"], fixture.Modules["candidate"], true);
                Assert.AreEqual(25, fixture.Verify().Length);
                fixture.Config.sites = fixture.Config.sites.Skip(1).ToArray();
                Assert.AreEqual("UnsupportedRawAdmissionHelper", Assert.Throws<ReflectionBindingException>(() => fixture.Verify()).Code);
            }
        }

        [Test] public void ProviderIdentityTypeAndFrameworkScopesAreExact()
        {
            foreach (string change in new[] { "provider-version", "provider-missing", "type-missing", "type-case", "ignore-case", "throw-false" })
            using (var fixture = new Fixture())
            {
                var site = fixture.Config.sites.Last();
                if (change == "provider-version") site.providerAssemblyIdentity = site.providerAssemblyIdentity.Replace("1.0.0.0", "2.0.0.0");
                if (change == "provider-missing") site.providerAssemblyIdentity = site.providerAssemblyIdentity.Replace("Candidate", "Missing");
                if (change == "type-missing") site.typeName = "Fixture.Missing";
                if (change == "type-case") site.typeName = "fixture.Payload";
                if (change == "ignore-case") site.ignoreCase = true;
                if (change == "throw-false") site.throwOnError = false;
                Assert.Throws<ReflectionBindingException>(() => fixture.Verify(), change);
            }
            foreach (string change in new[] { "owner", "return", "token", "version", "overload" })
            using (var fixture = new Fixture(module =>
            {
                var method = module.GetTypes().SelectMany(type => type.Methods).First(); var operation = (MemberRef)method.Body.Instructions[2].Operand;
                var scope = new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.FullName));
                if (change == "token") scope.PublicKeyOrToken = new PublicKeyToken("0011223344556677");
                else if (change == "version") scope.Version = new Version(9, 0, 0, 0); else scope.Name = "Business";
                if (change == "return") operation.MethodSig.RetType = new SZArraySig(new ClassSig(new TypeRefUser(module, "System", "Type", scope)));
                else if (change == "overload") operation.MethodSig.CallingConvention = CallingConvention.VarArg | CallingConvention.HasThis;
                else operation.Class = new TypeRefUser(module, "System.Reflection", "Assembly", scope);
            })) Assert.Throws<ReflectionBindingException>(() => fixture.Verify(), change);
        }

        [Test] public void ProviderForwardersAndMissingPhysicalImagesAreRejected()
        {
            using (var fixture = new Fixture())
            {
                var provider = fixture.Modules["candidate"];
                provider.ExportedTypes.Add(new ExportedTypeUser(provider, 0, "Fixture", "Forwarded", dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.Forwarder,
                    new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.FullName))));
                Assert.Throws<ReflectionBindingException>(() => fixture.Verify());
            }
            using (var fixture = new Fixture())
            { fixture.Modules.Remove("candidate"); Assert.Throws<ReflectionBindingException>(() => fixture.Verify()); }
        }

        [Test] public void RehashedInMemoryMetadataCannotPretendToBeCapturedBytes()
        {
            using (var fixture = new Fixture())
            {
                var method = fixture.Method("Run0"); var proof = fixture.Verify().First();
                method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
                fixture.Config.sites[0].methodHash = ReflectionBindingFingerprint.Compute(method); fixture.Config.sites[0].operationIndex++;
                Assert.IsFalse(proof.Matches(method, proof.OperationIndex));
                Assert.AreEqual("RawAdmissionBytesMismatch", Assert.Throws<ReflectionBindingException>(() => fixture.Verify()).Code);
            }
        }

        [Test] public void LinkedProofUsesCapturedPerTypeRetargetingAndRejectsBodyOrProfileTamper()
        {
            using (var fixture = new Fixture())
            using (var sourceCore = ModuleDefMD.Load(File.ReadAllBytes(typeof(object).Assembly.Location)))
            using (var sourceConsumer = ModuleDefMD.Load(fixture.ConsumerBytes))
            {
                var facadeIdentity = new AssemblyNameInfo(CapturedReflectionRetargetingProfile.RequiredFacadeIdentity);
                sourceCore.Assembly.Name = "netstandard"; sourceCore.Assembly.Version = new Version(2, 1, 0, 0); sourceCore.Assembly.PublicKey = new PublicKey(Convert.FromBase64String(FacadeKey));
                var scope = sourceConsumer.CorLibTypes.AssemblyRef; scope.Name = facadeIdentity.Name; scope.Version = facadeIdentity.Version; scope.PublicKeyOrToken = facadeIdentity.PublicKeyOrToken;
                using (var core = ModuleDefMD.Load(Bytes(sourceCore))) using (var consumer = ModuleDefMD.Load(Bytes(sourceConsumer)))
                {
                    var source = new Dictionary<string, ModuleDefMD>(StringComparer.OrdinalIgnoreCase) { { "netstandard", core }, { "consumer", consumer }, { "candidate", fixture.Modules["candidate"] } };
                    var configuration = Fixture.Configuration(consumer, fixture.Modules["candidate"]);
                    var profile = Profile();
                    var proof = RawTypeAdmissionVerifier.VerifyLinked(source, fixture.Modules, configuration, profile);
                    Assert.AreEqual(5, proof.Length); Assert.AreEqual(profile.ComputeHash(), proof[0].LinkedProfileHash);
                    Assert.AreNotEqual(proof[0].CompiledMethodHash, proof[0].LinkedMethodHash);
                    Assert.AreEqual(Hash(fixture.ConsumerBytes), proof[0].LinkedConsumerSha256);
                    configuration.sites[0].methodHash = new string('0', 64);
                    Assert.AreEqual("RawAdmissionMethodHashMismatch", Assert.Throws<ReflectionBindingException>(() => RawTypeAdmissionVerifier.VerifyLinked(source, fixture.Modules, configuration, profile)).Code);
                    configuration = Fixture.Configuration(consumer, fixture.Modules["candidate"]);
                    fixture.Method("Run0").Body.Instructions[0].Operand = "Other";
                    Assert.AreEqual("RawAdmissionLinkedMethodChanged", Assert.Throws<ReflectionBindingException>(() => RawTypeAdmissionVerifier.VerifyLinked(source, fixture.Modules, configuration, profile)).Code);
                }
            }
        }

        private const string FacadeKey = "ACQAAASAAACUAAAABgIAAAAkAABSU0ExAAQAAAEAAQBLhsTLeFSbNLq2GjsYAOI7/rWz7DkAdAQVNqfjy9l/XwTPD4VxVaiSjqop6/0Rz7utO6cO/qe9oyJsao03CkzTA/cUSGtuvCJZhaY4Rx5u9XHMkqRhPAC4+mXWHM7gy+XzYzDJoB9Bg1WfG+8kzCkXxtkT46VBMzodBdm+0is4yw==";
        private static CapturedReflectionRetargetingProfile Profile()
        {
            using (var facade = new ModuleDefUser("netstandard.dll"))
            {
                new AssemblyDefUser("netstandard", new Version(2, 1, 0, 0), new PublicKey(Convert.FromBase64String(FacadeKey))).Modules.Add(facade);
                foreach (string name in new[] { "System.Object", "System.String", "System.Type", "System.Boolean", "System.Void", "System.Reflection.Assembly", "System.Reflection.Module", "System.Reflection.TypeInfo", "System.Collections.Generic.IEnumerable`1" })
                {
                    int split = name.LastIndexOf('.'); facade.ExportedTypes.Add(new ExportedTypeUser(facade, 0, name.Substring(0, split), name.Substring(split + 1),
                        dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.Forwarder, new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.FullName))));
                }
                byte[] bytes = Bytes(facade); return CapturedReflectionRetargetingProfile.Load(bytes, Hash(bytes), new[] { File.ReadAllBytes(typeof(object).Assembly.Location) });
            }
        }
        private static byte[] Bytes(ModuleDef module) { using (var stream = new MemoryStream()) { module.Write(stream); return stream.ToArray(); } }
        private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2"))); }
        private static string Json(RawTypeAdmissionConfiguration configuration)
        { using (var stream = new MemoryStream()) { new DataContractJsonSerializer(typeof(RawTypeAdmissionConfiguration)).WriteObject(stream, configuration); return Encoding.UTF8.GetString(stream.ToArray()); } }

        private sealed class Fixture : IDisposable
        {
            internal readonly Dictionary<string, ModuleDefMD> Modules = new Dictionary<string, ModuleDefMD>(StringComparer.OrdinalIgnoreCase);
            internal byte[] ConsumerBytes, ProviderBytes;
            internal RawTypeAdmissionConfiguration Config;
            internal Fixture(Action<ModuleDefUser> mutate = null)
            {
                Modules.Add("mscorlib", ModuleDefMD.Load(File.ReadAllBytes(typeof(object).Assembly.Location)));
                using (var provider = Module("Candidate"))
                { provider.Types.Add(new TypeDefUser("Fixture", "Payload", provider.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public }); ProviderBytes = Bytes(provider); }
                Modules.Add("candidate", ModuleDefMD.Load(ProviderBytes));
                using (var consumer = Module("Consumer"))
                {
                    var host = new TypeDefUser("Fixture", "Host", consumer.CorLibTypes.Object.TypeDefOrRef); consumer.Types.Add(host);
                    var importer = new Importer(consumer);
                    MethodInfo[] operations = { typeof(Assembly).GetMethod("GetTypes", Type.EmptyTypes), typeof(Assembly).GetProperty("DefinedTypes").GetGetMethod(),
                        typeof(Assembly).GetProperty("ExportedTypes").GetGetMethod(), typeof(System.Reflection.Module).GetMethod("GetTypes", Type.EmptyTypes),
                        typeof(System.Reflection.Module).GetMethod("GetType", new[] { typeof(string), typeof(bool), typeof(bool) }) };
                    for (int index = 0; index < operations.Length; index++)
                    {
                        var method = new MethodDefUser("Run" + index, MethodSig.CreateStatic(importer.Import(operations[index].ReturnType).ToTypeSig(), consumer.CorLibTypes.String),
                            dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() }; host.Methods.Add(method);
                        var il = method.Body.Instructions; il.Add(Instruction.Create(OpCodes.Ldstr, "Candidate"));
                        il.Add(Instruction.Create(OpCodes.Call, importer.Import(typeof(Assembly).GetMethod("Load", new[] { typeof(string) }))));
                        if (index >= 3) il.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(typeof(Assembly).GetProperty("ManifestModule").GetGetMethod())));
                        if (index == 4) { il.Add(Instruction.Create(OpCodes.Ldstr, "Fixture.Payload")); il.Add(Instruction.Create(OpCodes.Ldc_I4_1)); il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); }
                        il.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(operations[index]))); il.Add(Instruction.Create(OpCodes.Ret));
                    }
                    if (mutate != null) mutate(consumer); ConsumerBytes = Bytes(consumer);
                }
                Modules.Add("consumer", ModuleDefMD.Load(ConsumerBytes)); Config = Configuration(Modules["consumer"], Modules["candidate"]);
            }
            internal MethodDef Method(string name) { return Modules["consumer"].GetTypes().SelectMany(type => type.Methods).Single(value => value.Name == name); }
            internal VerifiedRawTypeAdmission[] Verify() { return RawTypeAdmissionVerifier.Verify(Modules, Config); }
            internal static RawTypeAdmissionConfiguration Configuration(ModuleDefMD consumer, ModuleDefMD provider, bool allOperations = false)
            {
                return new RawTypeAdmissionConfiguration { sites = consumer.GetTypes().SelectMany(type => type.Methods).SelectMany(method =>
                {
                    var operations = method.Body.Instructions.Where(instruction => RawTypeAdmissionConfiguration.KindOf((instruction.Operand as IMethod)?.FullName) != null);
                    if (!allOperations) operations = operations.Take(1);
                    return operations.Select(operation => {
                    string signature = ((IMethod)operation.Operand).FullName; bool lookup = RawTypeAdmissionConfiguration.KindOf(signature) == "Module.GetType";
                    return new RawTypeAdmissionSite { id = method.Name.String + (allOperations ? "." + method.Body.Instructions.IndexOf(operation) : ""), consumerAssembly = consumer.Assembly.Name.String, declaringType = method.DeclaringType.FullName,
                        methodSignature = method.FullName, methodHash = ReflectionBindingFingerprint.Compute(method), operationIndex = method.Body.Instructions.IndexOf(operation),
                        operationSignature = signature, providerAssemblyIdentity = provider.Assembly.FullName, typeName = lookup ? "Fixture.Payload" : "",
                        throwOnError = lookup, ignoreCase = false, reason = "Observe the actual raw API; native lifecycle guards remain authoritative." }; });
                }).ToArray() };
            }
            private static ModuleDefUser Module(string name)
            {
                var module = new ModuleDefUser(name + ".dll", Guid.NewGuid(), new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.FullName))) { Kind = ModuleKind.Dll };
                new AssemblyDefUser(name, new Version(1, 0, 0, 0)).Modules.Add(module); return module;
            }
            public void Dispose() { foreach (var module in Modules.Values.Distinct()) module.Dispose(); }
        }
    }
}
