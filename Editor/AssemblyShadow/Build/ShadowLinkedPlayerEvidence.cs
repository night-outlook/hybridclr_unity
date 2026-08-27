using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class LinkedPlayerFile
    {
        public string name, path, sha256, mvid, pdbPath, pdbSha256;
    }

    [Serializable]
    public sealed class LinkedPlayerReceipt
    {
        public int schemaVersion = 1;
        public string buildGuid, nativeLibrarySha256, target, architecture, sourceDirectory;
        public string[] protectedAssemblies = new string[0];
        public LinkedPlayerFile[] assemblies = new LinkedPlayerFile[0];
    }

    /// <summary>Linked bytes prove runtime membership; pre-strip bytes remain the semantic comparison inputs.</summary>
    public static class ShadowLinkedPlayerEvidence
    {
        public const string DirectoryName = "LinkedPlayer";
        public const string ReceiptName = "linked-player-receipt.json";

        public static bool IsAbsent(AssemblySnapshotReceipt snapshot)
        {
            if (snapshot == null || !string.IsNullOrEmpty(snapshot.linkedPlayerReceiptHash) ||
                (snapshot.linkerExcludedAssemblies != null && snapshot.linkerExcludedAssemblies.Length != 0) ||
                (snapshot.linkerExcludedAssemblyCapabilities != null && snapshot.linkerExcludedAssemblyCapabilities.Length != 0)) return false;
            LinkedPlayerReceipt linked = snapshot.linkedPlayerReceipt;
            // Inline Unity serialization does not preserve null class identity.
            // Accept both zero-initialized and constructor-default empty objects,
            // but never discard partial provenance, entries, or an unknown schema.
            return linked == null || ((linked.schemaVersion == 0 || linked.schemaVersion == 1) &&
                string.IsNullOrEmpty(linked.buildGuid) && string.IsNullOrEmpty(linked.nativeLibrarySha256) &&
                string.IsNullOrEmpty(linked.target) && string.IsNullOrEmpty(linked.architecture) && string.IsNullOrEmpty(linked.sourceDirectory) &&
                (linked.protectedAssemblies == null || linked.protectedAssemblies.Length == 0) &&
                (linked.assemblies == null || linked.assemblies.Length == 0));
        }

        public static void Capture(string snapshotRoot, string sourceDirectory, AssemblySnapshotReceipt player,
            IEnumerable<string> protectedNames, IEnumerable<AssemblyCapability> capabilities)
        {
            string destination = Path.Combine(snapshotRoot, DirectoryName);
            ShadowHash.Require(!Directory.Exists(destination), "LinkedEvidenceExists", "Linked evidence is immutable: " + destination);
            ShadowHash.Require(Directory.Exists(sourceDirectory), "LinkedEvidenceMissing", sourceDirectory);
            var linked = new LinkedPlayerReceipt
            {
                buildGuid = player.buildGuid, nativeLibrarySha256 = player.nativeLibrarySha256, target = player.target,
                architecture = player.architecture, sourceDirectory = Path.GetFullPath(sourceDirectory),
                protectedAssemblies = (protectedNames ?? new string[0]).Select(AssemblyIdentityUtil.CanonicalName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            };
            var files = new List<LinkedPlayerFile>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var symbols = new HashSet<string>(StringComparer.Ordinal);
            Directory.CreateDirectory(Path.Combine(destination, "Assemblies"));
            foreach (string source in Directory.GetFiles(sourceDirectory, "*.dll", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                string name, mvid;
                using (var module = ModuleDefMD.Load(source))
                {
                    ShadowHash.Require(module.Assembly != null && module.Mvid.HasValue && module.Mvid.Value != Guid.Empty, "InvalidLinkedAssembly", source);
                    name = AssemblyIdentityUtil.CanonicalName(module.Assembly.Name.String); mvid = module.Mvid.Value.ToString("D");
                }
                ShadowHash.Require(names.Add(name), "DuplicateLinkedAssembly", name);
                var file = new LinkedPlayerFile { name = name, path = "Assemblies/" + name + ".dll", sha256 = ShadowHash.File(source), mvid = mvid };
                File.Copy(source, ShadowHash.SafeChild(destination, file.path), false);
                string pdb = Path.ChangeExtension(source, ".pdb");
                if (File.Exists(pdb))
                {
                    symbols.Add(Path.GetFullPath(pdb)); file.pdbPath = "Assemblies/" + name + ".pdb"; file.pdbSha256 = ShadowHash.File(pdb);
                    File.Copy(pdb, ShadowHash.SafeChild(destination, file.pdbPath), false);
                }
                files.Add(file);
            }
            ShadowHash.Require(symbols.SetEquals(Directory.GetFiles(sourceDirectory, "*.pdb", SearchOption.AllDirectories).Select(Path.GetFullPath)),
                "UnmatchedLinkedSymbols", "All linked PDBs must belong to a captured DLL.");
            linked.assemblies = files.OrderBy(f => f.name, StringComparer.Ordinal).ToArray();
            player.linkedPlayerReceipt = linked;
            player.linkedPlayerReceiptHash = ComputeHash(linked);
            player.linkerExcludedAssemblies = (player.assemblies ?? new SnapshotFile[0]).Select(f => AssemblyIdentityUtil.CanonicalName(f.name))
                .Where(n => !names.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var roles = (capabilities ?? new AssemblyCapability[0]).ToArray();
            player.linkerExcludedAssemblyCapabilities = player.linkerExcludedAssemblies.Select(name =>
            {
                var matches = roles.Where(c => c != null && string.Equals(AssemblyIdentityUtil.CanonicalName(c.name), name, StringComparison.OrdinalIgnoreCase)).ToArray();
                ShadowHash.Require(matches.Length == 1, "LinkedCapabilityMissing", "Excluded input needs one original capability: " + name);
                var role = matches[0];
                return new AssemblyCapability { name = role.name, classification = role.classification, isPrecompiled = role.isPrecompiled,
                    isShadowCapable = role.isShadowCapable, isBootstrap = role.isBootstrap, capabilityDeclared = role.capabilityDeclared };
            }).ToArray();
            ValidateReceipt(player);
            File.WriteAllText(Path.Combine(destination, ReceiptName), JsonUtility.ToJson(linked, true), new UTF8Encoding(false));
            ReadAndVerify(snapshotRoot, player);
        }

        public static string ComputeHash(LinkedPlayerReceipt receipt)
        {
            var text = new StringBuilder("assembly-shadow-linked-player:1\n");
            text.Append(receipt.schemaVersion).Append('\n').Append(receipt.buildGuid).Append('\n').Append(receipt.nativeLibrarySha256).Append('\n')
                .Append(receipt.target).Append('\n').Append(receipt.architecture).Append('\n').Append(receipt.sourceDirectory).Append('\n');
            foreach (string name in (receipt.protectedAssemblies ?? new string[0]).OrderBy(n => n, StringComparer.Ordinal)) text.Append("protected:").Append(name).Append('\n');
            foreach (var file in (receipt.assemblies ?? new LinkedPlayerFile[0]).OrderBy(f => f.path, StringComparer.Ordinal))
                text.Append(file.name).Append('\n').Append(file.path).Append('\n').Append(file.sha256).Append('\n').Append(file.mvid).Append('\n')
                    .Append(file.pdbPath).Append('\n').Append(file.pdbSha256).Append('\n');
            return ShadowHash.Text(text.ToString());
        }

        public static void ValidateReceipt(AssemblySnapshotReceipt player)
        {
            LinkedPlayerReceipt linked = player == null ? null : player.linkedPlayerReceipt;
            ShadowHash.Require(linked != null && linked.schemaVersion == 1 && player.kind == "PlayerBuildInputs" && player.playerBuildSucceeded &&
                player.playerBuildFilterCaptured && !string.IsNullOrWhiteSpace(linked.buildGuid) && IsHash(linked.nativeLibrarySha256) &&
                !string.IsNullOrWhiteSpace(linked.target) && !string.IsNullOrWhiteSpace(linked.architecture) && !string.IsNullOrWhiteSpace(linked.sourceDirectory) &&
                Path.IsPathRooted(linked.sourceDirectory) && linked.assemblies != null && linked.assemblies.Length > 0,
                "LinkedEvidenceMissing", "A successful Player requires a complete linked-output receipt.");
            ShadowHash.Require(linked.buildGuid == player.buildGuid && linked.nativeLibrarySha256 == player.nativeLibrarySha256 && linked.target == player.target &&
                linked.architecture == player.architecture, "LinkedBuildIdentityMismatch", "Linked evidence belongs to a different Player build.");
            var linkedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LinkedPlayerFile file in linked.assemblies)
            {
                Guid mvid;
                ShadowHash.Require(file != null && !string.IsNullOrWhiteSpace(file.name) && file.name == AssemblyIdentityUtil.CanonicalName(file.name) &&
                    file.path == "Assemblies/" + file.name + ".dll" && IsHash(file.sha256) && Guid.TryParse(file.mvid, out mvid) && mvid != Guid.Empty &&
                    ((string.IsNullOrEmpty(file.pdbPath) && string.IsNullOrEmpty(file.pdbSha256)) ||
                     (file.pdbPath == "Assemblies/" + file.name + ".pdb" && IsHash(file.pdbSha256))), "InvalidLinkedAssembly", "Linked file identity is incomplete.");
                ShadowHash.Require(linkedNames.Add(file.name), "DuplicateLinkedAssembly", file.name);
            }
            ShadowHash.Require(ComputeHash(linked) == player.linkedPlayerReceiptHash, "LinkedReceiptHashMismatch", "Linked receipt has changed.");
            var actual = Names((player.assemblies ?? new SnapshotFile[0]).Select(f => f.name), "DuplicatePlayerInput");
            var excluded = Names(player.linkerExcludedAssemblies, "InvalidLinkerExclusions");
            ShadowHash.Require(excluded.SetEquals(actual.Where(n => !linkedNames.Contains(n))), "LinkerExclusionSetMismatch", "Linker exclusions must exactly equal captured inputs absent from linked output.");
            var stamped = Names((player.linkerExcludedAssemblyCapabilities ?? new AssemblyCapability[0]).Select(c => c == null ? null : c.name), "InvalidLinkerCapability");
            ShadowHash.Require(excluded.SetEquals(stamped), "LinkerCapabilitySetMismatch", "Every linker-excluded input needs its original capability stamp.");
            var filtered = Names((player.filteredAssemblies ?? new SnapshotFile[0]).Select(f => f.name), "DuplicateFilteredAssembly");
            ShadowHash.Require(!filtered.Overlaps(linkedNames), "FilteredAssemblyLinked", "A callback-removed input unexpectedly entered the linked Player.");
            var protectedSet = Names(linked.protectedAssemblies, "InvalidLinkedProtectedSet");
            ShadowHash.Require(protectedSet.Count > 0 && protectedSet.IsSubsetOf(actual) && protectedSet.IsSubsetOf(linkedNames),
                "LinkedCandidateMissing", "Every captured candidate/bootstrap must remain present after linking.");
            foreach (AssemblyCapability role in player.linkerExcludedAssemblyCapabilities ?? new AssemblyCapability[0])
                ShadowHash.Require(!role.isShadowCapable && !role.isBootstrap && role.classification != AssemblyClassification.BuildFiltered,
                    "LinkedCandidateMissing", "Candidate/bootstrap or derived role cannot be a linker exclusion: " + role.name);
        }

        public static void ReadAndVerify(string snapshotRoot, AssemblySnapshotReceipt player)
        {
            ValidateReceipt(player);
            string root = Path.Combine(snapshotRoot, DirectoryName), receiptPath = Path.Combine(root, ReceiptName);
            ShadowHash.Require(File.Exists(receiptPath), "LinkedReceiptMissing", receiptPath);
            var disk = JsonUtility.FromJson<LinkedPlayerReceipt>(File.ReadAllText(receiptPath));
            ShadowHash.Require(disk != null && ComputeHash(disk) == player.linkedPlayerReceiptHash, "LinkedReceiptHashMismatch", receiptPath);
            var expected = new HashSet<string>(StringComparer.Ordinal) { Path.GetFullPath(receiptPath) };
            foreach (LinkedPlayerFile file in player.linkedPlayerReceipt.assemblies)
            {
                string path = ShadowHash.SafeChild(root, file.path); expected.Add(Path.GetFullPath(path));
                ShadowHash.Require(File.Exists(path) && ShadowHash.File(path) == file.sha256, "LinkedFileHashMismatch", file.path);
                using (var module = ModuleDefMD.Load(path))
                    ShadowHash.Require(module.Assembly != null && AssemblyIdentityUtil.CanonicalName(module.Assembly.Name.String) == file.name &&
                        module.Mvid.HasValue && module.Mvid.Value.ToString("D") == file.mvid, "LinkedFileIdentityMismatch", file.path);
                if (!string.IsNullOrEmpty(file.pdbPath))
                {
                    string pdb = ShadowHash.SafeChild(root, file.pdbPath); expected.Add(Path.GetFullPath(pdb));
                    ShadowHash.Require(File.Exists(pdb) && ShadowHash.File(pdb) == file.pdbSha256, "LinkedPdbHashMismatch", file.pdbPath);
                }
            }
            ShadowHash.Require(expected.SetEquals(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(Path.GetFullPath)),
                "LinkedFileSetMismatch", "Linked proof contains undeclared or missing files.");
        }

        public static void Copy(string sourceSnapshotRoot, string destinationSnapshotRoot, AssemblySnapshotReceipt player)
        {
            ReadAndVerify(sourceSnapshotRoot, player);
            string source = Path.Combine(sourceSnapshotRoot, DirectoryName), destination = Path.Combine(destinationSnapshotRoot, DirectoryName);
            ShadowHash.Require(!Directory.Exists(destination), "LinkedEvidenceExists", destination);
            foreach (string path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = path.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar);
                string target = ShadowHash.SafeChild(destination, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(path, target, false);
            }
            ReadAndVerify(destinationSnapshotRoot, player);
        }

        private static HashSet<string> Names(IEnumerable<string> values, string code)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in values ?? new string[0])
                ShadowHash.Require(!string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(AssemblyIdentityUtil.CanonicalName(value)) &&
                    result.Add(AssemblyIdentityUtil.CanonicalName(value)), code, "Invalid or duplicate assembly identity: " + value);
            return result;
        }

        private static bool IsHash(string value) { return value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'); }
    }
}
