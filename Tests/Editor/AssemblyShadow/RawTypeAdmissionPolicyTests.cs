using System;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HybridCLR.AssemblyShadow.CodeGen;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class RawTypeAdmissionPolicyTests
    {
        [Test] public void RealLoaderRequiresTheNewDomainAndAcceptsOnlyItsExactBootstrapOperations()
        {
            using (var fixture = new Fixture())
            {
                CollectionAssert.Contains(fixture.Set.Modules.Keys, "consumer");
                StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate(null).ToString());
                var result = fixture.Validate(fixture.Configuration);
                Assert.IsTrue(result.IsValid, result.ToString());
                fixture.Configuration.sites[0].methodHash = new string('0', 64);
                StringAssert.Contains("InvalidRawTypeAdmissionContract", fixture.Validate(fixture.Configuration).ToString());
                StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate(fixture.Configuration).ToString());
            }
        }

        [Test] public void MethodProseAndOldBootstrapDeclarationsDoNotAuthorizeRawAcquisition()
        {
            using (var fixture = new Fixture())
            {
                fixture.Policy.dependencies.bootstrapEntrypoints = new[] { new BootstrapEntrypointDeclaration
                { consumer = "Consumer", provider = "Candidate", typeName = "Fixture.Payload", method = "Fixture.Host::Run", target = "Candidate", reason = "All raw operations are intended" } };
                StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate(null).ToString());
                fixture.Set.Get("Consumer").isBootstrap = false;
                fixture.Policy.dependencies.bootstrapEntrypoints = new BootstrapEntrypointDeclaration[0];
                fixture.Policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency
                { consumer = "Consumer", provider = "Candidate", callSite = "Fixture.Host::Run", kind = "RawTypeAdmission", evidence = "Method prose is not a typed proof" } };
                StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate(null).ToString());
            }
        }

        [Test] public void ActualDescriptorRolesCannotBeInferredFromConfigurationOrResolverPresence()
        {
            foreach (var classification in new[] { AssemblyClassification.Reference, AssemblyClassification.NormalHotUpdate, AssemblyClassification.BuildFiltered, AssemblyClassification.EditorOnly, AssemblyClassification.TestOnly })
            using (var fixture = new Fixture())
            {
                fixture.Set.Get("Candidate").classification = classification;
                StringAssert.Contains("InvalidRawAdmissionProviderRole", fixture.Validate(fixture.Configuration).ToString(), classification.ToString());
            }
            using (var fixture = new Fixture())
            { fixture.Set.Get("Candidate").isBootstrap = true; StringAssert.Contains("InvalidRawAdmissionProviderRole", fixture.Validate(fixture.Configuration).ToString()); }
            using (var fixture = new Fixture())
            { fixture.Set.Get("Candidate").isShadowCapable = false; StringAssert.Contains("InvalidRawAdmissionProviderRole", fixture.Validate(fixture.Configuration).ToString()); }
            using (var fixture = new Fixture())
            { fixture.Set.Get("Consumer").classification = AssemblyClassification.BuildFiltered; StringAssert.Contains("InvalidRawAdmissionConsumerRole", fixture.Validate(fixture.Configuration).ToString()); }
        }

        [Test] public void NonBootstrapConsumersStillNeedTruthfulDependencyEdges()
        {
            using (var fixture = new Fixture())
            {
                fixture.Set.Get("Consumer").isBootstrap = false;
                StringAssert.Contains("UndeclaredReflectionDependency", fixture.Validate(fixture.Configuration).ToString());
                fixture.Policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency
                { consumer = "Consumer", provider = "Candidate", kind = "raw type query", evidence = "The compiled exact helper acquires Candidate definitions" } };
                Assert.IsTrue(fixture.Validate(fixture.Configuration).IsValid, fixture.Validate(fixture.Configuration).ToString());
            }
        }

        [Test] public void UnconfiguredSiblingEnumerationAndIndirectLookupRemainRejected()
        {
            using (var fixture = new Fixture(true))
            {
                var result = fixture.Validate(fixture.Configuration);
                StringAssert.Contains("UnboundedManagedAcquisition", result.ToString());
                StringAssert.Contains("Other", result.ToString());
            }
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetTempPath(), "RawTypeAdmissionPolicy-" + Guid.NewGuid().ToString("N"));
            internal CompiledAssemblySet Set;
            internal readonly ShadowPolicyConfiguration Policy = new ShadowPolicyConfiguration();
            internal RawTypeAdmissionConfiguration Configuration;
            internal Fixture(bool extra = false)
            {
                string assemblies = Path.Combine(root, "Assemblies"), references = Path.Combine(root, "References");
                Directory.CreateDirectory(assemblies); Directory.CreateDirectory(references);
                File.Copy(typeof(object).Assembly.Location, Path.Combine(references, "mscorlib.dll"));
                using (var provider = Module("Candidate"))
                { provider.Types.Add(new TypeDefUser("Fixture", "Payload", provider.CorLibTypes.Object.TypeDefOrRef)); provider.Write(Path.Combine(assemblies, "Candidate.dll")); }
                using (var consumer = Module("Consumer"))
                {
                    var host = new TypeDefUser("Fixture", "Host", consumer.CorLibTypes.Object.TypeDefOrRef); consumer.Types.Add(host);
                    AddMethod(consumer, host, "Run", false); if (extra) AddMethod(consumer, host, "Other", true);
                    consumer.Write(Path.Combine(assemblies, "Consumer.dll"));
                }
                Set = DnlibAssemblyLoader.Load(assemblies, new[] { references }, new[] {
                    new AssemblyCapability { name = "Consumer", isBootstrap = true }, new AssemblyCapability { name = "Candidate", isShadowCapable = true } });
                var method = Set.GetModule("Consumer").GetTypes().SelectMany(type => type.Methods).Single(value => value.Name == "Run");
                Configuration = new RawTypeAdmissionConfiguration { sites = new[] { new RawTypeAdmissionSite
                { id = "raw", consumerAssembly = "Consumer", declaringType = "Fixture.Host", methodSignature = method.FullName,
                    methodHash = ReflectionBindingFingerprint.Compute(method), operationIndex = 2,
                    operationSignature = ((IMethod)method.Body.Instructions[2].Operand).FullName,
                    providerAssemblyIdentity = Set.GetModule("Candidate").Assembly.FullName, typeName = "", throwOnError = false, ignoreCase = false,
                    reason = "An exact raw query is observed; native state remains independently enforced." } } };
            }
            internal ShadowPolicyValidationResult Validate(RawTypeAdmissionConfiguration configuration)
            { return ShadowAssemblyPolicyValidator.ValidateCompiled(Set, Policy, DateTime.UtcNow, rawTypeAdmissionConfiguration: configuration); }
            private static void AddMethod(ModuleDefUser module, TypeDef host, string name, bool indirect)
            {
                var importer = new Importer(module); var method = new MethodDefUser(name, MethodSig.CreateStatic(importer.Import(typeof(Type[])).ToTypeSig()),
                    dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() }; host.Methods.Add(method);
                var il = method.Body.Instructions; il.Add(Instruction.Create(OpCodes.Ldstr, "Candidate"));
                il.Add(Instruction.Create(OpCodes.Call, importer.Import(typeof(Assembly).GetMethod("Load", new[] { typeof(string) }))));
                il.Add(Instruction.Create(indirect ? OpCodes.Ldvirtftn : OpCodes.Callvirt, importer.Import(typeof(Assembly).GetMethod("GetTypes", Type.EmptyTypes))));
                if (indirect) { il.Add(Instruction.Create(OpCodes.Pop)); il.Add(Instruction.Create(OpCodes.Ldnull)); }
                il.Add(Instruction.Create(OpCodes.Ret));
            }
            private static ModuleDefUser Module(string name)
            {
                var module = new ModuleDefUser(name + ".dll", Guid.NewGuid(), new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.FullName))) { Kind = ModuleKind.Dll };
                new AssemblyDefUser(name, new Version(1, 0, 0, 0)).Modules.Add(module); return module;
            }
            public void Dispose() { if (Set != null) Set.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
