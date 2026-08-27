namespace HybridCLR
{
    /// <summary>Process-lifetime Assembly Shadow transaction state.</summary>
    public enum AssemblyShadowState
    {
        Disabled = 0,
        CandidatesRegistered = 1,
        Staging = 2,
        Staged = 3,
        Validated = 4,
        Committing = 5,
        Committed = 6,
        Aborted = 7,
        Failed = 8,
        FailedAfterCommit = 9,
    }

    /// <summary>Execution mode for a logical assembly name.</summary>
    public enum AssemblyExecutionMode
    {
        AotBaseline = 0,
        InterpreterShadow = 1,
    }
}
