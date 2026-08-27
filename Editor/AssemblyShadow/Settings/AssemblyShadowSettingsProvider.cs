using UnityEditor;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    public sealed class AssemblyShadowSettingsProvider : SettingsProvider
    {
        private SerializedObject serializedObject;

        public AssemblyShadowSettingsProvider()
            : base("Project/HybridCLR Assembly Shadow", SettingsScope.Project) { }

        public override void OnActivate(string searchContext, UnityEngine.UIElements.VisualElement rootElement)
        {
            serializedObject = new SerializedObject(AssemblyShadowSettings.LoadOrCreate());
        }

        public override void OnGUI(string searchContext)
        {
            if (serializedObject == null || !serializedObject.targetObject)
                serializedObject = new SerializedObject(AssemblyShadowSettings.LoadOrCreate());

            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            Draw("enableAssemblyShadow", "Enable Assembly Shadow");
            EditorGUILayout.Space();
            Draw("shadowAssemblyDefinitions", "Shadow assembly definitions");
            Draw("shadowAssemblyNames", "Additional shadow assembly names");
            Draw("bootstrapAssemblyDefinitions", "Bootstrap assembly definitions");
            Draw("bootstrapAssemblyNames", "Additional bootstrap assembly names");
            EditorGUILayout.Space();
            Draw("patchOutputRoot", "Patch output root");
            Draw("baselineOutputRoot", "Baseline output root");
            Draw("baselineManifestPath", "Baseline manifest");
            Draw("buildId", "Build ID");
            Draw("currentCompileOutput", "Current compile output");
            Draw("playerInputSnapshot", "Captured Player input snapshot");
            Draw("resourceBuildMapPath", "Resource build map");
            Draw("resourceBaselinePath", "Frozen resource build receipt root");
            Draw("patchId", "Patch ID");
            Draw("targetReferenceDirectories", "Target reference directories");
            Draw("precompiledAssemblyNames", "Shadow-capable precompiled names (legacy)");
            Draw("precompiledAssemblyCapabilities", "Explicit precompiled capabilities");
            Draw("architecture", "Architecture");
            Draw("sourcePinFile", "Source pins");
            EditorGUILayout.Space();
            Draw("explicitDependencyConfig", "Explicit dependency asset");
            Draw("explicitDependencyConfigPath", "Explicit dependency path");
            Draw("extensibilityWhitelist", "Extensibility whitelist asset");
            Draw("extensibilityWhitelistPath", "Extensibility whitelist path");
            Draw("allowedInternalEditorAssemblies", "Allowed internal editor assemblies");
            Draw("enforceResourceAbi", "Enforce resource ABI");
            Draw("rejectUnknownReflectionDependencies", "Reject unknown reflection dependencies");
            Draw("includePdbInDevelopmentPatch", "Include PDB in development patches");
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                AssemblyShadowSettings.Save();
            }
        }

        private void Draw(string propertyName, string label)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property != null)
                EditorGUILayout.PropertyField(property, new GUIContent(label), true);
        }

        public override void OnDeactivate()
        {
            base.OnDeactivate();
            AssemblyShadowSettings.Save();
            serializedObject?.Dispose();
            serializedObject = null;
        }

        private static AssemblyShadowSettingsProvider s_provider;

        [SettingsProvider]
        public static SettingsProvider CreateAssemblyShadowSettingsProvider()
        {
            return s_provider ?? (s_provider = new AssemblyShadowSettingsProvider());
        }
    }
}
