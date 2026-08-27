using System;

namespace HybridCLR
{
    /// <summary>Exception raised when a caller explicitly requires a successful shadow operation.</summary>
    public sealed class AssemblyShadowException : Exception
    {
        public AssemblyShadowErrorCode ErrorCode { get; }

        public AssemblyShadowException(AssemblyShadowErrorCode errorCode, string detail = null)
            : base(string.IsNullOrEmpty(detail) ? errorCode.ToString() : detail)
        {
            ErrorCode = errorCode;
        }

        /// <summary>Throws an <see cref="AssemblyShadowException"/> unless <paramref name="errorCode"/> is Success.</summary>
        public static void RequireSuccess(AssemblyShadowErrorCode errorCode, string detail = null)
        {
            if (errorCode != AssemblyShadowErrorCode.Success)
                throw new AssemblyShadowException(errorCode, detail);
        }
    }
}
