using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using HybridCLR.Editor.Settings;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    /// <summary>
    /// A bounded capture around an explicit baseline build. The last assembly
    /// filter observes the actual Player inputs after HybridCLR's ordinary filter.
    /// It returns the same array and never changes what enters AOT.
    /// </summary>
    public sealed class ShadowPlayerInputCapture : IFilterBuildAssemblies, IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        private const string SessionKey = "HybridCLR.AssemblyShadow.PlayerCapture";
        [Serializable] private sealed class Request
        {
            public string root, buildId, architecture, target;
            public ShadowSourcePins pins;
            public string[] protectedAssemblies, normalHotUpdateAssemblies, extraScriptingDefines;
            public AssemblyCapability[] capabilities;
            public SnapshotFile[] beforeFilters;
            public int beforeFilterOptions;
            public bool beforeFiltersCaptured, afterFiltersCaptured;
            public bool linkedDirectoryPrepared;
            public string preprocessBuildGuid, linkedSourceDirectory, linkedCopyDirectory;
            public bool postprocessObserved;
            public string postprocessBuildGuid;
            public int preprocessReportInstanceId;
            public string preprocessOutputPath;
            public bool expectedDevelopment;
        }
        public int callbackOrder { get { return int.MaxValue; } }

        public static void Begin(string root, string buildId, BuildTarget target, string architecture, ShadowSourcePins pins, string[] candidates,
            string[] extraScriptingDefines = null)
        {
            Begin(root, buildId, target, architecture, pins, candidates, extraScriptingDefines, true);
        }

        public static void Begin(string root, string buildId, BuildTarget target, string architecture, ShadowSourcePins pins, string[] candidates,
            string[] extraScriptingDefines, bool developmentBuild)
        {
            ShadowHash.Require(!string.IsNullOrWhiteSpace(buildId) && !Directory.Exists(root), "InvalidCapture", "A new snapshot root and build ID are required.");
            ShadowHash.Require(string.IsNullOrEmpty(SessionState.GetString(SessionKey, "")), "CaptureInProgress", "Finish or abort the preceding explicit Player capture.");
            ShadowPolicyConfiguration policy = AssemblyShadowSettingsUtil.CreatePolicyConfiguration(target);
            ShadowReflectionBindingEvidence.RequireProjectDefines(extraScriptingDefines);
            SessionState.SetString(SessionKey, JsonUtility.ToJson(new Request
            {
                root = Path.GetFullPath(root), buildId = buildId, target = target.ToString(), architecture = architecture, pins = pins,
                protectedAssemblies = (candidates ?? new string[0]).Concat(policy.assemblies.Where(item => item.isShadowCapable || item.isBootstrap).Select(item => item.name))
                    .Select(AssemblyIdentityUtil.CanonicalName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                normalHotUpdateAssemblies = NormalHotUpdateNames(), capabilities = policy.assemblies,
                extraScriptingDefines = extraScriptingDefines ?? new string[0],
                expectedDevelopment = developmentBuild,
            }));
        }

        public static void End() { SessionState.EraseString(SessionKey); }

        public void OnPreprocessBuild(BuildReport report)
        {
            string json = SessionState.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(json)) return;
            var request = JsonUtility.FromJson<Request>(json);
            ShadowHash.Require(!request.linkedDirectoryPrepared && report.summary.platform.ToString() == request.target,
                "LinkedCaptureLifecycle", "A unique matching Player preprocess callback is required. " +
                "linkedDirectoryPrepared=" + request.linkedDirectoryPrepared + "; expectedPlatform=" + request.target +
                "; observedPlatform=" + report.summary.platform + "; observedGuid=" + report.summary.guid +
                "; observedOptions=" + OptionsText((int)report.summary.options) + "; buildId=" + request.buildId);
            request.linkedCopyDirectory = Path.GetFullPath(HybridCLR.Editor.SettingsUtil.GetAssembliesPostIl2CppStripDir(report.summary.platform));
            // HybridCLR's order-0 preprocessor has already recreated this directory.
            // Observe it empty; never erase stale evidence here to manufacture freshness.
            ShadowHash.Require(Directory.Exists(request.linkedCopyDirectory) && Directory.GetFiles(request.linkedCopyDirectory, "*", SearchOption.AllDirectories).Length == 0,
                "LinkedOutputNotCleared", "The upstream stripped-output directory was not freshly cleared: " + request.linkedCopyDirectory);
            // The upstream class is internal; use its public version-specific source
            // locator rather than duplicating platform/Bee path conventions here.
            var copier = typeof(HybridCLR.Editor.SettingsUtil).Assembly.GetType("HybridCLR.Editor.BuildProcessors.CopyStrippedAOTAssemblies");
            var locator = copier == null ? null : copier.GetMethod("GetStripAssembliesDir2021", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            ShadowHash.Require(locator != null, "LinkedSourceLocatorMissing", "This capture requires HybridCLR's Unity-2021-or-newer linked-output locator.");
            string source = (string)locator.Invoke(null, new object[] { report.summary.platform });
            ShadowHash.Require(!string.IsNullOrWhiteSpace(source), "LinkedSourceLocatorMissing", request.target);
            request.linkedSourceDirectory = Path.GetFullPath(source);
            request.preprocessBuildGuid = report.summary.guid.ToString();
            request.preprocessReportInstanceId = report.GetInstanceID();
            request.preprocessOutputPath = CanonicalOutputPath(report.summary.outputPath);
            Guid preprocessGuid;
            ShadowHash.Require(request.preprocessReportInstanceId != 0 && Guid.TryParseExact(request.preprocessBuildGuid, "N", out preprocessGuid),
                "LinkedCaptureLifecycle", "Preprocess requires a live native BuildReport identity and a well-formed (possibly unassigned) GUID.");
            request.linkedDirectoryPrepared = true;
            SessionState.SetString(SessionKey, JsonUtility.ToJson(request));
            Debug.Log("[AssemblyShadow] Player preprocess observed: buildId=" + request.buildId + "; guid=" + request.preprocessBuildGuid +
                "; platform=" + request.target + "; options=" + OptionsText((int)report.summary.options) +
                "; reportInstanceId=" + request.preprocessReportInstanceId + "; output=" + request.preprocessOutputPath + "; root=" + request.root);
        }

        internal static string[] BeforeFilters(BuildOptions buildOptions, string[] assemblies)
        {
            string json = SessionState.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(json)) return assemblies;
            var request = JsonUtility.FromJson<Request>(json);
            ShadowHash.Require(!request.beforeFiltersCaptured, "DuplicateFilterCapture", "The early Player input filter ran more than once.");
            request.beforeFilters = RecordFilterInputs(assemblies);
            request.beforeFilterOptions = (int)buildOptions;
            request.beforeFiltersCaptured = true;
            SessionState.SetString(SessionKey, JsonUtility.ToJson(request));
            Debug.Log("[AssemblyShadow] Player early filter observed: buildId=" + request.buildId + "; options=" + OptionsText(request.beforeFilterOptions) +
                "; linkedDirectoryPrepared=" + request.linkedDirectoryPrepared + "; preprocessGuid=" + request.preprocessBuildGuid);
            return assemblies;
        }

        public string[] OnFilterAssemblies(BuildOptions buildOptions, string[] assemblies)
        {
            string json = SessionState.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(json)) return assemblies;
            var request = JsonUtility.FromJson<Request>(json);
            ShadowHash.Require(((buildOptions & BuildOptions.Development) != 0) == request.expectedDevelopment,
                "CaptureOptions", "The actual Player compilation mode differs from the explicit capture request.");
            var target = (BuildTarget)Enum.Parse(typeof(BuildTarget), request.target);
            ShadowHash.Require(target == EditorUserBuildSettings.activeBuildTarget, "TargetMismatch", "Player capture target changed.");
            ShadowHash.Require(request.beforeFiltersCaptured && request.beforeFilters != null && !request.afterFiltersCaptured, "FilterCaptureMissing", "A unique early input capture is required before the late Player input filter.");
            ShadowHash.Require(request.beforeFilterOptions == (int)buildOptions, "FilterOptionsChanged", "Build options changed between Player assembly filters.");
            ShadowHash.Require(request.normalHotUpdateAssemblies.SequenceEqual(NormalHotUpdateNames(), StringComparer.OrdinalIgnoreCase),
                "HotUpdateRoleChanged", "Ordinary hot-update settings changed during Player filtering.");
            SnapshotFile[] remaining = RecordFilterInputs(assemblies);
            SnapshotFile[] removed = ValidateFilterTransition(request.beforeFilters, remaining, request.protectedAssemblies);
            // Removed paths are read again too: filters cannot modify or delete
            // bytes that the frozen exclusion evidence is about to archive.
            foreach (SnapshotFile file in removed)
                ShadowHash.Require(File.Exists(file.sourcePath) && ShadowHash.File(file.sourcePath) == file.sha256,
                    "FilteredInputChanged", file.sourcePath);
            var receipt = AssemblySnapshot.Capture(request.root, assemblies, AssemblySnapshot.TargetCompilerReferences(), "PlayerBuildInputs", target,
                request.architecture, request.pins, request.extraScriptingDefines, removed.Select(file => file.sourcePath), request.expectedDevelopment);
            receipt.buildId = request.buildId;
            foreach (string candidate in request.protectedAssemblies)
                ShadowHash.Require(receipt.assemblies.Any(f => string.Equals(AssemblyIdentityUtil.CanonicalName(f.name), AssemblyIdentityUtil.CanonicalName(candidate), StringComparison.OrdinalIgnoreCase)), "CandidateFilteredOut", "Shadow candidate/bootstrap did not enter AOT Player: " + candidate);
            var frozenInputs = receipt.assemblies.Concat(receipt.filteredAssemblies).ToDictionary(file => AssemblyIdentityUtil.CanonicalName(file.name), StringComparer.OrdinalIgnoreCase);
            ShadowHash.Require(frozenInputs.Count == request.beforeFilters.Length && request.beforeFilters.All(file =>
                frozenInputs.ContainsKey(AssemblyIdentityUtil.CanonicalName(file.name)) && frozenInputs[AssemblyIdentityUtil.CanonicalName(file.name)].sha256 == file.sha256),
                "FilterCaptureChanged", "Archived pre/post-filter input bytes differ from the early filter capture.");
            receipt.normalHotUpdateAssemblies = request.normalHotUpdateAssemblies;
            receipt.filteredAssemblyCapabilities = removed.Select(file =>
            {
                AssemblyCapability capability = request.capabilities.SingleOrDefault(item =>
                    string.Equals(AssemblyIdentityUtil.CanonicalName(item.name), AssemblyIdentityUtil.CanonicalName(file.name), StringComparison.OrdinalIgnoreCase));
                ShadowHash.Require(capability != null, "FilteredCapabilityMissing", "Removed input was not in the frozen source policy: " + file.name);
                return capability;
            }).OrderBy(item => item.name, StringComparer.Ordinal).ToArray();
            receipt.playerBuildFilterCaptured = true;
            receipt.playerBuildOptions = (int)buildOptions;
            receipt.snapshotHash = AssemblySnapshot.ComputeHash(receipt);
            AssemblySnapshot.WriteReceipt(request.root, receipt);
            request.afterFiltersCaptured = true;
            SessionState.SetString(SessionKey, JsonUtility.ToJson(request));
            Debug.Log("[AssemblyShadow] Captured actual Player input DLLs: " + request.root);
            return assemblies;
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            string json = SessionState.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(json)) return;
            var request = JsonUtility.FromJson<Request>(json);
            // Unity has not finalized summary.result while postprocessors run.
            // Observe this phase without claiming a successful build; the caller
            // must seal only after BuildPipeline.BuildPlayer has returned.
            RequireBuildLifecycle(false, request.linkedDirectoryPrepared, request.afterFiltersCaptured, request.postprocessObserved,
                request.target, report.summary.platform.ToString(), request.preprocessBuildGuid, request.postprocessBuildGuid,
                report.summary.guid.ToString(), request.beforeFilterOptions, (int)report.summary.options,
                request.preprocessReportInstanceId, report.GetInstanceID(), request.preprocessOutputPath, CanonicalOutputPath(report.summary.outputPath));
            request.postprocessObserved = true;
            request.postprocessBuildGuid = report.summary.guid.ToString();
            SessionState.SetString(SessionKey, JsonUtility.ToJson(request));
        }

        public static void CompleteSuccessfulBuild(BuildReport report)
        {
            string json = SessionState.GetString(SessionKey, "");
            ShadowHash.Require(!string.IsNullOrEmpty(json), "CaptureMissing", "Begin a Player capture before sealing its result.");
            var request = JsonUtility.FromJson<Request>(json);
            ShadowHash.Require(report != null && report.summary.result == BuildResult.Succeeded, "PlayerBuildFailed", "Only the finalized successful Player result can establish a baseline.");
            RequireBuildLifecycle(true, request.linkedDirectoryPrepared, request.afterFiltersCaptured, request.postprocessObserved,
                request.target, report.summary.platform.ToString(), request.preprocessBuildGuid, request.postprocessBuildGuid,
                report.summary.guid.ToString(), request.beforeFilterOptions, (int)report.summary.options,
                request.preprocessReportInstanceId, report.GetInstanceID(), request.preprocessOutputPath, CanonicalOutputPath(report.summary.outputPath));
            string receiptPath = Path.Combine(request.root, AssemblySnapshot.ReceiptName);
            ShadowHash.Require(File.Exists(receiptPath), "CaptureMissing", "Player completed without the input capture callback.");
            var receipt = JsonUtility.FromJson<AssemblySnapshotReceipt>(File.ReadAllText(receiptPath));
            ShadowHash.Require(!receipt.playerBuildSucceeded, "CaptureAlreadySealed", "A Player capture can be sealed only once.");
            receipt.playerBuildSucceeded = true;
            receipt.buildGuid = report.summary.guid.ToString();
            receipt.playerOutput = request.preprocessOutputPath;
            string directory = Directory.Exists(receipt.playerOutput) ? receipt.playerOutput : Path.GetDirectoryName(receipt.playerOutput);
            string nativeName = report.summary.platform == BuildTarget.StandaloneOSX ? "GameAssembly.dylib" : "GameAssembly.dll";
            var nativeFiles = Directory.GetFiles(directory, nativeName, SearchOption.AllDirectories);
            ShadowHash.Require(nativeFiles.Length == 1, "NativeEvidenceMissing", "Expected one built " + nativeName + " below " + directory);
            receipt.nativeLibraryPath = Path.GetFullPath(nativeFiles[0]);
            receipt.nativeLibrarySha256 = ShadowHash.File(nativeFiles[0]);
            ValidateLinkedCopy(request.linkedSourceDirectory, request.linkedCopyDirectory);
            ShadowLinkedPlayerEvidence.Capture(request.root, request.linkedSourceDirectory, receipt, request.protectedAssemblies, request.capabilities);
            receipt.snapshotHash = AssemblySnapshot.ComputeHash(receipt);
            AssemblySnapshot.WriteReceipt(request.root, receipt);
            AssemblySnapshot.ReadAndVerify(request.root, true);
            Debug.Log("[AssemblyShadow] Sealed Player input snapshot " + receipt.snapshotHash + " for native " + receipt.nativeLibrarySha256);
        }

        // Unity 2022.3 leaves the preprocess GUID all-zero and assigns it before
        // postprocess. Bind that interval to the live native report and output;
        // once assigned, the GUID must remain stable through finalized success.
        internal static void RequireBuildLifecycle(bool completing, bool linkedDirectoryPrepared, bool afterFiltersCaptured, bool postprocessObserved,
            string expectedTarget, string observedTarget, string preprocessGuid, string postprocessGuid, string observedGuid,
            int expectedOptions, int observedOptions, int expectedReportInstanceId, int observedReportInstanceId,
            string expectedOutputPath, string observedOutputPath)
        {
            var failures = new List<string>();
            if (postprocessObserved != completing) failures.Add("postprocessObserved");
            if (!linkedDirectoryPrepared) failures.Add("linkedDirectoryPrepared");
            if (!afterFiltersCaptured) failures.Add("afterFiltersCaptured");
            if (observedTarget != expectedTarget) failures.Add("platform");
            Guid parsedPreprocess, parsedObserved;
            bool validPreprocess = Guid.TryParseExact(preprocessGuid, "N", out parsedPreprocess);
            bool validObserved = Guid.TryParseExact(observedGuid, "N", out parsedObserved) && parsedObserved != Guid.Empty;
            if (!validPreprocess || (parsedPreprocess != Guid.Empty && observedGuid != preprocessGuid)) failures.Add("preprocessBuildGuid");
            if (!validObserved) failures.Add("assignedBuildGuid");
            if (completing && observedGuid != postprocessGuid) failures.Add("postprocessBuildGuid");
            if (observedOptions != expectedOptions) failures.Add("buildOptions");
            if (expectedReportInstanceId == 0 || observedReportInstanceId != expectedReportInstanceId) failures.Add("reportInstanceId");
            if (string.IsNullOrWhiteSpace(expectedOutputPath) || !Path.IsPathRooted(expectedOutputPath) || observedOutputPath != expectedOutputPath) failures.Add("outputPath");
            ShadowHash.Require(failures.Count == 0, "LinkedCaptureLifecycle",
                "phase=" + (completing ? "CompleteSuccessfulBuild" : "OnPostprocessBuild") + "; failed=[" + string.Join(",", failures.ToArray()) + "]" +
                "; linkedDirectoryPrepared=" + linkedDirectoryPrepared + "; afterFiltersCaptured=" + afterFiltersCaptured +
                "; expectedPostprocessObserved=" + completing + "; observedPostprocessObserved=" + postprocessObserved +
                "; expectedPlatform=" + expectedTarget + "; observedPlatform=" + observedTarget +
                "; preprocessGuid=" + (preprocessGuid ?? "<null>") + "; postprocessGuid=" + (postprocessGuid ?? "<null>") +
                "; observedGuid=" + (observedGuid ?? "<null>") + "; expectedFilterOptions=" + OptionsText(expectedOptions) +
                "; observedReportOptions=" + OptionsText(observedOptions) +
                "; expectedReportInstanceId=" + expectedReportInstanceId + "; observedReportInstanceId=" + observedReportInstanceId +
                "; expectedOutputPath=" + (expectedOutputPath ?? "<null>") + "; observedOutputPath=" + (observedOutputPath ?? "<null>"));
        }

        internal static string CanonicalOutputPath(string path)
        {
            ShadowHash.Require(!string.IsNullOrWhiteSpace(path), "LinkedCaptureLifecycle", "A nonempty Player output path is required in every build phase.");
            string full = Path.GetFullPath(path);
            int rootLength = Path.GetPathRoot(full).Length;
            while (full.Length > rootLength && (full[full.Length - 1] == Path.DirectorySeparatorChar || full[full.Length - 1] == Path.AltDirectorySeparatorChar))
                full = full.Substring(0, full.Length - 1);
            return full;
        }

        private static string OptionsText(int options)
        { return options.ToString(System.Globalization.CultureInfo.InvariantCulture) + " (" + ((BuildOptions)options).ToString() + ")"; }

        private static void ValidateLinkedCopy(string source, string copy)
        {
            ShadowHash.Require(Directory.Exists(source) && Directory.Exists(copy), "LinkedEvidenceMissing", "Current linked output and its fresh upstream copy are required.");
            string[] originals = Directory.GetFiles(source, "*.dll", SearchOption.AllDirectories);
            string[] copied = Directory.GetFiles(copy, "*.dll", SearchOption.AllDirectories);
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in originals)
            {
                string name = Path.GetFileName(path);
                ShadowHash.Require(!expected.ContainsKey(name), "DuplicateLinkedAssembly", name); expected.Add(name, ShadowHash.File(path));
            }
            ShadowHash.Require(expected.Count > 0 && copied.Length == expected.Count, "LinkedCopyMismatch", "Fresh upstream copy differs from the actual linked output set.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in copied)
            {
                string hash, name = Path.GetFileName(path);
                ShadowHash.Require(seen.Add(name) && expected.TryGetValue(name, out hash) && hash == ShadowHash.File(path), "LinkedCopyMismatch", path);
            }
        }

        private static string[] NormalHotUpdateNames()
        {
            var errors = new List<string>();
            string[] names = AssemblyShadowSettingsUtil.Names(HybridCLRSettings.Instance.hotUpdateAssemblyDefinitions,
                HybridCLRSettings.Instance.hotUpdateAssemblies, errors, "ordinary hot-update");
            ShadowHash.Require(errors.Count == 0, "InvalidHotUpdateSettings", string.Join("; ", errors.ToArray()));
            return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        private static SnapshotFile[] RecordFilterInputs(IEnumerable<string> paths)
        {
            ShadowHash.Require(paths != null, "FilterInputsMissing", "Player filter did not supply input DLLs.");
            var result = new List<SnapshotFile>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                ShadowHash.Require(!string.IsNullOrWhiteSpace(path) && File.Exists(path), "FilterInputMissing", path ?? "<null>");
                string physical = Path.GetFullPath(path);
                string name;
                using (ModuleDefMD module = ModuleDefMD.Load(physical))
                {
                    ShadowHash.Require(module.Assembly != null, "InvalidFilterInput", physical);
                    name = AssemblyIdentityUtil.CanonicalName(module.Assembly.Name.String);
                }
                ShadowHash.Require(names.Add(name), "DuplicateFilterInput", name);
                result.Add(new SnapshotFile { name = name, sourcePath = physical, sha256 = ShadowHash.File(physical) });
            }
            return result.OrderBy(file => file.name, StringComparer.Ordinal).ToArray();
        }

        internal static SnapshotFile[] ValidateFilterTransition(SnapshotFile[] before, SnapshotFile[] after, IEnumerable<string> protectedNames)
        {
            ShadowHash.Require(before != null && after != null, "FilterCaptureMissing", "Both pre-filter and post-filter input evidence is required.");
            var original = new Dictionary<string, SnapshotFile>(StringComparer.OrdinalIgnoreCase);
            foreach (SnapshotFile file in before)
            {
                ShadowHash.Require(file != null && !string.IsNullOrWhiteSpace(file.name) && !string.IsNullOrWhiteSpace(file.sourcePath) &&
                    !string.IsNullOrWhiteSpace(file.sha256), "InvalidFilterInput", "Early input evidence is incomplete.");
                string name = AssemblyIdentityUtil.CanonicalName(file.name);
                ShadowHash.Require(!original.ContainsKey(name), "DuplicateFilterInput", name); original.Add(name, file);
            }
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SnapshotFile file in after)
            {
                ShadowHash.Require(file != null && !string.IsNullOrWhiteSpace(file.name), "InvalidFilterInput", "Late input evidence has no identity.");
                string name = AssemblyIdentityUtil.CanonicalName(file.name);
                SnapshotFile prior;
                ShadowHash.Require(retained.Add(name), "DuplicateFilterInput", name);
                ShadowHash.Require(original.TryGetValue(name, out prior), "InjectedFilterInput", "A filter inserted an uncaptured assembly: " + name);
                ShadowHash.Require(string.Equals(prior.sourcePath, file.sourcePath, StringComparison.Ordinal) && prior.sha256 == file.sha256,
                    "FilterInputChanged", "A filter replaced or modified input bytes: " + name);
            }
            foreach (string protectedName in protectedNames ?? new string[0])
                ShadowHash.Require(retained.Contains(AssemblyIdentityUtil.CanonicalName(protectedName)), "CandidateFilteredOut",
                    "Candidate/bootstrap did not enter AOT Player: " + protectedName);
            return before.Where(file => !retained.Contains(AssemblyIdentityUtil.CanonicalName(file.name))).ToArray();
        }
    }

    // Separate callbacks are necessary: one records the complete input set before
    // ordinary-hotupdate, test framework, or any other registered Player filters.
    public sealed class ShadowPlayerInputBeforeFiltersCapture : IFilterBuildAssemblies
    {
        public int callbackOrder { get { return int.MinValue; } }
        public string[] OnFilterAssemblies(BuildOptions buildOptions, string[] assemblies)
        { return ShadowPlayerInputCapture.BeforeFilters(buildOptions, assemblies); }
    }
}
