using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace HybridCLR.AssemblyShadow.CodeGen
{
    public static partial class ReflectionBindingTransformer
    {
        public static VerifiedReflectionBinding[] VerifyLinked(ModuleDefMD compiled, ModuleDefMD linked,
            ReflectionBindingConfiguration configuration, CapturedReflectionRetargetingProfile profile)
        {
            // Never weaken the compiler/config proof with linker normalization.
            var sourceBindings = Verify(compiled, configuration);
            BindingChecks.Require(linked != null && linked.Assembly != null && compiled.Assembly.FullName == linked.Assembly.FullName,
                "LinkedAssemblyIdentityMismatch", "Compiler and linked assembly identities must match exactly.");
            BindingChecks.Require(profile != null, "MissingRetargetingProfile", "Captured per-type facade and destination evidence is required.");
            RequireUnsigned(linked);
            string configHash = configuration.ComputeHash();
            var expectedGuards = new HashSet<MethodDef>();
            var result = new List<VerifiedReflectionBinding>();
            foreach (var source in sourceBindings)
            {
                var site = configuration.sites.Single(value => value.id == source.SiteId);
                var original = FindMethod(linked, site);
                BindingChecks.Require(site.operationIndex < original.Body.Instructions.Count, "MissingGuardedSite", site.id);
                var operation = original.Body.Instructions[site.operationIndex]; var guard = operation.Operand as MethodDef;
                BindingChecks.Require(operation.OpCode.Code == Code.Call && guard != null && guard.DeclaringType == original.DeclaringType &&
                    guard.Name == GuardName(site, configHash), "MissingGuardedSite", site.id);
                BindingChecks.Require(expectedGuards.Add(guard), "AmbiguousGuard", site.id);
                BindingChecks.Require(original.Body.Instructions.All(instruction => !IsAnyTypeLookup(instruction.Operand as IMethod)), "AdditionalLookup", site.id);

                // Independently reconstruct the entire immutable guard in the
                // linked corlib scope, before comparing with the compiler guard.
                BindingChecks.Require(guard.HasBody && guard.MethodSig != null && guard.MethodSig.Params.Count == 1, "GuardTemplateMismatch", site.id);
                var returnType = guard.MethodSig.RetType.ToTypeDefOrRef();
                BindingChecks.Require(returnType != null, "GuardTemplateMismatch", site.id);
                var lookup = new MemberRefUser(linked, "GetType", MethodSig.CreateStatic(guard.MethodSig.RetType, guard.MethodSig.Params[0]), returnType);
                BindingChecks.Require(IsExactTypeLookup(lookup, linked), "GuardTemplateMismatch", site.id);
                var expected = CreateGuard(linked, site, configHash, lookup);
                string linkedGuardHash = ReflectionBindingFingerprint.Compute(guard);
                BindingChecks.Require(linkedGuardHash == ReflectionBindingFingerprint.Shape(expected, original.DeclaringType.FullName, -1, null), "GuardTemplateMismatch", site.id);
                string linkedMethodHash = ReflectionBindingFingerprint.Compute(original);
                BindingChecks.Require(ReflectionBindingFingerprint.Shape(source.OriginalMethod, source.TypeName, -1, null, profile.LinkedScope) == linkedMethodHash,
                    "LinkedMethodChanged", site.id);
                BindingChecks.Require(ReflectionBindingFingerprint.Shape(source.GuardMethod, source.TypeName, -1, null, profile.LinkedScope) == linkedGuardHash,
                    "LinkedGuardChanged", site.id);
                foreach (var method in linked.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody))
                    foreach (var instruction in method.Body.Instructions)
                    {
                        var target = instruction.Operand as IMethod;
                        if (target == null || target.Name != guard.Name) continue;
                        BindingChecks.Require(method == original && instruction == operation && target == guard, "AdditionalGuardReference", site.id);
                    }
                result.Add(new VerifiedReflectionBinding { SiteId = source.SiteId, Assembly = source.Assembly, TypeName = source.TypeName,
                    MethodSignature = source.MethodSignature, OperationIndex = source.OperationIndex, OriginalMethod = original, GuardMethod = guard,
                    ConfigurationHash = source.ConfigurationHash, AllowedTypes = source.AllowedTypes, Providers = source.Providers,
                    LinkedProfileHash = profile.ComputeHash(), CompiledMethodHash = ReflectionBindingFingerprint.Compute(source.OriginalMethod),
                    LinkedMethodHash = linkedMethodHash, CompiledGuardHash = ReflectionBindingFingerprint.Compute(source.GuardMethod), LinkedGuardHash = linkedGuardHash });
            }
            foreach (var method in linked.GetTypes().SelectMany(type => type.Methods))
                BindingChecks.Require(!method.Name.String.StartsWith(GuardPrefix, StringComparison.Ordinal) || expectedGuards.Contains(method), "UnexpectedGuard", method.FullName);
            BindingChecks.Require(configuration.ComputeHash() == configHash, "ConfigurationChanged", "Configuration changed during linked verification.");
            return result.ToArray();
        }
    }
}
