using System;
using System.IO;
using System.Linq;

namespace HybridCLR.AssemblyShadow.CodeGen
{
    /// <summary>Runs all diagnostic routes against one authenticated PE/PDB pair.</summary>
    public static class ReflectionBindingFailureReplay
    {
        public sealed class Result
        {
            public int schemaVersion = 1;
            public string kind = "ReflectionBindingFailureReplay";
            public string mode, captureManifestSha256, result, exception, peSha256, pdbSha256, configurationHash;
            public bool diagnosticOnly = true, humanGatePassed = false, mayEnterR02 = false;
        }

        public static void RunFromCommandLine()
        {
            string root = Argument("-shadowH1ReplayCapture");
            string output = Argument("-shadowH1ReplayOutput");
            if (!Path.IsPathRooted(root) || !Path.IsPathRooted(output) || Directory.Exists(output) || File.Exists(output))
                throw new ArgumentException("Replay requires an absolute capture root and a new absolute output directory.");
            var capture = ReflectionBindingFailureCapture.ReadVerified(root);
            byte[] pe = ReadRole(root, capture, "pe", true);
            byte[] pdb = ReadRole(root, capture, "pdb", false);
            byte[] configBytes = ReadRole(root, capture, "configuration", true);
            string enabledHash;
            if (!ReflectionBindingDefines.TryGetEnabledHash(capture.defines, out enabledHash) || BindingChecks.Sha256(configBytes) != enabledHash)
                throw new InvalidOperationException("Captured configuration does not match the original compiler control define.");
            var configuration = ReflectionBindingConfiguration.Parse(configBytes);
            if (!configuration.Targets(capture.assemblyName)) throw new InvalidOperationException("Capture does not target the requested assembly.");
            string[] references = capture.files.Where(f => f.role == "reference")
                .Select(f => Path.Combine(root, f.path.Replace('/', Path.DirectorySeparatorChar))).ToArray();
            Directory.CreateDirectory(output);
            foreach (string mode in new[] { "unresolved", "compiler-references", "resolved-enums" })
            {
                var result = new Result { mode = mode, result = "Failed",
                    captureManifestSha256 = BindingChecks.Sha256(File.ReadAllBytes(Path.Combine(root, "capture.json"))) };
                try
                {
                    ReflectionTransformResult emitted = mode == "unresolved"
                        ? ReflectionBindingTransformer.Transform(pe, pdb, configuration)
                        : ReflectionBindingTransformer.Transform(pe, pdb, configuration, references, mode == "resolved-enums");
                    WriteBytesNew(Path.Combine(output, mode + ".dll"), emitted.PeData);
                    WriteBytesNew(Path.Combine(output, mode + ".pdb"), emitted.PdbData);
                    result.peSha256 = BindingChecks.Sha256(emitted.PeData); result.pdbSha256 = BindingChecks.Sha256(emitted.PdbData);
                    result.configurationHash = emitted.ConfigurationHash; result.result = "EmittedAndVerified";
                }
                catch (Exception error) { result.exception = error.ToString(); }
                ReflectionBindingFailureCapture.WriteNew(Path.Combine(output, mode + ".json"), result);
            }
            // A legacy failure is a useful observation, not a failed entire replay batch.
            // No route is selected automatically and no build/readiness claim is emitted.
        }

        private static byte[] ReadRole(string root, ReflectionBindingFailureCapture.Manifest capture, string role, bool required)
        {
            var rows = capture.files.Where(f => f.role == role).ToArray();
            if (rows.Length == 0 && !required) return null;
            if (rows.Length != 1) throw new InvalidOperationException("Capture must contain exactly one " + role + " member.");
            return File.ReadAllBytes(Path.Combine(root, rows[0].path.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static void WriteBytesNew(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }

        private static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs(); string value = null;
            for (int i = 0; i < args.Length; ++i) if (args[i] == key)
            {
                if (value != null || i + 1 >= args.Length) throw new ArgumentException("Missing/duplicate " + key);
                value = args[++i];
            }
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Required: " + key);
            return value;
        }
    }
}
