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
            }

            public string Generate(IEnumerable<string> ordinary, IEnumerable<string> startup)
            {
                var generator = new Il2CppDefGenerator(new Il2CppDefGenerator.Options
                {
                    UnityVersion = "2022.3.62f2",
                    HotUpdateAssemblies = new List<string>(ordinary),
                    AssemblyShadowStartupCandidates = new List<string>(startup),
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
