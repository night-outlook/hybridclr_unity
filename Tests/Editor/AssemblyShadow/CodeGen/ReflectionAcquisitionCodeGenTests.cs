using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HybridCLR.AssemblyShadow.CodeGen;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class ReflectionAcquisitionCodeGenTests
    {
        [Test] public void FiniteAssembliesAreBuiltFromAnchorsWithoutDomainEnumeration()
        {
            var fixture = AcquisitionFixture.Create("FiniteAssemblyList", typeof(string).AssemblyQualifiedName, typeof(int).AssemblyQualifiedName);
            var output = fixture.Transform(); var run = fixture.Run(output.PeData);
            CollectionAssert.AreEqual(new[] { typeof(string).Assembly }, (Assembly[])run.Invoke(null, new object[] { AppDomain.CurrentDomain }));
            Denied(() => run.Invoke(null, new object[] { null }));
            using (var module = ModuleDefMD.Load(output.PeData))
            {
                var binding = ReflectionBindingTransformer.Verify(module, fixture.Config).Single();
                Assert.AreEqual("FiniteAssemblyList", binding.Kind);
                Assert.IsFalse(module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                    .Any(i => (i.Operand as IMethod)?.Name == "GetAssemblies"));
            }
        }

        [Test] public void FiniteTypesRejectUnknownReceiverAndNeverEnumerateItsTypes()
        {
            var fixture = AcquisitionFixture.Create("FiniteAssemblyTypes", typeof(string).AssemblyQualifiedName, typeof(int).AssemblyQualifiedName);
            var output = fixture.Transform(); var run = fixture.Run(output.PeData);
            CollectionAssert.AreEquivalent(new[] { typeof(string), typeof(int) }, (Type[])run.Invoke(null, new object[] { typeof(string).Assembly }));
            int resolutions = 0; ResolveEventHandler resolver = (sender, args) => { resolutions++; return null; };
            AppDomain.CurrentDomain.AssemblyResolve += resolver;
            try { Denied(() => run.Invoke(null, new object[] { typeof(ReflectionAcquisitionCodeGenTests).Assembly })); Denied(() => run.Invoke(null, new object[] { null })); }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolver; }
            Assert.AreEqual(0, resolutions);
            using (var module = ModuleDefMD.Load(output.PeData))
                Assert.IsFalse(module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                    .Any(i => (i.Operand as IMethod)?.Name == "GetTypes" || (i.Operand as IMethod)?.Name == "GetExportedTypes"));
        }

        [Test] public void FixedImageGuardRejectsNullTamperedAndOtherImagesBeforeLoading()
        {
            var fixture = AcquisitionFixture.Fixed(); var output = fixture.Transform(); var run = fixture.Run(output.PeData);
            Assert.AreEqual(fixture.Config.sites[0].providerAssemblyIdentity, ((Assembly)run.Invoke(null, new object[] { fixture.Image })).FullName);
            var bad = (byte[])fixture.Image.Clone(); bad[0] ^= 1;
            Denied(() => run.Invoke(null, new object[] { null })); Denied(() => run.Invoke(null, new object[] { bad }));
            Denied(() => run.Invoke(null, new object[] { new byte[0] }));
            Denied(() => run.Invoke(null, new object[] { fixture.Pe }));
            using (var module = ModuleDefMD.Load(output.PeData))
            {
                var binding = ReflectionBindingTransformer.Verify(module, fixture.Config).Single();
                Assert.AreEqual("FixedAssemblyBytes", binding.Kind); Assert.AreEqual(fixture.Config.sites[0].imageSha256, binding.ImageSha256);
                Assert.IsFalse(module.GetAssemblyRefs().Any(a => a.FullName == binding.ProviderAssemblyIdentity), "No provider compile-time dependency may be introduced.");
                var il = binding.GuardMethod.Body.Instructions;
                var load = il.Single(i => (i.Operand as IMethod)?.Name == "Load");
                var privateCopy = binding.GuardMethod.Body.Variables[0];
                Assert.AreSame(privateCopy, il[il.IndexOf(load) - 1].Operand);
                var hash = il.Single(i => (i.Operand as IMethod)?.Name == "ComputeHash");
                Assert.AreSame(privateCopy, il[il.IndexOf(hash) - 1].Operand);
                Assert.AreEqual(1, il.Count(i => (i.Operand as IMethod)?.Name == "Clone"));
                Assert.AreEqual(ExceptionHandlerType.Finally, binding.GuardMethod.Body.ExceptionHandlers.Single().HandlerType);
            }
        }

        [Test] public void ConcurrentCallerMutationCannotChangeTheImageAfterHashing()
        {
            var fixture = AcquisitionFixture.Fixed(); var run = fixture.Run(fixture.Transform().PeData);
            byte[] shared = (byte[])fixture.Image.Clone(); int stop = 0; byte first = shared[0];
            var writer = new Thread(() => { while (Interlocked.CompareExchange(ref stop, 0, 0) == 0) { shared[0] = (byte)(first ^ 1); shared[0] = first; } });
            writer.Start();
            try
            {
                for (int i = 0; i < 64; i++)
                    try { Assert.AreEqual(fixture.Config.sites[0].providerAssemblyIdentity, ((Assembly)run.Invoke(null, new object[] { shared })).FullName); }
                    catch (TargetInvocationException error) { Assert.IsInstanceOf<InvalidOperationException>(error.InnerException); }
            }
            finally { Interlocked.Exchange(ref stop, 1); writer.Join(); }
        }

        [Test] public void FixedImageEvidenceRequiresExactBytesAndFullIdentity()
        {
            var fixture = AcquisitionFixture.Fixed(); var site = fixture.Config.sites[0];
            Assert.AreEqual("FixedImageEvidenceMissing", Assert.Throws<ReflectionBindingException>(() => fixture.Config.ValidateImageEvidence(null)).Code);
            var evidence = new Dictionary<string, byte[]> { { site.imagePath, fixture.Image } };
            fixture.Config.ValidateImageEvidence(evidence);
            evidence[site.imagePath] = fixture.Pe;
            Assert.AreEqual("FixedImageHashMismatch", Assert.Throws<ReflectionBindingException>(() => fixture.Config.ValidateImageEvidence(evidence)).Code);
            evidence[site.imagePath] = fixture.Image; site.providerAssemblyIdentity = "Other, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
            Assert.AreEqual("FixedImageIdentityMismatch", Assert.Throws<ReflectionBindingException>(() => fixture.Config.ValidateImageEvidence(evidence)).Code);
        }

        [Test] public void EveryAcquisitionKindHasDeterministicOutputAndRejectsReprocessing()
        {
            foreach (var fixture in new[] { AcquisitionFixture.Fixed(), AcquisitionFixture.Create("FiniteAssemblyList", typeof(string).AssemblyQualifiedName), AcquisitionFixture.Create("FiniteAssemblyTypes", typeof(string).AssemblyQualifiedName) })
            {
                var first = fixture.Transform(); var second = fixture.Transform();
                CollectionAssert.AreEqual(first.PeData, second.PeData); CollectionAssert.AreEqual(first.PdbData, second.PdbData);
                Assert.AreEqual("AlreadyProcessed", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Transform(first.PeData, first.PdbData, fixture.Config)).Code);
            }
        }

        [Test] public void AcquisitionSiteAndGuardTamperingFailsClosed()
        {
            foreach (var fixture in new[] { AcquisitionFixture.Fixed(), AcquisitionFixture.Create("FiniteAssemblyList", typeof(string).AssemblyQualifiedName), AcquisitionFixture.Create("FiniteAssemblyTypes", typeof(string).AssemblyQualifiedName) })
            {
                var output = fixture.Transform();
                using (var module = ModuleDefMD.Load(output.PeData))
                {
                    var binding = ReflectionBindingTransformer.Verify(module, fixture.Config).Single();
                    binding.GuardMethod.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
                    Assert.AreEqual("GuardTemplateMismatch", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Verify(module, fixture.Config)).Code);
                }
                using (var module = ModuleDefMD.Load(output.PeData))
                {
                    var binding = ReflectionBindingTransformer.Verify(module, fixture.Config).Single();
                    binding.OriginalMethod.Body.Instructions[0].OpCode = OpCodes.Ldnull;
                    Assert.AreEqual("OriginalMethodChanged", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Verify(module, fixture.Config)).Code);
                }
                fixture.Config.sites[0].originalMethodHash = new string('0', 64);
                Assert.AreEqual("OriginalMethodChanged", Assert.Throws<ReflectionBindingException>(() => fixture.Transform()).Code);
            }
        }

        [Test] public void SchemaTwoRejectsMalformedKindsPathsIdentitiesAndCompositeTypes()
        {
            var fixture = AcquisitionFixture.Fixed();
            foreach (string path in new[] { "../image", "/absolute", "Images/../image", "Images\\image", "C:image", "Images//image" })
            { fixture.Config.sites[0].imagePath = path; Assert.Throws<ReflectionBindingException>(() => fixture.Config.Validate()); }
            fixture = AcquisitionFixture.Fixed(); fixture.Config.sites[0].kind = "AssemblyLoad"; Assert.Throws<ReflectionBindingException>(() => fixture.Config.Validate());
            fixture = AcquisitionFixture.Fixed(); fixture.Config.sites[0].providerAssemblyIdentity = "Image"; Assert.Throws<ReflectionBindingException>(() => fixture.Config.Validate());
            fixture = AcquisitionFixture.Create("FiniteAssemblyTypes", "System.String, mscorlib"); Assert.Throws<ReflectionBindingException>(() => fixture.Config.Validate());
            fixture = AcquisitionFixture.Create("FiniteAssemblyTypes", "System.String[], mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"); Assert.Throws<ReflectionBindingException>(() => fixture.Config.Validate());
        }

        [Test] public void EveryAcquisitionRejectsAdditionalCallsAndOverloadChanges()
        {
            foreach (string kind in new[] { "FixedAssemblyBytes", "FiniteAssemblyList", "FiniteAssemblyTypes" })
            {
                var fixture = kind == "FixedAssemblyBytes" ? AcquisitionFixture.Fixed() : AcquisitionFixture.Create(kind, typeof(string).AssemblyQualifiedName);
                fixture.RewriteSource(method => {
                    var original = method.Body.Instructions[1];
                    method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldnull));
                    method.Body.Instructions.Insert(1, Instruction.Create(original.OpCode, (IMethod)original.Operand));
                    method.Body.Instructions.Insert(2, Instruction.Create(OpCodes.Pop));
                });
                fixture.Config.sites[0].operationIndex = 4;
                Assert.AreEqual("AdditionalLookup", Assert.Throws<ReflectionBindingException>(() => fixture.Transform()).Code);
                fixture = kind == "FixedAssemblyBytes" ? AcquisitionFixture.Fixed() : AcquisitionFixture.Create(kind, typeof(string).AssemblyQualifiedName);
                fixture.RewriteSource(method => {
                    var old = (IMethod)method.Body.Instructions[1].Operand;
                    var signature = old.MethodSig.Clone(); signature.Params.Add(method.Module.CorLibTypes.Boolean);
                    method.Body.Instructions[1].Operand = new MemberRefUser(method.Module, old.Name, signature, old.DeclaringType);
                    method.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Ldc_I4_0));
                });
                fixture.Config.sites[0].operationIndex = 2;
                Assert.AreEqual("WrongLookupOverload", Assert.Throws<ReflectionBindingException>(() => fixture.Transform()).Code);
            }
        }

        [Test] public void FixedGuardCannotLoadCallerBytesOrBypassTheDigestOrChangeTheHash()
        {
            var fixture = AcquisitionFixture.Fixed(); var output = fixture.Transform();
            foreach (string tamper in new[] { "caller", "digest", "branch", "dispose" })
            using (var module = ModuleDefMD.Load(output.PeData))
            {
                var guard = ReflectionBindingTransformer.Verify(module, fixture.Config).Single().GuardMethod; var il = guard.Body.Instructions;
                if (tamper == "caller") { int index = il.IndexOf(il.Single(i => (i.Operand as IMethod)?.Name == "Load")); il[index - 1] = Instruction.Create(OpCodes.Ldarg_0); }
                if (tamper == "digest") { int index = il.IndexOf(il.First(i => i.OpCode.Code == Code.Ldelem_U1)); il[index + 1] = Instruction.CreateLdcI4(256); }
                if (tamper == "branch") il.First(i => i.OpCode.Code == Code.Bne_Un).OpCode = OpCodes.Beq;
                if (tamper == "dispose") guard.Body.ExceptionHandlers.Clear();
                Assert.AreEqual("GuardTemplateMismatch", Assert.Throws<ReflectionBindingException>(() => ReflectionBindingTransformer.Verify(module, fixture.Config)).Code);
            }
        }

        private static void Denied(TestDelegate action)
        {
            var exception = Assert.Throws<TargetInvocationException>(action);
            Assert.IsInstanceOf<InvalidOperationException>(exception.InnerException);
            StringAssert.StartsWith("AssemblyShadow reflection denied; configuration=", exception.InnerException.Message);
        }

        private sealed class AcquisitionFixture
        {
            internal byte[] Pe, Image;
            internal ReflectionBindingConfiguration Config;
            internal ReflectionTransformResult Transform() { return ReflectionBindingTransformer.Transform(Pe, null, Config); }
            internal void RewriteSource(Action<MethodDef> rewrite)
            {
                using (var module = ModuleDefMD.Load(Pe)) using (var output = new MemoryStream())
                { rewrite(module.GetTypes().SelectMany(t => t.Methods).Single()); module.Write(output); Pe = output.ToArray(); }
                using (var module = ModuleDefMD.Load(Pe)) Config.sites[0].originalMethodHash = ReflectionBindingFingerprint.Compute(module.GetTypes().SelectMany(t => t.Methods).Single());
            }
            internal MethodInfo Run(byte[] pe) { return Assembly.Load(pe).GetType("Fixture.AcquisitionHost").GetMethod("Run"); }
            internal static AcquisitionFixture Fixed()
            {
                var result = Create("FixedAssemblyBytes");
                using (var module = new ModuleDefUser("Image.dll") { Kind = ModuleKind.Dll }) using (var output = new MemoryStream())
                {
                    new AssemblyDefUser("ApprovedImage", new Version(1, 0, 0, 0)).Modules.Add(module);
                    module.Types.Add(new TypeDefUser("Fixture", "Payload", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public });
                    module.Write(output); result.Image = output.ToArray(); result.Config.sites[0].providerAssemblyIdentity = module.Assembly.FullName;
                }
                using (var hash = SHA256.Create()) result.Config.sites[0].imageSha256 = string.Concat(hash.ComputeHash(result.Image).Select(b => b.ToString("x2")));
                result.Config.sites[0].imagePath = "Images/ApprovedImage.dll.bytes";
                return result;
            }
            internal static AcquisitionFixture Create(string kind, params string[] allowed)
            {
                var result = new AcquisitionFixture();
                using (var module = new ModuleDefUser("AcquisitionFixture.dll", new Guid("b72f011b-2c67-410b-8dc6-c253a25b59d4"), new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.FullName))) { Kind = ModuleKind.Dll }) using (var output = new MemoryStream())
                {
                    new AssemblyDefUser("AcquisitionFixture", new Version(1, 0, 0, 0)).Modules.Add(module);
                    var type = new TypeDefUser("Fixture", "AcquisitionHost", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public };
                    module.Types.Add(type);
                    var assembly = new TypeRefUser(module, "System.Reflection", "Assembly", module.CorLibTypes.AssemblyRef);
                    var domain = new TypeRefUser(module, "System", "AppDomain", module.CorLibTypes.AssemblyRef);
                    var runtimeType = new TypeRefUser(module, "System", "Type", module.CorLibTypes.AssemblyRef);
                    TypeSig parameter = kind == "FiniteAssemblyList" ? (TypeSig)new ClassSig(domain) : kind == "FiniteAssemblyTypes" ? new ClassSig(assembly) : (TypeSig)new SZArraySig(module.CorLibTypes.Byte);
                    TypeSig returns = kind == "FiniteAssemblyList" ? (TypeSig)new SZArraySig(new ClassSig(assembly)) : kind == "FiniteAssemblyTypes" ? new SZArraySig(new ClassSig(runtimeType)) : (TypeSig)new ClassSig(assembly);
                    var lookup = kind == "FiniteAssemblyList" ? new MemberRefUser(module, "GetAssemblies", MethodSig.CreateInstance(returns), domain) :
                        kind == "FiniteAssemblyTypes" ? new MemberRefUser(module, "GetTypes", MethodSig.CreateInstance(returns), assembly) : new MemberRefUser(module, "Load", MethodSig.CreateStatic(returns, parameter), assembly);
                    var method = new MethodDefUser("Run", MethodSig.CreateStatic(returns, parameter), dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() };
                    type.Methods.Add(method); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                    method.Body.Instructions.Add(Instruction.Create(kind == "FixedAssemblyBytes" ? OpCodes.Call : OpCodes.Callvirt, lookup)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    module.Write(output); result.Pe = output.ToArray();
                }
                using (var module = ModuleDefMD.Load(result.Pe))
                {
                    var method = module.GetTypes().SelectMany(t => t.Methods).Single();
                    result.Config = new ReflectionBindingConfiguration { schemaVersion = 2, transformerVersion = 2, sites = new[] { new ReflectionBindingSite { id = "test.acquisition", assembly = module.Assembly.Name,
                        typeName = method.DeclaringType.FullName, methodSignature = ReflectionBindingFingerprint.MethodSignature(method), originalMethodHash = ReflectionBindingFingerprint.Compute(method), operationIndex = 1,
                        kind = kind, allowedTypes = allowed, reason = "Finite managed acquisition fixture" } } };
                }
                return result;
            }
        }
    }
}
