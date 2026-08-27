using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using dnlib.DotNet;
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

        [Test]
        public void FixedImageDeclarationAddsProviderWithoutATypeAnchor()
        {
            using (var fixture = new FixedImageFixture())
            {
                var policy = new ShadowPolicyConfiguration();
                ShadowReflectionBindingEvidence.Declare(policy, Serialize(fixture.Configuration));
                ShadowReflectionBindingEvidence.AddCompiledDependencies(policy, new[] { A("Consumer"), A("NormalHotUpdate") });
                var declaration = policy.reflectionBindings.Single();
                Assert.That(declaration.kind, Is.EqualTo("FixedAssemblyBytes"));
                Assert.That(declaration.allowedTypes, Is.Empty);
                Assert.That(declaration.providers, Is.EquivalentTo(new[] { AssemblyIdentityUtil.CanonicalName("NormalHotUpdate") }));
                Assert.That(policy.dependencies.runtimeDependencies.Single().callSite, Is.Null);
            }
        }

        [Test]
        public void FixedImageReaderUsesCapturedBytesNotProjectSourcePath()
        {
            using (var fixture = new FixedImageFixture())
            {
                var images = ShadowReflectionBindingEvidence.ReadFixedImages(fixture.Root, fixture.Configuration);
                CollectionAssert.AreEqual(fixture.Payload, images[fixture.Configuration.sites[0].imagePath]);
                Assert.That(File.Exists(fixture.Configuration.sites[0].imagePath), Is.False,
                    "The fixture intentionally has no live project source file.");
            }
        }

        [Test]
        public void FixedImageReaderRejectsTamperedAndMissingCapturedBytes()
        {
            using (var fixture = new FixedImageFixture())
            {
                byte[] changed = (byte[])fixture.Payload.Clone();
                changed[changed.Length / 2] ^= 1;
                File.WriteAllBytes(fixture.ImagePath, changed);
                Assert.Throws<ReflectionBindingException>(() => ShadowReflectionBindingEvidence.ReadFixedImages(fixture.Root, fixture.Configuration));
                File.Delete(fixture.ImagePath);
                var missing = Assert.Throws<ShadowBuildException>(() => ShadowReflectionBindingEvidence.ReadFixedImages(fixture.Root, fixture.Configuration));
                Assert.That(missing.Code, Is.EqualTo("FixedAssemblyImageMissing"));
            }
        }

        [Test]
        public void FixedImageReaderRejectsAHashValidForeignProviderIdentity()
        {
            using (var fixture = new FixedImageFixture())
            {
                fixture.Configuration.sites[0].providerAssemblyIdentity =
                    "ForeignProvider, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
                Assert.Throws<ReflectionBindingException>(() => ShadowReflectionBindingEvidence.ReadFixedImages(fixture.Root, fixture.Configuration));
            }
        }

        [Test]
        public void FixedImageSnapshotPathRejectsNonHashPathInput()
        {
            Assert.Throws<ShadowBuildException>(() => ShadowReflectionBindingEvidence.FixedImageSnapshotPath("../outside"));
        }

        private sealed class FixedImageFixture : IDisposable
        {
            public readonly string Root;
            public readonly string ImagePath;
            public readonly byte[] Payload;
            public readonly ReflectionBindingConfiguration Configuration;

            public FixedImageFixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "ShadowFixedImageEvidence-" + Guid.NewGuid().ToString("N"));
                var module = new ModuleDefUser("NormalHotUpdate.dll") { Kind = ModuleKind.Dll };
                var assembly = new AssemblyDefUser("NormalHotUpdate", new Version(1, 0, 0, 0));
                assembly.Modules.Add(module);
                using (var stream = new MemoryStream()) { module.Write(stream); Payload = stream.ToArray(); }
                string identity = assembly.FullName;
                module.Dispose();
                string hash = ShadowHash.Bytes(Payload);
                Configuration = new ReflectionBindingConfiguration
                {
                    schemaVersion = 2, transformerVersion = 2,
                    sites = new[] { new ReflectionBindingSite
                    {
                        id = "fixed-image-fixture", assembly = "Consumer", typeName = "Fixture.Host",
                        methodSignature = "System.Reflection.Assembly Fixture.Host::Load(System.Byte[])",
                        originalMethodHash = new string('0', 64), operationIndex = 1,
                        allowedTypes = new string[0], reason = "Captured fixed-image evidence regression.",
                        kind = "FixedAssemblyBytes", imageSha256 = hash, providerAssemblyIdentity = identity,
                        imagePath = "Assets/MissingFixedImageFixture-" + Guid.NewGuid().ToString("N") + ".dll.bytes",
                    } },
                };
                ImagePath = Path.Combine(Root, ShadowReflectionBindingEvidence.FixedImageSnapshotPath(hash));
                Directory.CreateDirectory(Path.GetDirectoryName(ImagePath));
                File.WriteAllBytes(ImagePath, Payload);
            }

            public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        }

        private static byte[] Serialize(ReflectionBindingConfiguration configuration)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(ReflectionBindingConfiguration)).WriteObject(stream, configuration);
                return stream.ToArray();
            }
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
