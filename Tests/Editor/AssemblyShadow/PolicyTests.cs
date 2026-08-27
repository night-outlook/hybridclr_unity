using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class PolicyTests
    {
        [Test]
        public void ExternalInternalReferenceIsRejected()
        {
            var result = ShadowAssemblyPolicyValidator.ValidateDefinitions(new[]
            {
                Definition("AssemblyA.Implementation.Internal", AssemblyClassification.Runtime, true, true),
                Definition("Consumer", AssemblyClassification.Runtime, true, true, "AssemblyA.Implementation.Internal"),
            }, Policy("AssemblyA.Implementation.Internal"), DateTime.UtcNow);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ToString(), Does.Contain("InternalDependency"));
        }

        [Test]
        public void ExpiredExtensibilityApprovalIsRejected()
        {
            var policy = Policy("AssemblyA.Implementation.Extensibility");
            policy.extensibilityWhitelist.entries = new[] { new ExtensibilityWhitelistEntry
            {
                provider = "AssemblyA.Implementation.Extensibility", consumer = "Consumer", owner = "team",
                reason = "test", reviewer = "reviewer", expires = "2020-01-01"
            } };
            var result = ShadowAssemblyPolicyValidator.ValidateDefinitions(new[]
            {
                Definition("AssemblyA.Implementation.Extensibility", AssemblyClassification.Runtime, true, true),
                Definition("Consumer", AssemblyClassification.Runtime, true, true, "AssemblyA.Implementation.Extensibility"),
            }, policy, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ToString(), Does.Contain("ExtensibilityWhitelistExpired"));
        }

        [Test]
        public void BootstrapReflectionRequiresApprovedEntrypoint()
        {
            var policy = Policy("AssemblyA.Contracts");
            policy.dependencies.bootstrapEntrypoints = new[] { new BootstrapEntrypointDeclaration
            {
                consumer = "Bootstrap", provider = "AssemblyA.Contracts", typeName = "AssemblyA.Contracts.Entry", method = "Bootstrap::Run", reason = "test"
            } };
            var bootstrap = Definition("Bootstrap", AssemblyClassification.Runtime, true, false);
            bootstrap.isBootstrap = true;
            bootstrap.reflectionReferences = new[] { "Bootstrap::Run|AssemblyA.Contracts.Entry" };
            var result = ShadowAssemblyPolicyValidator.ValidateDefinitions(new[]
            {
                Definition("AssemblyA.Contracts", AssemblyClassification.Runtime, true, true), bootstrap,
            }, policy, DateTime.UtcNow);
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void UnknownReflectionIsFailClosed()
        {
            var consumer = Definition("Consumer", AssemblyClassification.Runtime, true, true);
            consumer.unknownReflectionDependencies = true;
            var result = ShadowAssemblyPolicyValidator.ValidateDefinitions(new[] { consumer }, new ShadowPolicyConfiguration(), DateTime.UtcNow);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ToString(), Does.Contain("UnknownReflectionDependency"));
        }

        [Test]
        public void BootstrapApprovalMustMatchFullCallsiteAndExactTarget()
        {
            var policy = Policy("AssemblyA.Contracts");
            policy.dependencies.bootstrapEntrypoints = new[] { new BootstrapEntrypointDeclaration
            {
                consumer = "Bootstrap", provider = "AssemblyA.Contracts", typeName = "Entry", method = "Bootstrap::Run", reason = "test"
            } };
            var bootstrap = Definition("Bootstrap", AssemblyClassification.Runtime, true, false);
            bootstrap.isBootstrap = true;
            bootstrap.reflectionReferences = new[] { "Bootstrap::Other|AssemblyA.Contracts.Entry" };
            var result = ShadowAssemblyPolicyValidator.ValidateDefinitions(new[]
            {
                Definition("AssemblyA.Contracts", AssemblyClassification.Runtime, true, true), bootstrap,
            }, policy, DateTime.UtcNow);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ToString(), Does.Contain("BootstrapReflection"));
        }

        [Test]
        public void BootstrapApprovalRejectsBlankFields()
        {
            var policy = Policy("AssemblyA.Contracts");
            policy.dependencies.bootstrapEntrypoints = new[] { new BootstrapEntrypointDeclaration
            {
                consumer = "Bootstrap", provider = "AssemblyA.Contracts", typeName = "", method = "Bootstrap::Run", reason = "test"
            } };
            var bootstrap = Definition("Bootstrap", AssemblyClassification.Runtime, true, false);
            bootstrap.isBootstrap = true;
            bootstrap.reflectionReferences = new[] { "Bootstrap::Run|AssemblyA.Contracts.Entry" };
            var result = ShadowAssemblyPolicyValidator.ValidateDefinitions(new[]
            {
                Definition("AssemblyA.Contracts", AssemblyClassification.Runtime, true, true), bootstrap,
            }, policy, DateTime.UtcNow);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ToString(), Does.Contain("BootstrapReflection"));
        }

        [Test]
        public void TypeOverloadWithoutStringEvidenceIsNotReflectionDependency()
        {
            var definition = Definition("Bootstrap", AssemblyClassification.Runtime, true, false);
            definition.isBootstrap = true;
            var result = ShadowAssemblyPolicyValidator.ValidateDefinitions(new[] { definition },
                new ShadowPolicyConfiguration(), DateTime.UtcNow);
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void CompilerInventoryPreservesRuntimeAndReferenceRoles()
        {
            var settings = (AssemblyShadowSettings)FormatterServices.GetUninitializedObject(typeof(AssemblyShadowSettings));
            settings.shadowAssemblyNames = new[] { "Business" };
            settings.bootstrapAssemblyNames = new[] { "Bootstrap" };
            settings.precompiledAssemblyCapabilities = new[] { new AssemblyCapability { name = "Plugin", isShadowCapable = false } };
            var inventory = new[]
            {
                new AssemblyCapability { name = "Business" }, new AssemblyCapability { name = "Bootstrap" },
                new AssemblyCapability { name = "OrdinaryAot" }, new AssemblyCapability { name = "OrdinaryHotUpdate" },
                new AssemblyCapability { name = "Plugin", isPrecompiled = true },
                new AssemblyCapability { name = "System.Runtime", classification = AssemblyClassification.Reference },
            };
            MethodInfo build = typeof(AssemblyShadowSettingsUtil).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(method => method.Name == "BuildCapabilities" && method.GetParameters().Length == 3);
            var capabilities = ((AssemblyCapability[])build.Invoke(null, new object[] { settings, inventory, new[] { "OrdinaryHotUpdate" } }))
                .ToDictionary(item => item.name);
            Assert.That(capabilities["OrdinaryAot"].classification, Is.EqualTo(AssemblyClassification.Runtime));
            Assert.That(capabilities["OrdinaryAot"].isShadowCapable, Is.False);
            Assert.That(capabilities["OrdinaryHotUpdate"].classification, Is.EqualTo(AssemblyClassification.NormalHotUpdate));
            Assert.That(capabilities["System.Runtime"].classification, Is.EqualTo(AssemblyClassification.Reference));
            Assert.That(capabilities["System.Runtime"].isBootstrap, Is.False);
            Assert.That(capabilities["Plugin"].classification, Is.EqualTo(AssemblyClassification.Runtime));
            Assert.That(capabilities["Plugin"].capabilityDeclared, Is.True);
            Assert.That(capabilities["Plugin"].isShadowCapable, Is.False);
        }

        [Test]
        public void RuntimePluginRequiresExplicitCapabilityEvenWhenNotShadowCapable()
        {
            var plugin = Definition("Plugin", AssemblyClassification.Runtime, true, false);
            plugin.isPrecompiled = true; plugin.capabilityDeclared = false;
            Assert.That(Validate(plugin).ToString(), Does.Contain("UndeclaredPrecompiledCapability"));
            plugin.capabilityDeclared = true;
            Assert.That(Validate(plugin).IsValid, Is.True);
        }

        [Test]
        public void CompilerProductionSetSeparatesTestPluginsWithoutNamingExceptions()
        {
            var runtime = CompilerAssembly("RuntimeConsumer", "Shared.dll", "nunit.framework.dll");
            var test = CompilerAssembly("ArbitraryDiagnosticAssembly", "Shared.dll", "AssertionCore.dll");
            MethodInfo method = typeof(AssemblyShadowSettingsUtil).GetMethod("BuildCompilerInventory", BindingFlags.Static | BindingFlags.NonPublic);
            var result = ((AssemblyCapability[])method.Invoke(null, new object[]
            { new[] { runtime, test }, new[] { runtime }, new Func<string, bool>(path => false) })).ToDictionary(item => item.name);
            Assert.That(result["RuntimeConsumer"].classification, Is.EqualTo(AssemblyClassification.Runtime));
            Assert.That(result["ArbitraryDiagnosticAssembly"].classification, Is.EqualTo(AssemblyClassification.TestOnly));
            Assert.That(result["AssertionCore"].classification, Is.EqualTo(AssemblyClassification.TestOnly));
            Assert.That(result["AssertionCore"].isPrecompiled, Is.True);
            Assert.That(result["Shared"].classification, Is.EqualTo(AssemblyClassification.Runtime));
            // A real production reference is not exempt merely because its name
            // happens to be a familiar testing library.
            Assert.That(result["nunit.framework"].classification, Is.EqualTo(AssemblyClassification.Runtime));
        }

        [Test]
        public void OrdinaryHotUpdatePluginCanDeclareFalseButCannotBecomeShadow()
        {
            var settings = (AssemblyShadowSettings)FormatterServices.GetUninitializedObject(typeof(AssemblyShadowSettings));
            settings.precompiledAssemblyCapabilities = new[] { new AssemblyCapability { name = "HotPlugin", isShadowCapable = false } };
            var inventory = new[] { new AssemblyCapability { name = "HotPlugin", isPrecompiled = true } };
            MethodInfo build = typeof(AssemblyShadowSettingsUtil).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(method => method.Name == "BuildCapabilities" && method.GetParameters().Length == 3);
            var result = (AssemblyCapability[])build.Invoke(null, new object[] { settings, inventory, new[] { "HotPlugin" } });
            Assert.That(result.Single().classification, Is.EqualTo(AssemblyClassification.NormalHotUpdate));
            Assert.That(result.Single().capabilityDeclared, Is.True);
            Assert.That(result.Single().isShadowCapable, Is.False);
            settings.precompiledAssemblyCapabilities[0].isShadowCapable = true;
            var error = Assert.Throws<TargetInvocationException>(() => build.Invoke(null, new object[] { settings, inventory, new[] { "HotPlugin" } }));
            Assert.That(error.InnerException.Message, Does.Contain("InvalidCapabilityClassification"));
        }

        [Test]
        public void OptionalPackageReferencesUseEffectiveCompilerEdges()
        {
            var errors = new List<string>();
            string[] references = SelectReferences(new[] { "Missing.Optional", "GUID:11111111111111111111111111111111", "Actual.Plugin.dll" },
                new[] { "Actual.Plugin" }, new string[0], false, errors);
            Assert.That(errors, Is.Empty);
            Assert.That(references, Is.EqualTo(new[] { "Actual.Plugin" }));
        }

        [Test]
        public void ProjectOrCandidateSourceRetainsUnknownReferencesAndRejectsUnknownGuids()
        {
            var errors = new List<string>();
            string[] references = SelectReferences(new[] { "Missing.Optional", "GUID:11111111111111111111111111111111" },
                new string[0], new string[0], true, errors);
            Assert.That(references, Is.EqualTo(new[] { "Missing.Optional" }));
            Assert.That(errors.Single(), Does.Contain("Unresolved asmdef GUID"));
        }

        [Test]
        public void OptionalPackageFilteringCannotDiscardKnownCandidateReferences()
        {
            var errors = new List<string>();
            string[] references = SelectReferences(new[] { "Business", "Missing.Optional" }, new[] { "OtherCandidate" },
                new[] { "Business", "OtherCandidate" }, false, errors);
            Assert.That(errors, Is.Empty);
            Assert.That(references, Is.EquivalentTo(new[] { "Business", "OtherCandidate" }));
        }

        [Test]
        public void MalformedPackageGuidRemainsFailClosed()
        {
            var errors = new List<string>();
            SelectReferences(new[] { "GUID:not-a-guid" }, new string[0], new string[0], false, errors);
            Assert.That(errors.Single(), Does.Contain("Malformed asmdef GUID"));
        }

        [Test]
        public void ImportedAssetInventoryDoesNotExpandIntoPhysicalPackageSourceCopies()
        {
            MethodInfo filter = typeof(ShadowAssemblyPolicyValidator).GetMethod("FilterImportedAssetPaths", BindingFlags.Static | BindingFlags.NonPublic);
            var imported = new[] { "Assets/Game.asmdef", "Packages/local/Game.asmdef", "Packages/registry/Runtime.asmdef",
                "Library/PackageCache/pkg/source~/Ignored.asmdef", "Packages/registry/Runtime.asmdef", "Assets/Game.cs" };
            string[] result = (string[])filter.Invoke(null, new object[] { imported, new[] { ".asmdef" } });
            Assert.That(result, Is.EquivalentTo(new[] { "Assets/Game.asmdef", "Packages/local/Game.asmdef", "Packages/registry/Runtime.asmdef" }));
        }

        private static string[] SelectReferences(string[] declared, string[] effective, string[] controlled, bool strict, List<string> errors)
        {
            MethodInfo method = typeof(ShadowAssemblyPolicyValidator).GetMethod("SelectPreflightReferences", BindingFlags.Static | BindingFlags.NonPublic);
            return (string[])method.Invoke(null, new object[] { declared, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                effective, new HashSet<string>(controlled, StringComparer.OrdinalIgnoreCase), strict, errors, "Fixture.asmdef" });
        }

        private static UnityEditor.Compilation.Assembly CompilerAssembly(string name, params string[] references)
        {
            return new UnityEditor.Compilation.Assembly(name, name + ".dll", new string[0], new string[0],
                new UnityEditor.Compilation.Assembly[0], references, UnityEditor.Compilation.AssemblyFlags.None);
        }

        [Test]
        public void FrameworkPrefixCannotHideAnUnresolvedBusinessReference()
        {
            Assert.That(Validate(Definition("Consumer", AssemblyClassification.Runtime, true, false, "System.Business"))
                .ToString(), Does.Contain("UnresolvedRuntimeReference"));
        }

        [Test]
        public void BootstrapMayReferenceStableRuntimeButNotCandidate()
        {
            var bootstrap = Definition("Bootstrap", AssemblyClassification.Runtime, true, false, "HybridCLR.Runtime");
            bootstrap.isBootstrap = true;
            var stable = Definition("HybridCLR.Runtime", AssemblyClassification.Runtime, true, false);
            Assert.That(ShadowAssemblyPolicyValidator.ValidateDefinitions(new[] { bootstrap, stable }, new ShadowPolicyConfiguration(), DateTime.UtcNow).IsValid, Is.True);
            stable.isShadowCapable = true;
            Assert.That(ShadowAssemblyPolicyValidator.ValidateDefinitions(new[] { bootstrap, stable }, new ShadowPolicyConfiguration(), DateTime.UtcNow)
                .ToString(), Does.Contain("BootstrapBusinessReference"));
        }

        [Test]
        public void CompiledReferenceModulesNeedNotBeSnapshotDescriptors()
        {
            var modules = new Dictionary<string, ModuleDefMD>(StringComparer.OrdinalIgnoreCase)
            { { "mscorlib", ModuleDefMD.Load(typeof(object).Assembly.Location) } };
            var descriptors = new Dictionary<string, AssemblyDescriptor>
            { { "Consumer", new AssemblyDescriptor { name = "Consumer", classification = AssemblyClassification.EditorOnly, references = new[] { "mscorlib" } } } };
            var constructor = typeof(CompiledAssemblySet).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            using (var set = (CompiledAssemblySet)constructor.Invoke(new object[] { descriptors, modules, new Resolver(new AssemblyResolver()), new string[0] }))
            {
                var policy = new ShadowPolicyConfiguration { assemblies = new[] { new AssemblyCapability { name = "mscorlib", classification = AssemblyClassification.Reference } } };
                Assert.That(ShadowAssemblyPolicyValidator.ValidateCompiled(set, policy, DateTime.UtcNow).IsValid, Is.True);
            }
        }

        [Test]
        public void TypeGetTypeBooleanOverloadUsesFirstArgumentNotNearbyString()
        {
            var scanned = ScanFixture((module, method) =>
            {
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Business.Entry, Business"));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, TypeGetType(module, true)));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
            });
            Assert.That(scanned.unknownReflectionCallSites, Is.Empty);
            Assert.That(scanned.reflectionDependencies.Single().provider, Is.EqualTo("Business"));
        }

        [Test]
        public void PoppedApprovedStringCannotApproveUnknownArgument()
        {
            var scanned = ScanFixture((module, method) =>
            {
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Business.Entry, Business"));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, TypeGetType(module, false)));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
            });
            Assert.That(scanned.reflectionDependencies, Is.Empty);
            Assert.That(scanned.unknownReflectionCallSites, Is.EquivalentTo(new[] { "Consumer.Probe::Run" }));
        }

        [Test]
        public void ConflictingBranchConstantsAreUnknown()
        {
            var scanned = ScanFixture((module, method) =>
            {
                Instruction alternative = Instruction.Create(OpCodes.Ldstr, "Business.Other, Business");
                Instruction call = Instruction.Create(OpCodes.Call, TypeGetType(module, false));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Brtrue, alternative));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Business.Entry, Business"));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Br, call));
                method.Body.Instructions.Add(alternative); method.Body.Instructions.Add(call);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
            });
            Assert.That(scanned.reflectionDependencies, Is.Empty);
            Assert.That(scanned.unknownReflectionCallSites, Is.Not.Empty);
        }

        [Test]
        public void LocalConstantRetainsItsActualReflectionTarget()
        {
            var scanned = ScanFixture((module, method) =>
            {
                method.Body.Variables.Add(new Local(module.CorLibTypes.String));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Business.Entry, Business"));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Stloc_0));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "unrelated"));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldloc_0));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, TypeGetType(module, false)));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
            });
            Assert.That(scanned.unknownReflectionCallSites, Is.Empty);
            Assert.That(scanned.reflectionDependencies.Single().target, Is.EqualTo("Business.Entry, Business"));
        }

        [Test]
        public void AssemblyLoadByteArrayIsNotAStringDependency()
        {
            var scanned = ScanFixture((module, method) =>
            {
                var assembly = new TypeRefUser(module, "System.Reflection", "Assembly", module.CorLibTypes.AssemblyRef);
                var load = new MemberRefUser(module, "Load", MethodSig.CreateStatic(module.CorLibTypes.Object, new SZArraySig(module.CorLibTypes.Byte)), assembly);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, load));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
            });
            Assert.That(scanned.unknownReflectionCallSites, Is.Empty);
            Assert.That(scanned.reflectionDependencies, Is.Empty);
        }

        [Test]
        public void AssemblyGetTypeTracksTheLoadedAssemblyReceiver()
        {
            var scanned = ScanFixture((module, method) =>
            {
                var assembly = new TypeRefUser(module, "System.Reflection", "Assembly", module.CorLibTypes.AssemblyRef);
                var load = new MemberRefUser(module, "Load", MethodSig.CreateStatic(new ClassSig(assembly), module.CorLibTypes.String), assembly);
                var get = new MemberRefUser(module, "GetType", MethodSig.CreateInstance(module.CorLibTypes.Object, module.CorLibTypes.String, module.CorLibTypes.Boolean), assembly);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Business"));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, load));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Business.Entry"));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, get));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
            });
            Assert.That(scanned.unknownReflectionCallSites, Is.Empty);
            Assert.That(scanned.reflectionDependencies.Select(item => item.kind), Is.EquivalentTo(new[] { "Assembly.Load", "Assembly.GetType" }));
            Assert.That(scanned.reflectionDependencies.Single(item => item.kind == "Assembly.GetType").typeName, Is.EqualTo("Business.Entry"));
        }

        [Test]
        public void TypeofCreatesAStaticDependencyThatBootstrapApprovalCannotBypass()
        {
            var scanned = ScanFixture((module, method) =>
            {
                var business = new TypeRefUser(module, "Business", "Entry", new AssemblyRefUser("Business"));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldtoken, business));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
            });
            scanned.isBootstrap = true;
            Assert.That(Validate(scanned).ToString(), Does.Contain("BootstrapBusinessReference"));
        }

        [Test]
        public void NonShadowAotConsumerStillRejectsUnknownReflection()
        {
            var consumer = Definition("OrdinaryAot", AssemblyClassification.Runtime, true, false);
            consumer.unknownReflectionCallSites = new[] { "Ordinary.Probe::Run" };
            Assert.That(Validate(consumer).ToString(), Does.Contain("UnknownReflectionDependency"));
        }

        [Test]
        public void BoundedSerializeReferenceDeclarationDoesNotApproveOtherCallsites()
        {
            var consumer = Definition("Consumer", AssemblyClassification.Runtime, true, false);
            consumer.unknownReflectionCallSites = new[] { "Consumer.Probe::field" };
            var policy = Policy("Business");
            policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency
            { consumer = "Consumer", provider = "Business", kind = "SerializeReference", evidence = "Concrete type set is Business.Entry", callSite = "Consumer.Probe::field" } };
            Assert.That(Validate(consumer, policy).IsValid, Is.True);
            consumer.unknownReflectionCallSites = new[] { "Consumer.Probe::Other" };
            Assert.That(Validate(consumer, policy).ToString(), Does.Contain("UnknownReflectionDependency"));
        }

        [Test]
        public void BootstrapApprovalMustMatchResolvedProviderAndType()
        {
            var bootstrap = Definition("Bootstrap", AssemblyClassification.Runtime, true, false);
            bootstrap.isBootstrap = true;
            bootstrap.reflectionDependencies = new[] { new ReflectionDependencyEvidence
            { callSite = "Bootstrap::Run", target = "Entry", provider = "Business", typeName = "Business.Other", kind = "GetComponent" } };
            var policy = Policy("Business");
            policy.dependencies.bootstrapEntrypoints = new[] { new BootstrapEntrypointDeclaration
            { consumer = "Bootstrap", provider = "Business", typeName = "Business.Entry", callSite = "Bootstrap::Run", target = "Entry", reason = "approved exact entry" } };
            Assert.That(Validate(bootstrap, policy).ToString(), Does.Contain("BootstrapReflection"));
            bootstrap.reflectionDependencies[0].typeName = "Business.Entry";
            Assert.That(Validate(bootstrap, policy).IsValid, Is.True);
        }

        [Test]
        public void SerializeReferenceFieldIsScannedAsAnExplicitUnknownSite()
        {
            var scanned = ScanFixture((module, method) =>
            {
                var attribute = new TypeRefUser(module, "UnityEngine", "SerializeReference", new AssemblyRefUser("UnityEngine.CoreModule"));
                var constructor = new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void), attribute);
                var field = new FieldDefUser("state", new FieldSig(module.CorLibTypes.Object));
                field.CustomAttributes.Add(new CustomAttribute(constructor)); method.DeclaringType.Fields.Add(field);
            });
            Assert.That(scanned.unknownReflectionCallSites, Is.EquivalentTo(new[] { "Consumer.Probe::state" }));
        }

        [Test]
        public void UnknownAssemblyGetTypeReceiverCannotBeApprovedByAConstantTypeName()
        {
            var scanned = ScanFixture((module, method) =>
            {
                var assembly = new TypeRefUser(module, "System.Reflection", "Assembly", module.CorLibTypes.AssemblyRef);
                var get = new MemberRefUser(module, "GetType", MethodSig.CreateInstance(module.CorLibTypes.Object, module.CorLibTypes.String), assembly);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Business.Entry"));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, get));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
            });
            Assert.That(scanned.reflectionDependencies, Is.Empty);
            Assert.That(scanned.unknownReflectionCallSites, Is.Not.Empty);
        }

        [Test]
        public void RuntimePluginCannotExistOnlyAsACompilerReference()
        {
            var plugin = Definition("Plugin", AssemblyClassification.Runtime, false, false);
            plugin.isPrecompiled = true;
            Assert.That(Validate(plugin).ToString(), Does.Contain("RuntimeAssemblyMissingFromSnapshot"));
        }

        [Test]
        public void CandidateCannotBeEditorTestReferenceOrOrdinaryHotUpdate()
        {
            foreach (AssemblyClassification classification in new[] { AssemblyClassification.EditorOnly, AssemblyClassification.TestOnly,
                AssemblyClassification.Reference, AssemblyClassification.NormalHotUpdate })
                Assert.That(Validate(Definition("Consumer", classification, true, true)).ToString(), Does.Contain("InvalidCandidateClassification"));
        }

        [Test]
        public void ExplicitSelfDependencyIsRejected()
        {
            var policy = new ShadowPolicyConfiguration();
            policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency { consumer = "Consumer", provider = "Consumer", kind = "Reflection", evidence = "invalid self edge" } };
            Assert.That(Validate(Definition("Consumer", AssemblyClassification.Runtime, true, false), policy).ToString(), Does.Contain("SelfDependency"));
        }

        [Test]
        public void FrozenPlayerFilterPolicySeparatesAuxiliariesAndOrdinaryHotUpdates()
        {
            var policy = FilterSourcePolicy();
            var receipt = FilterReceipt(policy);
            ShadowPolicyConfiguration derived = ShadowFilteredInputPolicy.Apply(policy, receipt);
            Assert.That(derived.assemblies.Single(item => item.name == "Auxiliary").classification, Is.EqualTo(AssemblyClassification.BuildFiltered));
            Assert.That(derived.assemblies.Single(item => item.name == "Hot").classification, Is.EqualTo(AssemblyClassification.NormalHotUpdate));
            Assert.That(derived.assemblies.Single(item => item.name == "Business").classification, Is.EqualTo(AssemblyClassification.Runtime));
            Assert.That(policy.assemblies.Single(item => item.name == "Auxiliary").classification, Is.EqualTo(AssemblyClassification.Runtime));
            derived.dependencies.runtimeDependencies[0].evidence = "modified clone";
            Assert.That(policy.dependencies.runtimeDependencies[0].evidence, Is.EqualTo("original"));
        }

        [Test]
        public void FilterPolicyClonePreservesReflectionBindingSchemaFieldsAndNulls()
        {
            var populated = new ShadowReflectionBindingDeclaration
            {
                id = "binding-1", consumer = "Consumer", typeName = "Example.Type", methodSignature = "Run()",
                originalMethodHash = ShadowHash.Text("method"), operationIndex = 7, allowedTypes = new[] { "Example.Allowed" },
                providers = new[] { "Provider" }, reason = "fixture", kind = "FixedAssemblyBytes", imageSha256 = ShadowHash.Text("image"),
                providerAssemblyIdentity = "Provider, Version=1.0.0.0", imagePath = "Assets/Provider.dll",
            };
            var schema1 = new ShadowReflectionBindingDeclaration
            {
                id = "binding-legacy", consumer = "LegacyConsumer", typeName = "Legacy.Type", methodSignature = "Run()",
                originalMethodHash = ShadowHash.Text("legacy-method"), operationIndex = 0, allowedTypes = null, providers = new string[0],
                reason = "legacy", kind = null, imageSha256 = null, providerAssemblyIdentity = null, imagePath = null,
            };
            var policy = FilterSourcePolicy();
            policy.reflectionBindings = new[] { populated, schema1 };
            var receipt = FilterReceipt(policy);

            var derived = ShadowFilteredInputPolicy.Apply(policy, receipt);
            var derivedPatch = ShadowFilteredInputPolicy.ApplyPatch(policy, receipt, new[] { "Business" }, new string[0]);
            foreach (ShadowPolicyConfiguration clone in new[] { derived, derivedPatch })
            {
                Assert.That(clone.reflectionBindings, Is.Not.SameAs(policy.reflectionBindings));
                Assert.That(clone.reflectionBindings[0], Is.Not.SameAs(populated));
                Assert.That(clone.reflectionBindings[0].id, Is.EqualTo(populated.id));
                Assert.That(clone.reflectionBindings[0].consumer, Is.EqualTo(populated.consumer));
                Assert.That(clone.reflectionBindings[0].typeName, Is.EqualTo(populated.typeName));
                Assert.That(clone.reflectionBindings[0].methodSignature, Is.EqualTo(populated.methodSignature));
                Assert.That(clone.reflectionBindings[0].originalMethodHash, Is.EqualTo(populated.originalMethodHash));
                Assert.That(clone.reflectionBindings[0].operationIndex, Is.EqualTo(populated.operationIndex));
                Assert.That(clone.reflectionBindings[0].allowedTypes, Is.Not.SameAs(populated.allowedTypes));
                Assert.That(clone.reflectionBindings[0].allowedTypes, Is.EqualTo(populated.allowedTypes));
                Assert.That(clone.reflectionBindings[0].providers, Is.Not.SameAs(populated.providers));
                Assert.That(clone.reflectionBindings[0].providers, Is.EqualTo(populated.providers));
                Assert.That(clone.reflectionBindings[0].reason, Is.EqualTo(populated.reason));
                Assert.That(clone.reflectionBindings[0].kind, Is.EqualTo(populated.kind));
                Assert.That(clone.reflectionBindings[0].imageSha256, Is.EqualTo(populated.imageSha256));
                Assert.That(clone.reflectionBindings[0].providerAssemblyIdentity, Is.EqualTo(populated.providerAssemblyIdentity));
                Assert.That(clone.reflectionBindings[0].imagePath, Is.EqualTo(populated.imagePath));
                Assert.That(clone.reflectionBindings[1].allowedTypes, Is.Null);
                Assert.That(clone.reflectionBindings[1].providers, Is.Not.Null);
                Assert.That(clone.reflectionBindings[1].providers, Is.Empty);
                Assert.That(clone.reflectionBindings[1].kind, Is.Null);
                Assert.That(clone.reflectionBindings[1].imageSha256, Is.Null);
                Assert.That(clone.reflectionBindings[1].providerAssemblyIdentity, Is.Null);
                Assert.That(clone.reflectionBindings[1].imagePath, Is.Null);
            }

            derived.reflectionBindings[0].allowedTypes[0] = "changed";
            derived.reflectionBindings[0].providers[0] = "changed";
            Assert.That(populated.allowedTypes[0], Is.EqualTo("Example.Allowed"));
            Assert.That(populated.providers[0], Is.EqualTo("Provider"));
            Assert.That(derivedPatch.reflectionBindings[0].allowedTypes[0], Is.EqualTo("Example.Allowed"));
            Assert.That(derivedPatch.reflectionBindings[0].providers[0], Is.EqualTo("Provider"));
        }

        [Test]
        public void FilterPolicyRejectsUnsealedMissingAndModifiedEvidence()
        {
            var policy = FilterSourcePolicy();
            var receipt = FilterReceipt(policy);
            receipt.playerBuildSucceeded = false;
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.Apply(policy, receipt)).Code, Is.EqualTo("FilterEvidenceMissing"));
            receipt = FilterReceipt(policy); receipt.playerBuildFilterCaptured = false;
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.Apply(policy, receipt)).Code, Is.EqualTo("FilterEvidenceMissing"));
            receipt = FilterReceipt(policy); receipt.playerBuildOptions++;
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.Apply(policy, receipt)).Code, Is.EqualTo("FilterEvidenceHashMismatch"));
            receipt = FilterReceipt(policy); receipt.filteredAssemblies[0].sha256 = "altered";
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.Apply(policy, receipt)).Code, Is.EqualTo("FilterEvidenceHashMismatch"));
        }

        [Test]
        public void FilterPolicyRejectsCandidatePromotionAndChangedSourceRoles()
        {
            var policy = FilterSourcePolicy(); var receipt = FilterReceipt(policy);
            policy.assemblies.Single(item => item.name == "Auxiliary").isShadowCapable = true;
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.Apply(policy, receipt)).Code, Is.EqualTo("FilteredCandidatePromotion"));
            policy = FilterSourcePolicy(); receipt = FilterReceipt(policy);
            policy.assemblies.Single(item => item.name == "Auxiliary").classification = AssemblyClassification.Reference;
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.Apply(policy, receipt)).Code, Is.EqualTo("FilteredRoleChanged"));
            policy = FilterSourcePolicy(); receipt = FilterReceipt(policy);
            policy.assemblies.Single(item => item.name == "Hot").classification = AssemblyClassification.Runtime;
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.Apply(policy, receipt)).Code, Is.EqualTo("HotUpdateRoleChanged"));
        }

        [Test]
        public void LinkedExclusionsPreserveInputsAndRetainedRuntimeRoles()
        {
            var policy = FilterSourcePolicy(); var receipt = FilterReceipt(policy);
            receipt.assemblies = new[] { FilterFile("Business"), FilterFile("Auxiliary") };
            receipt.filteredAssemblies = receipt.filteredAssemblies.Where(file => file.name == "Hot").ToArray();
            receipt.linkerExcludedAssemblies = new[] { "Auxiliary" };
            receipt.linkerExcludedAssemblyCapabilities = receipt.filteredAssemblyCapabilities.Where(role => role.name == "Auxiliary").ToArray();
            receipt.filteredAssemblyCapabilities = receipt.filteredAssemblyCapabilities.Where(role => role.name == "Hot").ToArray();
            receipt.snapshotHash = AssemblySnapshot.ComputeHash(receipt);
            var result = ShadowFilteredInputPolicy.Apply(policy, receipt);
            Assert.That(result.assemblies.Single(role => role.name == "Auxiliary").classification, Is.EqualTo(AssemblyClassification.BuildFiltered));
            Assert.That(result.assemblies.Single(role => role.name == "Business").classification, Is.EqualTo(AssemblyClassification.Runtime));
            Assert.That(result.assemblies.Single(role => role.name == "Hot").classification, Is.EqualTo(AssemblyClassification.NormalHotUpdate));
            Assert.That(receipt.assemblies.Select(file => file.name), Is.EquivalentTo(new[] { "Business", "Auxiliary" }));
            Assert.That(policy.assemblies.Single(role => role.name == "Auxiliary").classification, Is.EqualTo(AssemblyClassification.Runtime));
        }

        [Test]
        public void LinkedPolicyRejectsMissingStaleAndUnprovedExclusions()
        {
            var policy = FilterSourcePolicy(); var receipt = FilterReceipt(policy);
            receipt.linkedPlayerReceipt = null;
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.Apply(policy, receipt)).Code, Is.EqualTo("LinkedEvidenceMissing"));
            receipt = FilterReceipt(policy); receipt.buildGuid = "different-build";
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.Apply(policy, receipt)).Code, Is.EqualTo("LinkedBuildIdentityMismatch"));
            receipt = FilterReceipt(policy); receipt.linkerExcludedAssemblies = new[] { "Business" };
            receipt.snapshotHash = AssemblySnapshot.ComputeHash(receipt);
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.Apply(policy, receipt)).Code, Is.EqualTo("LinkerExclusionSetMismatch"));
            receipt = FilterReceipt(policy); receipt.linkedPlayerReceipt.protectedAssemblies = new[] { "MissingCandidate" };
            receipt.linkedPlayerReceiptHash = ShadowLinkedPlayerEvidence.ComputeHash(receipt.linkedPlayerReceipt);
            receipt.snapshotHash = AssemblySnapshot.ComputeHash(receipt);
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.Apply(policy, receipt)).Code, Is.EqualTo("LinkedCandidateMissing"));
        }

        [Test]
        public void PatchPolicyAllowsCandidateDemotionSoGraphReportsTheFullConsumerPath()
        {
            var policy = FilterSourcePolicy();
            policy.assemblies = policy.assemblies.Concat(new[] { new AssemblyCapability { name = "BusinessConsumer",
                classification = AssemblyClassification.Runtime, isShadowCapable = true, capabilityDeclared = true } }).ToArray();
            var receipt = FilterReceipt(policy);
            receipt.assemblies = new[] { FilterFile("Business"), FilterFile("BusinessConsumer") };
            receipt.filteredAssemblyCapabilities = receipt.filteredAssemblyCapabilities.Where(item => item.name != "BusinessConsumer").ToArray();
            receipt.linkedPlayerReceipt.protectedAssemblies = new[] { "business", "businessconsumer" };
            receipt.linkedPlayerReceipt.assemblies = receipt.linkedPlayerReceipt.assemblies.Concat(new[] { new LinkedPlayerFile
            {
                name = "businessconsumer", path = "Assemblies/businessconsumer.dll", sha256 = ShadowHash.Text("stripped-businessconsumer"),
                mvid = "7c6c0b71-2f0a-4f65-9a7f-78f06fb2f846"
            } }).ToArray();
            receipt.linkedPlayerReceiptHash = ShadowLinkedPlayerEvidence.ComputeHash(receipt.linkedPlayerReceipt);
            receipt.snapshotHash = AssemblySnapshot.ComputeHash(receipt);
            policy.assemblies.Single(item => item.name == "BusinessConsumer").isShadowCapable = false;

            var derived = ShadowFilteredInputPolicy.ApplyPatch(policy, receipt, new[] { "Business", "BusinessConsumer" }, new string[0]);
            var descriptors = derived.assemblies.Select(item => new AssemblyDescriptor { name = item.name,
                references = item.name == "BusinessConsumer" ? new[] { "Business" } : new string[0], classification = item.classification,
                isShadowCapable = item.isShadowCapable, isBootstrap = item.isBootstrap, isPrecompiled = item.isPrecompiled,
                capabilityDeclared = item.capabilityDeclared }).ToArray();
            var error = Assert.Throws<ShadowBuildException>(() => new AssemblyReferenceGraph(descriptors).ReverseClosure(new[] { "Business" }));
            Assert.That(error.Code, Is.EqualTo("NonShadowConsumer"));
            StringAssert.Contains("BusinessConsumer -> Business", error.Message);
        }

        [Test]
        public void PatchPolicyRejectsPromotionOmissionBootstrapChangeAndRoleChange()
        {
            var policy = FilterSourcePolicy();
            var receipt = FilterReceipt(policy);
            policy.assemblies.Single(item => item.name == "Auxiliary").isShadowCapable = true;
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.ApplyPatch(policy, receipt, new[] { "Business" }, new string[0])).Code,
                Is.EqualTo("FilteredCandidatePromotion"));

            policy = FilterSourcePolicy(); receipt = FilterReceipt(policy);
            policy.assemblies = policy.assemblies.Where(item => item.name != "Business").ToArray();
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.ApplyPatch(policy, receipt, new[] { "Business" }, new string[0])).Code,
                Is.EqualTo("LinkedCandidateMissing"));

            policy = FilterSourcePolicy(); receipt = FilterReceipt(policy);
            policy.assemblies.Single(item => item.name == "Business").isBootstrap = true;
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.ApplyPatch(policy, receipt, new[] { "Business" }, new string[0])).Code,
                Is.EqualTo("LinkedBootstrapSetChanged"));

            policy = FilterSourcePolicy(); receipt = FilterReceipt(policy);
            policy.assemblies.Single(item => item.name == "Business").classification = AssemblyClassification.EditorOnly;
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.ApplyPatch(policy, receipt, new[] { "Business" }, new string[0])).Code,
                Is.EqualTo("LinkedCandidateRoleChanged"));

            policy = FilterSourcePolicy(); receipt = FilterReceipt(policy);
            var bootstrap = policy.assemblies.Single(item => item.name == "Business");
            bootstrap.isShadowCapable = false; bootstrap.isBootstrap = true; bootstrap.classification = AssemblyClassification.EditorOnly;
            Assert.That(Assert.Throws<ShadowBuildException>(() => ShadowFilteredInputPolicy.ApplyPatch(policy, receipt, new string[0], new[] { "Business" })).Code,
                Is.EqualTo("LinkedBootstrapRoleChanged"));
        }

        [Test]
        public void LinkerExcludedOrdinaryHotUpdateRemainsAConsumer()
        {
            var policy = FilterSourcePolicy(); var receipt = FilterReceipt(policy);
            receipt.assemblies = new[] { FilterFile("Business"), FilterFile("Hot") };
            receipt.filteredAssemblies = receipt.filteredAssemblies.Where(file => file.name == "Auxiliary").ToArray();
            receipt.linkerExcludedAssemblies = new[] { "Hot" };
            receipt.linkerExcludedAssemblyCapabilities = receipt.filteredAssemblyCapabilities.Where(role => role.name == "Hot").ToArray();
            receipt.filteredAssemblyCapabilities = receipt.filteredAssemblyCapabilities.Where(role => role.name == "Auxiliary").ToArray();
            receipt.snapshotHash = AssemblySnapshot.ComputeHash(receipt);
            var derived = ShadowFilteredInputPolicy.Apply(policy, receipt);
            Assert.That(derived.assemblies.Single(role => role.name == "Hot").classification, Is.EqualTo(AssemblyClassification.NormalHotUpdate));
            Assert.That(derived.dependencies.runtimeDependencies.Single().consumer, Is.EqualTo("Hot"));
        }

        [Test]
        public void RuntimeAndOrdinaryHotUpdateCannotReferenceBuildFilteredProvider()
        {
            foreach (AssemblyClassification classification in new[] { AssemblyClassification.Runtime, AssemblyClassification.NormalHotUpdate })
            {
                var consumer = Definition("Consumer", classification, true, false, "Auxiliary");
                var filtered = Definition("Auxiliary", AssemblyClassification.BuildFiltered, false, false);
                var result = ShadowAssemblyPolicyValidator.ValidateDefinitions(new[] { consumer, filtered }, new ShadowPolicyConfiguration(), DateTime.UtcNow);
                Assert.That(result.ToString(), Does.Contain("RuntimeReferencesFilteredAssembly"));
                consumer.references = new string[0];
                var policy = new ShadowPolicyConfiguration { dependencies = new ShadowDependencyConfiguration { runtimeDependencies = new[]
                { new DeclaredRuntimeDependency { consumer = "Consumer", provider = "Auxiliary", kind = "Reflection", evidence = "must not bypass" } } } };
                Assert.That(ShadowAssemblyPolicyValidator.ValidateDefinitions(new[] { consumer, filtered }, policy, DateTime.UtcNow).ToString(), Does.Contain("RuntimeReferencesFilteredAssembly"));
            }
        }

        [Test]
        public void FilterTransitionCapturesOnlyRemovedInputs()
        {
            var before = new[] { FilterFile("Business"), FilterFile("Auxiliary"), FilterFile("Hot") };
            SnapshotFile[] removed = FilterTransition(before, new[] { FilterFile("Business") }, new[] { "Business" });
            Assert.That(removed.Select(file => file.name), Is.EquivalentTo(new[] { "Auxiliary", "Hot" }));
        }

        [Test]
        public void FilterTransitionRejectsInjectedChangedAndProtectedInputs()
        {
            var before = new[] { FilterFile("Business"), FilterFile("Auxiliary") };
            var error = Assert.Throws<TargetInvocationException>(() => FilterTransition(before, new[] { FilterFile("Injected") }, new string[0]));
            Assert.That(error.InnerException.Message, Does.Contain("InjectedFilterInput"));
            var changed = FilterFile("Business"); changed.sha256 = "changed";
            error = Assert.Throws<TargetInvocationException>(() => FilterTransition(before, new[] { changed }, new string[0]));
            Assert.That(error.InnerException.Message, Does.Contain("FilterInputChanged"));
            error = Assert.Throws<TargetInvocationException>(() => FilterTransition(before, new[] { FilterFile("Auxiliary") }, new[] { "Business" }));
            Assert.That(error.InnerException.Message, Does.Contain("CandidateFilteredOut"));
        }

        private static SnapshotFile[] FilterTransition(SnapshotFile[] before, SnapshotFile[] after, string[] protectedNames)
        {
            return (SnapshotFile[])typeof(ShadowPlayerInputCapture).GetMethod("ValidateFilterTransition", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { before, after, protectedNames });
        }

        private static SnapshotFile FilterFile(string name)
        {
            return new SnapshotFile { name = name, sourcePath = "/captured/" + name + ".dll", path = "Assemblies/" + name + ".dll", sha256 = ShadowHash.Text(name) };
        }

        private static ShadowPolicyConfiguration FilterSourcePolicy()
        {
            return new ShadowPolicyConfiguration
            {
                assemblies = new[] { new AssemblyCapability { name = "Business", isShadowCapable = true, capabilityDeclared = true },
                    new AssemblyCapability { name = "Auxiliary", isPrecompiled = true, capabilityDeclared = true },
                    new AssemblyCapability { name = "Hot", classification = AssemblyClassification.NormalHotUpdate } },
                dependencies = new ShadowDependencyConfiguration { runtimeDependencies = new[]
                    { new DeclaredRuntimeDependency { consumer = "Hot", provider = "Business", kind = "Reflection", evidence = "original" } } },
            };
        }

        private static AssemblySnapshotReceipt FilterReceipt(ShadowPolicyConfiguration policy)
        {
            var pins = new ShadowSourcePins { unityVersion = "fixture", target = "StandaloneOSX", architecture = "arm64",
                hybridclr = new ShadowRepositoryPin { revision = new string('1', 40) }, il2cppPlus = new ShadowRepositoryPin { revision = new string('2', 40) },
                hybridclrUnity = new ShadowRepositoryPin { revision = new string('3', 40) } };
            var receipt = new AssemblySnapshotReceipt { kind = "PlayerBuildInputs", playerBuildSucceeded = true, playerBuildFilterCaptured = true,
                buildGuid = "fixture", target = "StandaloneOSX", architecture = "arm64", nativeLibrarySha256 = ShadowHash.Text("native"), sourcePins = pins, playerBuildOptions = (int)UnityEditor.BuildOptions.Development,
                assemblies = new[] { FilterFile("Business") }, filteredAssemblies = new[] { FilterFile("Auxiliary"), FilterFile("Hot") },
                references = new SnapshotFile[0], normalHotUpdateAssemblies = new[] { "Hot" },
                filteredAssemblyCapabilities = policy.assemblies.Where(item => item.name != "Business").Select(item => new AssemblyCapability
                { name = item.name, classification = item.classification, isPrecompiled = item.isPrecompiled, capabilityDeclared = item.capabilityDeclared,
                    isShadowCapable = item.isShadowCapable, isBootstrap = item.isBootstrap }).ToArray() };
            foreach (SnapshotFile file in receipt.filteredAssemblies) file.path = "Assemblies/Filtered/" + file.name + ".dll";
            receipt.linkedPlayerReceipt = new LinkedPlayerReceipt { buildGuid = receipt.buildGuid, nativeLibrarySha256 = receipt.nativeLibrarySha256,
                target = receipt.target, architecture = receipt.architecture, sourceDirectory = "/captured/linked", protectedAssemblies = new[] { "business" },
                assemblies = new[] { new LinkedPlayerFile { name = "business", path = "Assemblies/business.dll", sha256 = ShadowHash.Text("stripped-business"),
                    mvid = "bdcce148-2090-47bd-b6e8-3b4e0275ac05" } } };
            receipt.linkedPlayerReceiptHash = ShadowLinkedPlayerEvidence.ComputeHash(receipt.linkedPlayerReceipt);
            receipt.snapshotHash = AssemblySnapshot.ComputeHash(receipt); return receipt;
        }

        private static ShadowPolicyValidationResult Validate(AssemblyPolicyDefinition consumer, ShadowPolicyConfiguration policy = null)
        {
            return ShadowAssemblyPolicyValidator.ValidateDefinitions(new[]
            { consumer, Definition("Business", AssemblyClassification.Runtime, true, true), Definition("mscorlib", AssemblyClassification.Reference, false, false) },
                policy ?? new ShadowPolicyConfiguration(), DateTime.UtcNow);
        }

        private static IMethod TypeGetType(ModuleDefUser module, bool withBoolean)
        {
            var type = new TypeRefUser(module, "System", "Type", module.CorLibTypes.AssemblyRef);
            MethodSig signature = withBoolean ? MethodSig.CreateStatic(module.CorLibTypes.Object, module.CorLibTypes.String, module.CorLibTypes.Boolean) :
                MethodSig.CreateStatic(module.CorLibTypes.Object, module.CorLibTypes.String);
            return new MemberRefUser(module, "GetType", signature, type);
        }

        private static AssemblyPolicyDefinition ScanFixture(Action<ModuleDefUser, MethodDef> emit)
        {
            using (var module = NewModule("Consumer"))
            using (var business = NewModule("Business"))
            {
                business.Types.Add(new TypeDefUser("Business", "Entry", business.CorLibTypes.Object.TypeDefOrRef));
                business.Types.Add(new TypeDefUser("Business", "Other", business.CorLibTypes.Object.TypeDefOrRef));
                var type = new TypeDefUser("Consumer", "Probe", module.CorLibTypes.Object.TypeDefOrRef);
                module.Types.Add(type);
                var method = new MethodDefUser("Run", MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.String),
                    dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() };
                type.Methods.Add(method); emit(module, method); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var definition = Definition("Consumer", AssemblyClassification.Runtime, true, false, "mscorlib");
                using (ModuleDefMD consumerModule = Reload(module))
                using (ModuleDefMD businessModule = Reload(business))
                using (ModuleDefMD core = ModuleDefMD.Load(typeof(object).Assembly.Location))
                {
                    var modules = new Dictionary<string, ModuleDefMD>(StringComparer.OrdinalIgnoreCase)
                    { { "Consumer", consumerModule }, { "Business", businessModule }, { "mscorlib", core } };
                    Type scanner = typeof(ShadowAssemblyPolicyValidator).Assembly.GetType("HybridCLR.Editor.AssemblyShadow.ReflectionDependencyScanner", true);
                    scanner.GetMethod("Scan", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { modules, "Consumer", definition });
                }
                return definition;
            }
        }

        private static ModuleDefUser NewModule(string name)
        {
            var module = new ModuleDefUser(name + ".dll") { Kind = ModuleKind.Dll };
            var assembly = new AssemblyDefUser(name, new Version(1, 0, 0, 0)); assembly.Modules.Add(module); return module;
        }

        private static ModuleDefMD Reload(ModuleDef module)
        {
            using (var stream = new MemoryStream()) { module.Write(stream); return ModuleDefMD.Load(stream.ToArray()); }
        }

        [Test]
        [Category("UnityEditorIntegration")]
        public void StartupSceneBusinessScriptIsRejectedWithoutFilenameConvention()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssemblyShadowPolicy-" + Guid.NewGuid().ToString("N"));
            string assets = Path.Combine(root, "Assets");
            string module = Path.Combine(assets, "Runtime");
            string scene = Path.Combine(assets, "Startup.unity");
            Directory.CreateDirectory(module);
            try
            {
                File.WriteAllText(Path.Combine(module, "Runtime.asmdef"), "{\"name\":\"AssemblyA.Implementation.Internal\"}");
                File.WriteAllText(Path.Combine(module, "Runtime.asmdef.meta"), "guid: 11111111111111111111111111111111\n");
                File.WriteAllText(Path.Combine(module, "Business.cs"), "class Business {}\n");
                File.WriteAllText(Path.Combine(module, "Business.cs.meta"), "guid: 22222222222222222222222222222222\n");
                File.WriteAllText(scene, "--- !u!114 &1\nMonoBehaviour:\n  m_Script: {fileID: 11500000, guid: 22222222222222222222222222222222, type: 3}\n");
                var policy = Policy("AssemblyA.Implementation.Internal");
                var result = new ShadowPolicyValidationResult();
                ShadowAssemblyPolicyValidator.ValidateBootstrapResources(new[] { scene }, root, policy, result);
                Assert.That(result.IsValid, Is.False);
                Assert.That(result.ToString(), Does.Contain("BootstrapResourceBusinessScript"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static AssemblyPolicyDefinition Definition(string name, AssemblyClassification classification,
            bool entersPlayer, bool shadow, params string[] references)
        {
            return new AssemblyPolicyDefinition
            {
                name = name,
                classification = classification,
                entersPlayer = entersPlayer,
                isShadowCapable = shadow,
                capabilityDeclared = true,
                references = references,
            };
        }

        private static ShadowPolicyConfiguration Policy(params string[] names)
        {
            var policy = new ShadowPolicyConfiguration();
            policy.assemblies = new AssemblyCapability[names.Length];
            for (int i = 0; i < names.Length; ++i)
                policy.assemblies[i] = new AssemblyCapability
                {
                    name = names[i], classification = AssemblyClassification.Runtime,
                    isShadowCapable = true, capabilityDeclared = true,
                };
            return policy;
        }
    }
}
