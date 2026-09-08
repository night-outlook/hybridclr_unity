using System;
using System.Runtime.CompilerServices;
using UnityEngine.Scripting;

namespace HybridCLR
{
    /// <summary>Public process-lifetime Assembly Shadow transaction API.</summary>
    [Preserve]
    public static class AssemblyShadowRuntime
    {
        /// <summary>
        /// Registers the verified baseline candidate set and stable external allowlist.
        /// This is valid only once, while the native state is Disabled. Candidate names
        /// must be exact logical assembly names; stable AOT names are a trusted explicit
        /// external allowlist and are not inferred from prefixes.
        /// </summary>
#if UNITY_EDITOR || !ENABLE_IL2CPP
        public static AssemblyShadowErrorCode ConfigureCandidates(string baselineBuildId, string[] candidateAssemblyNames, string[] stableAotNames)
            => throw new NotSupportedException("Assembly Shadow requires a native IL2CPP Player; Editor and Mono execution are unsupported.");
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern AssemblyShadowErrorCode ConfigureCandidates(string baselineBuildId, string[] candidateAssemblyNames, string[] stableAotNames);
#endif

        /// <summary>
        /// Starts the one process-lifetime transaction using the verified provider-before-consumer closure order.
        /// The order must contain exactly the patch closure, each a registered candidate, before any StageAssembly call.
        /// </summary>
#if UNITY_EDITOR || !ENABLE_IL2CPP
        public static AssemblyShadowErrorCode BeginTransaction(string patchId, string expectedBaselineBuildId, string[] closureLoadOrder, int runtimeAbiVersion = 1)
            => throw new NotSupportedException("Assembly Shadow requires a native IL2CPP Player; Editor and Mono execution are unsupported.");
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern AssemblyShadowErrorCode BeginTransaction(string patchId, string expectedBaselineBuildId, string[] closureLoadOrder, int runtimeAbiVersion = 1);
#endif

        /// <summary>
        /// Stages one DLL and optional PDB. The native runtime owns accepted bytes for the process lifetime;
        /// callers may safely reuse or release their managed arrays after this call returns.
        /// </summary>
#if UNITY_EDITOR || !ENABLE_IL2CPP
        public static AssemblyShadowErrorCode StageAssembly(byte[] dllBytes, byte[] pdbBytes)
            => throw new NotSupportedException("Assembly Shadow requires a native IL2CPP Player; Editor and Mono execution are unsupported.");
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern AssemblyShadowErrorCode StageAssembly(byte[] dllBytes, byte[] pdbBytes);
#endif

        /// <summary>Validates staged identities, references, metadata, baseline use and exact closure membership.</summary>
#if UNITY_EDITOR || !ENABLE_IL2CPP
        public static AssemblyShadowErrorCode ValidateTransaction()
            => throw new NotSupportedException("Assembly Shadow requires a native IL2CPP Player; Editor and Mono execution are unsupported.");
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern AssemblyShadowErrorCode ValidateTransaction();
#endif

        /// <summary>Publishes the validated transaction as one immutable active snapshot.</summary>
#if UNITY_EDITOR || !ENABLE_IL2CPP
        public static AssemblyShadowErrorCode CommitTransaction()
            => throw new NotSupportedException("Assembly Shadow requires a native IL2CPP Player; Editor and Mono execution are unsupported.");
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern AssemblyShadowErrorCode CommitTransaction();
#endif

        /// <summary>Aborts before publication; retained private metadata permanently seals this process.</summary>
#if UNITY_EDITOR || !ENABLE_IL2CPP
        public static AssemblyShadowErrorCode AbortTransaction()
            => throw new NotSupportedException("Assembly Shadow requires a native IL2CPP Player; Editor and Mono execution are unsupported.");
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern AssemblyShadowErrorCode AbortTransaction();
#endif

