using System;
using System.Reflection;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class R01EarlyCapabilityNegotiationTests
    {
#if UNITY_EDITOR
        [Test]
        public void EditorCapabilityOptionsAreReadOnlyAndReportNoNativeCapability()
        {
            foreach (RuntimeOptionId option in new[] { RuntimeOptionId.AssemblyShadowMetadataBudgetCapabilityVersion,
                RuntimeOptionId.AssemblyShadowRecoveryCapabilityVersion })
            {
                Assert.That(RuntimeApi.GetRuntimeOption(option), Is.Zero);
                ArgumentException error = Assert.Throws<ArgumentException>(() => RuntimeApi.SetRuntimeOption(option, 2));
                Assert.That(error.ParamName, Is.EqualTo(((int)option).ToString()));
                Assert.That(RuntimeApi.GetRuntimeOption(option), Is.Zero);
            }
        }
#endif

        [Test]
        public void LiveCapabilitiesDoNotDependOnGrowingDiagnosticSnapshotsOrCacheTruth()
        {
            int reads = 0, legacyReads = 0;
            int metadataVersion = 2;
            Func<RuntimeOptionId, int> reader = option =>
            {
                ++reads;
                return option == RuntimeOptionId.AssemblyShadowMetadataBudgetCapabilityVersion ? metadataVersion : 1;
            };
            LegacyProvider diagnostics = (out string json) =>
            {
                ++legacyReads;
                json = new string(' ', 4 * 1024 * 1024 + 1);
                return AssemblyShadowErrorCode.Success;
            };
            for (int loadedImages = 0; loadedImages <= 8192; ++loadedImages)
                Assert.That(NegotiateLive(reader, diagnostics, false, 2), Is.EqualTo(AssemblyShadowErrorCode.Success));
            Assert.That(reads, Is.EqualTo(8193));
            Assert.That(legacyReads, Is.Zero);
            metadataVersion = 0;
            Assert.That(NegotiateLive(reader, diagnostics, false, 2), Is.EqualTo(AssemblyShadowErrorCode.FeatureDisabled));
            metadataVersion = -1;
            Assert.That(NegotiateLive(reader, diagnostics, false, 2), Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
            metadataVersion = 3;
            Assert.That(NegotiateLive(reader, diagnostics, false, 3), Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
            metadataVersion = 1;
            Assert.That(NegotiateLive(reader, diagnostics, false, 2), Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
            Assert.That(NegotiateLive(reader, diagnostics, true, 1), Is.EqualTo(AssemblyShadowErrorCode.Success));
            Assert.That(legacyReads, Is.Zero);
        }

        [Test]
        public void OnlyTheExactLegacyUnknownOptionExceptionPermitsFallback()
        {
            int legacyReads = 0;
            LegacyProvider diagnostics = (out string json) =>
            {
                ++legacyReads;
                json = Diagnostics(2, 1);
                return AssemblyShadowErrorCode.Success;
            };
            Func<RuntimeOptionId, int> oldNative = option =>
            {
                throw new ArgumentException("invalid runtime option id", ((int)option).ToString());
            };
            Assert.That(NegotiateLive(oldNative, diagnostics, false, 2), Is.EqualTo(AssemblyShadowErrorCode.Success));
            Assert.That(NegotiateLive(oldNative, diagnostics, true, 1), Is.EqualTo(AssemblyShadowErrorCode.Success));
            Assert.That(legacyReads, Is.EqualTo(2));
            foreach (Exception failure in new Exception[]
            {
                new InvalidOperationException("native failure"),
                new ArgumentException("another native failure", "7"),
                new ArgumentException("invalid runtime option id", "8"),
                new ArgumentNullException("7", "invalid runtime option id"),
                new ArgumentException("invalid runtime option id"),
            })
                Assert.That(NegotiateLive(option => { throw failure; }, diagnostics, false, 2),
                    Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
            Assert.That(legacyReads, Is.EqualTo(2));
        }

        [Test]
        public void LegacyFallbackRetainsDisabledProfileAndResourceBoundSemantics()
        {
            Func<RuntimeOptionId, int> oldNative = option =>
            {
                throw new ArgumentException("invalid runtime option id", ((int)option).ToString());
            };
            Assert.That(NegotiateLive(oldNative, (out string json) =>
            {
                json = "invalid";
                return AssemblyShadowErrorCode.FeatureDisabled;
            }, false, 2), Is.EqualTo(AssemblyShadowErrorCode.FeatureDisabled));
            Assert.That(NegotiateLive(oldNative, (out string json) =>
            {
                json = Minimal();
                return AssemblyShadowErrorCode.Success;
            }, false, 2), Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
            Assert.That(NegotiateLive(oldNative, (out string json) =>
            {
                json = new string(' ', 4 * 1024 * 1024 + 1);
                return AssemblyShadowErrorCode.Success;
            }, false, 2), Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
        }

        private delegate AssemblyShadowErrorCode LegacyProvider(out string json);

        private static AssemblyShadowErrorCode NegotiateLive(Func<RuntimeOptionId, int> reader,
            LegacyProvider legacy, bool recovery, int requiredVersion)
        {
            MethodInfo method = typeof(AssemblyShadowDiagnostics).Assembly
                .GetType("HybridCLR.AssemblyShadowRuntimeCapabilityNegotiation", true)
                .GetMethod("NegotiateLive", BindingFlags.Static | BindingFlags.NonPublic);
            Delegate provider = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType, legacy.Target, legacy.Method);
            return (AssemblyShadowErrorCode)method.Invoke(null, new object[] { reader, provider, recovery, requiredVersion });
        }

        [Test]
        public void MinimalAndFullDiagnosticsNegotiateWithoutUnityJsonUtility()
        {
            Assert.That(Negotiate(Minimal(), false), Is.EqualTo(AssemblyShadowErrorCode.Success));
            Assert.That(Negotiate(FullDiagnostics(), false), Is.EqualTo(AssemblyShadowErrorCode.Success));
            Assert.That(Negotiate(FullDiagnostics(), true), Is.EqualTo(AssemblyShadowErrorCode.Success));
            Assert.That(Negotiate(Minimal().Replace("\"metadataBudgetCapabilityVersion\":1", "\"metadataBudgetCapabilityVersion\":2"), false, 2),
                Is.EqualTo(AssemblyShadowErrorCode.Success));
            Assert.That(Negotiate(Minimal().Replace("\"recoveryCapabilityVersion\":1", "\"recoveryCapabilityVersion\":2"), true),
                Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
        }

        [Test]
        public void MetadataCapabilityMustMatchTheRequestedProfileExactly()
        {
            string capabilityOne = Minimal();
            string capabilityTwo = capabilityOne.Replace("\"metadataBudgetCapabilityVersion\":1", "\"metadataBudgetCapabilityVersion\":2");

            Assert.That(Negotiate(capabilityOne, false, 1), Is.EqualTo(AssemblyShadowErrorCode.Success));
            Assert.That(Negotiate(capabilityOne, false, 2), Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
            Assert.That(Negotiate(capabilityTwo, false, 2), Is.EqualTo(AssemblyShadowErrorCode.Success));
            Assert.That(Negotiate(capabilityTwo, false, 1), Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
            Assert.That(Negotiate(capabilityTwo, false, 0), Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
            Assert.That(Negotiate(Diagnostics(3, 1), false, 3), Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
        }

        [Test]
        public void NestedFakeCapabilityFieldsDoNotOverrideRootCapabilities()
        {
            string json = Minimal().Replace(
                "\"recoveryCapabilityVersion\":1}",
                "\"recoveryCapabilityVersion\":1,\"nested\":{\"metadataBudgetCapabilityVersion\":99,\"recoveryCapabilityVersion\":99}}");
            Assert.That(Negotiate(json, false), Is.EqualTo(AssemblyShadowErrorCode.Success));
            Assert.That(Negotiate(json, true), Is.EqualTo(AssemblyShadowErrorCode.Success));
        }

        [Test]
        public void DuplicateEscapedKeysAreRejectedAtEveryObjectLevel()
        {
            string duplicateRoot = Minimal().Replace(
                "\"recoveryCapabilityVersion\":1}",
                "\"recoveryCapabilityVersion\":1,\"metadataBudgetCapabilit\\u0079Version\":1}");
            Assert.That(Negotiate(duplicateRoot, false), Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));

            string duplicateNested = Minimal().Replace(
                "\"recoveryCapabilityVersion\":1}",
                "\"recoveryCapabilityVersion\":1,\"nested\":{\"x\":1,\"\\u0078\":2}}");
            Assert.That(Negotiate(duplicateNested, false), Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
        }

        [Test]
        public void MalformedTrailingAndWrongTypeDocumentsFailClosed()
        {
            foreach (string json in new[]
            {
                "",
                "{",
                Minimal() + " trailing",
                Minimal().Replace("\"enabled\":true", "\"enabled\":1"),
                Minimal().Replace("\"metadataBudgetCapabilityVersion\":1", "\"metadataBudgetCapabilityVersion\":1.0"),
                Minimal().Replace("\"recoveryCapabilityVersion\":1", "\"recoveryCapabilityVersion\":- 1"),
            })
            {
                Assert.That(Negotiate(json, false), Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable), json);
            }
        }

        [Test]
        public void MissingRelevantFieldsAndOldSchemaFailClosed()
        {
            Assert.That(Negotiate("{\"schemaVersion\":1,\"enabled\":true,\"recoveryCapabilityVersion\":1}", false),
                Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
            Assert.That(Negotiate("{\"schemaVersion\":1,\"enabled\":true,\"metadataBudgetCapabilityVersion\":1}", true),
                Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
            Assert.That(Negotiate(Minimal().Replace("\"schemaVersion\":1", "\"schemaVersion\":0"), false),
                Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
        }

        [Test]
        public void UnknownValuesHonorDepthAndNumericBounds()
        {
            Assert.That(Negotiate(DepthDocument(64), false), Is.EqualTo(AssemblyShadowErrorCode.Success));
            Assert.That(Negotiate(DepthDocument(65), false), Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));

            string uint64 = Minimal().Replace(
                "\"recoveryCapabilityVersion\":1}",
                "\"recoveryCapabilityVersion\":1,\"generation\":18446744073709551615}");
            Assert.That(Negotiate(uint64, false), Is.EqualTo(AssemblyShadowErrorCode.Success));

            string hugeCapability = Minimal().Replace(
                "\"metadataBudgetCapabilityVersion\":1",
                "\"metadataBudgetCapabilityVersion\":18446744073709551615");
            Assert.That(Negotiate(hugeCapability, false), Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
        }

        [Test]
        public void FeatureDisabledShortCircuitsMalformedDiagnostics()
        {
            Assert.That(Negotiate("not json", false, 2, AssemblyShadowErrorCode.FeatureDisabled),
                Is.EqualTo(AssemblyShadowErrorCode.FeatureDisabled));
        }

        private static AssemblyShadowErrorCode Negotiate(string json, bool recovery, int requiredCapabilityVersion = 1,
            AssemblyShadowErrorCode diagnosticsCode = AssemblyShadowErrorCode.Success)
        {
            MethodInfo method = typeof(AssemblyShadowDiagnostics).Assembly
                .GetType("HybridCLR.AssemblyShadowRuntimeCapabilityNegotiation", true)
                .GetMethod("Negotiate", BindingFlags.Static | BindingFlags.NonPublic, null,
                    new[] { typeof(string), typeof(AssemblyShadowErrorCode), typeof(bool), typeof(int) }, null);
            return (AssemblyShadowErrorCode)method.Invoke(null, new object[] { json, diagnosticsCode, recovery, requiredCapabilityVersion });
        }

        private static string Minimal()
        {
            return "{\"schemaVersion\":1,\"enabled\":true,\"metadataBudgetCapabilityVersion\":1,\"recoveryCapabilityVersion\":1}";
        }

        private static string FullDiagnostics()
        {
            return "{\"schemaVersion\":1,\"enabled\":true,\"metadataBudgetCapabilityVersion\":1,\"recoveryCapabilityVersion\":1," +
                "\"generation\":18446744073709551615,\"ordinaryAssemblies\":[{\"name\":\"A\",\"isInterpreter\":false}]," +
                "\"events\":[{\"sequence\":1,\"kind\":\"x\",\"nested\":[true,null,3.5e+2,{\"deep\":\"value\"}]}]," +
                "\"unknownObject\":{\"array\":[1,2,{\"leaf\":\"ok\"}]}}";
        }

        private static string Diagnostics(int metadataBudgetVersion, int recoveryVersion)
        {
            return "{\"schemaVersion\":1,\"enabled\":true,\"metadataBudgetCapabilityVersion\":" + metadataBudgetVersion +
                ",\"recoveryCapabilityVersion\":" + recoveryVersion + "}";
        }

        private static string DepthDocument(int nestedObjects)
        {
            string json = Minimal().Replace(
                "\"recoveryCapabilityVersion\":1}",
                "\"recoveryCapabilityVersion\":1,\"unknown\":");
            for (int index = 0; index < nestedObjects; ++index)
                json += "{\"x\":";
            json += "0";
            for (int index = 0; index < nestedObjects; ++index)
                json += "}";
            return json + "}";
        }
    }
}
