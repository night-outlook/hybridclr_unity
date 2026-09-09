using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCLR.Editor.AssemblyShadow
{
    /// <summary>
    /// Explicit identity and bounds for the sparse signed-int32 metadata
    /// capacity profile.  These values describe the admission envelope only;
    /// native runtime finalization still owns the page-footprint decision.
    /// </summary>
    public static class MetadataCapacityProfile2
    {
        public const int ProfileVersion = 2;
        public const int BudgetCapabilityVersion = 2;
        public const string CodecId = "SparseSignedInt32";
        public const int CodecBits = 32;
        public const int InvalidIndexSentinel = -1;
        public const int AotMaxIndex = int.MaxValue;
        public const int MinImageId = 1;
        public const int MaxImages = 8192;
        public const int PageValues = 4096;
        public const int UsablePages = 524287;
        public const int MaxChargedPages = 393215;
        public const ulong MaxDllBytes = 33554432UL;
        public const ulong AggregateDllEnvelopeBytes = 536870912UL;
    }

    [Serializable]
    public sealed class MetadataEncodingProfile2
    {
        public int schemaVersion = 2;
        public int profileVersion = MetadataCapacityProfile2.ProfileVersion;
        public int nativeBudgetCapabilityVersion = MetadataCapacityProfile2.BudgetCapabilityVersion;
        public string codecId = MetadataCapacityProfile2.CodecId;
        public int codecBits = MetadataCapacityProfile2.CodecBits;
        public int invalidIndexSentinel = MetadataCapacityProfile2.InvalidIndexSentinel;
        public int aotMaxIndex = MetadataCapacityProfile2.AotMaxIndex;
        public int minImageId = MetadataCapacityProfile2.MinImageId;
        public int maximumImageCount = MetadataCapacityProfile2.MaxImages;
        public int pageValues = MetadataCapacityProfile2.PageValues;
        public int usablePageCapacity = MetadataCapacityProfile2.UsablePages;
        public int chargedPageCeiling = MetadataCapacityProfile2.MaxChargedPages;
        public int minimumFreePageMargin = MetadataCapacityProfile2.UsablePages - MetadataCapacityProfile2.MaxChargedPages;
        public ulong maximumDllBytes = MetadataCapacityProfile2.MaxDllBytes;
        public ulong aggregateDllEnvelopeBytes = MetadataCapacityProfile2.AggregateDllEnvelopeBytes;
        public string nativeSourceRevision;
        public string nativeCodecHeaderSha256;

        public bool IsValid()
        {
            return schemaVersion == 2 && profileVersion == MetadataCapacityProfile2.ProfileVersion &&
                nativeBudgetCapabilityVersion == MetadataCapacityProfile2.BudgetCapabilityVersion && codecId == MetadataCapacityProfile2.CodecId &&
                codecBits == MetadataCapacityProfile2.CodecBits && invalidIndexSentinel == MetadataCapacityProfile2.InvalidIndexSentinel &&
                aotMaxIndex == MetadataCapacityProfile2.AotMaxIndex && minImageId == MetadataCapacityProfile2.MinImageId &&
                maximumImageCount == MetadataCapacityProfile2.MaxImages && pageValues == MetadataCapacityProfile2.PageValues &&
                usablePageCapacity == MetadataCapacityProfile2.UsablePages && chargedPageCeiling == MetadataCapacityProfile2.MaxChargedPages &&
                minimumFreePageMargin == MetadataCapacityProfile2.UsablePages - MetadataCapacityProfile2.MaxChargedPages &&
                maximumDllBytes == MetadataCapacityProfile2.MaxDllBytes &&
                aggregateDllEnvelopeBytes == MetadataCapacityProfile2.AggregateDllEnvelopeBytes &&
                !string.IsNullOrWhiteSpace(nativeSourceRevision) && RegexSha256(nativeCodecHeaderSha256);
        }

        public void ValidateOrThrow()
        {
            if (!IsValid())
                throw new InvalidOperationException("Metadata encoding profile must be the explicit version 2 sparse signed-int32 profile bound to a native codec hash.");
        }

        private static bool RegexSha256(string value)
        {
            if (value == null || value.Length != 64) return false;
            for (int index = 0; index < value.Length; ++index)
            {
                char c = value[index];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }
    }

    [Serializable]
    public sealed class MetadataCapacityProfile2Input
    {
        public string name;
        public ulong dllSize;
        public string sha256;
    }

    [Serializable]
    public sealed class MetadataCapacityProfile2Allocation
    {
        public string name;
        public ulong dllSize;
        public string sha256;
        public int imageId;
    }

    /// <summary>
    /// A deterministic, ordered preflight report.  A successful report admits
    /// image identities and input bytes only.  It deliberately never claims
    /// that the native sparse page footprint will fit.
    /// </summary>
    [Serializable]
    public sealed class MetadataCapacityProfile2PreliminaryReport
    {
        public int schemaVersion = 2;
        public int profileVersion = MetadataCapacityProfile2.ProfileVersion;
        public int budgetCapabilityVersion = MetadataCapacityProfile2.BudgetCapabilityVersion;
        public int nativeBudgetCapabilityVersion = MetadataCapacityProfile2.BudgetCapabilityVersion;
        public string nativeSourceRevision;
        public string nativeCodecHeaderSha256;
        public string codecId = MetadataCapacityProfile2.CodecId;
        public int codecBits = MetadataCapacityProfile2.CodecBits;
        public int invalidIndexSentinel = MetadataCapacityProfile2.InvalidIndexSentinel;
        public int aotMaxIndex = MetadataCapacityProfile2.AotMaxIndex;
        public int minImageId = MetadataCapacityProfile2.MinImageId;
        public int maxImages = MetadataCapacityProfile2.MaxImages;
        public int pageValues = MetadataCapacityProfile2.PageValues;
        public int usablePages = MetadataCapacityProfile2.UsablePages;
        public int maxChargedPages = MetadataCapacityProfile2.MaxChargedPages;
        public ulong maxDllBytes = MetadataCapacityProfile2.MaxDllBytes;
        public ulong aggregateDllEnvelopeBytes = MetadataCapacityProfile2.AggregateDllEnvelopeBytes;
        // These names mirror the profile-2 native capacity JSON. The shorter
        // aliases above remain useful to Editor callers and are serialized as
        // part of this explicitly versioned managed report.
        public int maximumImageCount = MetadataCapacityProfile2.MaxImages;
        public ulong maximumDllBytes = MetadataCapacityProfile2.MaxDllBytes;
        public int usablePageCapacity = MetadataCapacityProfile2.UsablePages;
        public int chargedPageCeiling = MetadataCapacityProfile2.MaxChargedPages;
        public int minimumFreePageMargin = MetadataCapacityProfile2.UsablePages - MetadataCapacityProfile2.MaxChargedPages;

        public int currentReservedImageCount;
        public int reservedImageCountBefore;
        public int reservedImageCountAfter;
        public int requestedImageCount;
        public int requiredImages;
        public ulong aggregateDllBytes;
        public ulong aggregateInputDllBytes;
        public bool aggregateInputDllBytesInformational = true;
        public ulong reservedPages;
        public ulong mappedPages;
        public ulong lifetimeReservedImageCount;
        public ulong remainingImageCount;
        public MetadataCapacityProfile2Input[] inputs = new MetadataCapacityProfile2Input[0];
        public MetadataCapacityProfile2Allocation[] allocations = new MetadataCapacityProfile2Allocation[0];
        public int acceptedImages;
        public bool fitsImageCount;
        public bool aggregateDllEnvelopeFits;
        public bool aggregateDllEnvelopeExceeded;
        public bool inputCountWasBounded;
        public bool admissionAccepted;
        public bool fitsPreliminary;
        public bool finalPageFitKnown;
        public bool runtimeFinalizationRequired = true;
        public string admissionKind = "Preliminary";
        public int firstFailingIndex = -1;
        public string firstFailingAssembly;
        public string failureReason = "None";
    }

    /// <summary>
    /// Pure-BCL profile-2 admission planning.  It mirrors the native
    /// reservation order: state, null/count, image-limit, then ordered size
    /// checks.  Failed proposals are all-or-nothing and retain no allocations.
    /// </summary>
    public static class MetadataCapacityPlannerProfile2
    {
        public static MetadataCapacityProfile2PreliminaryReport PreliminaryPlan(
            IEnumerable<MetadataCapacityProfile2Input> inputs, int currentReservedImageCount)
        {
            var report = CreateReport(currentReservedImageCount);
            if (currentReservedImageCount < 0 || currentReservedImageCount > MetadataCapacityProfile2.MaxImages)
            {
                return Fail(report, 0, null, "InvalidState");
            }
            if (inputs == null)
            {
                return Fail(report, 0, null, "InvalidInput");
            }

            int remaining = MetadataCapacityProfile2.MaxImages - currentReservedImageCount;
            bool bounded;
            MetadataCapacityProfile2Input[] values = ReadBounded(inputs, remaining + 1, out bounded);
            report.inputs = values;
            report.requestedImageCount = values.Length;
            report.requiredImages = values.Length;
            report.inputCountWasBounded = bounded;
            // The serialized native profile-2 report uses -1 as its success
            // sentinel. The internal evaluator's count sentinel is not part
            // of this managed wire DTO.
            report.firstFailingIndex = -1;

            // The native API checks the proposal count before inspecting any
            // DLL bytes.  Reading at most remaining+1 elements preserves that
            // priority without materializing an unbounded enumerable.
            if (values.Length > remaining)
            {
                return Fail(report, remaining, values[remaining], "ImageLimit");
            }

            report.fitsImageCount = true;
            for (int index = 0; index < values.Length; ++index)
            {
                MetadataCapacityProfile2Input value = values[index];
                if (value == null)
                {
                    return Fail(report, index, null, "InvalidInput");
                }
                if (value.dllSize == 0)
                {
                    return Fail(report, index, value, "EmptyDll");
                }
                if (value.dllSize > MetadataCapacityProfile2.MaxDllBytes)
                {
                    return Fail(report, index, value, "DllTooLarge");
                }
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ulong aggregate = 0;
            for (int index = 0; index < values.Length; ++index)
            {
                MetadataCapacityProfile2Input value = values[index];
                if (string.IsNullOrWhiteSpace(value.name) || !names.Add(value.name))
                {
                    return Fail(report, index, value, "InvalidInput");
                }
                if (!IsSha256(value.sha256))
                {
                    return Fail(report, index, value, "InvalidInput");
                }
                try
                {
                    aggregate = checked(aggregate + value.dllSize);
                }
                catch (OverflowException)
                {
                    return Fail(report, index, value, "ArithmeticOverflow");
                }
            }

            report.aggregateDllBytes = aggregate;
            report.aggregateInputDllBytes = aggregate;
            report.aggregateDllEnvelopeFits = aggregate <= MetadataCapacityProfile2.AggregateDllEnvelopeBytes;
            report.aggregateDllEnvelopeExceeded = !report.aggregateDllEnvelopeFits;
            report.acceptedImages = values.Length;
            report.reservedImageCountAfter = checked(currentReservedImageCount + values.Length);
            report.admissionAccepted = true;
            report.fitsPreliminary = true;
            report.lifetimeReservedImageCount = (ulong)report.reservedImageCountAfter;
            report.remainingImageCount = (ulong)(MetadataCapacityProfile2.MaxImages - report.reservedImageCountAfter);
            report.allocations = new MetadataCapacityProfile2Allocation[values.Length];
            for (int index = 0; index < values.Length; ++index)
            {
                MetadataCapacityProfile2Input value = values[index];
                report.allocations[index] = new MetadataCapacityProfile2Allocation
                {
                    name = value.name,
                    dllSize = value.dllSize,
                    sha256 = value.sha256,
                    imageId = checked(currentReservedImageCount + index + MetadataCapacityProfile2.MinImageId),
                };
            }
            return report;
        }

        public static MetadataCapacityProfile2PreliminaryReport PreliminaryPlan(
            MetadataCapacityProfile2Input[] inputs, int currentReservedImageCount)
        {
            return PreliminaryPlan((IEnumerable<MetadataCapacityProfile2Input>)inputs, currentReservedImageCount);
        }

        private static MetadataCapacityProfile2PreliminaryReport CreateReport(int currentReservedImageCount)
        {
            return new MetadataCapacityProfile2PreliminaryReport
            {
                currentReservedImageCount = currentReservedImageCount,
                reservedImageCountBefore = currentReservedImageCount,
                reservedImageCountAfter = currentReservedImageCount,
                lifetimeReservedImageCount = currentReservedImageCount < 0 ? 0UL : (ulong)currentReservedImageCount,
                remainingImageCount = currentReservedImageCount < 0 || currentReservedImageCount > MetadataCapacityProfile2.MaxImages ?
                    0UL : (ulong)(MetadataCapacityProfile2.MaxImages - currentReservedImageCount),
                aggregateDllEnvelopeFits = true,
                aggregateInputDllBytesInformational = true,
                finalPageFitKnown = false,
                runtimeFinalizationRequired = true,
            };
        }

        private static MetadataCapacityProfile2PreliminaryReport Fail(
            MetadataCapacityProfile2PreliminaryReport report, int index,
            MetadataCapacityProfile2Input value, string reason)
        {
            report.firstFailingIndex = index;
            report.firstFailingAssembly = value == null ? null : value.name;
            report.failureReason = reason;
            report.acceptedImages = 0;
            report.admissionAccepted = false;
            report.fitsPreliminary = false;
            report.allocations = new MetadataCapacityProfile2Allocation[0];
            report.reservedImageCountAfter = report.reservedImageCountBefore;
            return report;
        }

        private static MetadataCapacityProfile2Input[] ReadBounded(
            IEnumerable<MetadataCapacityProfile2Input> inputs, int maximum,
            out bool bounded)
        {
            bounded = false;
            var values = new List<MetadataCapacityProfile2Input>(Math.Min(maximum, MetadataCapacityProfile2.MaxImages));
            using (IEnumerator<MetadataCapacityProfile2Input> enumerator = inputs.GetEnumerator())
            {
                while (values.Count < maximum && enumerator.MoveNext())
                {
                    values.Add(enumerator.Current);
                }
                // Reaching this cap is sufficient to prove ImageLimit: the
                // cap is remaining+1, so there is no need to consume another
                // element from a potentially unbounded producer.
                bounded = values.Count == maximum;
            }
            return values.ToArray();
        }

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }
            for (int index = 0; index < value.Length; ++index)
            {
                char c = value[index];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
