using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    public static class TargetFrameworkReferenceVerifier
    {
        /// <summary>Corroborates a verified snapshot with the active target's installed compiler framework bytes.</summary>
        public static VerifiedTargetFrameworkReferences Verify(string root, AssemblySnapshotReceipt verifiedReceipt)
        {
            ShadowHash.Require(verifiedReceipt != null, "FrameworkSnapshotMissing", "A verified compiler/Player snapshot is required.");
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            var namedTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target));
            ApiCompatibilityLevel api = PlayerSettings.GetApiCompatibilityLevel(namedTarget);
            return VerifyAgainstCatalog(root, verifiedReceipt, Application.unityVersion, target.ToString(), CurrentArchitecture(target),
                api.ToString(), EditorApplication.applicationContentsPath, CompilationPipeline.GetSystemAssemblyDirectories(api),
                AssemblySnapshot.TargetCompilerReferences());
        }

        // The public entry point supplies these authorities from Unity, not from a receipt,
        // configuration file, name-prefix rule or caller-selected filesystem root.
        internal static VerifiedTargetFrameworkReferences VerifyAgainstCatalog(string root, AssemblySnapshotReceipt receipt,
            string unityVersion, string target, string architecture, string apiCompatibilityLevel, string editorContentsPath,
            IEnumerable<string> systemDirectories, IEnumerable<string> compilerReferences)
        {
            ShadowHash.Require(receipt != null && receipt.schemaVersion == 1 && receipt.sourcePins != null &&
                !string.IsNullOrWhiteSpace(unityVersion) && !string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(architecture) &&
                !string.IsNullOrWhiteSpace(apiCompatibilityLevel) && receipt.unityVersion == unityVersion && receipt.target == target && receipt.architecture == architecture &&
                receipt.sourcePins.unityVersion == unityVersion && receipt.sourcePins.target == target && receipt.sourcePins.architecture == architecture,
                "FrameworkTargetMismatch", "Snapshot, source pins and current Unity target/architecture must match.");
            ShadowHash.Require(receipt.snapshotHash == AssemblySnapshot.ComputeHash(receipt), "FrameworkSnapshotHashMismatch", "Framework evidence requires unchanged snapshot identity.");
            ShadowHash.Require(!string.IsNullOrWhiteSpace(editorContentsPath) && Path.IsPathRooted(editorContentsPath) &&
                Directory.Exists(editorContentsPath) && systemDirectories != null && compilerReferences != null,
                "FrameworkCatalogUnavailable", "The installed Editor and its compiler system-reference directories are required.");
            string contents = Path.GetFullPath(editorContentsPath).TrimEnd(Path.DirectorySeparatorChar);
            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach (string directory in systemDirectories)
            {
                ShadowHash.Require(!string.IsNullOrWhiteSpace(directory) && Path.IsPathRooted(directory), "FrameworkCatalogUnavailable", "System-reference directory is not absolute.");
                string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
                RequireInstalledPath(contents, full);
                ShadowHash.Require(Directory.Exists(full), "FrameworkCatalogUnavailable", full);
                directories.Add(full);
            }
            ShadowHash.Require(directories.Count > 0, "FrameworkCatalogUnavailable", "Unity supplied no system-reference directories.");
            var catalog = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string reference in compilerReferences.Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(reference) || !Path.IsPathRooted(reference)) continue;
                string full = Path.GetFullPath(reference);
                if (!directories.Contains(Path.GetDirectoryName(full))) continue;
                RequireInstalledPath(contents, full);
                byte[] bytes = File.ReadAllBytes(full);
                using (var module = ModuleDefMD.Load(bytes))
                {
                    ShadowHash.Require(module.Assembly != null, "FrameworkCatalogInvalid", full);
                    string name = AssemblyIdentityUtil.CanonicalName(module.Assembly.Name);
                    var item = new KeyValuePair<string, string>(module.Assembly.FullName, ShadowHash.Bytes(bytes));
                    KeyValuePair<string, string> prior;
                    ShadowHash.Require(!catalog.TryGetValue(name, out prior) || (prior.Key == item.Key && prior.Value == item.Value),
                        "FrameworkCatalogAmbiguous", "Unity supplied conflicting framework DLLs for " + name);
                    catalog[name] = item;
                }
            }
            ShadowHash.Require(catalog.Count > 0, "FrameworkCatalogUnavailable", "No Player compiler references belong to the installed target system directories.");
            var providers = new Dictionary<string, string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SnapshotFile file in receipt.references ?? new SnapshotFile[0])
            {
                ShadowHash.Require(file != null && !string.IsNullOrWhiteSpace(file.name) && file.path == "References/" + file.name + ".dll" &&
                    seen.Add(AssemblyIdentityUtil.CanonicalName(file.name)), "FrameworkReferenceRoleMismatch", "Only unique captured reference-root DLLs can receive framework evidence.");
                byte[] bytes = File.ReadAllBytes(ShadowHash.SafeChild(root, file.path));
                string hash = ShadowHash.Bytes(bytes);
                ShadowHash.Require(hash == file.sha256, "FrameworkReferenceHashMismatch", file.path);
                using (var module = ModuleDefMD.Load(bytes))
                {
                    ShadowHash.Require(module.Assembly != null && AssemblyIdentityUtil.CanonicalName(module.Assembly.Name) == AssemblyIdentityUtil.CanonicalName(file.name),
                        "FrameworkReferenceIdentityMismatch", file.path);
                    KeyValuePair<string, string> installed;
                    if (!catalog.TryGetValue(AssemblyIdentityUtil.CanonicalName(file.name), out installed)) continue;
                    ShadowHash.Require(installed.Key == module.Assembly.FullName && installed.Value == hash, "FrameworkReferenceMismatch",
                        "Captured reference is not the exact installed target compiler framework: " + file.path);
                    providers.Add(installed.Key, installed.Value);
                }
            }
            return new VerifiedTargetFrameworkReferences(unityVersion, target, architecture, apiCompatibilityLevel, receipt.snapshotHash, providers);
        }

        private static void RequireInstalledPath(string contents, string path)
        {
            ShadowHash.Require(path.StartsWith(contents + Path.DirectorySeparatorChar, StringComparison.Ordinal), "FrameworkCatalogOutsideEditor", path);
            for (string current = path; current != null && current.Length >= contents.Length; current = Path.GetDirectoryName(current))
                ShadowHash.Require((File.GetAttributes(current) & System.IO.FileAttributes.ReparsePoint) == 0,
                    "FrameworkCatalogLink", "Compiler framework provenance cannot traverse a filesystem link: " + current);
        }

        private static string CurrentArchitecture(BuildTarget target)
        {
            if (target == BuildTarget.StandaloneOSX)
            {
                var type = Type.GetType("UnityEditor.OSXStandalone.UserBuildSettings, UnityEditor.OSXStandalone.Extensions", false);
                var property = type == null ? null : type.GetProperty("architecture", BindingFlags.Public | BindingFlags.Static);
                ShadowHash.Require(property != null, "FrameworkArchitectureUnavailable", "The pinned macOS target architecture API is unavailable.");
                object value = property.GetValue(null, null);
                ShadowHash.Require(value != null, "FrameworkArchitectureUnavailable", "Unity returned no macOS target architecture.");
                return value.ToString().ToLowerInvariant();
            }
            if (target == BuildTarget.StandaloneWindows64 || target == BuildTarget.StandaloneLinux64) return "x64";
            if (target == BuildTarget.StandaloneWindows) return "x86";
            throw new ShadowBuildException("FrameworkArchitectureUnavailable", "Target framework provenance does not yet support this target: " + target);
        }
    }
}
