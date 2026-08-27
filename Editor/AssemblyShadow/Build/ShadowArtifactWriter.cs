using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    internal static class ShadowArtifactWriter
    {
        public static string Begin(string destination)
        {
            ShadowHash.Require(!string.IsNullOrWhiteSpace(destination) && !Directory.Exists(destination) && !File.Exists(destination), "ArtifactExists", "Never replace an immutable artifact: " + destination);
            string temporary = Path.GetFullPath(destination) + ".building-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(temporary);
            return temporary;
        }

        public static void Json(string root, string relative, object value)
        {
            string path = ShadowHash.SafeChild(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(value, true), new UTF8Encoding(false));
        }

        public static void CopyVerified(string source, string destinationRoot, string relative, string expectedHash)
        {
            ShadowHash.Require(ShadowHash.File(source) == expectedHash, "InputChangedDuringBuild", source);
            string destination = ShadowHash.SafeChild(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, false);
            ShadowHash.Require(ShadowHash.File(destination) == expectedHash, "ArtifactHashMismatch", destination);
        }

        public static void Finish(string temporary, string destination, string manifest)
        {
            string hash = ShadowHash.File(Path.Combine(temporary, manifest));
            File.WriteAllText(Path.Combine(temporary, "manifest.sha256"), hash + "\n", new UTF8Encoding(false));
            Directory.Move(temporary, Path.GetFullPath(destination));
        }

        public static T ReadVerifiedManifest<T>(string path) where T : class
        {
            string hashPath = Path.Combine(Path.GetDirectoryName(path), "manifest.sha256");
            ShadowHash.Require(File.Exists(path) && File.Exists(hashPath) && File.ReadAllText(hashPath).Trim() == ShadowHash.File(path), "ManifestHashMismatch", path);
            var result = JsonUtility.FromJson<T>(File.ReadAllText(path));
            ShadowHash.Require(result != null, "InvalidManifest", path);
            return result;
        }
    }
}
