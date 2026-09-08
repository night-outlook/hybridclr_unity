using HybridCLR.Editor.Link;
using HybridCLR.Editor.AssemblyShadow;
using HybridCLR.Editor.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace HybridCLR.Editor.Commands
{

    public static class Il2CppDefGeneratorCommand
    {

        [MenuItem("HybridCLR/Generate/Il2CppDef", priority = 104)]
        public static void GenerateIl2CppDef()
        {
            AssemblyShadowSettings settings = AssemblyShadowSettings.Instance;
            var startupCandidates = new List<string>();
            var startupBootstrapAssemblies = new List<string>(settings.bootstrapAssemblyNames ?? new string[0]);
            var startupStableAotAssemblies = new List<string>(HybridCLRSettings.Instance.patchAOTAssemblies ?? new string[0]);
            if (settings.enableAssemblyShadow)
            {
                var policy = AssemblyShadowSettingsUtil.CreatePolicyConfiguration(EditorUserBuildSettings.activeBuildTarget);
                startupCandidates = policy.assemblies
                    .Where(item => item != null && item.isShadowCapable)
                    .Select(item => item.name)
                    .ToList();
                startupBootstrapAssemblies = policy.assemblies
                    .Where(item => item != null && item.isBootstrap)
                    .Select(item => item.name)
                    .ToList();
                startupStableAotAssemblies.AddRange(policy.assemblies
                    .Where(item => item != null && item.isPrecompiled && !item.isShadowCapable && !item.isBootstrap)
                    .Select(item => item.name));
            }

            var options = new Il2CppDef.Il2CppDefGenerator.Options()
            {
                UnityVersion = Application.unityVersion,
                HotUpdateAssemblies = SettingsUtil.HotUpdateAssemblyNamesIncludePreserved,
                AssemblyShadowStartupCandidates = startupCandidates,
                AssemblyShadowStartupBootstrapAssemblies = startupBootstrapAssemblies,
                AssemblyShadowStartupStableAotAssemblies = startupStableAotAssemblies,
                AssemblyShadowStartupBootstrapAssembly = settings.startupBootstrapAssembly,
                AssemblyShadowStartupBootstrapNamespace = settings.startupBootstrapNamespace,
                AssemblyShadowStartupBootstrapType = settings.startupBootstrapType,
                AssemblyShadowStartupBootstrapMethod = settings.startupBootstrapMethod,
                UnityVersionTemplateFile = $"{SettingsUtil.TemplatePathInPackage}/UnityVersion.h.tpl",
                UnityVersionOutputFile = $"{SettingsUtil.LocalIl2CppDir}/libil2cpp/hybridclr/generated/UnityVersion.h",
                AssemblyManifestTemplateFile = $"{SettingsUtil.TemplatePathInPackage}/AssemblyManifest.cpp.tpl",
                AssemblyManifestOutputFile = $"{SettingsUtil.LocalIl2CppDir}/libil2cpp/hybridclr/generated/AssemblyManifest.cpp",
            };

            var g = new Il2CppDef.Il2CppDefGenerator(options);
            g.Generate();
        }
    }
}
