using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class GraphAndInputTests
    {
        private static AssemblyDescriptor A(string name, params string[] references)
        {
            return new AssemblyDescriptor { name = name, references = references, classification = AssemblyClassification.Runtime,
                isShadowCapable = true, capabilityDeclared = true, semanticHash = "unchanged" };
        }

        private static AssemblyDescriptor[] Demo()
        {
            return new[] { A("Contracts"), A("Extensibility", "Contracts"), A("Internal", "Extensibility", "Contracts"),
                A("ContractsConsumer", "Contracts"), A("ExtensibilityConsumer", "Extensibility", "Contracts") };
        }

        [Test] public void InternalRootDoesNotPullProviders()
        {
            CollectionAssert.AreEqual(new[] { "Internal" }, new AssemblyReferenceGraph(Demo()).ReverseClosure(new[] { "INTERNAL.dll" }));
        }

        [Test] public void ExtensibilityPullsInternalAndExternalConsumer()
        {
            CollectionAssert.AreEquivalent(new[] { "Extensibility", "Internal", "ExtensibilityConsumer" }, new AssemblyReferenceGraph(Demo()).ReverseClosure(new[] { "Extensibility" }));
        }

        [Test] public void ContractsPullsEntireRuntimeClosureDependencyFirst()
        {
            var graph = new AssemblyReferenceGraph(Demo());
            var closure = graph.ReverseClosure(new[] { "Contracts" });
            Assert.AreEqual(5, closure.Length);
            var ordered = graph.LoadOrder(closure);
            Assert.AreEqual("Contracts", ordered[0]);
            Assert.Less(Array.IndexOf(ordered, "Extensibility"), Array.IndexOf(ordered, "Internal"));
            CollectionAssert.AreEqual(ordered, new AssemblyReferenceGraph(Demo().Reverse()).LoadOrder(closure.Reverse()));
        }

        [Test] public void NonShadowConsumerReportsWholeReferencePath()
        {
            var modules = Demo(); modules.Last().isShadowCapable = false;
            var error = Assert.Throws<ShadowBuildException>(() => new AssemblyReferenceGraph(modules).ReverseClosure(new[] { "Extensibility" }));
            StringAssert.Contains("ExtensibilityConsumer -> Extensibility", error.Message);
            Assert.AreEqual("NonShadowConsumer", error.Code);
        }

        [Test] public void FixedBootstrapInReverseClosureFails()
        {
            var modules = Demo(); modules.Last().isBootstrap = true;
            Assert.AreEqual("BootstrapInClosure", Assert.Throws<ShadowBuildException>(() => new AssemblyReferenceGraph(modules).ReverseClosure(new[] { "Contracts" })).Code);
        }

        [Test] public void EditorAndTestsNeverEnterClosure()
        {
            var editor = A("Editor", "Contracts"); editor.classification = AssemblyClassification.EditorOnly; editor.isShadowCapable = false;
            var test = A("Tests", "Contracts"); test.classification = AssemblyClassification.TestOnly; test.isShadowCapable = false;
            Assert.AreEqual(5, new AssemblyReferenceGraph(Demo().Concat(new[] { editor, test })).ReverseClosure(new[] { "Contracts" }).Length);
        }

        [Test] public void PrecompiledConsumerNeedsExplicitCapability()
        {
            var modules = Demo(); modules.Last().isPrecompiled = true; modules.Last().capabilityDeclared = false;
            Assert.AreEqual("UndeclaredPrecompiledCapability", Assert.Throws<ShadowBuildException>(() => new AssemblyReferenceGraph(modules).ReverseClosure(new[] { "Contracts" })).Code);
        }

        [Test] public void ExplicitRuntimeDependencyParticipatesInReverseClosure()
        {
            var modules = new[] { A("Provider"), A("ReflectionConsumer") };
            var config = new ShadowDependencyConfiguration { runtimeDependencies = new[] { new DeclaredRuntimeDependency { consumer = "ReflectionConsumer", provider = "Provider", kind = "ReflectionString", evidence = "A configured Type.GetType boundary" } } };
            CollectionAssert.AreEquivalent(new[] { "Provider", "ReflectionConsumer" }, new AssemblyReferenceGraph(modules, config).ReverseClosure(new[] { "Provider" }));
        }

        [Test] public void RemovingDeclarationCannotHideBaselineConsumer()
        {
            var modules = new[] { A("Provider"), A("Consumer") };
            var baselineEdges = new[] { new AssemblyDependencyEdge { consumer = "Consumer", provider = "Provider", kind = "Reflection", evidence = "Frozen baseline declaration" } };
            CollectionAssert.AreEquivalent(new[] { "Provider", "Consumer" }, new AssemblyReferenceGraph(modules, null, baselineEdges).ReverseClosure(new[] { "Provider" }));
        }

        [Test] public void NormalHotUpdateConsumerIsNotMistakenForAotOrAllowedToEscape()
        {
            var hot = A("OrdinaryHotUpdate", "Contracts"); hot.classification = AssemblyClassification.NormalHotUpdate; hot.isShadowCapable = false;
            Assert.IsEmpty(AssemblyReferenceGraph.DetectChangedRoots(Demo(), Demo().Concat(new[] { hot })));
            Assert.AreEqual("NonShadowConsumer", Assert.Throws<ShadowBuildException>(() => new AssemblyReferenceGraph(Demo().Concat(new[] { hot })).ReverseClosure(new[] { "Contracts" })).Code);
        }

        [Test] public void OrdinaryHotUpdateEntrypointRetainsItsExplicitRuntimeEdge()
        {
            var modules = EntrypointModules();
            var config = EntrypointConfiguration();
            var graph = new AssemblyReferenceGraph(modules, config);
            Assert.AreEqual(1, graph.Edges.Length);
            Assert.AreEqual("Bootstrap", graph.Edges[0].consumer);
            Assert.AreEqual("OrdinaryHotUpdate", graph.Edges[0].provider);
            Assert.AreEqual("ReflectionString", graph.Edges[0].kind);
            var definitions = EntrypointDefinitions(modules);
            var policy = new ShadowPolicyConfiguration { dependencies = config };
            Assert.IsTrue(ShadowAssemblyPolicyValidator.ValidateDefinitions(definitions, policy, DateTime.UtcNow).IsValid);

            // An exact entry approval is not authority to perform an unguarded image load.
            definitions[0].managedAcquisitions = new[] { new ManagedAcquisitionEvidence
            { kind = "AssemblyLoadBytes", callSite = "Bootstrap::Probe", requiresContract = true, verified = false } };
            StringAssert.Contains("UnboundedManagedAcquisition",
                ShadowAssemblyPolicyValidator.ValidateDefinitions(definitions, policy, DateTime.UtcNow).ToString());
        }

        [TestCase("Bootstrap::Other", "OrdinaryHotUpdate.Entry, OrdinaryHotUpdate")]
        [TestCase("Bootstrap::Probe", "OrdinaryHotUpdate.Other, OrdinaryHotUpdate")]
        public void OrdinaryHotUpdateEntrypointStillRequiresExactCallsiteAndTarget(string callSite, string target)
        {
            var definitions = EntrypointDefinitions(EntrypointModules());
            definitions[0].reflectionDependencies[0].callSite = callSite;
            definitions[0].reflectionDependencies[0].target = target;
            StringAssert.Contains("BootstrapReflection", ShadowAssemblyPolicyValidator.ValidateDefinitions(definitions,
                new ShadowPolicyConfiguration { dependencies = EntrypointConfiguration() }, DateTime.UtcNow).ToString());
        }

        [TestCase(AssemblyClassification.Runtime, false, false)]
        [TestCase(AssemblyClassification.EditorOnly, false, false)]
        [TestCase(AssemblyClassification.TestOnly, false, false)]
        [TestCase(AssemblyClassification.Reference, false, false)]
        [TestCase(AssemblyClassification.BuildFiltered, false, false)]
        [TestCase(AssemblyClassification.NormalHotUpdate, true, false)]
        [TestCase(AssemblyClassification.NormalHotUpdate, false, true)]
        [TestCase(AssemblyClassification.Runtime, true, true)]
        public void EntrypointCannotPromoteAnIneligibleProvider(AssemblyClassification classification, bool candidate, bool bootstrap)
        {
            var modules = EntrypointModules();
            modules[1].classification = classification;
            modules[1].isShadowCapable = candidate;
            modules[1].isBootstrap = bootstrap;
            var config = EntrypointConfiguration();
            config.runtimeDependencies = new DeclaredRuntimeDependency[0];
            Assert.AreEqual("InvalidEntrypoint", Assert.Throws<ShadowBuildException>(() => new AssemblyReferenceGraph(modules, config)).Code);
            StringAssert.Contains("InvalidEntrypoint", ShadowAssemblyPolicyValidator.ValidateDefinitions(EntrypointDefinitions(modules),
                new ShadowPolicyConfiguration { dependencies = config }, DateTime.UtcNow).ToString());
        }

        [Test] public void EntrypointCallsiteAliasHasTheSameGraphIdentity()
        {
            var config = EntrypointConfiguration();
            var entry = config.bootstrapEntrypoints[0];
            entry.callSite = entry.method; entry.method = null;
            Assert.DoesNotThrow(() => new AssemblyReferenceGraph(EntrypointModules(), config));
            config.bootstrapEntrypoints = new[] { entry, new BootstrapEntrypointDeclaration
            { consumer = entry.consumer, provider = entry.provider, typeName = entry.typeName, method = entry.callSite, reason = entry.reason } };
            Assert.AreEqual("DuplicateEntrypoint", Assert.Throws<ShadowBuildException>(() => new AssemblyReferenceGraph(EntrypointModules(), config)).Code);
        }

        private static AssemblyDescriptor[] EntrypointModules()
        {
            var bootstrap = A("Bootstrap"); bootstrap.isShadowCapable = false; bootstrap.isBootstrap = true;
            var hot = A("OrdinaryHotUpdate"); hot.classification = AssemblyClassification.NormalHotUpdate; hot.isShadowCapable = false;
            return new[] { bootstrap, hot };
        }

        private static ShadowDependencyConfiguration EntrypointConfiguration()
        {
            return new ShadowDependencyConfiguration
            {
                runtimeDependencies = new[] { new DeclaredRuntimeDependency
                { consumer = "Bootstrap", provider = "OrdinaryHotUpdate", kind = "ReflectionString", callSite = "Bootstrap::Probe", evidence = "Verified fixed-image entry probe" } },
                bootstrapEntrypoints = new[] { new BootstrapEntrypointDeclaration
                { consumer = "Bootstrap", provider = "OrdinaryHotUpdate", typeName = "OrdinaryHotUpdate.Entry", method = "Bootstrap::Probe", reason = "Exact ordinary-hot-update probe" } },
            };
        }

        private static AssemblyPolicyDefinition[] EntrypointDefinitions(AssemblyDescriptor[] modules)
        {
            var definitions = modules.Select(module => new AssemblyPolicyDefinition
            { name = module.name, classification = module.classification, isShadowCapable = module.isShadowCapable, isBootstrap = module.isBootstrap }).ToArray();
            definitions[0].reflectionDependencies = new[] { new ReflectionDependencyEvidence
            { callSite = "Bootstrap::Probe", target = "OrdinaryHotUpdate.Entry, OrdinaryHotUpdate", provider = "OrdinaryHotUpdate", typeName = "OrdinaryHotUpdate.Entry" } };
            return definitions;
        }

        [TestCase("Missing", "Provider", "UnknownAssembly")]
        [TestCase("Provider", "Provider", "SelfDependency")]
        [TestCase("Consumer", "Provider", "DuplicateDependency")]
        public void InvalidExplicitEdgesFail(string consumer, string provider, string code)
        {
            var config = new ShadowDependencyConfiguration { runtimeDependencies = new[] { new DeclaredRuntimeDependency { consumer = consumer, provider = provider, kind = "Reflection", evidence = "fixture" } } };
            Assert.AreEqual(code, Assert.Throws<ShadowBuildException>(() => new AssemblyReferenceGraph(new[] { A("Provider"), A("Consumer", "Provider") }, config)).Code);
        }

        [Test] public void CyclesReportClosedPath()
        {
            var error = Assert.Throws<ShadowBuildException>(() => new AssemblyReferenceGraph(new[] { A("A", "B"), A("B", "C"), A("C", "A") }).ReverseClosure(new[] { "A" }));
            Assert.AreEqual("DependencyCycle", error.Code);
            StringAssert.Contains("A -> B -> C -> A", error.Message);
        }

        [Test] public void ExplicitRootsCannotHideDetectedChanges()
        {
            var baseline = Demo(); var current = Demo(); current[0].semanticHash = "changed";
            Assert.AreEqual("ChangedRootsOmitted", Assert.Throws<ShadowBuildException>(() => AssemblyReferenceGraph.DetectChangedRoots(baseline, current, new[] { "Internal" })).Code);
        }

        [Test] public void StableBootstrapCannotChange()
        {
            var baseline = new[] { A("Bootstrap") }; baseline[0].isBootstrap = true;
            var current = new[] { A("Bootstrap") }; current[0].semanticHash = "changed";
            Assert.AreEqual("BootstrapAbiChanged", Assert.Throws<ShadowBuildException>(() => AssemblyReferenceGraph.DetectChangedRoots(baseline, current)).Code);
        }

        [Test] public void GeneratorAdapterUsesClosureNotAllCandidates()
        {
            var provider = new ShadowRuntimeAssemblyInputProvider(BuildTarget.StandaloneOSX, new[] { "OrdinaryHotUpdate", "Internal.dll" }, new[] { "Internal" });
            foreach (RuntimeAssemblyInputKind kind in Enum.GetValues(typeof(RuntimeAssemblyInputKind)))
            {
                var inputs = provider.GetAssemblies(BuildTarget.StandaloneOSX, kind);
                Assert.AreEqual(2, inputs.Count);
                Assert.IsFalse(inputs.Contains("Contracts"));
            }
            Assert.Throws<ShadowBuildException>(() => provider.GetAssemblies(BuildTarget.Android, RuntimeAssemblyInputKind.Link));
        }
    }
}
