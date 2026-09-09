using System;
using UnityEngine;
using UnityEngine.Scripting;

namespace HybridCLR
{
    /// <summary>Schema 1 diagnostics returned by <see cref="AssemblyShadowRuntime.GetDiagnosticsJson"/>.</summary>
    // JsonUtility reads the complete native schema, including fields unused by
    // managed callers. Preserve fields explicitly: preserving a type alone only
    // retains its constructor, so linked Players can otherwise lose evidence.
    // Native uint64_t and arm64 size_t values are unsigned JSON integer tokens;
    // keep their full range rather than narrowing thread hashes or counters.
    [Serializable, Preserve]
    public sealed class AssemblyShadowDiagnostics
    {
        [Preserve] public int schemaVersion;
        [Preserve] public int startupCandidateSchemaVersion;
        [Preserve] public string[] startupCandidateNames;
        [Preserve] public string startupObservationMode;
        [Preserve] public bool enabled;
        [Preserve] public int metadataBudgetCapabilityVersion;
        [Preserve] public int recoveryCapabilityVersion;
        [Preserve] public int runtimeAbiVersion;
        [Preserve] public string state;
        [Preserve] public int stateCode;
        [Preserve] public int lastError;
        [Preserve] public string detail;
        [Preserve] public string baselineBuildId;
        [Preserve] public string patchId;
        [Preserve] public ulong generation;
        [Preserve] public ulong expected;
        [Preserve] public ulong staged;
        [Preserve] public ulong retainedBytes;
        [Preserve] public ulong enumerationGeneration;
        [Preserve] public AssemblyShadowOrdinaryAssembly[] ordinaryAssemblies;
        [Preserve] public ulong classEnumerationGeneration;
        [Preserve] public AssemblyShadowOrdinaryClass[] ordinaryClasses;
        [Preserve] public string[] closureLoadOrder;
        [Preserve] public string[] stableAotNames;
        [Preserve] public string[] commitOrder;
        [Preserve] public AssemblyShadowDiagnosticAssembly[] assemblies;
        [Preserve] public AssemblyShadowDiagnosticEvent[] events;
        [Preserve] public AssemblyShadowBaselineUse[] baselineUses;

        /// <summary>Parses schema 1 JSON produced by the native runtime.</summary>
        public static AssemblyShadowDiagnostics Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("Diagnostics JSON must not be null or empty.", nameof(json));

            AssemblyShadowDiagnostics result = JsonUtility.FromJson<AssemblyShadowDiagnostics>(json);
            if (result == null)
                throw new FormatException("Diagnostics JSON did not contain an Assembly Shadow object.");
            return result;
        }

