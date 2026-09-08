using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    /// <summary>
    /// Integrity/metadata replay, not baseline policy authorization. The caller
    /// supplies the independently verified current compiler set and still owns
    /// baseline, linked-runtime, source-pin, resource and acquisition policy.
    /// </summary>
    public static class ShadowWarmupManifestReader
    {
        public static ShadowWarmupManifestReadResult ReadAndVerify(string path, CompiledAssemblySet verifiedInputs)
        {
            ShadowHash.Require(verifiedInputs != null && Path.IsPathRooted(path ?? ""), "WarmupInputs", "An absolute manifest path and verified input set are required.");
            string root = Path.GetDirectoryName(path), hashPath = Path.Combine(root, "manifest.sha256");
            ShadowHash.Require(File.Exists(path) && File.Exists(hashPath), "ManifestHashMismatch", path);
            ShadowHash.Require(new FileInfo(path).Length <= 16 * 1024 * 1024, "WarmupManifestSchema", "Oversized manifest.");
            byte[] bytes = File.ReadAllBytes(path);
            ShadowHash.Require(File.ReadAllText(hashPath).Trim() == ShadowHash.Bytes(bytes), "ManifestHashMismatch", path);
            var json = ShadowWarmupJson.Parse(bytes);
            object version;
            ShadowHash.Require(json.TryGetValue("schemaVersion", out version) && version is int && ((int)version == 1 || (int)version == 2),
                "ManifestSchema", "Only explicit schema 1 or 2 patch manifests are supported.");
            ShadowPatchManifest manifest; ShadowWarmupPlan plan = null;
            if ((int)version == 1)
            {
                // Keep historical schema-1 deserialization/default behavior.
                manifest = JsonUtility.FromJson<ShadowPatchManifest>(new UTF8Encoding(false, true).GetString(bytes));
            }
            else
            {
                var envelope = ShadowWarmupJson.Convert<ShadowPatchManifestV2>(json);
                manifest = envelope.patch; plan = envelope.warmup;
            }
            ShadowHash.Require(manifest != null && manifest.schemaVersion == 1 && manifest.semanticHashSchema == 1 &&
                manifest.closure != null && manifest.loadOrder != null && manifest.closure.Length > 0 && manifest.closure.Length <= 4096 &&
                manifest.closure.All(entry => entry != null && !string.IsNullOrWhiteSpace(entry.name)), "ManifestSchema", "Malformed base patch proof.");
            ShadowHash.Require(manifest.nativeBudgetCapabilityVersion == 0 || manifest.nativeBudgetCapabilityVersion == 1,
                "WarmupManifestSchema", "Unsupported metadata budget capability.");
            if (manifest.nativeBudgetCapabilityVersion == 1)
            {
                ShadowHash.Require(manifest.metadataEncodingProfile != null && manifest.metadataCapacityReport != null &&
                    manifest.metadataCapacityReport.nativeBudgetCapabilityVersion == 1 && manifest.metadataCapacityReport.profileVersion == 1 &&
                    manifest.metadataCapacityReport.runtimeReserveMetadataBudget && manifest.metadataCapacityReport.fits &&
                    manifest.metadataCapacityReport.requiredImages == manifest.closure.Length && manifest.closure.All(entry => entry.dllSize > 0),
                    "WarmupManifestSchema", "Incomplete R01 metadata reservation contract.");
                manifest.metadataEncodingProfile.ValidateOrThrow();
            }
            var names = manifest.closure.Select(entry => entry.name).ToArray();
            ShadowHash.Require(names.Select(AssemblyIdentityUtil.CanonicalName).Distinct(StringComparer.Ordinal).Count() == names.Length &&
                manifest.loadOrder.Length == names.Length && manifest.loadOrder.Distinct(StringComparer.Ordinal).Count() == names.Length &&
                names.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(manifest.loadOrder.OrderBy(name => name, StringComparer.Ordinal)),
                "WarmupClosure", "Closure/load-order inventory differs.");
            foreach (var entry in manifest.closure)
            {
                AssemblyDescriptor captured = verifiedInputs.Get(entry.name);
                ShadowHash.Require(entry.name == captured.name && entry.sha256 == captured.sha256 && entry.mvid == captured.mvid &&
                    ShadowHash.File(ShadowHash.SafeChild(root, entry.dll)) == captured.sha256,
                    "WarmupClosureBytes", "Patch DLL differs from verified compiler bytes: " + entry.name);
                ShadowHash.Require(entry.dllSize == 0 || entry.dllSize == (ulong)new FileInfo(ShadowHash.SafeChild(root, entry.dll)).Length,
                    "WarmupClosureBytes", "Patch DLL size differs from verified compiler bytes: " + entry.name);
                ShadowHash.Require(string.IsNullOrEmpty(entry.pdb) == string.IsNullOrEmpty(entry.pdbSha256), "WarmupClosureBytes", "Incomplete PDB binding.");
                if (!string.IsNullOrEmpty(entry.pdb))
                    ShadowHash.Require(ShadowHash.File(ShadowHash.SafeChild(root, entry.pdb)) == entry.pdbSha256, "WarmupClosureBytes", "Patch PDB changed.");
            }
            if ((int)version == 2) plan = ShadowWarmupValidator.ValidateAndClone(plan, verifiedInputs, names);
            return new ShadowWarmupManifestReadResult { SchemaVersion = (int)version, BaseManifest = manifest, Warmup = plan };
        }
    }
}
