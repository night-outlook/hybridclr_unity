using System;
using UnityEngine;

namespace HybridCLR
{
    /// <summary>Schema 1 diagnostics returned by <see cref="AssemblyShadowRuntime.GetDiagnosticsJson"/>.</summary>
    [Serializable]
    public sealed class AssemblyShadowDiagnostics
    {
        public int schemaVersion;
        public bool enabled;
        public int runtimeAbiVersion;
        public string state;
        public int stateCode;
        public int lastError;
        public string detail;
        public string baselineBuildId;
        public string patchId;
        public long generation;
        public long expected;
        public long staged;
        public long retainedBytes;
        public long enumerationGeneration;
        public AssemblyShadowOrdinaryAssembly[] ordinaryAssemblies;
        public long classEnumerationGeneration;
        public AssemblyShadowOrdinaryClass[] ordinaryClasses;
        public string[] closureLoadOrder;
        public string[] stableAotNames;
        public string[] commitOrder;
        public AssemblyShadowDiagnosticAssembly[] assemblies;
        public AssemblyShadowDiagnosticEvent[] events;
        public AssemblyShadowBaselineUse[] baselineUses;

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

    [Serializable]
    public sealed class AssemblyShadowOrdinaryAssembly
    {
        public string name;
        public bool isInterpreter;
    }

    [Serializable]
    public sealed class AssemblyShadowOrdinaryClass
    {
        public string assemblyName;
        public string typeName;
        public bool isInterpreter;
        public bool isConstructedGeneric;
        public bool usesStagedMetadata;
    }

    [Serializable]
    public sealed class AssemblyShadowDiagnosticAssembly
    {
        public string name;
        public string mvid;
        public bool skeletonBuilt;
        public bool runtimeMetadataInitialized;
        public bool published;
        public bool moduleInitializerAttempted;
        public bool moduleInitializerRan;
    }

    [Serializable]
    public sealed class AssemblyShadowDiagnosticEvent
    {
        public long sequence;
        public string kind;
        public string name;
        public long generation;
        public int stagedCount;
    }

    [Serializable]
    public sealed class AssemblyShadowBaselineUse
    {
        public string name;
        public string kind;
        public string detail;
        public string type;
        public long thread;
        public long timestamp;
    }
}
