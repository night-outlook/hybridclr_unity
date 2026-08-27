namespace HybridCLR
{
    /// <summary>Stable result codes returned by every Assembly Shadow operation.</summary>
    public enum AssemblyShadowErrorCode
    {
        Success = 0,
        FeatureDisabled = 1,
        InvalidState = 2,
        InvalidArgument = 3,
        CandidateNotRegistered = 4,
        DuplicateAssemblyName = 5,
        BaselineAssemblyNotFound = 6,
        BaselineBuildMismatch = 7,
        AssemblyNameMismatch = 8,
        BadImage = 9,
        UnsupportedAssembly = 10,
        ClosureMemberMissing = 11,
        UnexpectedClosureMember = 12,
        ReferenceResolutionFailed = 13,
        ReferenceEscapesClosure = 14,
        BaselineAlreadyUsed = 15,
        ResourceAbiMismatch = 16,
        RuntimeAbiMismatch = 17,
        AlreadyCommitted = 18,
        ModuleInitializerFailed = 19,
        InternalError = 20,
    }
}
