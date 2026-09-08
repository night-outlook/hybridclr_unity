using System;
using System.Collections.Generic;
using System.IO;
using HybridCLR.Editor.Il2CppDef;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class R01Il2CppDefGeneratorTests
    {
        [Test]
        public void OrdinaryAndStartupCandidatesAreGeneratedIntoSeparateArrays()
        {
            using (var fixture = new Fixture())
            {
                string output = fixture.Generate(new[] { "OrdinaryHotUpdate" }, new[] { "zeta", "Alpha" });
                string ordinary = fixture.ArrayBody(output, "g_placeHolderAssemblies");
                string startup = fixture.ArrayBody(output, "g_assemblyShadowStartupCandidates");

                StringAssert.Contains("\"OrdinaryHotUpdate\",", ordinary);
                StringAssert.DoesNotContain("\"Alpha\",", ordinary);
                StringAssert.DoesNotContain("\"zeta\",", ordinary);
                StringAssert.Contains("\"Alpha\",", startup);
                StringAssert.Contains("\"zeta\",", startup);
                Assert.Less(startup.IndexOf("\"Alpha\"", StringComparison.Ordinal), startup.IndexOf("\"zeta\"", StringComparison.Ordinal));
            }
        }

        [Test]
        public void StartupCandidateNamesAreEscapedAsCppLiterals()
        {
            using (var fixture = new Fixture())
            {
                string output = fixture.Generate(new string[0], new[] { "A\"B\\C" });
                StringAssert.Contains("\"A\\\"B\\\\C\",", fixture.ArrayBody(output, "g_assemblyShadowStartupCandidates"));
            }
        }

        [Test]
        public void EmptyStartupCandidatesLeaveOnlyTheNullTerminator()
        {
            using (var fixture = new Fixture())
            {
                string startup = fixture.ArrayBody(fixture.Generate(new[] { "Ordinary" }, new string[0]), "g_assemblyShadowStartupCandidates");
                StringAssert.DoesNotContain("\"Ordinary\"", startup);
                StringAssert.DoesNotContain("\"Alpha\"", startup);
                StringAssert.Contains("nullptr,", startup);
            }
        }

        [Test]
        public void StartupBootstrapDefaultsAreEmpty()
        {
            using (var fixture = new Fixture())
            {
                string output = fixture.Generate(new string[0], new string[0]);
                StringAssert.Contains("g_assemblyShadowStartupBootstrapAssembly = \"\";", output);
                StringAssert.Contains("g_assemblyShadowStartupBootstrapNamespace = \"\";", output);
                StringAssert.Contains("g_assemblyShadowStartupBootstrapType = \"\";", output);
                StringAssert.Contains("g_assemblyShadowStartupBootstrapMethod = \"\";", output);
            }
        }

        [Test]
        public void ConfiguredStartupBootstrapValuesAreGeneratedAsCppStrings()
        {
            using (var fixture = new Fixture())
            {
                string output = fixture.Generate(new string[0], new string[0],
                    "Bootstrap-Core", "Early.Startup", "EntryPoint_1", "Initialize_2",
                    new[] { "Bootstrap-Core" }, new string[0]);
                StringAssert.Contains("g_assemblyShadowStartupBootstrapAssembly = \"Bootstrap-Core\";", output);
                StringAssert.Contains("g_assemblyShadowStartupBootstrapNamespace = \"Early.Startup\";", output);
                StringAssert.Contains("g_assemblyShadowStartupBootstrapType = \"EntryPoint_1\";", output);
                StringAssert.Contains("g_assemblyShadowStartupBootstrapMethod = \"Initialize_2\";", output);

                output = fixture.Generate(new string[0], new string[0],
                    "Stable.Core", "", "EntryPoint", "Initialize",
                    new string[0], new[] { "Stable.Core" });
                StringAssert.Contains("g_assemblyShadowStartupBootstrapAssembly = \"Stable.Core\";", output);
            }
        }

        [Test]
        public void StartupBootstrapAssemblyMustMatchDeclaredPhysicalRootExactly()
        {
            using (var fixture = new Fixture())
            {
                Assert.Throws<ShadowBuildException>(() => fixture.Generate(new string[0], new string[0],
                    "Bootstrap.dll", "", "EntryPoint", "Initialize", new[] { "Bootstrap" }, new string[0]));
                Assert.Throws<ShadowBuildException>(() => fixture.Generate(new string[0], new string[0],
                    "bootstrap", "", "EntryPoint", "Initialize", new[] { "Bootstrap" }, new string[0]));
            }
        }

        [Test]
        public void StartupBootstrapNamesHonorNativeUtf8ByteLimit()
        {
            using (var fixture = new Fixture())
            {
                string exact = new string('\u00E9', 256); // 512 UTF-8 bytes.
                string over = new string('\u00E9', 257); // 514 UTF-8 bytes.
                Assert.DoesNotThrow(() => fixture.Generate(new string[0], new string[0],
                    exact, "", "EntryPoint", "Initialize", new[] { exact }, new string[0]));
                Assert.Throws<ShadowBuildException>(() => fixture.Generate(new string[0], new string[0],
                    over, "", "EntryPoint", "Initialize", new[] { over }, new string[0]));

                Assert.DoesNotThrow(() => fixture.Generate(new string[0], new string[0],
                    "Bootstrap", exact, "EntryPoint", "Initialize", new[] { "Bootstrap" }, new string[0]));
                Assert.Throws<ShadowBuildException>(() => fixture.Generate(new string[0], new string[0],
                    "Bootstrap", over, "EntryPoint", "Initialize", new[] { "Bootstrap" }, new string[0]));

                Assert.DoesNotThrow(() => fixture.Generate(new string[0], new string[0],
                    "Bootstrap", "", exact, "Initialize", new[] { "Bootstrap" }, new string[0]));
                Assert.Throws<ShadowBuildException>(() => fixture.Generate(new string[0], new string[0],
                    "Bootstrap", "", over, "Initialize", new[] { "Bootstrap" }, new string[0]));

                Assert.DoesNotThrow(() => fixture.Generate(new string[0], new string[0],
                    "Bootstrap", "", "EntryPoint", exact, new[] { "Bootstrap" }, new string[0]));
                Assert.Throws<ShadowBuildException>(() => fixture.Generate(new string[0], new string[0],
                    "Bootstrap", "", "EntryPoint", over, new[] { "Bootstrap" }, new string[0]));
            }
        }

        [Test]
        public void StartupBootstrapPartialConfigurationIsRejected()
        {
            using (var fixture = new Fixture())
            {
                foreach (var values in new[]
                {
                    new[] { "Bootstrap", "", "", "Initialize" },
                    new[] { "Bootstrap", "", "EntryPoint", "" },
                    new[] { "", "", "EntryPoint", "Initialize" },
                })
                {
                    Assert.Throws<ShadowBuildException>(() => fixture.Generate(new string[0], new string[0],
                        values[0], values[1], values[2], values[3], new[] { "Bootstrap" }, new string[0]));
                }
            }
        }

        [Test]
        public void StartupBootstrapUnsafeValuesAreRejected()
        {
            using (var fixture = new Fixture())
            {
                foreach (string value in new[] { "Bad\nName", "Bad\\Name", "Bad\"Name", "Bad;Name" })
                {
                    Assert.Throws<ShadowBuildException>(() => fixture.Generate(new string[0], new string[0],
                        value, "Early", "EntryPoint", "Initialize", new[] { value }, new string[0]));
                }
            }
        }

        [Test]
        public void StartupBootstrapMustUseDeclaredBootstrapOrStableAotRoot()
        {
            using (var fixture = new Fixture())
            {
                Assert.Throws<ShadowBuildException>(() => fixture.Generate(new string[0], new string[0],
                    "Runtime", "", "EntryPoint", "Initialize", new[] { "Bootstrap" }, new string[0]));
            }
        }

        [Test]
        public void StartupBootstrapRejectsShadowAndOrdinaryRoles()
        {
            using (var fixture = new Fixture())
            {
                Assert.Throws<ShadowBuildException>(() => fixture.Generate(new[] { "Ordinary" }, new[] { "Shadow" },
                    "Shadow", "", "EntryPoint", "Initialize", new[] { "Shadow" }, new string[0]));
                Assert.Throws<ShadowBuildException>(() => fixture.Generate(new[] { "Ordinary" }, new[] { "Shadow" },
                    "Ordinary", "", "EntryPoint", "Initialize", new[] { "Ordinary" }, new string[0]));
            }
        }

        [Test]
        public void StartupBootstrapGenerationReplacesStaleValues()
        {
            using (var fixture = new Fixture())
            {
                fixture.Generate(new string[0], new string[0], "Bootstrap", "Early", "EntryPoint", "Initialize",
                    new[] { "Bootstrap" }, new string[0]);
                string output = fixture.Generate(new string[0], new string[0]);
                StringAssert.DoesNotContain("g_assemblyShadowStartupBootstrapAssembly = \"Bootstrap\";", output);
                StringAssert.Contains("g_assemblyShadowStartupBootstrapAssembly = \"\";", output);
            }
        }

        [Test]
        public void DuplicateStartupCandidatesAreRejectedCaseInsensitively()
        {
            using (var fixture = new Fixture())
            {
                var error = Assert.Throws<ShadowBuildException>(() => fixture.Generate(new string[0], new[] { "Candidate", "candidate" }));
                Assert.AreEqual("DuplicateStartupCandidate", error.Code);
            }
        }

        [Test]
        public void InvalidStartupCandidateNamesAreRejected()
        {
            using (var fixture = new Fixture())
            {
                foreach (string candidate in new[] { null, "", "  ", " Leading", "Trailing ", "A\0B" })
                    Assert.Throws<ShadowBuildException>(() => fixture.Generate(new string[0], new[] { candidate }), candidate ?? "null");
            }
        }

        [Test]
        public void StartupCandidateOrderingIsDeterministicAndPreservesSpelling()
        {
            using (var fixture = new Fixture())
            {
                string first = fixture.Generate(new string[0], new[] { "zeta", "Alpha" });
                string second = fixture.Generate(new string[0], new[] { "Alpha", "zeta" });
                Assert.AreEqual(first, second);
                StringAssert.Contains("\"Alpha\",", fixture.ArrayBody(second, "g_assemblyShadowStartupCandidates"));
            }
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "AssemblyShadowIl2CppDef-" + Guid.NewGuid().ToString("N"));
            private readonly string _unityVersionTemplate;
            private readonly string _assemblyManifestTemplate;
            private readonly string _unityVersionOutput;
            private readonly string _assemblyManifestOutput;

            public Fixture()
            {
                Directory.CreateDirectory(_root);
                _unityVersionTemplate = Path.Combine(_root, "UnityVersion.h.tpl");
                _assemblyManifestTemplate = Path.Combine(_root, "AssemblyManifest.cpp.tpl");
                _unityVersionOutput = Path.Combine(_root, "UnityVersion.h");
                _assemblyManifestOutput = Path.Combine(_root, "AssemblyManifest.cpp");
                File.WriteAllText(_unityVersionTemplate, "//!!!{{UNITY_VERSION\n\n//!!!}}UNITY_VERSION\n");
                File.WriteAllText(_assemblyManifestTemplate,
                    "g_placeHolderAssemblies[] = {\n//!!!{{PLACE_HOLDER\n\n//!!!}}PLACE_HOLDER\nnullptr,\n};\n" +
                    "g_assemblyShadowStartupCandidates[] = {\n//!!!{{ASSEMBLY_SHADOW_STARTUP_CANDIDATES\n\n//!!!}}ASSEMBLY_SHADOW_STARTUP_CANDIDATES\nnullptr,\n};\n");
                File.AppendAllText(_assemblyManifestTemplate,
                    "extern const uint32_t g_assemblyShadowStartupBootstrapSchemaVersion = 1;\n" +
                    "//!!!{{ASSEMBLY_SHADOW_STARTUP_BOOTSTRAP\n\n//!!!}}ASSEMBLY_SHADOW_STARTUP_BOOTSTRAP\n");
            }

            public string Generate(IEnumerable<string> ordinary, IEnumerable<string> startup)
            {
                return Generate(ordinary, startup, "", "", "", "", null, null);
            }

            public string Generate(IEnumerable<string> ordinary, IEnumerable<string> startup,
                string bootstrapAssembly, string bootstrapNamespace, string bootstrapType, string bootstrapMethod,
                IEnumerable<string> bootstrapAssemblies, IEnumerable<string> stableAotAssemblies)
            {
                var generator = new Il2CppDefGenerator(new Il2CppDefGenerator.Options
                {
                    UnityVersion = "2022.3.62f2",
                    HotUpdateAssemblies = new List<string>(ordinary),
                    AssemblyShadowStartupCandidates = new List<string>(startup),
                    AssemblyShadowStartupBootstrapAssemblies = new List<string>(bootstrapAssemblies ?? new string[0]),
                    AssemblyShadowStartupStableAotAssemblies = new List<string>(stableAotAssemblies ?? new string[0]),
                    AssemblyShadowStartupBootstrapAssembly = bootstrapAssembly,
                    AssemblyShadowStartupBootstrapNamespace = bootstrapNamespace,
                    AssemblyShadowStartupBootstrapType = bootstrapType,
                    AssemblyShadowStartupBootstrapMethod = bootstrapMethod,
                    UnityVersionTemplateFile = _unityVersionTemplate,
                    UnityVersionOutputFile = _unityVersionOutput,
                    AssemblyManifestTemplateFile = _assemblyManifestTemplate,
                    AssemblyManifestOutputFile = _assemblyManifestOutput,
                });
                generator.Generate();
                return File.ReadAllText(_assemblyManifestOutput);
            }

            public string ArrayBody(string output, string arrayName)
            {
                int start = output.IndexOf(arrayName, StringComparison.Ordinal);
                Assert.GreaterOrEqual(start, 0, arrayName);
                start = output.IndexOf('{', start);
                int end = output.IndexOf("};", start, StringComparison.Ordinal);
                Assert.GreaterOrEqual(end, start, arrayName);
                return output.Substring(start, end - start);
            }

            public void Dispose()
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, true);
            }
        }
    }
}
