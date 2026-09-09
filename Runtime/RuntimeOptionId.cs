namespace HybridCLR
{
    public enum RuntimeOptionId
    {
        InterpreterThreadObjectStackSize = 1,
        InterpreterThreadFrameStackSize = 2,
        ThreadExceptionFlowSize = 3,
        MaxMethodBodyCacheSize = 4,
        MaxMethodInlineDepth = 5,
        MaxInlineableMethodBodySize = 6,
        /// <summary>Read-only exact metadata-budget wire version; zero when Assembly Shadow is compiled out.</summary>
        AssemblyShadowMetadataBudgetCapabilityVersion = 7,
        /// <summary>Read-only exact recovery wire version; zero when Assembly Shadow is compiled out.</summary>
        AssemblyShadowRecoveryCapabilityVersion = 8,
    }
}
