using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace HybridCLR.Editor.AssemblyShadow
{
    public enum RuntimeAssemblyInputKind { Link, MethodBridge, AotGenericReference, ReversePInvoke, Diagnostics }

    public interface IRuntimeAssemblyInputProvider
    {
        IReadOnlyList<string> GetAssemblies(BuildTarget target, RuntimeAssemblyInputKind kind);
    }

    /// <summary>Adapter for M06 generators. Never changes the Player's hot-update filter.</summary>
    public sealed class ShadowRuntimeAssemblyInputProvider : IRuntimeAssemblyInputProvider
    {
        private readonly BuildTarget target;
        private readonly string[] assemblies;

        public ShadowRuntimeAssemblyInputProvider(BuildTarget target, IEnumerable<string> normalHotUpdateAssemblies, IEnumerable<string> currentShadowPatchClosure)
        {
            this.target = target;
            assemblies = normalHotUpdateAssemblies.Concat(currentShadowPatchClosure)
                .GroupBy(AssemblyIdentityUtil.CanonicalName, StringComparer.Ordinal)
                .Select(group => group.OrderBy(n => n, StringComparer.Ordinal).First())
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        public IReadOnlyList<string> GetAssemblies(BuildTarget requestedTarget, RuntimeAssemblyInputKind kind)
        {
            ShadowHash.Require(target == requestedTarget, "TargetMismatch", "Generator input snapshot belongs to " + target);
            ShadowHash.Require(Enum.IsDefined(typeof(RuntimeAssemblyInputKind), kind), "InputKind", "Unknown generator input kind.");
            return Array.AsReadOnly(assemblies);
        }
    }
}
