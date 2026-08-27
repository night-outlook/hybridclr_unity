using System;
using System.IO;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    /// <summary>Machine-generated selections are not project policy or source-pin inputs.</summary>
    [Serializable]
    public sealed class ShadowBuildSession
    {
        private const string PathName = "UserSettings/AssemblyShadowBuildSession.json";
        public string playerInputSnapshot;
        public string baselineManifestPath;
        public string currentCompileSnapshot;
        public string baselineBuildId;
        public string resourceBaselinePath;

        public static ShadowBuildSession Load()
        {
            return File.Exists(PathName) ? JsonUtility.FromJson<ShadowBuildSession>(File.ReadAllText(PathName)) : new ShadowBuildSession();
        }

        public void Save()
        {
            Directory.CreateDirectory("UserSettings");
            File.WriteAllText(PathName, JsonUtility.ToJson(this, true));
        }
    }
}
