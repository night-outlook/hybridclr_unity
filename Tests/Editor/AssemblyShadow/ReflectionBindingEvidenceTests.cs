using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using HybridCLR.AssemblyShadow.CodeGen;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class ReflectionBindingEvidenceTests
    {
        [Test]
        public void DeclarationTargetsOwnAssemblyWithoutAddingSelfEdge()
        {
            ShadowPolicyConfiguration policy = Declare("Consumer", "Consumer");
            ShadowReflectionBindingEvidence.AddCompiledDependencies(policy, new[] { A("Consumer") });

            Assert.That(policy.reflectionBindings, Has.Length.EqualTo(1));
            Assert.That(policy.dependencies.runtimeDependencies, Is.Empty);
        }

        [Test]
        public void ExistingAssemblyReferenceIsNotDuplicatedAsExplicitDependency()
        {
            ShadowPolicyConfiguration policy = Declare("Consumer", "Provider");
            ShadowReflectionBindingEvidence.AddCompiledDependencies(policy, new[]
            {
                A("Provider"), A("Consumer", "Provider"),
            });

            Assert.That(policy.dependencies.runtimeDependencies, Is.Empty,
                "A normal AssemblyRef already represents this dependency.");
        }

        [Test]
        public void ExistingManualDeclarationIsNotDuplicatedAndHasNoCallSiteWaiver()
        {
            ShadowPolicyConfiguration policy = Declare("Consumer", "Provider");
            policy.dependencies.runtimeDependencies = new[]
            {
                new DeclaredRuntimeDependency
                {
                    consumer = "Consumer", provider = "Provider", kind = "Manual", evidence = "reviewed fixture",
                    callSite = "must-not-waive",
                },
            };
            ShadowReflectionBindingEvidence.AddCompiledDependencies(policy, new[]
            {
                A("Provider"), A("Consumer"),
            });

            Assert.That(policy.dependencies.runtimeDependencies, Has.Length.EqualTo(1));
            Assert.That(policy.dependencies.runtimeDependencies[0].callSite, Is.EqualTo("must-not-waive"));
        }

        [Test]
        public void InjectedFiniteBindingDependencyUsesNoCallSiteWaiver()
        {
            ShadowPolicyConfiguration policy = Declare("Consumer", "Provider");
            ShadowReflectionBindingEvidence.AddCompiledDependencies(policy, new[]
            {
                A("Provider"), A("Consumer"),
            });

            DeclaredRuntimeDependency edge = policy.dependencies.runtimeDependencies.Single();
            Assert.That(edge.kind, Is.EqualTo("EnforcedFiniteReflectionBinding"));
            Assert.That(edge.callSite, Is.Null,
                "Finite binding evidence must not become a call-site waiver for untransformed code.");
            Assert.That(edge.evidence, Does.Contain(policy.reflectionBindingConfigurationHash));
        }

        [Test]
        public void ShadowCapableConsumerEnlargesCandidateClosure()
        {
            AssemblyDescriptor[] modules =
            {
                A("Candidate"),
                A("Consumer"),
            };
            ShadowPolicyConfiguration policy = Declare("Consumer", "Candidate");
            ShadowReflectionBindingEvidence.AddCompiledDependencies(policy, modules);
            AssertInjectedFiniteEdge(policy, "Consumer");
            string[] closure = new AssemblyReferenceGraph(modules, policy.dependencies).ReverseClosure(new[] { "Candidate" });

            CollectionAssert.AreEquivalent(new[] { "Candidate", "Consumer" }, closure);
        }

        [Test]
        public void FixedNonShadowAotConsumerReportsFullReferencePath()
        {
            AssemblyDescriptor[] modules =
            {
                A("Candidate"),
                A("Consumer"),
            };
            modules[1].isShadowCapable = false;
            ShadowPolicyConfiguration policy = Declare("Consumer", "Candidate");
            ShadowReflectionBindingEvidence.AddCompiledDependencies(policy, modules);
            AssertInjectedFiniteEdge(policy, "Consumer");

            ShadowBuildException error = Assert.Throws<ShadowBuildException>(() =>
                new AssemblyReferenceGraph(modules, policy.dependencies).ReverseClosure(new[] { "Candidate" }));

            Assert.That(error.Code, Is.EqualTo("NonShadowConsumer"));
            StringAssert.Contains("Consumer -> Candidate", error.Message);
        }

        [Test]
        public void BootstrapConsumerProducesBootstrapInClosure()
        {
            AssemblyDescriptor[] modules =
            {
                A("Candidate"),
                A("Bootstrap"),
            };
            modules[1].isBootstrap = true;
            ShadowPolicyConfiguration policy = Declare("Bootstrap", "Candidate");
            ShadowReflectionBindingEvidence.AddCompiledDependencies(policy, modules);
            AssertInjectedFiniteEdge(policy, "Bootstrap");

            ShadowBuildException error = Assert.Throws<ShadowBuildException>(() =>
                new AssemblyReferenceGraph(modules, policy.dependencies).ReverseClosure(new[] { "Candidate" }));

            Assert.That(error.Code, Is.EqualTo("BootstrapInClosure"));
            StringAssert.Contains("Bootstrap -> Candidate", error.Message);
        }

        [Test]
        public void InvalidRawConfigurationFailsWithoutTouchingProjectSettings()
        {
            ShadowPolicyConfiguration policy = new ShadowPolicyConfiguration();
            ReflectionBindingException error = Assert.Throws<ReflectionBindingException>(() =>
                ShadowReflectionBindingEvidence.Declare(policy, new byte[] { 0x7b, 0x7d }));

            Assert.That(error.Code, Is.EqualTo("InvalidConfiguration"));
        }

        [Test]
        public void ReservedBindingDefineIsRejectedBeforeProjectConfigurationRead()
        {
            string define = ReflectionBindingDefines.Prefix + new string('a', 64);
            ShadowBuildException error = Assert.Throws<ShadowBuildException>(() =>
                ShadowReflectionBindingEvidence.CompilationDefines(new[] { define }));

            Assert.That(error.Code, Is.EqualTo("ReservedCompilerDefine"));
        }

        private static AssemblyDescriptor A(string name, params string[] references)
        {
            return new AssemblyDescriptor
            {
                name = name,
                references = references,
                classification = AssemblyClassification.Runtime,
                isShadowCapable = true,
                capabilityDeclared = true,
                semanticHash = "fixture",
            };
        }

        private static ShadowPolicyConfiguration Declare(string consumer, string provider)
        {
            var policy = new ShadowPolicyConfiguration();
            ShadowReflectionBindingEvidence.Declare(policy, ValidConfiguration(consumer, provider));
            return policy;
        }

        private static void AssertInjectedFiniteEdge(ShadowPolicyConfiguration policy, string expectedConsumer)
        {
            DeclaredRuntimeDependency edge = policy.dependencies.runtimeDependencies.Single();
            Assert.That(edge.consumer, Is.EqualTo(expectedConsumer));
            Assert.That(edge.provider, Is.EqualTo(AssemblyIdentityUtil.CanonicalName("Candidate")));
            Assert.That(edge.kind, Is.EqualTo("EnforcedFiniteReflectionBinding"));
            Assert.That(edge.callSite, Is.Null);
        }

        private static byte[] ValidConfiguration(string consumer, string provider)
        {
            var configuration = new ReflectionBindingConfiguration
            {
                schemaVersion = 1,
                transformerVersion = 1,
                sites = new[]
                {
                    new ReflectionBindingSite
                    {
                        id = "fixture-binding",
                        assembly = consumer,
                        typeName = "Fixture.Host",
                        methodSignature = "System.Type Fixture.Host::Resolve(System.String)",
                        originalMethodHash = new string('0', 64),
                        operationIndex = 1,
                        allowedTypes = new[] { "Fixture.Target, " + provider },
                        reason = "Declaration-only graph fixture; no transform is claimed.",
                    },
                },
            };
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(ReflectionBindingConfiguration)).WriteObject(stream, configuration);
                return stream.ToArray();
            }
        }
    }
}