        /// <summary>Gets the current state without mutating the transaction.</summary>
#if UNITY_EDITOR || !ENABLE_IL2CPP
        public static AssemblyShadowErrorCode GetState(out AssemblyShadowState state)
        {
            state = AssemblyShadowState.Disabled;
            throw new NotSupportedException("Assembly Shadow requires a native IL2CPP Player; Editor and Mono execution are unsupported.");
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern AssemblyShadowErrorCode GetState(out AssemblyShadowState state);
#endif

        /// <summary>Gets the execution mode for a logical assembly without mutating the transaction.</summary>
#if UNITY_EDITOR || !ENABLE_IL2CPP
        public static AssemblyShadowErrorCode GetAssemblyExecutionMode(string logicalAssemblyName, out AssemblyExecutionMode mode)
        {
            mode = AssemblyExecutionMode.AotBaseline;
            throw new NotSupportedException("Assembly Shadow requires a native IL2CPP Player; Editor and Mono execution are unsupported.");
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern AssemblyShadowErrorCode GetAssemblyExecutionMode(string logicalAssemblyName, out AssemblyExecutionMode mode);
#endif

        /// <summary>Gets schema 1 diagnostics JSON without mutating the transaction.</summary>
#if UNITY_EDITOR || !ENABLE_IL2CPP
        public static AssemblyShadowErrorCode GetDiagnosticsJson(out string json)
        {
            json = null;
            throw new NotSupportedException("Assembly Shadow requires a native IL2CPP Player; Editor and Mono execution are unsupported.");
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern AssemblyShadowErrorCode GetDiagnosticsJson(out string json);
#endif

        /// <summary>
        /// Gets the separate schema 1 type-resolution diagnostic without changing
        /// transaction state. Pointer details are optional development evidence,
        /// not stable type identities. Editor and Mono execution are unsupported.
        /// </summary>
#if UNITY_EDITOR || !ENABLE_IL2CPP
        public static AssemblyShadowErrorCode GetTypeResolutionInfo(Type type, out string json)
        {
            json = null;
            throw new NotSupportedException("Assembly Shadow requires a native IL2CPP Player; Editor and Mono execution are unsupported.");
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern AssemblyShadowErrorCode GetTypeResolutionInfo(Type type, out string json);
#endif

        /// <summary>
        /// Gets the separate schema 1 execution observations without running a
        /// class initializer or creating a baseline class. Counts describe guard
        /// observations, not distinct executed bodies; addresses are development
        /// diagnostics only. Editor and Mono execution are unsupported.
        /// </summary>
#if UNITY_EDITOR || !ENABLE_IL2CPP
        public static AssemblyShadowErrorCode GetExecutionDiagnosticsJson(out string json)
        {
            json = null;
            throw new NotSupportedException("Assembly Shadow requires a native IL2CPP Player; Editor and Mono execution are unsupported.");
        }
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern AssemblyShadowErrorCode GetExecutionDiagnosticsJson(out string json);
#endif

        /// <summary>
        /// Queries the shared metadata index budget for an ordered DLL size list.
        /// Capability negotiation reads the existing diagnostics snapshot before
        /// invoking the additive internal call.
        /// </summary>
#if UNITY_EDITOR || !ENABLE_IL2CPP
        public static AssemblyShadowErrorCode GetMetadataCapacityJson(long[] dllSizes, out string json)
        {
            json = null;
            throw new NotSupportedException("Assembly Shadow requires a native IL2CPP Player; Editor and Mono execution are unsupported.");
        }
#else
        public static AssemblyShadowErrorCode GetMetadataCapacityJson(long[] dllSizes, out string json)
        {
            json = null;
            AssemblyShadowErrorCode capability = NegotiateCapability(false);
            if (capability != AssemblyShadowErrorCode.Success)
                return capability;
            return GetMetadataCapacityJsonInternal(dllSizes, out json);
        }
#endif

        /// <summary>Reserves a complete ordered metadata budget transactionally.</summary>
#if UNITY_EDITOR || !ENABLE_IL2CPP
        public static AssemblyShadowErrorCode ReserveMetadataBudget(long[] orderedDllSizes, int profileVersion = 1)
            => throw new NotSupportedException("Assembly Shadow requires a native IL2CPP Player; Editor and Mono execution are unsupported.");
#else
        public static AssemblyShadowErrorCode ReserveMetadataBudget(long[] orderedDllSizes, int profileVersion = 1)
        {
            AssemblyShadowErrorCode capability = NegotiateCapability(false);
            if (capability != AssemblyShadowErrorCode.Success)
                return capability;
            return ReserveMetadataBudgetInternal(orderedDllSizes, profileVersion);
        }
#endif

        /// <summary>Queries the state based recovery disposition without mutating the runtime.</summary>
#if UNITY_EDITOR || !ENABLE_IL2CPP
        public static AssemblyShadowErrorCode GetRecoveryInfoJson(out string json)
        {
            json = null;
            throw new NotSupportedException("Assembly Shadow requires a native IL2CPP Player; Editor and Mono execution are unsupported.");
        }
#else
        public static AssemblyShadowErrorCode GetRecoveryInfoJson(out string json)
        {
            json = null;
            AssemblyShadowErrorCode capability = NegotiateCapability(true);
            if (capability != AssemblyShadowErrorCode.Success)
                return capability;
            return GetRecoveryInfoJsonInternal(out json);
        }
#endif

#if !UNITY_EDITOR && ENABLE_IL2CPP
        private static AssemblyShadowErrorCode NegotiateCapability(bool recovery)
        {
            string diagnosticsJson;
            AssemblyShadowErrorCode diagnosticsCode = GetDiagnosticsJson(out diagnosticsJson);
            return AssemblyShadowRuntimeCapabilityNegotiation.Negotiate(diagnosticsJson, diagnosticsCode, recovery);
        }

        [Preserve, MethodImpl(MethodImplOptions.InternalCall)]
        private static extern AssemblyShadowErrorCode GetMetadataCapacityJsonInternal(long[] dllSizes, out string json);

        [Preserve, MethodImpl(MethodImplOptions.InternalCall)]
        private static extern AssemblyShadowErrorCode ReserveMetadataBudgetInternal(long[] orderedDllSizes, int profileVersion);

        [Preserve, MethodImpl(MethodImplOptions.InternalCall)]
        private static extern AssemblyShadowErrorCode GetRecoveryInfoJsonInternal(out string json);
#endif
    }
}
