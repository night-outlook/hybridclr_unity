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
        public const string NormalizeEnumsDefine = "ASSEMBLY_SHADOW_PDB_RESOLVED_ENUMS";
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
                var result = ProcessAssembly(assembly.Name, assembly.Defines, assembly.InMemoryAssembly.PeData, assembly.InMemoryAssembly.PdbData,
                    Environment.CurrentDirectory, assembly.References,
                    (assembly.Defines ?? new string[0]).Contains(NormalizeEnumsDefine, StringComparer.Ordinal));
                return new ILPostProcessResult(result == null ? null : new InMemoryAssembly(result.PeData, result.PdbData), diagnostics);
            }
            catch (Exception error)
            {
                string capture = ReflectionBindingFailureCapture.TrySave(assembly.Name, assembly.Defines, assembly.InMemoryAssembly.PeData,
                    assembly.InMemoryAssembly.PdbData, assembly.References, Environment.CurrentDirectory, error);
                diagnostics.Add(new DiagnosticMessage { DiagnosticType = DiagnosticType.Error,
                    MessageData = "AssemblyShadow reflection binding failed: " + error + (string.IsNullOrEmpty(capture) ? "" : "\nDiagnostic capture: " + capture) });
                return new ILPostProcessResult(null, diagnostics);
            }
        }

        // Explicit projectRoot supports focused tests; the Unity entrypoint always
        // supplies exact CWD. No parent-directory search or machine-state fallback.
        public static ReflectionTransformResult ProcessAssembly(string assemblyName, IEnumerable<string> defines, byte[] pe, byte[] pdb, string projectRoot)
        { return ProcessAssembly(assemblyName, defines, pe, pdb, projectRoot, null, false); }

        public static ReflectionTransformResult ProcessAssembly(string assemblyName, IEnumerable<string> defines, byte[] pe, byte[] pdb,
            string projectRoot, IEnumerable<string> compilerReferences, bool normalizeResolvedEnums = false)
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
            string[] resolvedReferences = compilerReferences == null ? null : compilerReferences.Select(reference =>
            {
                BindingChecks.Require(!string.IsNullOrWhiteSpace(reference), "InvalidCompilerReference",
                    "Compiler references must be nonempty paths.");
                return Path.GetFullPath(Path.IsPathRooted(reference) ? reference : Path.Combine(projectRoot, reference));
            }).ToArray();
            return compilerReferences == null && !normalizeResolvedEnums
                ? ReflectionBindingTransformer.Transform(pe, pdb, configuration)
                : ReflectionBindingTransformer.Transform(pe, pdb, configuration, resolvedReferences, normalizeResolvedEnums);
        }
    }
}
