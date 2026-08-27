using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace HybridCLR.AssemblyShadow.CodeGen
{
    public static partial class ReflectionBindingTransformer
    {
        private static string[] Providers(ReflectionBindingSite site)
        {
            return ReflectionBindingConfiguration.KindOf(site) == "FixedAssemblyBytes"
                ? new[] { new AssemblyNameInfo(site.providerAssemblyIdentity).Name.String }
                : site.allowedTypes.Select(ReflectionBindingConfiguration.ProviderOf).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        private static Code OriginalCode(ReflectionBindingSite site)
        {
            string kind = ReflectionBindingConfiguration.KindOf(site);
            return kind == "FiniteAssemblyList" || kind == "FiniteAssemblyTypes" ? Code.Callvirt : Code.Call;
        }

        private static bool IsSiteAcquisition(IMethod method, ReflectionBindingSite site)
        {
            if (method == null || method.DeclaringType == null) return false;
            string kind = ReflectionBindingConfiguration.KindOf(site), owner = method.DeclaringType.FullName;
            if (kind == "TypeGetType") return IsAnyTypeLookup(method);
            if (kind == "FiniteAssemblyList") return owner == "System.AppDomain" && method.Name == "GetAssemblies";
            if (kind == "FiniteAssemblyTypes") return owner == "System.Reflection.Assembly" && (method.Name == "GetTypes" || method.Name == "GetExportedTypes");
            return owner == "System.Reflection.Assembly" && method.Name == "Load" && method.MethodSig != null &&
                method.MethodSig.Params.Count > 0 && method.MethodSig.Params[0].FullName == "System.Byte[]";
        }

        private static bool IsExactAcquisition(IMethod method, ModuleDef module, ReflectionBindingSite site)
        {
            if (ReflectionBindingConfiguration.KindOf(site) == "TypeGetType") return IsExactTypeLookup(method, module);
            return method != null && !(method is MethodSpec) && method.DeclaringType is TypeRef &&
                ReflectionBindingFingerprint.MethodIdentity(method) == ReflectionBindingFingerprint.MethodIdentity(OriginalAcquisition(module, site, null));
        }

        private static IMethod OriginalAcquisition(ModuleDef module, ReflectionBindingSite site, MethodDef guard)
        {
            string kind = ReflectionBindingConfiguration.KindOf(site);
            if (kind == "TypeGetType")
            {
                BindingChecks.Require(guard != null && guard.MethodSig != null && guard.MethodSig.Params.Count == 1 && guard.MethodSig.RetType.ToTypeDefOrRef() != null, "GuardTemplateMismatch", site.id);
                var lookup = new MemberRefUser(module, "GetType", MethodSig.CreateStatic(guard.MethodSig.RetType, guard.MethodSig.Params[0]), guard.MethodSig.RetType.ToTypeDefOrRef());
                BindingChecks.Require(IsExactTypeLookup(lookup, module), "GuardTemplateMismatch", site.id); return lookup;
            }
            var assembly = FrameworkType(module, "System.Reflection", "Assembly");
            if (kind == "FiniteAssemblyList") return new MemberRefUser(module, "GetAssemblies", MethodSig.CreateInstance(new SZArraySig(new ClassSig(assembly))), FrameworkType(module, "System", "AppDomain"));
            if (kind == "FiniteAssemblyTypes") return new MemberRefUser(module, "GetTypes", MethodSig.CreateInstance(new SZArraySig(new ClassSig(FrameworkType(module, "System", "Type")))), assembly);
            BindingChecks.Require(kind == "FixedAssemblyBytes", "InvalidAcquisitionKind", site.id);
            return new MemberRefUser(module, "Load", MethodSig.CreateStatic(new ClassSig(assembly), new SZArraySig(module.CorLibTypes.Byte)), assembly);
        }

        private static TypeRef FrameworkType(ModuleDef module, string ns, string name) { return new TypeRefUser(module, ns, name, module.CorLibTypes.AssemblyRef); }

        private static ITypeDefOrRef TargetType(ModuleDef module, string aqn)
        {
            int comma = aqn.IndexOf(','); string name = aqn.Substring(0, comma);
            var scope = new AssemblyRefUser(new AssemblyNameInfo(aqn.Substring(comma + 2)));
            string[] nested = name.Split('+'); int split = nested[0].LastIndexOf('.');
            TypeRef type = new TypeRefUser(module, split < 0 ? "" : nested[0].Substring(0, split), nested[0].Substring(split + 1), scope);
            foreach (string part in nested.Skip(1)) type = new TypeRefUser(module, "", part, type);
            return type;
        }

        private static MethodDef CreateAcquisitionGuard(ModuleDef module, ReflectionBindingSite site, string hash, IMethod lookup)
        {
            string kind = ReflectionBindingConfiguration.KindOf(site);
            TypeSig parameter = lookup.MethodSig.HasThis ? new ClassSig(lookup.DeclaringType) : lookup.MethodSig.Params[0];
            var guard = new MethodDefUser(GuardName(site, hash), MethodSig.CreateStatic(lookup.MethodSig.RetType, parameter),
                MethodImplAttributes.IL | MethodImplAttributes.Managed, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig)
            { Body = new CilBody { InitLocals = true, MaxStack = 5 } };
            var il = guard.Body.Instructions;
            var denied = Instruction.Create(OpCodes.Ldstr, "AssemblyShadow reflection denied; configuration=" + hash + "; site=" + site.id);
            if (kind == "FixedAssemblyBytes") EmitFixedBytes(module, site, guard, lookup, denied);
            else
            {
                var type = FrameworkType(module, "System", "Type");
                var assembly = FrameworkType(module, "System.Reflection", "Assembly");
                var fromHandle = new MemberRefUser(module, "GetTypeFromHandle", MethodSig.CreateStatic(new ClassSig(type), new ValueTypeSig(FrameworkType(module, "System", "RuntimeTypeHandle"))), type);
                var getAssembly = new MemberRefUser(module, "get_Assembly", MethodSig.CreateInstance(new ClassSig(assembly)), type);
                var same = new MemberRefUser(module, "ReferenceEquals", MethodSig.CreateStatic(module.CorLibTypes.Boolean, module.CorLibTypes.Object, module.CorLibTypes.Object), module.CorLibTypes.Object.TypeDefOrRef);
                var groups = site.allowedTypes.OrderBy(value => value, StringComparer.Ordinal).GroupBy(value => value.Substring(value.IndexOf(',') + 2), StringComparer.Ordinal).ToArray();
                if (kind == "FiniteAssemblyList")
                {
                    var domain = FrameworkType(module, "System", "AppDomain");
                    il.Add(Instruction.Create(OpCodes.Ldarg_0));
                    il.Add(Instruction.Create(OpCodes.Call, new MemberRefUser(module, "get_CurrentDomain", MethodSig.CreateStatic(new ClassSig(domain)), domain)));
                    il.Add(Instruction.Create(OpCodes.Call, same)); il.Add(Instruction.Create(OpCodes.Brfalse, denied));
                    il.Add(Instruction.CreateLdcI4(groups.Length)); il.Add(Instruction.Create(OpCodes.Newarr, assembly));
                    for (int index = 0; index < groups.Length; index++)
                    {
                        il.Add(Instruction.Create(OpCodes.Dup)); il.Add(Instruction.CreateLdcI4(index));
                        EmitType(il, module, groups[index].First(), fromHandle); il.Add(Instruction.Create(OpCodes.Callvirt, getAssembly)); il.Add(Instruction.Create(OpCodes.Stelem_Ref));
                    }
                    il.Add(Instruction.Create(OpCodes.Ret));
                }
                else
                {
                    BindingChecks.Require(kind == "FiniteAssemblyTypes", "InvalidAcquisitionKind", site.id);
                    foreach (var group in groups)
                    {
                        var next = Instruction.Create(OpCodes.Nop);
                        il.Add(Instruction.Create(OpCodes.Ldarg_0)); EmitType(il, module, group.First(), fromHandle); il.Add(Instruction.Create(OpCodes.Callvirt, getAssembly));
                        il.Add(Instruction.Create(OpCodes.Call, same)); il.Add(Instruction.Create(OpCodes.Brfalse, next));
                        string[] allowed = group.ToArray(); il.Add(Instruction.CreateLdcI4(allowed.Length)); il.Add(Instruction.Create(OpCodes.Newarr, type));
                        for (int index = 0; index < allowed.Length; index++)
                        { il.Add(Instruction.Create(OpCodes.Dup)); il.Add(Instruction.CreateLdcI4(index)); EmitType(il, module, allowed[index], fromHandle); il.Add(Instruction.Create(OpCodes.Stelem_Ref)); }
                        il.Add(Instruction.Create(OpCodes.Ret)); il.Add(next);
                    }
                    il.Add(Instruction.Create(OpCodes.Br, denied));
                }
            }
            il.Add(denied);
            il.Add(Instruction.Create(OpCodes.Newobj, new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void, module.CorLibTypes.String), FrameworkType(module, "System", "InvalidOperationException"))));
            il.Add(Instruction.Create(OpCodes.Throw));
            return guard;
        }

        private static void EmitType(IList<Instruction> il, ModuleDef module, string aqn, IMethod fromHandle)
        { il.Add(Instruction.Create(OpCodes.Ldtoken, TargetType(module, aqn))); il.Add(Instruction.Create(OpCodes.Call, fromHandle)); }

        private static void EmitFixedBytes(ModuleDef module, ReflectionBindingSite site, MethodDef guard, IMethod lookup, Instruction denied)
        {
            var il = guard.Body.Instructions; var bytes = new SZArraySig(module.CorLibTypes.Byte);
            var shaType = FrameworkType(module, "System.Security.Cryptography", "SHA256Managed");
            var copy = new Local(bytes); var sha = new Local(new ClassSig(shaType)); var digest = new Local(bytes);
            guard.Body.Variables.Add(copy); guard.Body.Variables.Add(sha); guard.Body.Variables.Add(digest);
            il.Add(Instruction.Create(OpCodes.Ldarg_0)); il.Add(Instruction.Create(OpCodes.Brfalse, denied));
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Callvirt, new MemberRefUser(module, "Clone", MethodSig.CreateInstance(module.CorLibTypes.Object), FrameworkType(module, "System", "Array"))));
            il.Add(Instruction.Create(OpCodes.Castclass, new TypeSpecUser(bytes))); il.Add(Instruction.Create(OpCodes.Stloc, copy));
            il.Add(Instruction.Create(OpCodes.Newobj, new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void), shaType))); il.Add(Instruction.Create(OpCodes.Stloc, sha));
            var start = Instruction.Create(OpCodes.Ldloc, sha); var cleanup = Instruction.Create(OpCodes.Ldloc, sha); var check = Instruction.Create(OpCodes.Ldloc, digest);
            il.Add(start); il.Add(Instruction.Create(OpCodes.Ldloc, copy));
            il.Add(Instruction.Create(OpCodes.Callvirt, new MemberRefUser(module, "ComputeHash", MethodSig.CreateInstance(bytes, bytes), FrameworkType(module, "System.Security.Cryptography", "HashAlgorithm"))));
            il.Add(Instruction.Create(OpCodes.Stloc, digest)); il.Add(Instruction.Create(OpCodes.Leave, check));
            il.Add(cleanup); il.Add(Instruction.Create(OpCodes.Callvirt, new MemberRefUser(module, "Dispose", MethodSig.CreateInstance(module.CorLibTypes.Void), FrameworkType(module, "System", "IDisposable")))); il.Add(Instruction.Create(OpCodes.Endfinally));
            guard.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = start, TryEnd = cleanup, HandlerStart = cleanup, HandlerEnd = check });
            il.Add(check); il.Add(Instruction.Create(OpCodes.Ldlen)); il.Add(Instruction.Create(OpCodes.Conv_I4)); il.Add(Instruction.CreateLdcI4(32)); il.Add(Instruction.Create(OpCodes.Bne_Un, denied));
            for (int index = 0; index < 32; index++)
            {
                il.Add(Instruction.Create(OpCodes.Ldloc, digest)); il.Add(Instruction.CreateLdcI4(index)); il.Add(Instruction.Create(OpCodes.Ldelem_U1));
                il.Add(Instruction.CreateLdcI4(Convert.ToByte(site.imageSha256.Substring(index * 2, 2), 16))); il.Add(Instruction.Create(OpCodes.Bne_Un, denied));
            }
            il.Add(Instruction.Create(OpCodes.Ldloc, copy)); il.Add(Instruction.Create(OpCodes.Call, lookup)); il.Add(Instruction.Create(OpCodes.Ret));
        }
    }
}
