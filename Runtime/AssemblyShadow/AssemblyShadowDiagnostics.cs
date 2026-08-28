using System;
using UnityEngine;
using UnityEngine.Scripting;

namespace HybridCLR
{
    /// <summary>Schema 1 diagnostics returned by <see cref="AssemblyShadowRuntime.GetDiagnosticsJson"/>.</summary>
    // JsonUtility reads the complete native schema, including fields unused by
    // managed callers. Preserve fields explicitly: preserving a type alone only
    // retains its constructor, so linked Players can otherwise lose evidence.
    [Serializable, Preserve]
    public sealed class AssemblyShadowDiagnostics
    {
        [Preserve] public int schemaVersion;
        [Preserve] public bool enabled;
        [Preserve] public int runtimeAbiVersion;
        [Preserve] public string state;
        [Preserve] public int stateCode;
        [Preserve] public int lastError;
        [Preserve] public string detail;
        [Preserve] public string baselineBuildId;
        [Preserve] public string patchId;
        [Preserve] public long generation;
        [Preserve] public long expected;
        [Preserve] public long staged;
        [Preserve] public long retainedBytes;
        [Preserve] public long enumerationGeneration;
        [Preserve] public AssemblyShadowOrdinaryAssembly[] ordinaryAssemblies;
        [Preserve] public long classEnumerationGeneration;
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
        [Preserve] public long sequence;
        [Preserve] public string kind;
        [Preserve] public string name;
        [Preserve] public long generation;
        [Preserve] public int stagedCount;
    }

    [Serializable, Preserve]
    public sealed class AssemblyShadowBaselineUse
    {
        [Preserve] public string name;
        [Preserve] public string kind;
        [Preserve] public string detail;
        [Preserve] public string type;
        [Preserve] public long thread;
        [Preserve] public long timestamp;
    }
}
