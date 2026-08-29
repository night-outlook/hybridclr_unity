using HybridCLR.Editor.Link;
using HybridCLR.Editor.Meta;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using System.Linq;
using HybridCLR.Editor.AssemblyShadow;

namespace HybridCLR.Editor.Commands
{
    using Analyzer = HybridCLR.Editor.Link.Analyzer;

    public static class LinkGeneratorCommand
    {
        public static ShadowGenerationOutput GenerateLinkXml(VerifiedGenerationPlan plan, string outputFile)
        {
            plan.VerifyUnchanged(); ShadowGenerationOutput.NewOutput(outputFile);
            using (var resolver = plan.CreateResolver())
            {
                var names = plan.SelectedNames.ToList();
                var types = new Analyzer(resolver).CollectRefs(names);
                new LinkXmlWriter().Write(outputFile, types);
                return ShadowGenerationOutput.Seal(outputFile, plan, null, new ShadowGenerationOutputReceipt
                {
                    stage = "Link", collectorRoots = names.ToArray(), resolverCatalog = resolver.Catalog.ToArray(),
                    collectorTypes = GenerationSignatures.Sorted(types.Select(type => type.DefinitionAssembly.FullName + "|" + type.FullName))
                });
            }
        }

        [MenuItem("HybridCLR/Generate/LinkXml", priority = 100)]
        public static void GenerateLinkXml()
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            CompileDllCommand.CompileDll(target);
            GenerateLinkXml(target);
        }

        public static void GenerateLinkXml(BuildTarget target)
        {
            var ls = SettingsUtil.HybridCLRSettings;

            List<string> hotfixAssemblies = SettingsUtil.HotUpdateAssemblyNamesExcludePreserved;

            var analyzer = new Analyzer(MetaUtil.CreateHotUpdateAndAOTAssemblyResolver(target, hotfixAssemblies));
            var refTypes = analyzer.CollectRefs(hotfixAssemblies);

            Debug.Log($"[LinkGeneratorCommand] hotfix assembly count:{hotfixAssemblies.Count}, ref type count:{refTypes.Count} output:{Application.dataPath}/{ls.outputLinkFile}");
            var linkXmlWriter = new LinkXmlWriter();
            linkXmlWriter.Write($"{Application.dataPath}/{ls.outputLinkFile}", refTypes);
            AssetDatabase.Refresh();
        }
    }
}
