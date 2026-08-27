using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class ShadowRepositoryPin
    {
        public string url;
        public string revision;
        public string localPath;
    }

    [Serializable]
    public sealed class ShadowSourcePins
    {
        public int schemaVersion = 1;
        public string unityVersion;
        public string target;
        public string architecture;
        public ShadowRepositoryPin hybridclr;
        public ShadowRepositoryPin hybridclrUnity;
        public ShadowRepositoryPin il2cppPlus;
        public ShadowRepositoryPin demo;

        public static ShadowSourcePins Read(string path, BuildTarget target, string architecture)
        {
            ShadowHash.Require(File.Exists(path), "SourcePinsMissing", path);
            var pins = JsonUtility.FromJson<ShadowSourcePins>(File.ReadAllText(path));
            ShadowHash.Require(pins != null && pins.schemaVersion == 1, "SourcePinSchema", path);
            ShadowHash.Require(pins.unityVersion == Application.unityVersion && pins.target == target.ToString() && pins.architecture == architecture,
                "SourcePinTarget", "Source pins do not match exact Unity version, target and architecture.");
            ValidatePin(pins.hybridclr, "hybridclr"); ValidatePin(pins.hybridclrUnity, "hybridclrUnity");
            ValidatePin(pins.il2cppPlus, "il2cppPlus"); ValidatePin(pins.demo, "demo");
            return pins;
        }

        private static void ValidatePin(ShadowRepositoryPin pin, string name)
        {
            ShadowHash.Require(pin != null && !string.IsNullOrWhiteSpace(pin.url) && Regex.IsMatch(pin.revision ?? "", "^[0-9a-f]{40}$"),
                "InvalidSourcePin", name + " needs an exact forty-character commit SHA and repository URL.");
        }

        public string RuntimeAbiHash()
        {
            return ShadowHash.Text("assembly-shadow-runtime-abi:1\n" + unityVersion + "\n" + target + "\n" + architecture + "\n" +
                hybridclr.revision + "\n" + il2cppPlus.revision + "\n" + hybridclrUnity.revision);
        }

        public static void RequireCompatible(ShadowSourcePins baseline, ShadowSourcePins current)
        {
            ShadowHash.Require(baseline != null && current != null && baseline.RuntimeAbiHash() == current.RuntimeAbiHash(),
                "RuntimeAbiMismatch", "Patch compiler/runtime source pins, Unity, target or architecture differ from the Player baseline.");
            ShadowHash.Require(baseline.hybridclr.url == current.hybridclr.url && baseline.hybridclrUnity.url == current.hybridclrUnity.url &&
                baseline.il2cppPlus.url == current.il2cppPlus.url, "SourceRepositoryMismatch", "Runtime source repository identity differs.");
        }

        public static void RequireSameBuildSources(ShadowSourcePins captured, ShadowSourcePins requested)
        {
            RequireCompatible(captured, requested);
            ShadowHash.Require(captured.demo != null && requested.demo != null && captured.demo.url == requested.demo.url && captured.demo.revision == requested.demo.revision,
                "BuildSourceProvenanceMismatch", "Baseline source pins must identify the actual captured Player build, including the demo source revision.");
        }
    }
}
