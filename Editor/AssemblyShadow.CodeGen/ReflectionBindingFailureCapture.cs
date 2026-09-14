using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using dnlib.DotNet;

namespace HybridCLR.AssemblyShadow.CodeGen
{
    /// <summary>Opt-in diagnostic copies. These are replay inputs, not build provenance.</summary>
    public static class ReflectionBindingFailureCapture
    {
        public const string RootEnvironmentVariable = "ASSEMBLY_SHADOW_ILPP_FAILURE_ROOT";
        public sealed class FileEntry { public string role, path, sourcePath, sha256; public long sizeBytes; }
        public sealed class Manifest
        {
            public int schemaVersion = 1;
            public string kind = "ReflectionBindingFailureCapture";
            public string assemblyName, exception, dnlibIdentity, codegenIdentity;
            public string[] defines, missingReferences;
            public FileEntry[] files;
            public bool buildProvenance = false, humanGatePassed = false, mayEnterR02 = false;
        }

        public static string TrySave(string assemblyName, IEnumerable<string> defines, byte[] pe, byte[] pdb,
            IEnumerable<string> references, string projectRoot, Exception error)
        {
            string root = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(root)) return "";
            try { return Save(root, assemblyName, defines, pe, pdb, references, projectRoot, error); }
            catch (Exception captureError) { return "Diagnostic capture failed (original error retained): " + captureError; }
        }

        public static string Save(string root, string assemblyName, IEnumerable<string> defines, byte[] pe, byte[] pdb,
            IEnumerable<string> references, string projectRoot, Exception error)
        {
            if (!Path.IsPathRooted(root)) throw new ArgumentException("Diagnostic root must be absolute.");
            if (!Path.IsPathRooted(projectRoot)) throw new ArgumentException("Diagnostic project root must be absolute.");
            projectRoot = Path.GetFullPath(projectRoot);
            root = Path.Combine(Path.GetFullPath(root), "failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var files = new List<FileEntry>(); var missing = new List<string>();
            Add(root, files, "pe", "input.dll", null, pe ?? new byte[0]);
            if (pdb != null && pdb.Length > 0) Add(root, files, "pdb", "input.pdb", null, pdb);
            foreach (string reference in (references ?? new string[0]).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
            {
                string resolved = string.IsNullOrEmpty(reference) ? reference :
                    Path.GetFullPath(Path.IsPathRooted(reference) ? reference : Path.Combine(projectRoot, reference));
                if (string.IsNullOrEmpty(resolved) || !File.Exists(resolved))
                { missing.Add(reference ?? "<null>"); continue; }
                Add(root, files, "reference", "references/" + files.Count.ToString("D4") + ".dll", resolved, File.ReadAllBytes(resolved));
            }
            string configuration = Path.Combine(projectRoot, ReflectionBindingConfiguration.ProjectRelativePath);
            if (File.Exists(configuration)) Add(root, files, "configuration", "configuration.json", configuration, File.ReadAllBytes(configuration));
            Add(root, files, "exception", "exception.txt", null, Encoding.UTF8.GetBytes(error == null ? "<missing exception>" : error.ToString()));
            // Capture exact tool bytes when exposed by the host; no dynamic assembly execution.
            foreach (var assembly in new[] { typeof(ModuleDefMD).Assembly, typeof(ReflectionBindingTransformer).Assembly })
                if (!string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location))
                    Add(root, files, "tool", "tools/" + files.Count.ToString("D4") + ".dll", assembly.Location, File.ReadAllBytes(assembly.Location));
            WriteNew(Path.Combine(root, "capture.json"), new Manifest {
                assemblyName = assemblyName, defines = (defines ?? new string[0]).ToArray(),
                exception = error == null ? "<missing exception>" : error.ToString(),
                dnlibIdentity = typeof(ModuleDefMD).Assembly.FullName, codegenIdentity = typeof(ReflectionBindingTransformer).Assembly.FullName,
                missingReferences = missing.ToArray(), files = files.ToArray(),
            });
            return root;
        }

        private static void Add(string root, List<FileEntry> files, string role, string path, string source, byte[] bytes)
        {
            string target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            files.Add(new FileEntry { role = role, path = path, sourcePath = source,
                sha256 = BindingChecks.Sha256(bytes), sizeBytes = bytes.LongLength });
        }

        public static void WriteNew<T>(string path, T value)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); stream.Flush(true); }
        }

        public static Manifest ReadVerified(string root)
        {
            root = Path.GetFullPath(root);
            string[] retainedMembers = EnumerateFiles(root).ToArray();
            Manifest manifest;
            using (var stream = File.OpenRead(Path.Combine(root, "capture.json")))
                manifest = (Manifest)new DataContractJsonSerializer(typeof(Manifest)).ReadObject(stream);
            BindingChecks.Require(manifest != null && manifest.schemaVersion == 1 && manifest.kind == "ReflectionBindingFailureCapture" &&
                manifest.files != null && manifest.missingReferences != null && manifest.missingReferences.Length == 0,
                "IncompleteFailureCapture", "A complete diagnostic capture is required.");
            var expected = new HashSet<string>(StringComparer.Ordinal) { "capture.json" };
            foreach (var entry in manifest.files)
            {
                BindingChecks.Require(entry != null && !string.IsNullOrEmpty(entry.path) && !Path.IsPathRooted(entry.path) &&
                    entry.path.IndexOf('\\') < 0 && entry.path.IndexOf(':') < 0 &&
                    entry.path.Split('/').All(part => part.Length > 0 && part != "." && part != "..") && expected.Add(entry.path),
                    "InvalidFailureCapturePath", "Capture member paths must be safe and unique.");
                string path = Path.Combine(root, entry.path.Replace('/', Path.DirectorySeparatorChar));
                RequireRegular(path);
                byte[] bytes = File.ReadAllBytes(path);
                BindingChecks.Require(bytes.LongLength == entry.sizeBytes && BindingChecks.Sha256(bytes) == entry.sha256,
                    "FailureCaptureHashMismatch", entry.path);
            }
            string[] actual = retainedMembers.Select(p => p.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1)
                .Replace(Path.DirectorySeparatorChar, '/')).ToArray();
            BindingChecks.Require(expected.SetEquals(actual), "FailureCaptureMembershipMismatch", "Capture has extra or missing files.");
            RequireRegular(Path.Combine(root, "capture.json"));
            return manifest;
        }

        private static IEnumerable<string> EnumerateFiles(string directory)
        {
            RequireRegular(directory);
            foreach (string entry in Directory.GetFileSystemEntries(directory))
            {
                RequireRegular(entry);
                if (Directory.Exists(entry))
                    foreach (string file in EnumerateFiles(entry)) yield return file;
                else yield return entry;
            }
        }

        private static void RequireRegular(string path)
        {
            BindingChecks.Require((File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) == 0,
                "FailureCaptureSymlink", "Capture members must not be symbolic links: " + path);
        }
    }
}
