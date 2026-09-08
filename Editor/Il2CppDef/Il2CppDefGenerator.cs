using HybridCLR.Editor.ABI;
using HybridCLR.Editor.AssemblyShadow;
using HybridCLR.Editor.Template;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace HybridCLR.Editor.Il2CppDef
{
    public class Il2CppDefGenerator
    {
        public class Options
        {
            public List<string> HotUpdateAssemblies { get; set; }

            public List<string> AssemblyShadowStartupCandidates { get; set; } = new List<string>();

            public List<string> AssemblyShadowStartupBootstrapAssemblies { get; set; } = new List<string>();

            public List<string> AssemblyShadowStartupStableAotAssemblies { get; set; } = new List<string>();

            public string AssemblyShadowStartupBootstrapAssembly { get; set; } = "";

            public string AssemblyShadowStartupBootstrapNamespace { get; set; } = "";

            public string AssemblyShadowStartupBootstrapType { get; set; } = "";

            public string AssemblyShadowStartupBootstrapMethod { get; set; } = "";

            public string UnityVersionTemplateFile { get; set; }

            public string UnityVersionOutputFile { get; set; }

            public string AssemblyManifestTemplateFile { get; set; }

            public string AssemblyManifestOutputFile { get; set; }

            public string UnityVersion { get; set; }
        }

        private readonly Options _options;
        public Il2CppDefGenerator(Options options)
        {
            _options = options;
        }


        private static readonly Regex s_unityVersionPat = new Regex(@"(\d+)\.(\d+)\.(\d+)");

        public void Generate()
        {
            GenerateIl2CppConfig();
            GeneratePlaceHolderAssemblies();
        }

        private void GenerateIl2CppConfig()
        {
            var frr = new FileRegionReplace(File.ReadAllText(_options.UnityVersionTemplateFile));

            List<string> lines = new List<string>();

            var match = s_unityVersionPat.Matches(_options.UnityVersion)[0];
            int majorVer = int.Parse(match.Groups[1].Value);
            int minorVer1 = int.Parse(match.Groups[2].Value);
            int minorVer2 = int.Parse(match.Groups[3].Value);

            lines.Add($"#define HYBRIDCLR_UNITY_VERSION {majorVer}{minorVer1.ToString("D2")}{minorVer2.ToString("D2")}");
            lines.Add($"#define HYBRIDCLR_UNITY_{majorVer} 1");
            for (int ver = 2019; ver <= 2023; ver++)
            {
                if (majorVer >= ver)
                {
                    lines.Add($"#define HYBRIDCLR_UNITY_{ver}_OR_NEW 1");
                }
            }
            for (int ver = 6000; ver <= 6100; ver++)
            {
                if (majorVer >= ver)
                {
                    lines.Add($"#define HYBRIDCLR_UNITY_{ver}_OR_NEW 1");
                }
            }

#if TUANJIE_1_1_OR_NEWER
            var tuanjieMatch = Regex.Matches(Application.tuanjieVersion, @"(\d+)\.(\d+)\.(\d+)");
            int tuanjieMajorVer = int.Parse(tuanjieMatch[0].Groups[1].Value);
            int tuanjieMinorVer1 = int.Parse(tuanjieMatch[0].Groups[2].Value);
            int tuanjieMinorVer2 = int.Parse(tuanjieMatch[0].Groups[3].Value);
            lines.Add($"#define HYBRIDCLR_TUANJIE_VERSION {tuanjieMajorVer}{tuanjieMinorVer1.ToString("D2")}{tuanjieMinorVer2.ToString("D2")}");
#elif TUANJIE_2022_3_OR_NEWER
            lines.Add($"#define HYBRIDCLR_TUANJIE_VERSION 10000");
#endif

            frr.Replace("UNITY_VERSION", string.Join("\n", lines));

            frr.Commit(_options.UnityVersionOutputFile);
            Debug.Log($"[HybridCLR.Editor.Il2CppDef.Generator] output:{_options.UnityVersionOutputFile}");
        }

        private void GeneratePlaceHolderAssemblies()
        {
            string template = File.ReadAllText(_options.AssemblyManifestTemplateFile);
            var frr = new FileRegionReplace(template);

            List<string> lines = new List<string>();

            foreach (var ass in _options.HotUpdateAssemblies)
            {
                lines.Add($"\t\t\"{ass}\",");
            }

            frr.Replace("PLACE_HOLDER", string.Join("\n", lines));
            frr.Replace("ASSEMBLY_SHADOW_STARTUP_CANDIDATES", BuildStartupCandidateLines(_options.AssemblyShadowStartupCandidates));
            string startupBootstrapLines = BuildStartupBootstrapLines(_options);
            bool hasStartupBootstrapRegion = template.IndexOf("//!!!{{ASSEMBLY_SHADOW_STARTUP_BOOTSTRAP", StringComparison.Ordinal) >= 0;
            if (hasStartupBootstrapRegion)
                frr.Replace("ASSEMBLY_SHADOW_STARTUP_BOOTSTRAP", startupBootstrapLines);
            else if (HasStartupBootstrapConfiguration(_options))
                throw new ShadowBuildException("MissingStartupBootstrapRegion", "AssemblyManifest template does not declare the startup bootstrap region.");

            frr.Commit(_options.AssemblyManifestOutputFile);
            Debug.Log($"[HybridCLR.Editor.Il2CppDef.Generator] output:{_options.AssemblyManifestOutputFile}");
        }

        private static string BuildStartupCandidateLines(IEnumerable<string> candidates)
        {
            var values = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in candidates ?? Enumerable.Empty<string>())
            {
                ValidateStartupCandidate(candidate);
                if (!seen.Add(candidate))
                    throw new ShadowBuildException("DuplicateStartupCandidate", "Assembly Shadow startup candidate names must be unique case-insensitively: " + candidate);
                values.Add(candidate);
            }

            values.Sort(StringComparer.Ordinal);
            return string.Join("\n", values.Select(value => "\t\t" + ToCppStringLiteral(value) + ","));
        }

        private static void ValidateStartupCandidate(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                throw new ShadowBuildException("InvalidStartupCandidate", "Assembly Shadow startup candidate names must be non-empty.");
            if (candidate.Trim() != candidate)
                throw new ShadowBuildException("InvalidStartupCandidate", "Assembly Shadow startup candidate names must not have leading or trailing whitespace: " + candidate);
            if (candidate.IndexOf('\0') >= 0)
                throw new ShadowBuildException("InvalidStartupCandidate", "Assembly Shadow startup candidate names must not contain a NUL character.");
        }

        private static string BuildStartupBootstrapLines(Options options)
        {
            string assembly = options.AssemblyShadowStartupBootstrapAssembly ?? "";
            string ns = options.AssemblyShadowStartupBootstrapNamespace ?? "";
            string type = options.AssemblyShadowStartupBootstrapType ?? "";
            string method = options.AssemblyShadowStartupBootstrapMethod ?? "";
            bool empty = assembly.Length == 0 && ns.Length == 0 && type.Length == 0 && method.Length == 0;
            if (!empty)
            {
                if (assembly.Length == 0 || type.Length == 0 || method.Length == 0)
                    throw new ShadowBuildException("InvalidStartupBootstrap", "Startup bootstrap assembly, type, and method must all be configured; namespace may be empty for the global namespace.");
                ValidateStartupBootstrapAssembly(assembly);
                ValidateStartupBootstrapNamespace(ns);
                ValidateStartupBootstrapIdentifier(type, "type");
                ValidateStartupBootstrapIdentifier(method, "method");

                var candidates = new HashSet<string>(NormalizeAssemblyNames(options.AssemblyShadowStartupCandidates), StringComparer.OrdinalIgnoreCase);
                var ordinary = new HashSet<string>(NormalizeAssemblyNames(options.HotUpdateAssemblies), StringComparer.OrdinalIgnoreCase);
                string logicalAssembly = AssemblyNamePolicy.Canonical(assembly);
                if (candidates.Contains(logicalAssembly) || ordinary.Contains(logicalAssembly))
                    throw new ShadowBuildException("StartupBootstrapAssemblyRole", "Startup bootstrap assembly must not be a shadow candidate or ordinary hot-update assembly: " + assembly);

                var roots = new HashSet<string>(NormalizeAssemblyNames(options.AssemblyShadowStartupBootstrapAssemblies), StringComparer.OrdinalIgnoreCase);
                roots.UnionWith(NormalizeAssemblyNames(options.AssemblyShadowStartupStableAotAssemblies));
                if (!roots.Contains(logicalAssembly) || !HasExactRoot(options, assembly))
                    throw new ShadowBuildException("StartupBootstrapAssemblyRole", "Startup bootstrap assembly must belong to a declared bootstrap or stable-AOT root: " + assembly);
            }

            return string.Join("\n", new[]
            {
                "\tconst char* g_assemblyShadowStartupBootstrapAssembly = " + ToCppStringLiteral(assembly) + ";",
                "\tconst char* g_assemblyShadowStartupBootstrapNamespace = " + ToCppStringLiteral(ns) + ";",
                "\tconst char* g_assemblyShadowStartupBootstrapType = " + ToCppStringLiteral(type) + ";",
                "\tconst char* g_assemblyShadowStartupBootstrapMethod = " + ToCppStringLiteral(method) + ";",
            });
        }

        private static bool HasStartupBootstrapConfiguration(Options options)
        {
            return !string.IsNullOrEmpty(options.AssemblyShadowStartupBootstrapAssembly) ||
                !string.IsNullOrEmpty(options.AssemblyShadowStartupBootstrapNamespace) ||
                !string.IsNullOrEmpty(options.AssemblyShadowStartupBootstrapType) ||
                !string.IsNullOrEmpty(options.AssemblyShadowStartupBootstrapMethod);
        }

        private static IEnumerable<string> NormalizeAssemblyNames(IEnumerable<string> names)
        {
            return (names ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(AssemblyNamePolicy.Canonical);
        }

        private static bool HasExactRoot(Options options, string assembly)
        {
            return (options.AssemblyShadowStartupBootstrapAssemblies ?? Enumerable.Empty<string>())
                .Concat(options.AssemblyShadowStartupStableAotAssemblies ?? Enumerable.Empty<string>())
                .Any(root => string.Equals(root, assembly, StringComparison.Ordinal) &&
                    AssemblyNamePolicy.Canonical(root) == root);
        }

        private static void ValidateStartupBootstrapAssembly(string value)
        {
            ValidateStartupBootstrapText(value, "assembly");
            if (!IsAssemblyName(value))
                throw new ShadowBuildException("InvalidStartupBootstrap", "Startup bootstrap assembly contains unsafe characters: " + value);
        }

        private static void ValidateStartupBootstrapNamespace(string value)
        {
            ValidateStartupBootstrapText(value, "namespace");
            if (value.Length == 0)
                return;
            foreach (string segment in value.Split('.'))
                ValidateStartupBootstrapIdentifier(segment, "namespace");
        }

        private static void ValidateStartupBootstrapIdentifier(string value, string kind)
        {
            ValidateStartupBootstrapText(value, kind);
            if (value.Length == 0 || !IsIdentifier(value))
                throw new ShadowBuildException("InvalidStartupBootstrap", "Startup bootstrap " + kind + " contains unsafe characters: " + value);
        }

        private static void ValidateStartupBootstrapText(string value, string kind)
        {
            if (value.Trim() != value)
                throw new ShadowBuildException("InvalidStartupBootstrap", "Startup bootstrap " + kind + " must not have leading or trailing whitespace.");
            try
            {
                if (new UTF8Encoding(false, true).GetByteCount(value) > 512)
                    throw new ShadowBuildException("InvalidStartupBootstrap", "Startup bootstrap " + kind + " exceeds the native 512-byte UTF-8 limit.");
            }
            catch (EncoderFallbackException exception)
            {
                throw new ShadowBuildException("InvalidStartupBootstrap", "Startup bootstrap " + kind + " is not valid UTF-8: " + exception.Message);
            }
            foreach (char character in value)
            {
                if (char.IsControl(character) || character == '"' || character == '\\')
                    throw new ShadowBuildException("InvalidStartupBootstrap", "Startup bootstrap " + kind + " contains unsafe characters.");
            }
        }

        private static bool IsAssemblyName(string value)
        {
            if (value.Length == 0 || !IsNameStart(value[0]))
                return false;
            for (int i = 1; i < value.Length; i++)
            {
                char character = value[i];
                if (!(IsNamePart(character) || character == '.' || character == '-'))
                    return false;
            }
            return true;
        }

        private static bool IsIdentifier(string value)
        {
            if (value.Length == 0 || !IsNameStart(value[0]))
                return false;
            for (int i = 1; i < value.Length; i++)
            {
                if (!IsNamePart(value[i]))
                    return false;
            }
            return true;
        }

        private static bool IsNameStart(char value) { return char.IsLetter(value) || value == '_'; }
        private static bool IsNamePart(char value) { return char.IsLetterOrDigit(value) || value == '_'; }

        private static string ToCppStringLiteral(string value)
        {
            var result = new StringBuilder(value.Length + 2);
            result.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': result.Append("\\\""); break;
                    case '\\': result.Append("\\\\"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    case '\b': result.Append("\\b"); break;
                    case '\f': result.Append("\\f"); break;
                    case '\v': result.Append("\\v"); break;
                    default:
                        if (char.IsControl(character))
                            result.Append("\\u").Append(((int)character).ToString("x4"));
                        else
                            result.Append(character);
                        break;
                }
            }
            result.Append('"');
            return result.ToString();
        }
    }
}
