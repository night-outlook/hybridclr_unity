using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;

namespace HybridCLR.AssemblyShadow.CodeGen
{
    public sealed class ReflectionBindingsILPostProcessor : ILPostProcessor
    {
        public override ILPostProcessor GetInstance() { return new ReflectionBindingsILPostProcessor(); }
        public override bool WillProcess(ICompiledAssembly assembly)
        {
            var defines = assembly.Defines ?? new string[0];
            return !defines.Contains("UNITY_EDITOR", StringComparer.Ordinal) && defines.Any(value => value != null && value.StartsWith(ReflectionBindingDefines.Prefix, StringComparison.Ordinal));
        }

        public override ILPostProcessResult Process(ICompiledAssembly assembly)
        {
            var diagnostics = new List<DiagnosticMessage>();
            try
            {
                var result = ProcessAssembly(assembly.Name, assembly.Defines, assembly.InMemoryAssembly.PeData, assembly.InMemoryAssembly.PdbData, Environment.CurrentDirectory);
                return new ILPostProcessResult(result == null ? null : new InMemoryAssembly(result.PeData, result.PdbData), diagnostics);
            }
            catch (Exception error)
            {
                diagnostics.Add(new DiagnosticMessage { DiagnosticType = DiagnosticType.Error, MessageData = "AssemblyShadow reflection binding failed: " + error.Message });
                return new ILPostProcessResult(null, diagnostics);
            }
        }

        // Explicit projectRoot supports focused tests; the Unity entrypoint always
        // supplies exact CWD. No parent-directory search or machine-state fallback.
        public static ReflectionTransformResult ProcessAssembly(string assemblyName, IEnumerable<string> defines, byte[] pe, byte[] pdb, string projectRoot)
        {
            string rawHash; if (!ReflectionBindingDefines.TryGetEnabledHash(defines, out rawHash)) return null;
            BindingChecks.Require(!string.IsNullOrWhiteSpace(projectRoot) && Path.IsPathRooted(projectRoot) &&
                File.Exists(Path.Combine(projectRoot, "ProjectSettings/ProjectVersion.txt")), "InvalidProjectRoot", "ILPP CWD must be the exact Unity project root.");
            string path = Path.Combine(projectRoot, ReflectionBindingConfiguration.ProjectRelativePath);
            BindingChecks.Require(File.Exists(path), "MissingBindingConfiguration", path);
            byte[] bytes = File.ReadAllBytes(path);
            BindingChecks.Require(BindingChecks.Sha256(bytes) == rawHash, "BindingConfigurationHashMismatch", "The control define does not identify the current raw configuration bytes.");
            var configuration = ReflectionBindingConfiguration.Parse(bytes);
            if (!configuration.Targets(assemblyName)) return null;
            var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var site in configuration.sites.Where(value => ReflectionBindingConfiguration.KindOf(value) == "FixedAssemblyBytes"))
                if (!images.ContainsKey(site.imagePath)) images.Add(site.imagePath, File.ReadAllBytes(Path.Combine(projectRoot, site.imagePath)));
            configuration.ValidateImageEvidence(images);
            return ReflectionBindingTransformer.Transform(pe, pdb, configuration);
        }
    }
}
