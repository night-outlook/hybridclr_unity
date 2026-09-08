using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    /// <summary>
    /// The managed description of the native InterpreterImageBudget profile.
    /// The values are deliberately serialized so a baseline cannot silently use
    /// a different allocator after it has been published.
    /// </summary>
    [Serializable]
    public sealed class MetadataEncodingProfile
    {
        public int profileVersion = 1;
        public int metadataIndexBits = 22;
        public int metadataKindBits = 2;
        public int[] extraShiftBits = { 6, 4, 2, 0 };
        public int[] kindStrides = { 64, 16, 4, 1 };
        public uint[] indexMasks = { 268435455u, 67108863u, 16777215u, 4194303u };
        public uint[] initialCursors = { 64u, 0u, 0u, 0u };
        public uint[] kindLimits = { 256u, 256u, 256u, 255u };
        public int sizeMultiplier = 4;
        public string nativeSourceRevision;
        public string nativeHelperSha256;
        public int nativeBudgetCapabilityVersion = 1;

        public static MetadataEncodingProfile CreateV1(string nativeSourceRevision, string nativeHelperSha256 = null)
        {
            var profile = new MetadataEncodingProfile
            {
                nativeSourceRevision = nativeSourceRevision,
                nativeHelperSha256 = nativeHelperSha256,
            };
            Validate(profile);
            return profile;
        }

        /// <summary>
        /// Creates the current profile only when the installed native helper is
        /// present and its installer receipt binds its bytes to the pinned
        /// hybridclr revision. Older installations fail closed with the small
        /// integration step still required to establish that capability.
        /// </summary>
        public static MetadataEncodingProfile CreateCurrentV1(ShadowSourcePins pins)
        {
            ShadowHash.Require(pins != null && pins.hybridclr != null && !string.IsNullOrWhiteSpace(pins.hybridclr.revision),
                "CapabilityMissing", "Metadata capacity capability requires a pinned hybridclr source revision.");

            string installedRoot = Path.Combine(SettingsUtil.LocalIl2CppDir, "libil2cpp");
            string helperPath = Path.Combine(installedRoot, "hybridclr", "metadata", "InterpreterImageBudget.h");
            string receiptPath = Path.Combine(installedRoot, "assembly-shadow-install.json");
            if (!File.Exists(helperPath) || !File.Exists(receiptPath))
            {
                throw new ShadowBuildException("CapabilityMissing",
                    "Installed native metadata budget helper is unavailable. Install the pinned native sources and regenerate native definitions before building an R01 baseline.");
            }

            string helperHash;
            try { helperHash = ShadowHash.File(helperPath); }
            catch (Exception error)
            {
                throw new ShadowBuildException("CapabilityMissing", "Cannot hash the installed metadata budget helper: " + error.Message);
            }

            InstallReceiptEvidence receipt;
            try { receipt = JsonUtility.FromJson<InstallReceiptEvidence>(File.ReadAllText(receiptPath)); }
            catch (Exception error)
            {
                throw new ShadowBuildException("CapabilityMissing", "The pinned native install receipt is unreadable: " + error.Message);
            }

            bool revisionMatches = receipt != null && receipt.repositories != null && receipt.repositories.hybridclr != null &&
                string.Equals(receipt.repositories.hybridclr.revision, pins.hybridclr.revision, StringComparison.OrdinalIgnoreCase);
            bool helperMatches = receipt != null && (receipt.sourceFileHashes ?? new SourceFileHashEvidence[0]).Any(file =>
                file != null && file.source == "hybridclr" && file.path == "hybridclr/metadata/InterpreterImageBudget.h" &&
                string.Equals(file.sha256, helperHash, StringComparison.OrdinalIgnoreCase));
            if (!revisionMatches || !helperMatches)
            {
                throw new ShadowBuildException("CapabilityMissing",
                    "Installed native metadata budget helper is not hash-bound to the pinned hybridclr revision. Reinstall the pinned native sources before building an R01 baseline.");
            }

            var profile = CreateV1(pins.hybridclr.revision, helperHash);
            profile.ValidateOrThrow();
            return profile;
        }

        public void ValidateOrThrow() { Validate(this); }

        internal static void Validate(MetadataEncodingProfile profile)
        {
            ShadowHash.Require(profile != null && profile.profileVersion == 1 && profile.metadataIndexBits == 22 && profile.metadataKindBits == 2 &&
                profile.sizeMultiplier == 4 && profile.nativeBudgetCapabilityVersion == 1,
                "CapabilityMissing", "Metadata encoding profile must be version 1 with the native R01 budget capability.");
            RequireArray(profile.extraShiftBits, new[] { 6, 4, 2, 0 }, "extraShiftBits");
            RequireArray(profile.kindStrides, new[] { 64, 16, 4, 1 }, "kindStrides");
            RequireArray(profile.indexMasks, new[] { 268435455u, 67108863u, 16777215u, 4194303u }, "indexMasks");
            RequireArray(profile.initialCursors, new[] { 64u, 0u, 0u, 0u }, "initialCursors");
            RequireArray(profile.kindLimits, new[] { 256u, 256u, 256u, 255u }, "kindLimits");
            ShadowHash.Require(!string.IsNullOrWhiteSpace(profile.nativeSourceRevision), "CapabilityMissing", "Metadata profile has no native source revision.");
        }

        private static void RequireArray(int[] actual, int[] expected, string name)
        {
            ShadowHash.Require(actual != null && actual.Length == expected.Length && actual.SequenceEqual(expected), "CapabilityMissing",
                "Metadata profile field is not the native version 1 value: " + name);
        }

        private static void RequireArray(uint[] actual, uint[] expected, string name)
        {
            ShadowHash.Require(actual != null && actual.Length == expected.Length && actual.SequenceEqual(expected), "CapabilityMissing",
                "Metadata profile field is not the native version 1 value: " + name);
        }

        [Serializable]
        private sealed class InstallReceiptEvidence
        {
            public InstallRepositoriesEvidence repositories;
            public SourceFileHashEvidence[] sourceFileHashes;
        }

        [Serializable]
        private sealed class InstallRepositoriesEvidence
        {
            public InstallRepositoryEvidence hybridclr;
        }

        [Serializable]
        private sealed class InstallRepositoryEvidence
        {
            public string revision;
        }

        [Serializable]
        private sealed class SourceFileHashEvidence
        {
            public string source;
            public string path;
            public string sha256;
        }
    }

    [Serializable]
    public sealed class MetadataCapacityInput
    {
        public string name;
        public ulong bytes;
        public ulong dllSize;
        public string sha256;

        public static MetadataCapacityInput FromVerifiedFile(string root, SnapshotFile file)
        {
            ShadowHash.Require(file != null && !string.IsNullOrWhiteSpace(file.name) && !string.IsNullOrWhiteSpace(file.path),
                "MetadataInputMissing", "Verified snapshot file identity is required.");
            string path = ShadowHash.SafeChild(root, file.path);
            ShadowHash.Require(File.Exists(path), "MetadataInputMissing", path);
            ulong size;
            string hash;
            using (var stream = File.OpenRead(path))
            using (var sha256 = SHA256.Create())
            {
                size = checked((ulong)stream.Length);
                hash = string.Concat(sha256.ComputeHash(stream).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
            }
            ShadowHash.Require(string.Equals(hash, file.sha256, StringComparison.OrdinalIgnoreCase), "SnapshotHashMismatch", file.path);
            return new MetadataCapacityInput { name = file.name, bytes = size, dllSize = size, sha256 = hash };
        }
    }

    [Serializable]
    public sealed class MetadataCapacityAllocation
    {
        public string name;
        public ulong bytes;
        public ulong dllSize;
        public string sha256;
        public uint imageIndex;
        public int kind;
        public uint cursorBefore;
        public uint cursorAfter;
        public uint slot;
    }

    [Serializable]
    public sealed class MetadataCapacityReport
    {
        public int schemaVersion = 1;
        public int profileVersion = 1;
        public int metadataIndexBits = 22;
        public int metadataKindBits = 2;
        public string nativeSourceRevision;
        public string nativeHelperSha256;
        public int nativeBudgetCapabilityVersion = 1;
        public MetadataCapacityInput[] inputs = new MetadataCapacityInput[0];
        public MetadataCapacityAllocation[] allocations = new MetadataCapacityAllocation[0];
        public uint[] cursorsBefore = new uint[4];
        public uint[] cursorsAfter = new uint[4];
        public uint[] remainingSlotsBefore = new uint[4];
        public uint[] remainingSlotsAfter = new uint[4];
        public int requiredImages;
        public int acceptedImages;
        public int availableImages;
        public int availableImagesAtFailure;
        public bool fits;
        public int firstFailingIndex = -1;
        public string firstFailingAssembly;
        public ulong firstFailingBytes;
        public ulong firstFailingSize;
        public string failureReason = "None";
        public string firstFailingReason = "None";
        public int ordinaryAssemblyCount;
        public int aotCandidateAssemblyCount;
        public string actualRemainingRuntime = "NotKnown";
        public string runtimeCursorSource = "BaselineOrdinaryPlan";
        public bool ordinaryConsumptionIsEstimate = true;
        public bool runtimeReserveMetadataBudget;
    }

    /// <summary>
    /// The one managed capacity algorithm. Its state transitions intentionally
    /// mirror InterpreterImageBudget.h so dry-run and publication admission use
    /// the same profile and fallback semantics.
    /// </summary>
    public static class MetadataCapacityPlanner
    {
        private const int KindCount = 4;

        public static MetadataCapacityReport Plan(MetadataEncodingProfile profile, IEnumerable<MetadataCapacityInput> inputs)
        {
            return Plan(profile, inputs, (uint[])null);
        }

        public static MetadataCapacityReport Plan(MetadataEncodingProfile profile, IEnumerable<MetadataCapacityInput> inputs, uint[] initialCursors)
        {
            MetadataEncodingProfile.Validate(profile);
            var values = (inputs ?? Enumerable.Empty<MetadataCapacityInput>()).ToArray();
            var before = initialCursors == null ? (uint[])profile.initialCursors.Clone() : (uint[])initialCursors.Clone();
            ShadowHash.Require(before.Length == KindCount, "MetadataCapacityInvalidState", "Metadata capacity cursors must contain four values.");
            var report = new MetadataCapacityReport
            {
                profileVersion = profile.profileVersion,
                metadataIndexBits = profile.metadataIndexBits,
                metadataKindBits = profile.metadataKindBits,
                nativeSourceRevision = profile.nativeSourceRevision,
                nativeHelperSha256 = profile.nativeHelperSha256,
                nativeBudgetCapabilityVersion = profile.nativeBudgetCapabilityVersion,
                inputs = values,
                requiredImages = values.Length,
                cursorsBefore = before,
            };
            ValidateState(profile, before);
            report.remainingSlotsBefore = RemainingSlots(profile, before);
            report.availableImages = TotalSlots(profile, before);
            var current = (uint[])before.Clone();
            var allocations = new List<MetadataCapacityAllocation>(values.Length);
            for (int index = 0; index < values.Length; ++index)
            {
                MetadataCapacityInput input = values[index];
                ShadowHash.Require(input != null && !string.IsNullOrWhiteSpace(input.name), "MetadataInputMissing", "Each metadata input needs an assembly name.");
                ShadowHash.Require(input.dllSize == 0 || input.dllSize == input.bytes, "MetadataInputInvalid",
                    "Metadata input byte fields disagree for " + input.name + ".");
                MetadataCapacityAllocation allocation;
                string reason;
                if (!TryAllocate(profile, current, input.bytes, input, out allocation, out reason))
                {
                    report.acceptedImages = allocations.Count;
                    report.allocations = allocations.ToArray();
                    report.cursorsAfter = (uint[])current.Clone();
                    report.remainingSlotsAfter = RemainingSlots(profile, current);
                    report.availableImagesAtFailure = TotalSlots(profile, current);
                    report.fits = false;
                    report.firstFailingIndex = index;
                    report.firstFailingAssembly = input.name;
                    report.firstFailingBytes = input.bytes;
                    report.firstFailingSize = input.bytes;
                    report.failureReason = reason;
                    report.firstFailingReason = reason;
                    return report;
                }
                allocations.Add(allocation);
            }
            report.acceptedImages = allocations.Count;
            report.allocations = allocations.ToArray();
            report.cursorsAfter = current;
            report.remainingSlotsAfter = RemainingSlots(profile, current);
            report.availableImagesAtFailure = 0;
            report.fits = true;
            report.firstFailingIndex = -1;
            report.failureReason = "None";
            report.firstFailingReason = "None";
            return report;
        }

        public static MetadataCapacityInput[] OrdinaryInputs(string snapshotRoot, AssemblySnapshotReceipt receipt)
        {
            ShadowHash.Require(receipt != null, "MetadataInputMissing", "A verified Player snapshot is required for ordinary metadata capacity.");
            var files = AssemblySnapshot.AllFiles(receipt).ToDictionary(file => AssemblyIdentityUtil.CanonicalName(file.name), StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<MetadataCapacityInput>();
            foreach (string name in receipt.normalHotUpdateAssemblies ?? new string[0])
            {
                string key = AssemblyIdentityUtil.CanonicalName(name);
                ShadowHash.Require(!string.IsNullOrWhiteSpace(key) && seen.Add(key), "MetadataInputMissing", "Ordinary hot-update assembly names must be unique: " + name);
                SnapshotFile file;
                ShadowHash.Require(files.TryGetValue(key, out file), "MetadataInputMissing",
                    "The verified snapshot does not contain the configured ordinary hot-update assembly: " + name);
                result.Add(MetadataCapacityInput.FromVerifiedFile(snapshotRoot, file));
            }
            return result.ToArray();
        }

        public static MetadataCapacityInput[] ClosureInputs(string snapshotRoot, AssemblySnapshotReceipt receipt, IEnumerable<string> loadOrder)
        {
            ShadowHash.Require(receipt != null && loadOrder != null, "MetadataInputMissing", "Verified patch inputs and load order are required.");
            var files = AssemblySnapshot.AllFiles(receipt).ToDictionary(file => AssemblyIdentityUtil.CanonicalName(file.name), StringComparer.OrdinalIgnoreCase);
            var result = new List<MetadataCapacityInput>();
            foreach (string name in loadOrder)
            {
                SnapshotFile file;
                ShadowHash.Require(files.TryGetValue(AssemblyIdentityUtil.CanonicalName(name), out file), "MetadataInputMissing",
                    "The verified compile snapshot does not contain closure assembly: " + name);
                result.Add(MetadataCapacityInput.FromVerifiedFile(snapshotRoot, file));
            }
            return result.ToArray();
        }

        public static void RequireFits(MetadataCapacityReport report)
        {
            ShadowHash.Require(report != null && report.fits, "MetadataCapacityExceeded",
                report == null ? "Metadata capacity report is missing." :
                "Metadata capacity exceeded before publication at " + (report.firstFailingAssembly ?? "<unknown>") +
                " (index " + report.firstFailingIndex + ", reason " + report.failureReason + ").");
        }

        internal static uint[] RemainingSlots(MetadataEncodingProfile profile, uint[] cursors)
        {
            var result = new uint[KindCount];
            for (int kind = 0; kind < KindCount; ++kind)
                result[kind] = cursors[kind] >= profile.kindLimits[kind] ? 0u : (profile.kindLimits[kind] - cursors[kind]) / (uint)profile.kindStrides[kind];
            return result;
        }

        private static int TotalSlots(MetadataEncodingProfile profile, uint[] cursors)
        {
            return (int)RemainingSlots(profile, cursors).Sum(value => (long)value);
        }

        private static void ValidateState(MetadataEncodingProfile profile, uint[] cursors)
        {
            for (int kind = 0; kind < KindCount; ++kind)
                ShadowHash.Require(cursors[kind] <= profile.kindLimits[kind] && cursors[kind] % (uint)profile.kindStrides[kind] == 0 &&
                    (kind != 0 || cursors[kind] >= profile.initialCursors[kind]), "MetadataCapacityInvalidState",
                    "Metadata capacity cursor state is invalid for kind " + kind + ".");
        }

        private static bool TryAllocate(MetadataEncodingProfile profile, uint[] cursors, ulong size, MetadataCapacityInput input,
            out MetadataCapacityAllocation allocation, out string reason)
        {
            allocation = null;
            if (size == 0) { reason = "InvalidSize"; return false; }
            if (size > ulong.MaxValue / (ulong)profile.sizeMultiplier) { reason = "Overflow"; return false; }
            ulong maxDllSizeExclusive = ((ulong)profile.indexMasks[0] + 1UL) / (ulong)profile.sizeMultiplier;
            if (size >= maxDllSizeExclusive) { reason = "Oversize"; return false; }
            ulong scaled = size * (ulong)profile.sizeMultiplier;
            int requestedKind = -1;
            for (int kind = KindCount - 1; kind >= 0; --kind)
                if (scaled <= profile.indexMasks[kind]) { requestedKind = kind; break; }
            if (requestedKind < 0) { reason = "Oversize"; return false; }
            for (int kind = requestedKind; kind >= 0; --kind)
            {
                uint cursor = cursors[kind];
                uint stride = (uint)profile.kindStrides[kind];
                if (cursor >= profile.kindLimits[kind] || profile.kindLimits[kind] - cursor < stride) continue;
                uint next = cursor + stride;
                int kindShift = (32 - profile.metadataIndexBits) - profile.metadataKindBits;
                uint imageIndex = cursor | ((uint)kind << kindShift);
                if (imageIndex == 0) continue;
                cursors[kind] = next;
                allocation = new MetadataCapacityAllocation
                {
                    name = input.name, bytes = size, dllSize = size, sha256 = input.sha256,
                    imageIndex = imageIndex, kind = kind, cursorBefore = cursor, cursorAfter = next, slot = cursor / stride,
                };
                reason = "None";
                return true;
            }
            reason = "Exhausted";
            return false;
        }
    }
}
