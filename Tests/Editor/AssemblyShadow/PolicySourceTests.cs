using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class PolicySourceTests
    {
        private string root;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "AssemblyShadowPolicySourceTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }

        [Test]
        public void MissingExplicitSourceDoesNotFallBackToPermissiveDefault()
        {
            string fallback = Path.Combine(root, "default-whitelist.json");
            string missing = Path.Combine(root, "custom-whitelist.json");
            File.WriteAllText(fallback,
                "{\"entries\":[{\"provider\":\"Candidate\",\"consumer\":\"Consumer\",\"owner\":\"test\",\"reason\":\"test\",\"reviewer\":\"reviewer\",\"expires\":\"2099-01-01\"}]}");

            ShadowBuildException exception = Assert.Throws<ShadowBuildException>(
                () => ReadTextAssetOrPath(null, missing, fallback));
            Assert.That(exception.Code, Is.EqualTo("InvalidPolicySource"));
            Assert.That(exception.Message, Does.Contain(missing));
        }

        [Test]
        public void ExplicitConfiguredFileIsReadInsteadOfFallback()
        {
            string fallback = Path.Combine(root, "default-policy.json");
            string configured = Path.Combine(root, "custom-policy.json");
            File.WriteAllText(fallback, "fallback");
            File.WriteAllText(configured, "configured");

            Assert.That(ReadTextAssetOrPath(null, configured, fallback), Is.EqualTo("configured"));
        }

        [Test]
        public void CaseDifferentExplicitPathIsNotReplacedByFallback()
        {
            string fallback = Path.Combine(root, "policy.json");
            string configured = Path.Combine(root, "Policy.json");
            File.WriteAllText(fallback, "fallback");
            File.WriteAllText(configured, "configured");

            Assert.That(PathsEqual(configured, fallback), Is.False);

            // APFS/HFS+ may be case-insensitive.  The path-selection assertion
            // above is still meaningful there; distinct-file content is not.
            if (string.Equals(File.ReadAllText(fallback), "configured", StringComparison.Ordinal))
                return;

            Assert.That(ReadTextAssetOrPath(null, configured, fallback), Is.EqualTo("configured"));
        }

        [Test]
        public void EmptyExplicitTextAssetDoesNotFallBackToPermissiveDefault()
        {
            string fallback = Path.Combine(root, "default-dependencies.json");
            File.WriteAllText(fallback, "{\"runtimeDependencies\":[{\"consumer\":\"Consumer\",\"provider\":\"Candidate\",\"kind\":\"runtime\",\"evidence\":\"test\"}]}");
            TextAsset source = new TextAsset(" \n");
            try
            {
                ShadowBuildException exception = Assert.Throws<ShadowBuildException>(
                    () => ReadTextAssetOrPath(source, Path.Combine(root, "custom-dependencies.json"), fallback));
                Assert.That(exception.Code, Is.EqualTo("InvalidPolicySource"));
                Assert.That(exception.Message, Does.Contain("TextAsset"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void MissingDefaultSourceMeansNoOptionalPolicy()
        {
            string missing = Path.Combine(root, "unconfigured-policy.json");
            Assert.That(ReadTextAssetOrPath(null, null, missing), Is.Empty);
        }

        private static string ReadTextAssetOrPath(TextAsset asset, string configuredPath, string fallbackPath)
        {
            MethodInfo method = typeof(AssemblyShadowSettingsUtil).GetMethod(
                "ReadTextAssetOrPath", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            try
            {
                return (string)method.Invoke(null, new object[] { asset, configuredPath, fallbackPath });
            }
            catch (TargetInvocationException exception)
            {
                throw exception.InnerException;
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            MethodInfo method = typeof(AssemblyShadowSettingsUtil).GetMethod(
                "PathsEqual", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return (bool)method.Invoke(null, new object[] { left, right });
        }
    }
}
