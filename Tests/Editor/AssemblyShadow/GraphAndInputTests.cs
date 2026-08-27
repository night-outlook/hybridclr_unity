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