        /// <summary>Attempts to parse schema 1 JSON without throwing for malformed input.</summary>
        public static bool TryParse(string json, out AssemblyShadowDiagnostics diagnostics)
        {
            diagnostics = null;
            if (string.IsNullOrEmpty(json))
                return false;

            try
            {
                diagnostics = Parse(json);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // Kept separate from the InternalCall declarations so the negotiation
    // contract can be exercised with captured diagnostics in Editor tests.
    internal static class AssemblyShadowRuntimeCapabilityNegotiation
    {
        internal delegate AssemblyShadowErrorCode DiagnosticsProvider(out string json);

        internal static AssemblyShadowErrorCode NegotiateLive(Func<RuntimeOptionId, int> readOption,
            DiagnosticsProvider readLegacyDiagnostics, bool recovery, int requiredCapabilityVersion)
        {
            RuntimeOptionId option = recovery ? RuntimeOptionId.AssemblyShadowRecoveryCapabilityVersion :
                RuntimeOptionId.AssemblyShadowMetadataBudgetCapabilityVersion;
            try
            {
                // Query every time: neither native capability nor failure is cached.
                int version = readOption(option);
                if (version == 0) return AssemblyShadowErrorCode.FeatureDisabled;
                return (requiredCapabilityVersion == 1 || requiredCapabilityVersion == 2) &&
                    version == requiredCapabilityVersion ? AssemblyShadowErrorCode.Success :
                    AssemblyShadowErrorCode.CapabilityUnavailable;
            }
            catch (ArgumentException error) when (IsUnknownLegacyOption(error, option))
            {
                // The established icall exists in older natives; its precise
                // unknown-option exception is the compatibility discriminator.
                // Other native errors must not fall back to a different truth.
                try
                {
                    string json;
                    AssemblyShadowErrorCode code = readLegacyDiagnostics(out json);
                    return Negotiate(json, code, recovery, requiredCapabilityVersion);
                }
                catch (Exception) { return AssemblyShadowErrorCode.CapabilityUnavailable; }
            }
            catch (Exception) { return AssemblyShadowErrorCode.CapabilityUnavailable; }
        }

        private static bool IsUnknownLegacyOption(ArgumentException error, RuntimeOptionId option)
        {
            return error.GetType() == typeof(ArgumentException) &&
                error.ParamName == ((int)option).ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                error.Message.StartsWith("invalid runtime option id", StringComparison.Ordinal);
        }

        internal static AssemblyShadowErrorCode Negotiate(string diagnosticsJson,
            AssemblyShadowErrorCode diagnosticsCode, bool recovery, int requiredCapabilityVersion)
        {
            if (diagnosticsCode == AssemblyShadowErrorCode.FeatureDisabled)
                return AssemblyShadowErrorCode.FeatureDisabled;
            if (diagnosticsCode != AssemblyShadowErrorCode.Success ||
                !AssemblyShadowCapabilityReader.TryRead(diagnosticsJson, out AssemblyShadowCapabilityReader.Snapshot diagnostics) ||
                !diagnostics.hasSchemaVersion || !diagnostics.hasEnabled || diagnostics.schemaVersion != 1 || !diagnostics.enabled)
                return AssemblyShadowErrorCode.CapabilityUnavailable;

            // Capability versions describe the exact wire contract for the
            // requested operation. Do not treat a different profile as a
            // compatible implementation: profile 1 and profile 2 produce
            // different metadata index representations.
            if (requiredCapabilityVersion != 1 && requiredCapabilityVersion != 2)
                return AssemblyShadowErrorCode.CapabilityUnavailable;

            if ((recovery && !diagnostics.hasRecoveryCapabilityVersion) ||
                (!recovery && !diagnostics.hasMetadataBudgetCapabilityVersion))
                return AssemblyShadowErrorCode.CapabilityUnavailable;
            int capabilityVersion = recovery ? diagnostics.recoveryCapabilityVersion : diagnostics.metadataBudgetCapabilityVersion;
            return capabilityVersion == requiredCapabilityVersion ? AssemblyShadowErrorCode.Success :
                AssemblyShadowErrorCode.CapabilityUnavailable;
        }
    }

    [Serializable, Preserve]
    public sealed class AssemblyShadowOrdinaryAssembly
    {
        [Preserve] public string name;
        [Preserve] public bool isInterpreter;
    }

    [Serializable, Preserve]
    public sealed class AssemblyShadowOrdinaryClass
    {
        [Preserve] public string assemblyName;
        [Preserve] public string typeName;
        [Preserve] public bool isInterpreter;
        [Preserve] public bool isConstructedGeneric;
        [Preserve] public bool usesStagedMetadata;
    }

    [Serializable, Preserve]
    public sealed class AssemblyShadowDiagnosticAssembly
    {
        [Preserve] public string name;
        [Preserve] public string mvid;
        [Preserve] public bool skeletonBuilt;
        [Preserve] public bool runtimeMetadataInitialized;
        [Preserve] public bool published;
        [Preserve] public bool moduleInitializerAttempted;
        [Preserve] public bool moduleInitializerRan;
    }

    [Serializable, Preserve]
    public sealed class AssemblyShadowDiagnosticEvent
    {
        [Preserve] public ulong sequence;
        [Preserve] public string kind;
        [Preserve] public string name;
        [Preserve] public ulong generation;
        [Preserve] public ulong stagedCount;
    }

    [Serializable, Preserve]
    public sealed class AssemblyShadowBaselineUse
    {
        [Preserve] public string name;
        [Preserve] public string kind;
        [Preserve] public string detail;
        [Preserve] public string type;
        [Preserve] public ulong thread;
        [Preserve] public ulong timestamp;
    }
}
