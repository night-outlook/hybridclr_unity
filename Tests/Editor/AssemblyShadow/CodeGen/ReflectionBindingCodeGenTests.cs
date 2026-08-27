using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using dnlib.DotNet.Writer;
using HybridCLR.AssemblyShadow.CodeGen;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class ReflectionBindingCodeGenTests
    {
        [Test] public void AllowedLookupKeepsOriginalReturnSemanticsAndProviders()
        {
            var fixture = Fixture.Create(typeof(string).AssemblyQualifiedName, "Missing.ConcreteType, mscorlib");
            var transformed = ReflectionBindingTransformer.Transform(fixture.Pe, fixture.Pdb, fixture.Config);
            MethodInfo run = RuntimeMethod(transformed.PeData);
            Assert.AreEqual(typeof(string), run.Invoke(null, new object[] { typeof(string).AssemblyQualifiedName }));
            Assert.IsNull(run.Invoke(null, new object[] { "Missing.ConcreteType, mscorlib" }));
            using (var module = ModuleDefMD.Load(transformed.PeData))
            {
                var binding = ReflectionBindingTransformer.Verify(module, fixture.Config).Single();
                Assert.That(binding.Providers, Is.EquivalentTo(new[] { "mscorlib" }));
                Assert.AreSame(binding.OriginalMethod.DeclaringType, binding.GuardMethod.DeclaringType);
                Assert.IsTrue(binding.GuardMethod.IsPrivate && binding.GuardMethod.IsStatic);
                foreach (var operation in binding.GuardMethod.Body.Instructions.Where(instruction => (instruction.Operand as IMethod)?.Name == "GetType"))
                    Assert.AreEqual(Code.Ldstr, binding.GuardMethod.Body.Instructions[binding.GuardMethod.Body.Instructions.IndexOf(operation) - 1].OpCode.Code);
            }
        }

        [Test] public void DeniedInputsThrowBeforeAnyAssemblyResolution()
        {
            var fixture = Fixture.Create(typeof(string).AssemblyQualifiedName);
            MethodInfo run = RuntimeMethod(ReflectionBindingTransformer.Transform(fixture.Pe, fixture.Pdb, fixture.Config).PeData);
            int attempts = 0;
            ResolveEventHandler resolver = (sender, args) => { attempts++; return null; };
            AppDomain.CurrentDomain.AssemblyResolve += resolver;
            try
            {
                foreach (string value in new[] { null, "System.String", "Candidate.Entry, DefinitelyMissingCandidateAssembly", "System.String[]", "System.String, mscorlib, [malformed]",
                    "System.Collections.Generic.List`1[[System.String, mscorlib]], mscorlib", typeof(string).AssemblyQualifiedName.ToUpperInvariant() })
                {
                    var error = Assert.Throws<TargetInvocationException>(() => run.Invoke(null, new object[] { value }));
                    Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
                }
                Assert.AreEqual(0, attempts, "Denied input must never reach Type/Assembly lookup.");
            }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolver; }
        }

        [Test] public void DenyAllContainsNoLookupAndStillReturnsTypeSignature()
        {
            var fixture = Fixture.Create(); var result = ReflectionBindingTransformer.Transform(fixture.Pe, fixture.Pdb, fixture.Config);
            using (var module = ModuleDefMD.Load(result.PeData))
            {
                var guard = ReflectionBindingTransformer.Verify(module, fixture.Config).Single().GuardMethod;
                Assert.AreEqual("System.Type", guard.MethodSig.RetType.FullName);
                Assert.IsFalse(guard.Body.Instructions.Any(instruction => (instruction.Operand as IMethod)?.Name == "GetType"));
                Assert.AreEqual(3, guard.Body.Instructions.Count);
            }
            Assert.IsInstanceOf<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => RuntimeMethod(result.PeData).Invoke(null, new object[] { typeof(string).AssemblyQualifiedName })).InnerException);
        }

        [Test] public void IdenticalOriginalInputsProduceIdenticalPeAndPortablePdb()
        {
            var fixture = Fixture.Create(typeof(string).AssemblyQualifiedName);
            var first = ReflectionBindingTransformer.Transform(fixture.Pe, fixture.Pdb, fixture.Config);
            var second = ReflectionBindingTransformer.Transform(fixture.Pe, fixture.Pdb, fixture.Config);
            CollectionAssert.AreEqual(first.PeData, second.PeData); CollectionAssert.AreEqual(first.PdbData, second.PdbData);
            using (var original = ModuleDefMD.Load(fixture.Pe, new ModuleCreationOptions { PdbFileOrData = fixture.Pdb }))
            using (var output = ModuleDefMD.Load(first.PeData, new ModuleCreationOptions { PdbFileOrData = first.PdbData }))
            {
                Assert.AreEqual(original.Mvid, output.Mvid); Assert.AreEqual(PdbFileKind.PortablePDB, output.PdbState.PdbFileKind);
                var a = original.GetTypes().SelectMany(type => type.Methods).Single(method => method.Name == "Run");
                var b = output.GetTypes().SelectMany(type => type.Methods).Single(method => method.Name == "Run");
                Assert.AreEqual(a.Body.Instructions[1].SequencePoint.StartLine, b.Body.Instructions[1].SequencePoint.StartLine);
            }
        }

        [Test] public void WrongFingerprintIndexAndOverloadFailClosed()
        {
            var fixture = Fixture.Create(); fixture.Config.sites[0].originalMethodHash = new string('0', 64);
            Assert.AreEqual("OriginalMethodChanged", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Transform(fixture.Pe, fixture.Pdb, fixture.Config)).Code);
            fixture = Fixture.Create(); fixture.Config.sites[0].operationIndex = 0;
            Assert.AreEqual("WrongLookupOverload", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Transform(fixture.Pe, fixture.Pdb, fixture.Config)).Code);
            fixture = Fixture.Create();
            fixture.Rewrite(method =>
            {
                var old = (IMethod)method.Body.Instructions[1].Operand;
                method.Body.Instructions[1].Operand = new MemberRefUser(method.Module, "GetType", MethodSig.CreateStatic(old.MethodSig.RetType, method.Module.CorLibTypes.String, method.Module.CorLibTypes.Boolean), old.DeclaringType);
                method.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Ldc_I4_0));
            });
            fixture.Config.sites[0].operationIndex = 2;
            Assert.AreEqual("WrongLookupOverload", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Transform(fixture.Pe, null, fixture.Config)).Code);
        }

        [Test] public void ExtraLookupFailsEvenWithUpdatedMethodFingerprint()
        {
            var fixture = Fixture.Create();
            fixture.Rewrite(method =>
            {
                var lookup = (IMethod)method.Body.Instructions[1].Operand;
                method.Body.Instructions.Insert(2, Instruction.Create(OpCodes.Pop));
                method.Body.Instructions.Insert(3, Instruction.Create(OpCodes.Ldarg_0));
                method.Body.Instructions.Insert(4, Instruction.Create(OpCodes.Call, lookup));
            });
            fixture.RefreshFingerprint();
            Assert.AreEqual("AdditionalLookup", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Transform(fixture.Pe, null, fixture.Config)).Code);
        }

        [Test] public void GuardAndSourceTamperingAndExtraGuardReferencesAreRejected()
        {
            var fixture = Fixture.Create(typeof(string).AssemblyQualifiedName); var output = ReflectionBindingTransformer.Transform(fixture.Pe, fixture.Pdb, fixture.Config);
            using (var module = ModuleDefMD.Load(output.PeData))
            {
                var binding = ReflectionBindingTransformer.Verify(module, fixture.Config).Single();
                binding.GuardMethod.Body.Instructions[1].Operand = "Candidate.Entry, Candidate";
                Assert.AreEqual("GuardTemplateMismatch", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Verify(module, fixture.Config)).Code);
            }
            using (var module = ModuleDefMD.Load(output.PeData))
            {
                var binding = ReflectionBindingTransformer.Verify(module, fixture.Config).Single();
                binding.OriginalMethod.Body.Instructions[0].OpCode = OpCodes.Ldnull;
                Assert.AreEqual("OriginalMethodChanged", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Verify(module, fixture.Config)).Code);
            }
            using (var module = ModuleDefMD.Load(output.PeData))
            {
                var binding = ReflectionBindingTransformer.Verify(module, fixture.Config).Single();
                var extra = new MethodDefUser("Extra", MethodSig.CreateStatic(module.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Private | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() };
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, binding.GuardMethod)); extra.Body.Instructions.Add(Instruction.Create(OpCodes.Pop)); extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                binding.OriginalMethod.DeclaringType.Methods.Add(extra);
                Assert.AreEqual("AdditionalGuardReference", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Verify(module, fixture.Config)).Code);
            }
        }

        [Test] public void StaleConfigurationReprocessingAndStrongNamesAreRejected()
        {
            var fixture = Fixture.Create(); var output = ReflectionBindingTransformer.Transform(fixture.Pe, fixture.Pdb, fixture.Config);
            Assert.AreEqual("AlreadyProcessed", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Transform(output.PeData, output.PdbData, fixture.Config)).Code);
            fixture.Config.sites[0].reason = "Different reviewed contract";
            using (var module = ModuleDefMD.Load(output.PeData))
                Assert.AreEqual("MissingGuardedSite", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Verify(module, fixture.Config)).Code);
            using (var module = ModuleDefMD.Load(fixture.Pe))
            {
                module.IsStrongNameSigned = true;
                Assert.AreEqual("StrongNameUnsupported", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Verify(module, fixture.Config)).Code);
            }
        }

        [Test] public void MissingAmbiguousAndAdditionalGeneratedSitesAreRejected()
        {
            var fixture = Fixture.Create(); fixture.Config.sites[0].methodSignature = "System.Type Fixture.Host::Missing(System.String)";
            Assert.AreEqual("MissingConfiguredMethod", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Transform(fixture.Pe, fixture.Pdb, fixture.Config)).Code);
            fixture = Fixture.Create();
            fixture.Rewrite(method => method.DeclaringType.Methods.Add(new MethodDefUser(method.Name, method.MethodSig, method.ImplAttributes, method.Attributes) { Body = method.Body }));
            Assert.AreEqual("MissingConfiguredMethod", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Transform(fixture.Pe, null, fixture.Config)).Code);
            fixture = Fixture.Create(); var output = ReflectionBindingTransformer.Transform(fixture.Pe, fixture.Pdb, fixture.Config);
            using (var module = ModuleDefMD.Load(output.PeData))
            {
                var binding = ReflectionBindingTransformer.Verify(module, fixture.Config).Single();
                var extra = new MethodDefUser(ReflectionBindingTransformer.GuardPrefix + "unverified", MethodSig.CreateStatic(module.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL,
                    dnlib.DotNet.MethodAttributes.Private | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() };
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); binding.OriginalMethod.DeclaringType.Methods.Add(extra);
                Assert.AreEqual("UnexpectedGuard", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Verify(module, fixture.Config)).Code);
            }
        }

        [Test] public void VerifyRejectsNewUnguardedLookupInConfiguredMethod()
        {
            var fixture = Fixture.Create(typeof(string).AssemblyQualifiedName); var output = ReflectionBindingTransformer.Transform(fixture.Pe, fixture.Pdb, fixture.Config);
            using (var module = ModuleDefMD.Load(output.PeData))
            {
                var binding = ReflectionBindingTransformer.Verify(module, fixture.Config).Single();
                var lookup = (IMethod)binding.GuardMethod.Body.Instructions.Single(instruction => (instruction.Operand as IMethod)?.Name == "GetType").Operand;
                var body = binding.OriginalMethod.Body.Instructions;
                body.Insert(2, Instruction.Create(OpCodes.Pop)); body.Insert(3, Instruction.Create(OpCodes.Ldarg_0)); body.Insert(4, Instruction.Create(OpCodes.Call, lookup));
                Assert.AreEqual("AdditionalLookup", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Verify(module, fixture.Config)).Code);
            }
        }

        [Test] public void FingerprintIgnoresMvidAndMetadataTokensButTracksLocalAndBranchShape()
        {
            var fixture = Fixture.Create();
            using (var module = ModuleDefMD.Load(fixture.Pe))
            {
                var method = module.GetTypes().SelectMany(type => type.Methods).Single(); string original = ReflectionBindingFingerprint.Compute(method);
                module.Mvid = Guid.NewGuid(); method.Rid += 10;
                Assert.AreEqual(original, ReflectionBindingFingerprint.Compute(method));
                method.Body.Variables.Add(new Local(module.CorLibTypes.Int32));
                Assert.AreNotEqual(original, ReflectionBindingFingerprint.Compute(method));
                method.Body.Variables.Clear();
                var branch = Instruction.Create(OpCodes.Br, method.Body.Instructions[0]); method.Body.Instructions.Insert(0, branch);
                string firstBranch = ReflectionBindingFingerprint.Compute(method);
                branch.Operand = method.Body.Instructions[2];
                Assert.AreNotEqual(firstBranch, ReflectionBindingFingerprint.Compute(method));
            }
        }

        [Test] public void ConfigRejectsCompositeTargetsAndDuplicateMethods()
        {
            foreach (string target in new[] { "System.String", "System.String[], mscorlib", "System.Int32*, mscorlib", "System.Int32&, mscorlib",
                "System.Collections.Generic.List`1, mscorlib", "System.String, mscorlib, Version=broken", "System.String, mscorlib, Version=1.2.3.4, Culture=neutral, PublicKeyToken=oops" })
                Assert.AreEqual("InvalidAllowedType", Assert.Throws<ReflectionBindingException>(() => Fixture.Create(target).Config.Validate()).Code);
            var fixture = Fixture.Create(); var site = fixture.Config.sites[0];
            fixture.Config.sites = new[] { site, new ReflectionBindingSite { id = "different", assembly = site.assembly, typeName = site.typeName,
                methodSignature = site.methodSignature, originalMethodHash = site.originalMethodHash, operationIndex = site.operationIndex, reason = "test", allowedTypes = new string[0] } };
            Assert.AreEqual("DuplicateMethodSite", Assert.Throws<ReflectionBindingException>(() => fixture.Config.Validate()).Code);
        }

        [Test] public void EditorAndDisabledFeaturesSkipWithoutReadingMachineState()
        {
            Assert.IsNull(ReflectionBindingsILPostProcessor.ProcessAssembly("ignored", new[] { "UNITY_EDITOR", ReflectionBindingDefines.Prefix + "invalid" }, null, null, "/nonexistent"));
            Assert.IsNull(ReflectionBindingsILPostProcessor.ProcessAssembly("ignored", new string[0], null, null, "/nonexistent"));
            string hash;
            Assert.AreEqual("InvalidBindingDefine", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingDefines.TryGetEnabledHash(new[] { ReflectionBindingDefines.Prefix + "invalid" }, out hash)).Code);
            string define = ReflectionBindingDefines.Create(new byte[] { 1 });
            Assert.AreEqual("AmbiguousBindingDefine", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingDefines.TryGetEnabledHash(new[] { define, define }, out hash)).Code);
        }

        [Test] public void RawConfigurationDefineBindsExactBytesAndProjectRoot()
        {
            var fixture = Fixture.Create(); string root = Path.Combine(Path.GetTempPath(), "ReflectionBindingTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            try
            {
                File.WriteAllText(Path.Combine(root, "ProjectSettings/ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f2");
                byte[] raw;
                using (var stream = new MemoryStream()) { new DataContractJsonSerializer(typeof(ReflectionBindingConfiguration)).WriteObject(stream, fixture.Config); raw = stream.ToArray(); }
                File.WriteAllBytes(Path.Combine(root, ReflectionBindingConfiguration.ProjectRelativePath), raw);
                string define = ReflectionBindingDefines.Create(raw);
                Assert.AreEqual(fixture.Config.ComputeHash(), ReflectionBindingConfiguration.Parse(raw).ComputeHash());
                Assert.IsNull(ReflectionBindingsILPostProcessor.ProcessAssembly("Other", new[] { define }, null, null, root));
                Assert.NotNull(ReflectionBindingsILPostProcessor.ProcessAssembly("ReflectionFixture", new[] { define }, fixture.Pe, fixture.Pdb, root));
                File.AppendAllText(Path.Combine(root, ReflectionBindingConfiguration.ProjectRelativePath), " ");
                Assert.AreEqual("BindingConfigurationHashMismatch", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingsILPostProcessor.ProcessAssembly("Other", new[] { define }, null, null, root)).Code);
                Assert.AreEqual("InvalidProjectRoot", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingsILPostProcessor.ProcessAssembly("Other", new[] { define }, null, null, Path.GetDirectoryName(root))).Code);
            }
            finally { Directory.Delete(root, true); }
        }

        [Test] public void LinkedVerificationUsesExactPerTypeForwardersIncludingNestedTypes()
        {
            var fixture = LinkedFixture.Create();
            using (var compiled = ModuleDefMD.Load(fixture.Compiled))
            using (var linked = ModuleDefMD.Load(fixture.Linked))
            {
                Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Verify(linked, fixture.Source.Config));
                var binding = ReflectionBindingTransformer.VerifyLinked(compiled, linked, fixture.Source.Config, fixture.Profile()).Single();
                Assert.AreEqual(fixture.Profile().ComputeHash(), binding.LinkedProfileHash);
                Assert.AreNotEqual(binding.CompiledMethodHash, binding.LinkedMethodHash);
                Assert.AreNotEqual(binding.CompiledGuardHash, binding.LinkedGuardHash);
                Assert.AreSame(linked, binding.GuardMethod.Module);
                var enumerable = linked.GetTypeRefs().Single(type => type.FullName == "System.Linq.Enumerable");
                Assert.AreEqual("System.Core", enumerable.DefinitionAssembly.Name.String);
                var nested = linked.GetTypeRefs().Single(type => type.FullName == "System.Collections.Generic.List`1/Enumerator");
                Assert.AreEqual("mscorlib", nested.DefinitionAssembly.Name.String);
            }
        }

        [Test] public void LinkedDenyAllStillVerifiesCompleteGuardWithoutLookup()
        {
            var fixture = LinkedFixture.Create(true);
            using (var compiled = ModuleDefMD.Load(fixture.Compiled)) using (var linked = ModuleDefMD.Load(fixture.Linked))
            {
                var binding = ReflectionBindingTransformer.VerifyLinked(compiled, linked, fixture.Source.Config, fixture.Profile()).Single();
                Assert.AreEqual(3, binding.GuardMethod.Body.Instructions.Count);
                Assert.IsFalse(binding.GuardMethod.Body.Instructions.Any(instruction => (instruction.Operand as IMethod)?.Name == "GetType"));
                binding.GuardMethod.Body.Instructions[0].Operand = "Changed denial";
                Assert.AreEqual("GuardTemplateMismatch", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.VerifyLinked(compiled, linked, fixture.Source.Config, fixture.Profile())).Code);
            }
        }

        [Test] public void LinkedProfileRejectsWrongHashSourceIdentityAndBrokenForwarders()
        {
            var fixture = LinkedFixture.Create();
            Assert.AreEqual("RetargetingFacadeHashMismatch", Assert.Throws<ReflectionBindingException>(() =>
                CapturedReflectionRetargetingProfile.Load(fixture.Facade, new string('0', 64), fixture.Framework)).Code);
            foreach (Action<ModuleDefMD> mutate in new Action<ModuleDefMD>[] {
                module => module.Assembly.Version = new Version(2, 0, 0, 0),
                module => module.Assembly.PublicKey = new PublicKey(Convert.FromBase64String(LinkedFixture.CoreKey)),
                module => module.ExportedTypes.Add(new ExportedTypeUser(module, 0, "System", "Type", dnlib.DotNet.TypeAttributes.Forwarder, new AssemblyRefUser(new AssemblyNameInfo(LinkedFixture.CoreIdentity)))),
                module => module.ExportedTypes[0].Implementation = module.ExportedTypes[0],
                module => module.ExportedTypes[0].Attributes &= ~dnlib.DotNet.TypeAttributes.Forwarder,
                module => module.ExportedTypes[0].Implementation = new FileDefUser { Name = "missing.netmodule" } })
            {
                byte[] changed = RewriteModule(fixture.Facade, mutate);
                Assert.Throws<ReflectionBindingException>(() => CapturedReflectionRetargetingProfile.Load(changed, Hash(changed), fixture.Framework));
            }
        }

        [Test] public void LinkedVerificationRejectsWrongProviderVersionTokenAndNonforwardedTypes()
        {
            var fixture = LinkedFixture.Create();
            foreach (Action<ModuleDefMD> mutate in new Action<ModuleDefMD>[] {
                module => module.CorLibTypes.AssemblyRef.Version = new Version(4, 1, 0, 0),
                module => module.CorLibTypes.AssemblyRef.PublicKeyOrToken = new PublicKeyToken("0011223344556677"),
                module => module.CorLibTypes.AssemblyRef.Name = "Business.Lookalike",
                module => module.GetTypeRefs().Single(type => type.FullName == "System.Linq.Enumerable").ResolutionScope = module.CorLibTypes.AssemblyRef })
                AssertLinkedRejected(fixture, mutate);
            byte[] missing = RewriteModule(fixture.Facade, module => module.ExportedTypes.Remove(module.ExportedTypes.Single(type => type.TypeName == "Enumerable")));
            using (var compiled = ModuleDefMD.Load(fixture.Compiled)) using (var linked = ModuleDefMD.Load(fixture.Linked))
            {
                var missingProfile = CapturedReflectionRetargetingProfile.Load(missing, Hash(missing), fixture.Framework);
                Assert.AreEqual("MissingRetargetingForwarder", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.VerifyLinked(compiled, linked, fixture.Source.Config, missingProfile)).Code);
                var noDestinations = CapturedReflectionRetargetingProfile.Load(fixture.Facade, Hash(fixture.Facade), new byte[0][]);
                Assert.AreEqual("MissingRetargetingDestinationType", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.VerifyLinked(compiled, linked, fixture.Source.Config, noDestinations)).Code);
                byte[][] wrongRuntime = fixture.Framework.Select(bytes => RewriteModule(bytes, module => module.Assembly.Version = new Version(9, 0, 0, 0))).ToArray();
                var wrongProfile = CapturedReflectionRetargetingProfile.Load(fixture.Facade, Hash(fixture.Facade), wrongRuntime);
                Assert.AreEqual("MissingRetargetingDestinationType", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.VerifyLinked(compiled, linked, fixture.Source.Config, wrongProfile)).Code);
                byte[][] missingType = fixture.Framework.Select(bytes => RewriteModule(bytes, module => {
                    var type = module.Types.SingleOrDefault(value => value.FullName == "System.Linq.Enumerable"); if (type != null) module.Types.Remove(type); })).ToArray();
                var missingTypeProfile = CapturedReflectionRetargetingProfile.Load(fixture.Facade, Hash(fixture.Facade), missingType);
                Assert.AreEqual("MissingRetargetingDestinationType", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.VerifyLinked(compiled, linked, fixture.Source.Config, missingTypeProfile)).Code);
            }
        }

        [Test] public void LinkedVerificationRejectsEveryGuardTemplateMutation()
        {
            var fixture = LinkedFixture.Create();
            foreach (Action<MethodDef> mutate in new Action<MethodDef>[] {
                guard => guard.Body.Instructions[1].Operand = "Candidate.Entry, Candidate",
                guard => guard.Body.Instructions[2].OpCode = OpCodes.Ldc_I4_5,
                guard => ((MemberRef)guard.Body.Instructions[3].Operand).Name = "WrongEquals",
                guard => guard.Body.Instructions[4].OpCode = OpCodes.Br,
                guard => ((IMethod)guard.Body.Instructions[6].Operand).MethodSig.Params.Add(guard.Module.CorLibTypes.Boolean),
                guard => guard.Body.Instructions[guard.Body.Instructions.Count - 1].OpCode = OpCodes.Ret,
                guard => guard.Body.Variables.Add(new Local(guard.Module.CorLibTypes.Int32)),
                guard => guard.MethodSig.Params[0] = guard.Module.CorLibTypes.Boolean,
                guard => guard.Attributes |= dnlib.DotNet.MethodAttributes.Public,
                guard => guard.Name += "_swapped" })
                AssertLinkedRejected(fixture, module => mutate(Guard(module)));
        }

        [Test] public void LinkedVerificationRejectsSourceCallsiteBodyIdentityAndCardinalityChanges()
        {
            var fixture = LinkedFixture.Create();
            foreach (Action<ModuleDefMD> mutate in new Action<ModuleDefMD>[] {
                module => module.Assembly.Version = new Version(9, 0, 0, 0),
                module => Run(module).Body.Instructions[0].OpCode = OpCodes.Ldnull,
                module => Run(module).Body.Variables.Add(new Local(module.CorLibTypes.Int32)),
                module => Run(module).MethodSig.Params[0] = module.CorLibTypes.Boolean,
                module => Run(module).Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop)),
                module => Run(module).Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) {
                    TryStart = Run(module).Body.Instructions[0], TryEnd = Run(module).Body.Instructions[2],
                    HandlerStart = Run(module).Body.Instructions[2], HandlerEnd = Run(module).Body.Instructions[4] }),
                module => Run(module).Body.Instructions[fixture.Source.Config.sites[0].operationIndex].Operand = Guard(module).Body.Instructions[6].Operand,
                module => Run(module).DeclaringType.Methods.Remove(Guard(module)),
                module => { var guard = Guard(module); var extra = new MethodDefUser("Other", MethodSig.CreateStatic(module.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL,
                    dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() }; extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, guard));
                    extra.Body.Instructions.Add(Instruction.Create(OpCodes.Pop)); extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); guard.DeclaringType.Methods.Add(extra); },
                module => { var extra = new MethodDefUser(ReflectionBindingTransformer.GuardPrefix + "unverified", MethodSig.CreateStatic(module.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL,
                    dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() }; extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); Run(module).DeclaringType.Methods.Add(extra); } })
                AssertLinkedRejected(fixture, mutate);
        }

        [Test] public void LinkedVerificationNeverNormalizesCompilerOrStaleConfigurationProof()
        {
            var fixture = LinkedFixture.Create();
            using (var compiled = ModuleDefMD.Load(fixture.Source.Pe)) using (var linked = ModuleDefMD.Load(fixture.Linked))
                Assert.AreEqual("MissingGuardedSite", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.VerifyLinked(compiled, linked, fixture.Source.Config, fixture.Profile())).Code);
            using (var compiled = ModuleDefMD.Load(fixture.Compiled)) using (var linked = ModuleDefMD.Load(fixture.Linked))
            {
                fixture.Source.Config.sites[0].originalMethodHash = new string('0', 64);
                Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.VerifyLinked(compiled, linked, fixture.Source.Config, fixture.Profile()));
            }
            fixture = LinkedFixture.Create();
            fixture.Source.Config.sites[0].reason = "Changed configuration";
            using (var compiled = ModuleDefMD.Load(fixture.Compiled)) using (var linked = ModuleDefMD.Load(fixture.Linked))
                Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.VerifyLinked(compiled, linked, fixture.Source.Config, fixture.Profile()));
        }

        [Test] public void LinkedProfileHashIsDeterministicAndBindsCapturedDestinationBytes()
        {
            var fixture = LinkedFixture.Create(); var profile = fixture.Profile();
            Assert.AreEqual(profile.ComputeHash(), CapturedReflectionRetargetingProfile.Load(fixture.Facade, Hash(fixture.Facade), fixture.Framework.Reverse()).ComputeHash());
            Assert.That(profile.Forwarders.Select(value => value.TypeFullName), Does.Contain("System.Collections.Generic.List`1/Enumerator"));
            var runtime = profile.RuntimeModules; runtime[0] = null;
            Assert.NotNull(profile.RuntimeModules[0]);
            byte[][] changed = fixture.Framework.Select(bytes => RewriteModule(bytes, module => module.Mvid = Guid.NewGuid())).ToArray();
            Assert.AreNotEqual(profile.ComputeHash(), CapturedReflectionRetargetingProfile.Load(fixture.Facade, Hash(fixture.Facade), changed).ComputeHash());
        }

        private static MethodDef Run(ModuleDef module) { return module.GetTypes().SelectMany(type => type.Methods).Single(method => method.Name == "Run"); }
        private static MethodDef Guard(ModuleDef module) { return module.GetTypes().SelectMany(type => type.Methods).Single(method => method.Name.String.StartsWith(ReflectionBindingTransformer.GuardPrefix, StringComparison.Ordinal)); }
        private static string Hash(byte[] bytes) { return ReflectionBindingDefines.Create(bytes).Substring(ReflectionBindingDefines.Prefix.Length); }
        private static byte[] RewriteModule(byte[] bytes, Action<ModuleDefMD> change)
        { using (var module = ModuleDefMD.Load(bytes)) using (var stream = new MemoryStream()) { change(module); module.Write(stream); return stream.ToArray(); } }
        private static void AssertLinkedRejected(LinkedFixture fixture, Action<ModuleDefMD> mutate)
        {
            using (var compiled = ModuleDefMD.Load(fixture.Compiled)) using (var linked = ModuleDefMD.Load(fixture.Linked))
            { mutate(linked); Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.VerifyLinked(compiled, linked, fixture.Source.Config, fixture.Profile())); }
        }

        private sealed class LinkedFixture
        {
            internal const string CoreIdentity = "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
            internal const string LinqIdentity = "System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e";
            internal const string CoreKey = "AAAAAAAAAAAEAAAAAAAAAA==";
            private const string FacadeKey = "ACQAAASAAACUAAAABgIAAAAkAABSU0ExAAQAAAEAAQBLhsTLeFSbNLq2GjsYAOI7/rWz7DkAdAQVNqfjy9l/XwTPD4VxVaiSjqop6/0Rz7utO6cO/qe9oyJsao03CkzTA/cUSGtuvCJZhaY4Rx5u9XHMkqRhPAC4+mXWHM7gy+XzYzDJoB9Bg1WfG+8kzCkXxtkT46VBMzodBdm+0is4yw==";
            internal Fixture Source;
            internal byte[] Compiled, Linked, Facade;
            internal byte[][] Framework;
            internal CapturedReflectionRetargetingProfile Profile() { return CapturedReflectionRetargetingProfile.Load(Facade, Hash(Facade), Framework); }
            internal static LinkedFixture Create(bool denyAll = false)
            {
                var result = new LinkedFixture();
                result.Source = Fixture.CreateWithScope(new AssemblyRefUser(new AssemblyNameInfo(CapturedReflectionRetargetingProfile.RequiredFacadeIdentity)),
                    denyAll ? new string[0] : new[] { typeof(string).AssemblyQualifiedName });
                result.Source.Rewrite(method => {
                    var module = method.Module; var scope = module.CorLibTypes.AssemblyRef;
                    var outer = new TypeRefUser(module, "System.Collections.Generic", "List`1", scope);
                    var nested = new TypeRefUser(module, "", "Enumerator", outer);
                    method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldtoken, new TypeRefUser(module, "System.Linq", "Enumerable", scope)));
                    method.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Pop));
                    method.Body.Instructions.Insert(2, Instruction.Create(OpCodes.Ldtoken, nested));
                    method.Body.Instructions.Insert(3, Instruction.Create(OpCodes.Pop)); });
                result.Source.Config.sites[0].operationIndex += 4; result.Source.RefreshFingerprint();
                result.Compiled = ReflectionBindingTransformer.Transform(result.Source.Pe, null, result.Source.Config).PeData;
                result.Linked = RewriteModule(result.Compiled, module => {
                    module.GetTypeRefs().Single(type => type.FullName == "System.Linq.Enumerable").ResolutionScope = new AssemblyRefUser(new AssemblyNameInfo(LinqIdentity));
                    var scope = module.CorLibTypes.AssemblyRef; scope.Name = "mscorlib"; scope.Version = new Version(4, 0, 0, 0); scope.PublicKeyOrToken = new PublicKeyToken("b77a5c561934e089"); });
                string[] coreTypes = { "System.Object", "System.Type", "System.String", "System.Boolean", "System.Void", "System.Int32", "System.Enum", "System.StringComparison", "System.InvalidOperationException", "System.Collections.Generic.List`1" };
                result.Framework = new[] { FrameworkBytes("mscorlib", CoreKey, coreTypes, true),
                    FrameworkBytes("System.Core", "ACQAAASAAACUAAAABgIAAAAkAABSU0ExAAQAAAEAAQCNVsdvnoZJODBJ84PES+DsIEGBgipsMc9et+9IaUTQMhiOodOSB2NxLMsS11+3fpgRFJ5hSOXTL7qrN2EcGHjdwZ4g7xNdDLLP8r/sPRFYEMPZBpY4/kviFdv3lYYZIOWrb32y4s7vE2rCPV3SvwMXAK7CMvbGsceFtDBcEjs3qw==", new[] { "System.Linq.Enumerable" }, false) };
                using (var facade = new ModuleDefUser("netstandard.dll")) using (var stream = new MemoryStream())
                {
                    new AssemblyDefUser("netstandard", new Version(2, 1, 0, 0), new PublicKey(Convert.FromBase64String(FacadeKey))).Modules.Add(facade);
                    foreach (string name in coreTypes.Concat(new[] { "System.Linq.Enumerable" }))
                    {
                        int split = name.LastIndexOf('.'); var export = new ExportedTypeUser(facade, 0, name.Substring(0, split), name.Substring(split + 1),
                            dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.Forwarder, new AssemblyRefUser(new AssemblyNameInfo(name == "System.Linq.Enumerable" ? LinqIdentity : CoreIdentity)));
                        facade.ExportedTypes.Add(export);
                        if (name.EndsWith("List`1", StringComparison.Ordinal)) facade.ExportedTypes.Add(new ExportedTypeUser(facade, 0, "", "Enumerator", dnlib.DotNet.TypeAttributes.NotPublic, export));
                    }
                    facade.Write(stream); result.Facade = stream.ToArray();
                }
                return result;
            }
            private static byte[] FrameworkBytes(string name, string key, string[] types, bool nested)
            {
                using (var module = new ModuleDefUser(name + ".dll")) using (var stream = new MemoryStream())
                {
                    new AssemblyDefUser(name, new Version(4, 0, 0, 0), new PublicKey(Convert.FromBase64String(key))).Modules.Add(module);
                    foreach (string fullName in types) { int split = fullName.LastIndexOf('.'); var type = new TypeDefUser(fullName.Substring(0, split), fullName.Substring(split + 1)); module.Types.Add(type);
                        if (nested && fullName.EndsWith("List`1", StringComparison.Ordinal)) type.NestedTypes.Add(new TypeDefUser("", "Enumerator")); }
                    module.Write(stream); return stream.ToArray();
                }
            }
        }

        private static MethodInfo RuntimeMethod(byte[] pe) { return Assembly.Load(pe).GetType("Fixture.Host", true).GetMethod("Run", BindingFlags.Public | BindingFlags.Static); }

        private sealed class FixtureDocument : dnlib.DotNet.Pdb.Symbols.SymbolDocument
        {
            public override string URL { get { return "ReflectionFixture.cs"; } }
            public override Guid Language { get { return Guid.Empty; } }
            public override Guid LanguageVendor { get { return Guid.Empty; } }
            public override Guid DocumentType { get { return Guid.Empty; } }
            public override Guid CheckSumAlgorithmId { get { return Guid.Empty; } }
            public override byte[] CheckSum { get { return new byte[0]; } }
            public override PdbCustomDebugInfo[] CustomDebugInfos { get { return new PdbCustomDebugInfo[0]; } }
            public override MDToken? MDToken { get { return null; } }
        }

        private sealed class Fixture
        {
            internal byte[] Pe, Pdb;
            internal ReflectionBindingConfiguration Config;
            internal static Fixture Create(params string[] allowed)
            { return CreateWithScope(null, allowed); }
            internal static Fixture CreateWithScope(AssemblyRef scope, params string[] allowed)
            {
                var result = new Fixture();
                using (var module = new ModuleDefUser("ReflectionFixture.dll", new Guid("f9168c5e-ceb2-4faa-b6bf-329bf39fa1e4"), scope) { Kind = ModuleKind.Dll })
                {
                    var assembly = new AssemblyDefUser("ReflectionFixture", new Version(1, 0)); assembly.Modules.Add(module);
                    var host = new TypeDefUser("Fixture", "Host", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.Abstract | dnlib.DotNet.TypeAttributes.Sealed };
                    module.Types.Add(host);
                    var type = new TypeRefUser(module, "System", "Type", module.CorLibTypes.AssemblyRef);
                    var lookup = new MemberRefUser(module, "GetType", MethodSig.CreateStatic(new ClassSig(type), module.CorLibTypes.String), type);
                    var method = new MethodDefUser("Run", MethodSig.CreateStatic(new ClassSig(type), module.CorLibTypes.String), dnlib.DotNet.MethodImplAttributes.IL,
                        dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static | dnlib.DotNet.MethodAttributes.HideBySig) { Body = new CilBody() };
                    host.Methods.Add(method); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, lookup)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    module.CreatePdbState(PdbFileKind.PortablePDB);
                    var document = new PdbDocument(new FixtureDocument());
                    method.Body.Instructions[1].SequencePoint = new SequencePoint { Document = document, StartLine = 10, StartColumn = 5, EndLine = 10, EndColumn = 25 };
                    method.Body.PdbMethod = new PdbMethod { Scope = new PdbScope { Start = method.Body.Instructions[0] } };
                    using (var pe = new MemoryStream()) using (var pdb = new MemoryStream())
                    {
                        var options = new ModuleWriterOptions(module) { WritePdb = true, PdbStream = pdb, PdbFileName = "ReflectionFixture.pdb", PdbOptions = PdbWriterOptions.Deterministic };
                        options.PEHeadersOptions.TimeDateStamp = 0; module.Write(pe, options); result.Pe = pe.ToArray(); result.Pdb = pdb.ToArray();
                    }
                }
                using (var module = ModuleDefMD.Load(result.Pe))
                {
                    var method = module.GetTypes().SelectMany(type => type.Methods).Single();
                    result.Config = new ReflectionBindingConfiguration { sites = new[] { new ReflectionBindingSite { id = "test.lookup", assembly = "ReflectionFixture", typeName = "Fixture.Host",
                        methodSignature = ReflectionBindingFingerprint.MethodSignature(method), originalMethodHash = ReflectionBindingFingerprint.Compute(method), operationIndex = 1, allowedTypes = allowed, reason = "Bounded unit fixture" } } };
                }
                return result;
            }
            internal void Rewrite(Action<MethodDef> change)
            {
                using (var module = ModuleDefMD.Load(Pe))
                using (var output = new MemoryStream()) { change(module.GetTypes().SelectMany(type => type.Methods).Single()); module.Write(output); Pe = output.ToArray(); Pdb = null; }
            }
            internal void RefreshFingerprint()
            {
                using (var module = ModuleDefMD.Load(Pe)) Config.sites[0].originalMethodHash = ReflectionBindingFingerprint.Compute(module.GetTypes().SelectMany(type => type.Methods).Single());
            }
        }
    }
}
