using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class R01RuntimeContractTests
    {
        [Test]
        public void LegacyErrorNumbersRemainStableAndR01CodesAreAdditive()
        {
            Assert.That(Enum.GetValues(typeof(AssemblyShadowErrorCode)).Cast<AssemblyShadowErrorCode>().Select(value => (int)value).ToArray(),
                Is.EqualTo(Enumerable.Range(0, 25).ToArray()));
            Assert.That((int)AssemblyShadowErrorCode.BaselineMethodExecution, Is.EqualTo(21));
            Assert.That((int)AssemblyShadowErrorCode.CapabilityUnavailable, Is.EqualTo(22));
            Assert.That((int)AssemblyShadowErrorCode.MetadataCapacityExceeded, Is.EqualTo(23));
            Assert.That((int)AssemblyShadowErrorCode.MetadataBudgetMismatch, Is.EqualTo(24));
        }

        [Test]
        public void PublicR01MethodsKeepEditorMonoNotSupportedConvention()
        {
            string json = "sentinel";
            Assert.Throws<NotSupportedException>(() => AssemblyShadowRuntime.GetMetadataCapacityJson(new long[0], out json));
            Assert.That(json, Is.Null);
            Assert.Throws<NotSupportedException>(() => AssemblyShadowRuntime.ReserveMetadataBudget(new long[0]));
            Assert.Throws<NotSupportedException>(() => AssemblyShadowRuntime.GetRecoveryInfoJson(out json));
            Assert.That(json, Is.Null);

            ParameterInfo reserve = typeof(AssemblyShadowRuntime).GetMethod("ReserveMetadataBudget").GetParameters()[1];
            Assert.That(reserve.DefaultValue, Is.EqualTo(2));
            Assert.That(typeof(AssemblyShadowRuntime).GetMethod("GetMetadataCapacityJson").GetParameters()[0].ParameterType,
                Is.EqualTo(typeof(long[])));
        }

        [Test]
        public void MetadataCapacityParserPreservesSchemaOneFields()
        {
            AssemblyShadowMetadataCapacity value = AssemblyShadowMetadataCapacity.Parse(QueryJson());
            Assert.That(value.enabled, Is.True);
            Assert.That(value.profileVersion, Is.EqualTo(1));
            Assert.That(value.cursors, Is.EqualTo(new uint[] { 64, 16, 4, 1 }));
            Assert.That(value.remainingSlots, Is.EqualTo(new uint[] { 3, 15, 63, 254 }));
            Assert.That(value.requiredImages, Is.EqualTo(2U));
            Assert.That(value.acceptedImages, Is.EqualTo(2U));
            Assert.That(value.firstFailingIndex, Is.EqualTo(-1));
            Assert.That(value.firstFailingSize, Is.EqualTo(0UL));
            Assert.That(value.allocations.Length, Is.EqualTo(2));
            Assert.That(value.allocations[1].dllSize, Is.EqualTo(18446744073709551615UL));
            Assert.That(value.ordinaryAllocatedCount, Is.EqualTo(9007199254740993UL));
            Assert.That(value.shadowAllocatedCount, Is.EqualTo(8UL));
            Assert.That(value.reservedImageCount, Is.EqualTo(2UL));
            Assert.That(typeof(AssemblyShadowMetadataCapacity).GetField("ordinaryLoadCount"), Is.Null);
        }

        [Test]
        public void RecoveryParserPreservesDispositionAndStartupGuard()
        {
            AssemblyShadowRecoveryInfo value = AssemblyShadowRecoveryInfo.Parse(RecoveryJson());
            Assert.That(value.enabled, Is.True);
            Assert.That(value.capabilityVersion, Is.EqualTo(1));
            Assert.That(value.stateCode, Is.EqualTo(8));
            Assert.That(value.state, Is.EqualTo("Failed"));
            Assert.That(value.dispositionCode, Is.EqualTo(0));
            Assert.That(value.disposition, Is.EqualTo("RestartRequired"));
            Assert.That(value.terminalFailureCode, Is.EqualTo(20));
            Assert.That(value.retainedBytes, Is.EqualTo(18446744073709551615UL));
            Assert.That(value.baselineEligibilityRequiresStartupValidation, Is.True);
        }

        [Test]
        public void ActiveShadowRecoveryDoesNotRequireBaselineStartupValidation()
        {
            AssemblyShadowRecoveryInfo value = AssemblyShadowRecoveryInfo.Parse(RecoveryJson()
                .Replace("\"stateCode\":8,\"state\":\"Failed\"", "\"stateCode\":6,\"state\":\"Committed\"")
                .Replace("\"published\":false", "\"published\":true")
                .Replace("\"dispositionCode\":0,\"disposition\":\"RestartRequired\"", "\"dispositionCode\":4,\"disposition\":\"ActiveShadow\"")
                .Replace("\"terminalFailureCode\":20", "\"terminalFailureCode\":0")
                .Replace("\"baselineEligibilityRequiresStartupValidation\":true", "\"baselineEligibilityRequiresStartupValidation\":false"));
            Assert.That(value.disposition, Is.EqualTo("ActiveShadow"));
            Assert.That(value.baselineEligibilityRequiresStartupValidation, Is.False);
        }

        [Test]
        public void CapabilityNegotiationUsesCapturedDiagnosticsWithoutNewInternalCalls()
        {
            MethodInfo negotiate = typeof(AssemblyShadowDiagnostics).Assembly
                .GetType("HybridCLR.AssemblyShadowRuntimeCapabilityNegotiation", true)
                .GetMethod("Negotiate", BindingFlags.Static | BindingFlags.NonPublic, null,
                    new[] { typeof(string), typeof(AssemblyShadowErrorCode), typeof(bool), typeof(int) }, null);
            Assert.That(negotiate.Invoke(null, new object[] { DiagnosticsJson(1, 1), AssemblyShadowErrorCode.Success, false, 1 }),
                Is.EqualTo(AssemblyShadowErrorCode.Success));
            Assert.That(negotiate.Invoke(null, new object[] { DiagnosticsJson(1, 1), AssemblyShadowErrorCode.Success, false, 2 }),
                Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable), "Profile 2 must not run on a profile 1 player.");
            Assert.That(negotiate.Invoke(null, new object[] { DiagnosticsJson(2, 1), AssemblyShadowErrorCode.Success, false, 2 }),
                Is.EqualTo(AssemblyShadowErrorCode.Success));
            Assert.That(negotiate.Invoke(null, new object[] { DiagnosticsJson(2, 1), AssemblyShadowErrorCode.Success, false, 1 }),
                Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable), "Legacy profile 1 must not claim profile 2 capability.");
            Assert.That(negotiate.Invoke(null, new object[] { DiagnosticsJson(0, 0), AssemblyShadowErrorCode.Success, false, 1 }),
                Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable), "Old players do not receive a new internal call.");
            Assert.That(negotiate.Invoke(null, new object[] { DiagnosticsJson(2, 2), AssemblyShadowErrorCode.Success, true, 1 }),
                Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
            Assert.That(negotiate.Invoke(null, new object[] { DiagnosticsJson(0, 0), AssemblyShadowErrorCode.FeatureDisabled, false, 2 }),
                Is.EqualTo(AssemblyShadowErrorCode.FeatureDisabled));
            Assert.That(negotiate.Invoke(null, new object[] { "{\"enabled\":true}", AssemblyShadowErrorCode.Success, false, 2 }),
                Is.EqualTo(AssemblyShadowErrorCode.CapabilityUnavailable));
        }

        [Test]
        public void StrictParsersRejectMissingAndWrongVersionFields()
        {
            Assert.That(AssemblyShadowMetadataCapacity.TryParse(QueryJson().Replace("\"fits\":true,", string.Empty), out _), Is.False);
            Assert.That(AssemblyShadowMetadataCapacity.TryParse(QueryJson().Replace("\"firstFailingIndex\":-1", "\"firstFailingIndex\":- 1"), out _), Is.False);
            Assert.That(AssemblyShadowMetadataCapacity.TryParse(QueryJson().Replace("\"profileVersion\":1", "\"profileVersion\":2"), out _), Is.False);
            Assert.That(AssemblyShadowMetadataCapacity.TryParse(QueryJson().Replace("\"indexBits\":22", "\"indexBits\":21"), out _), Is.False);
            Assert.That(AssemblyShadowMetadataCapacity.TryParse(QueryJson().Replace("\"acceptedImages\":2", "\"acceptedImages\":1"), out _), Is.False);
            Assert.That(AssemblyShadowMetadataCapacity.TryParse(QueryJson().Replace("\"failureReason\":\"None\"", "\"failureReason\":\"Exhausted\""), out _), Is.False);
            Assert.That(AssemblyShadowMetadataCapacity.TryParse(QueryJson().Replace("\"finalCursors\":[64,16,4,1]", "\"finalCursors\":[64,16,4,0]"), out _), Is.False);
            Assert.That(AssemblyShadowRecoveryInfo.TryParse(RecoveryJson().Replace("\"capabilityVersion\":1", "\"capabilityVersion\":2"), out _), Is.False);
            Assert.That(AssemblyShadowRecoveryInfo.TryParse(RecoveryJson().Replace("\"baselineEligibilityRequiresStartupValidation\":true", string.Empty), out _), Is.False);
            Assert.That(AssemblyShadowRecoveryInfo.TryParse(RecoveryJson().Replace("\"dispositionCode\":0,\"disposition\":\"RestartRequired\"", "\"dispositionCode\":3,\"disposition\":\"BaselineEligibleAfterAbort\""), out _), Is.False);
        }

        private static string DiagnosticsJson(int metadataBudgetVersion, int recoveryVersion)
        {
            return "{\"schemaVersion\":1,\"enabled\":true,\"metadataBudgetCapabilityVersion\":" + metadataBudgetVersion +
                ",\"recoveryCapabilityVersion\":" + recoveryVersion + "}";
        }

        private static string QueryJson()
        {
            return "{\"schemaVersion\":1,\"enabled\":true,\"profileVersion\":1,\"indexBits\":22,\"kindBits\":2,\"cursors\":[64,16,4,1]," +
                "\"remainingSlots\":[3,15,63,254],\"requiredImages\":2,\"acceptedImages\":2,\"firstFailingIndex\":-1," +
                "\"firstFailingSize\":0,\"failureReason\":\"None\",\"fits\":true," +
                "\"allocations\":[{\"imageIndex\":256,\"kind\":1,\"dllSize\":1048576},{\"imageIndex\":516,\"kind\":2,\"dllSize\":18446744073709551615}]," +
                "\"finalCursors\":[64,16,4,1],\"ordinaryAllocatedCount\":9007199254740993,\"shadowAllocatedCount\":8,\"reservedImageCount\":2}";
        }

        private static string RecoveryJson()
        {
            return "{\"schemaVersion\":1,\"enabled\":true,\"capabilityVersion\":1,\"stateCode\":8,\"state\":\"Failed\",\"published\":false," +
                "\"abortAllowed\":false,\"dispositionCode\":0,\"disposition\":\"RestartRequired\",\"terminalFailureCode\":20," +
                "\"reason\":\"restart\",\"retainedBytes\":18446744073709551615,\"baselineEligibilityRequiresStartupValidation\":true}";
        }
    }
}
