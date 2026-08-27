using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace HybridCLR.Editor.Installer
{
    /// <summary>
    /// Installs the native sources described by ProjectSettings/AssemblyShadowSourcePins.json.
    /// This is deliberately opt-in; InstallerController.InstallDefaultHybridCLR keeps its
    /// existing branch-based behavior.
    /// </summary>
    public static class PinnedSourceInstaller
    {
        private const int SchemaVersion = 1;
        private const string PinsFileName = "AssemblyShadowSourcePins.json";
        private const string ReceiptFileName = "assembly-shadow-install.json";
        private const string StagingDirectoryName = "PinnedSourceInstaller";
        private static readonly Regex s_revisionPattern = new Regex("^[0-9a-fA-F]{40}$");

        // These files are expected to change when the normal HybridCLR generators run.
        // Source hashes still include the three files that exist in the source repository;
        // the exclusions make their post-install mutability explicit to verifiers.
        private static readonly string[] s_generatedFileExclusions =
        {
            "hybridclr/generated/AssemblyManifest.cpp",
            "hybridclr/generated/MethodBridge.cpp",
            "hybridclr/generated/UnityVersion.h",
            "hybridclr/generated/libil2cpp-version.txt",
        };

        [Serializable]
        private sealed class RepositoryPin
        {
            public string url;
            public string revision;
            public string localPath;
        }

        [Serializable]
        private sealed class RepositoryPins
        {
            public RepositoryPin hybridclr;
            public RepositoryPin hybridclrUnity;
            public RepositoryPin il2cppPlus;
            public RepositoryPin demo;
        }

        [Serializable]
        private sealed class SourcePins
        {
            public int schemaVersion;
            public string unityVersion;
            public string target;

            // M00's original manifest put these four fields at the top level. The nested
            // form is accepted as well so a project may group all repository inputs.
            public RepositoryPin hybridclr;
            public RepositoryPin hybridclrUnity;
            public RepositoryPin il2cppPlus;
            public RepositoryPin demo;
            public RepositoryPins repositories;
        }

        [Serializable]
        private sealed class SourceFileHash
        {
            public string source;
            public string path;
            public string sha256;
        }

        [Serializable]
        private sealed class InstallReceipt
        {
            public int schemaVersion;
            public string installMode;
            public string unityVersion;
            public string target;
            public string packageVersion;
            public string packageRevision;
            public RepositoryPins repositories;
            public SourceFileHash[] sourceFileHashes;
            public int defaultShadowMacro;
            public string defaultShadowMacroSource;
            public string[] generatedFileExclusions;
            public string receiptFile;
        }

        private sealed class ResolvedRepository
        {
            public string name;
            public RepositoryPin pin;
            public string path;
            public string gitRoot;
            public string currentRevision;
        }

        private sealed class GitTreeEntry
        {
            public string path;
            public string objectId;
        }

        /// <summary>
        /// Installs exactly the local source revisions from the project's source pins.
        /// </summary>
        public static void Install()
        {
            SourcePins pins = ReadPins();
            ValidateProjectIdentity(pins);

            RepositoryPins repositories = GetRepositories(pins);
            ResolvedRepository hybridclr = ResolveRepository("hybridclr", repositories.hybridclr, true);
            ResolvedRepository hybridclrUnity = ResolveRepository("hybridclrUnity", repositories.hybridclrUnity, true);
            ResolvedRepository il2cppPlus = ResolveRepository("il2cppPlus", repositories.il2cppPlus, true);
            ResolvedRepository demo = ResolveRepository("demo", repositories.demo, false);

            ValidatePackageSource(hybridclrUnity);
            ValidateNativeSource(il2cppPlus);
            ValidateHybridclrSource(hybridclr);

            string stagingRoot = Path.Combine(SettingsUtil.HybridCLRDataDir, StagingDirectoryName);
            string stagingLibil2cpp = Path.Combine(stagingRoot, "libil2cpp");
            try
            {
                RecreatePrivateStagingDirectory(stagingRoot);
                BashUtil.CopyDir(Path.Combine(il2cppPlus.path, "libil2cpp"), stagingLibil2cpp);
                BashUtil.CopyDir(Path.Combine(hybridclr.path, "hybridclr"), Path.Combine(stagingLibil2cpp, "hybridclr"));

                var expectedFiles = BuildSourceHashes(hybridclr, il2cppPlus);
                VerifyStagingHashes(stagingLibil2cpp, expectedFiles);

                var controller = new InstallerController();
                if (controller.GetCompatibleType() == InstallerController.CompatibleType.Incompatible)
                {
                    throw new InvalidOperationException(
                        $"Pinned install is incompatible with Unity {Application.unityVersion}.");
                }

                controller.InstallFromLocal(stagingLibil2cpp);
                string installedRoot = Path.Combine(SettingsUtil.LocalIl2CppDir, "libil2cpp");
                VerifyInstalledFiles(installedRoot, expectedFiles);
                VerifyInstalledSourceHashes(installedRoot, expectedFiles);
                VerifyDefaultShadowMacro(installedRoot);
                WriteReceipt(installedRoot, pins, repositories, hybridclr, hybridclrUnity, il2cppPlus, demo, expectedFiles);
                Debug.Log($"Pinned HybridCLR source installation succeeded at {installedRoot}");
            }
            finally
            {
                // This is only our private staging directory. Do not remove source checkouts,
                // LocalIl2CppData, or the project's general build caches here.
                if (Directory.Exists(stagingRoot))
                {
                    BashUtil.RemoveDir(stagingRoot);
                }
            }
        }

        private static SourcePins ReadPins()
        {
            string path = Path.Combine(SettingsUtil.ProjectDir, "ProjectSettings", PinsFileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Pinned source manifest was not found: {path}");
            }

            SourcePins pins = JsonUtility.FromJson<SourcePins>(File.ReadAllText(path, Encoding.UTF8));
            if (pins == null || pins.schemaVersion != SchemaVersion)
            {
                throw new InvalidDataException($"{PinsFileName} must use schemaVersion {SchemaVersion}.");
            }
            if (string.IsNullOrEmpty(pins.unityVersion) || string.IsNullOrEmpty(pins.target))
            {
                throw new InvalidDataException($"{PinsFileName} must specify unityVersion and target.");
            }
            return pins;
        }

        private static RepositoryPins GetRepositories(SourcePins pins)
        {
            RepositoryPins repositories = pins.repositories ?? new RepositoryPins();
            repositories.hybridclr = repositories.hybridclr ?? pins.hybridclr;
            repositories.hybridclrUnity = repositories.hybridclrUnity ?? pins.hybridclrUnity;
            repositories.il2cppPlus = repositories.il2cppPlus ?? pins.il2cppPlus;
            repositories.demo = repositories.demo ?? pins.demo;
            return repositories;
        }

        private static void ValidateProjectIdentity(SourcePins pins)
        {
            if (!string.Equals(pins.unityVersion, Application.unityVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Pinned Unity version '{pins.unityVersion}' does not match '{Application.unityVersion}'.");
            }

            string currentTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
            if (!string.Equals(pins.target, currentTarget, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Pinned target '{pins.target}' does not match active target '{currentTarget}'.");
            }
        }

        private static ResolvedRepository ResolveRepository(string name, RepositoryPin pin, bool requireClean)
        {
            if (pin == null || string.IsNullOrEmpty(pin.url) || string.IsNullOrEmpty(pin.localPath)
                || !s_revisionPattern.IsMatch(pin.revision ?? string.Empty))
            {
                throw new InvalidDataException(
                    $"Repository '{name}' requires url, localPath, and a full 40-character revision.");
            }
            if (Path.IsPathRooted(pin.localPath))
            {
                throw new InvalidDataException($"Repository '{name}' localPath must be relative to the project.");
            }

            string path = Path.GetFullPath(Path.Combine(SettingsUtil.ProjectDir, pin.localPath));
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"Repository '{name}' localPath does not exist: {path}");
            }

            string root = Git(path, "rev-parse", "--show-toplevel");
            root = Path.GetFullPath(root);
            if (!string.Equals(root, path, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Repository '{name}' localPath must be its git root. Path '{path}', root '{root}'.");
            }

            string currentRevision = Git(path, "rev-parse", "HEAD");
            if (requireClean && !string.Equals(currentRevision, pin.revision, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Repository '{name}' HEAD '{currentRevision}' does not match pinned revision '{pin.revision}'.");
            }
            if (requireClean)
            {
                string status = Git(path, "status", "--porcelain=v1", "--untracked-files=all");
                if (!string.IsNullOrEmpty(status))
                {
                    throw new InvalidDataException(
                        $"Repository '{name}' has a dirty tracked/package source; clean it before pinned install.\n{status}");
                }
            }
            else
            {
                // The demo manifest cannot pin the commit that contains itself. Verify that
                // the requested object exists while leaving source-input validation to the demo verifier.
                Git(path, "cat-file", "-e", pin.revision + "^{commit}");
            }

            return new ResolvedRepository { name = name, pin = pin, path = path, gitRoot = root, currentRevision = currentRevision };
        }

        private static void ValidatePackageSource(ResolvedRepository repository)
        {
            string packageRoot = Path.GetFullPath(SettingsUtil.PackageRootPath);
            if (!string.Equals(packageRoot, repository.path, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Resolved UPM package path '{packageRoot}' does not match pinned hybridclrUnity localPath '{repository.path}'.");
            }
            if (!File.Exists(Path.Combine(packageRoot, "package.json")))
            {
                throw new InvalidDataException($"Pinned hybridclrUnity package has no package.json: {packageRoot}");
            }
        }

        private static void ValidateNativeSource(ResolvedRepository repository)
        {
            string libil2cpp = Path.Combine(repository.path, "libil2cpp");
            if (!Directory.Exists(libil2cpp))
            {
                throw new InvalidDataException($"Pinned il2cppPlus source has no libil2cpp directory: {libil2cpp}");
            }
        }

        private static void ValidateHybridclrSource(ResolvedRepository repository)
        {
            string hybridclr = Path.Combine(repository.path, "hybridclr");
            if (!Directory.Exists(hybridclr))
            {
                throw new InvalidDataException($"Pinned hybridclr source has no hybridclr directory: {hybridclr}");
            }
        }

        private static List<SourceFileHash> BuildSourceHashes(ResolvedRepository hybridclr, ResolvedRepository il2cppPlus)
        {
            var files = new List<SourceFileHash>();
            AddTrackedSourceHashes(files, il2cppPlus, "libil2cpp", "il2cppPlus");
            AddTrackedSourceHashes(files, hybridclr, "hybridclr", "hybridclr");
            if (files.Select(file => file.path).Distinct(StringComparer.Ordinal).Count() != files.Count)
            {
                throw new InvalidDataException("Pinned source repositories contain colliding libil2cpp paths.");
            }
            return files.OrderBy(file => file.path, StringComparer.Ordinal).ToList();
        }

        private static void AddTrackedSourceHashes(List<SourceFileHash> files, ResolvedRepository repository,
            string sourceDirectory, string sourceName)
        {
            List<GitTreeEntry> entries = GetGitTreeEntries(repository, sourceDirectory);
            var trackedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (GitTreeEntry entry in entries)
            {
                string normalizedRepoPath = entry.path.Replace('\\', '/');
                trackedPaths.Add(normalizedRepoPath);
                string sourcePath = Path.Combine(repository.path, normalizedRepoPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(sourcePath))
                {
                    throw new InvalidDataException($"Tracked source file is missing: {sourcePath}");
                }
                if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Symlinked source files are not allowed: {normalizedRepoPath}");
                }
                if (!string.Equals(entry.objectId, GitBlobSha1(sourcePath), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Source file does not match pinned git blob {repository.pin.revision}: {normalizedRepoPath}");
                }

                string installedPath = sourceName == "il2cppPlus"
                    ? normalizedRepoPath.Substring("libil2cpp/".Length)
                    : normalizedRepoPath;
                files.Add(new SourceFileHash
                {
                    source = sourceName,
                    path = installedPath,
                    sha256 = Sha256File(sourcePath),
                });
            }

            string sourceRoot = Path.Combine(repository.path, sourceDirectory.Replace('/', Path.DirectorySeparatorChar));
            foreach (string sourcePath in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relativeRepoPath = sourcePath.Substring(repository.path.Length + 1)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!trackedPaths.Contains(relativeRepoPath))
                {
                    throw new InvalidDataException(
                        $"Unexpected untracked file in pinned {sourceName} source: {relativeRepoPath}");
                }
            }
        }

        private static List<GitTreeEntry> GetGitTreeEntries(ResolvedRepository repository, string sourceDirectory)
        {
            string tree = Git(repository.path, "ls-tree", "-r", "-z", "HEAD", "--", sourceDirectory);
            var entries = new List<GitTreeEntry>();
            foreach (string record in tree.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = record.IndexOf('\t');
                if (tab <= 0 || tab == record.Length - 1)
                {
                    throw new InvalidDataException($"Malformed git tree entry in {repository.name}: {record}");
                }
                string[] metadata = record.Substring(0, tab).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (metadata.Length != 3 || (metadata[1] != "blob" && metadata[1] != "commit"))
                {
                    throw new InvalidDataException($"Unexpected git tree entry in {repository.name}: {record}");
                }
                if (metadata[1] != "blob")
                {
                    throw new InvalidDataException($"Submodule source is not allowed in {repository.name}: {record}");
                }
                entries.Add(new GitTreeEntry { objectId = metadata[2], path = record.Substring(tab + 1) });
            }
            if (entries.Count == 0)
            {
                throw new InvalidDataException($"Pinned {repository.name} source has no tracked files under {sourceDirectory}.");
            }
            return entries;
        }

        private static void VerifyStagingHashes(string stagingRoot, List<SourceFileHash> expected)
        {
            VerifyInstalledFiles(stagingRoot, expected, false);
            foreach (SourceFileHash file in expected)
            {
                string path = Path.Combine(stagingRoot, file.path.Replace('/', Path.DirectorySeparatorChar));
                if (!string.Equals(file.sha256, Sha256File(path), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Staged source hash mismatch: {file.path}");
                }
            }
        }

        private static void VerifyInstalledFiles(string installedRoot, List<SourceFileHash> expected, bool allowGeneratedOutputs = true)
        {
            if (!Directory.Exists(installedRoot))
            {
                throw new InvalidDataException($"Installed libil2cpp directory does not exist: {installedRoot}");
            }

            var expectedPaths = new HashSet<string>(expected.Select(file => file.path), StringComparer.Ordinal);
            if (allowGeneratedOutputs)
            {
                foreach (string path in s_generatedFileExclusions)
                {
                    expectedPaths.Add(path);
                }
                expectedPaths.Add(ReceiptFileName);
            }

            foreach (string file in Directory.GetFiles(installedRoot, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(installedRoot.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                if (!expectedPaths.Contains(relative))
                {
                    throw new InvalidDataException($"Unexpected installed libil2cpp file: {relative}");
                }
            }
            foreach (string expectedPath in expectedPaths)
            {
                if (allowGeneratedOutputs && string.Equals(expectedPath, ReceiptFileName, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!File.Exists(Path.Combine(installedRoot, expectedPath.Replace('/', Path.DirectorySeparatorChar))))
                {
                    throw new InvalidDataException($"Expected installed libil2cpp file is missing: {expectedPath}");
                }
            }
        }

        private static void VerifyInstalledSourceHashes(string installedRoot, List<SourceFileHash> expected)
        {
            foreach (SourceFileHash file in expected)
            {
                // Generated files are checked for presence but are allowed to differ after
                // InstallFromLocal because HybridCLR generation owns their contents.
                if (s_generatedFileExclusions.Contains(file.path, StringComparer.Ordinal))
                {
                    continue;
                }
                string path = Path.Combine(installedRoot, file.path.Replace('/', Path.DirectorySeparatorChar));
                if (!string.Equals(file.sha256, Sha256File(path), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Installed source hash mismatch: {file.path}");
                }
            }
        }

        private static void VerifyDefaultShadowMacro(string installedRoot)
        {
            string configPath = Path.Combine(installedRoot, "AssemblyShadowConfig.h");
            string text = File.ReadAllText(configPath, Encoding.UTF8);
            if (!Regex.IsMatch(text, @"(?m)^\s*#define\s+HYBRIDCLR_ENABLE_ASSEMBLY_SHADOW\s+0\b"))
            {
                throw new InvalidDataException("Installed AssemblyShadowConfig.h does not default HYBRIDCLR_ENABLE_ASSEMBLY_SHADOW to 0.");
            }
        }

        private static void WriteReceipt(string installedRoot, SourcePins pins, RepositoryPins repositories,
            ResolvedRepository hybridclr, ResolvedRepository hybridclrUnity, ResolvedRepository il2cppPlus,
            ResolvedRepository demo, List<SourceFileHash> sourceFiles)
        {
            var receipt = new InstallReceipt
            {
                schemaVersion = SchemaVersion,
                installMode = "PinnedLocal",
                unityVersion = pins.unityVersion,
                target = pins.target,
                packageVersion = ReadPackageVersion(),
                packageRevision = hybridclrUnity.currentRevision,
                repositories = new RepositoryPins
                {
                    hybridclr = ToReceiptPin(hybridclr),
                    hybridclrUnity = ToReceiptPin(hybridclrUnity),
                    il2cppPlus = ToReceiptPin(il2cppPlus),
                    demo = ToReceiptPin(demo),
                },
                sourceFileHashes = sourceFiles.ToArray(),
                defaultShadowMacro = 0,
                defaultShadowMacroSource = "AssemblyShadowConfig.h",
                generatedFileExclusions = (string[])s_generatedFileExclusions.Clone(),
                receiptFile = ReceiptFileName,
            };

            string receiptPath = Path.Combine(installedRoot, ReceiptFileName);
            string temporaryPath = receiptPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonUtility.ToJson(receipt, true), new UTF8Encoding(false));
            if (File.Exists(receiptPath))
            {
                File.Delete(receiptPath);
            }
            File.Move(temporaryPath, receiptPath);
        }

        private static RepositoryPin ToReceiptPin(ResolvedRepository repository)
        {
            return new RepositoryPin
            {
                url = repository.pin.url,
                revision = repository.pin.revision,
                localPath = repository.pin.localPath,
            };
        }

        private static string ReadPackageVersion()
        {
            string packagePath = Path.Combine(SettingsUtil.PackageRootPath, "package.json");
            var package = JsonUtility.FromJson<PackageVersion>(File.ReadAllText(packagePath, Encoding.UTF8));
            return package == null ? string.Empty : package.version;
        }

        [Serializable]
        private sealed class PackageVersion
        {
            public string version;
        }

        private static void RecreatePrivateStagingDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                BashUtil.RemoveDir(path);
            }
            Directory.CreateDirectory(path);
        }

        private static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string GitBlobSha1(string path)
        {
            using (var sha = SHA1.Create())
            using (var stream = File.OpenRead(path))
            {
                long length = stream.Length;
                byte[] header = Encoding.UTF8.GetBytes("blob " + length + "\0");
                sha.TransformBlock(header, 0, header.Length, header, 0);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string Git(string workingDirectory, params string[] arguments)
        {
            var result = BashUtil.RunCommand2(workingDirectory, "git", arguments, false);
            if (result.ExitCode != 0)
            {
                throw new InvalidDataException(
                    $"git {string.Join(" ", arguments)} failed in {workingDirectory}: {result.StdErr.Trim()}");
            }
            return result.StdOut.Trim();
        }
    }
}
