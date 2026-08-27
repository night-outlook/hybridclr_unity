using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace HybridCLR.AssemblyShadow.CodeGen
{
    public static class ReflectionBindingFingerprint
    {
        public static string MethodSignature(MethodDef method)
        {
            BindingChecks.Require(method != null && method.DeclaringType != null, "InvalidMethod", "A declared method is required.");
            return method.FullName;
        }

        public static string Compute(MethodDef method) { return Shape(method, method == null || method.DeclaringType == null ? null : method.DeclaringType.FullName, -1, null); }

        internal static string Shape(MethodDef method, string owner, int replacementIndex, IMethod replacement)
        { return Shape(method, owner, replacementIndex, replacement, null); }

        // Only linked-output comparison supplies a scope map. The compiler/config
        // fingerprint continues to use exactly the original byte encoding.
        internal static string Shape(MethodDef method, string owner, int replacementIndex, IMethod replacement, Func<ITypeDefOrRef, string> linkedScope)
        {
            BindingChecks.Require(method != null && method.HasBody && !string.IsNullOrEmpty(owner), "InvalidMethod", "A managed method body is required.");
            BindingChecks.Require(!method.HasImplMap && !method.HasDeclSecurities && !method.HasOverrides, "UnsupportedMethod", "Native, security and override metadata require a newer fingerprint format.");
            using (var hash = new BindingHash("assembly-shadow-reflection-method:1"))
            {
                hash.TypeScope = linkedScope;
                hash.Add(owner); hash.Add(method.Name.String); hash.Add((int)method.Attributes); hash.Add((int)method.ImplAttributes);
                Signature(hash, method.MethodSig); Attributes(hash, method.CustomAttributes);
                hash.Add(method.GenericParameters.Count);
                foreach (var generic in method.GenericParameters)
                {
                    hash.Add(generic.Number); hash.Add((int)generic.Flags); hash.Add(generic.GenericParamConstraints.Count);
                    foreach (var constraint in generic.GenericParamConstraints) TypeReference(hash, constraint.Constraint);
                    Attributes(hash, generic.CustomAttributes);
                }
                hash.Add(method.ParamDefs.Count);
                foreach (var parameter in method.ParamDefs.OrderBy(parameter => parameter.Sequence))
                {
                    BindingChecks.Require(!parameter.HasMarshalType, "UnsupportedMethod", "Marshal metadata is unsupported.");
                    hash.Add(parameter.Sequence); hash.Add((int)parameter.Attributes); hash.Add(parameter.Name.String);
                    hash.Add(parameter.HasConstant); if (parameter.HasConstant) Primitive(hash, parameter.Constant.Value);
                    Attributes(hash, parameter.CustomAttributes);
                }
                var body = method.Body; hash.Add(body.InitLocals); hash.Add(body.Variables.Count);
                foreach (var local in body.Variables) TypeSignature(hash, local.Type);
                var indices = body.Instructions.Select((instruction, index) => new { instruction, index }).ToDictionary(pair => pair.instruction, pair => pair.index);
                hash.Add(body.Instructions.Count);
                for (int index = 0; index < body.Instructions.Count; index++)
                {
                    var instruction = body.Instructions[index]; string code = instruction.OpCode.Code.ToString();
                    if (instruction.IsLdcI4()) { hash.Add("Ldc_I4"); hash.Add(instruction.GetLdcI4Value()); continue; }
                    if (code.StartsWith("Ldarg", StringComparison.Ordinal) || code.StartsWith("Starg", StringComparison.Ordinal))
                    {
                        hash.Add(code.StartsWith("Ldarga", StringComparison.Ordinal) ? "Ldarga" : code.StartsWith("Starg", StringComparison.Ordinal) ? "Starg" : "Ldarg");
                        hash.Add(instruction.GetParameterIndex()); continue;
                    }
                    if (code.StartsWith("Ldloc", StringComparison.Ordinal) || code.StartsWith("Stloc", StringComparison.Ordinal))
                    {
                        hash.Add(code.StartsWith("Ldloca", StringComparison.Ordinal) ? "Ldloca" : code.StartsWith("Stloc", StringComparison.Ordinal) ? "Stloc" : "Ldloc");
                        var local = instruction.GetLocal(body.Variables); BindingChecks.Require(local != null, "InvalidLocal", method.FullName);
                        hash.Add(body.Variables.IndexOf(local)); continue;
                    }
                    if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget && code.EndsWith("_S", StringComparison.Ordinal)) code = code.Substring(0, code.Length - 2);
                    hash.Add(code);
                    object operand = index == replacementIndex ? replacement : instruction.Operand;
                    Operand(hash, operand, indices);
                }
                hash.Add(body.ExceptionHandlers.Count);
                foreach (var handler in body.ExceptionHandlers)
                {
                    hash.Add((int)handler.HandlerType); Position(hash, handler.TryStart, indices); Position(hash, handler.TryEnd, indices);
                    Position(hash, handler.HandlerStart, indices); Position(hash, handler.HandlerEnd, indices); Position(hash, handler.FilterStart, indices);
                    TypeReference(hash, handler.CatchType);
                }
                return hash.Finish();
            }
        }

        internal static string MethodIdentity(IMethod method)
        {
            using (var hash = new BindingHash("method-reference:1")) { Method(hash, method); return hash.Finish(); }
        }

        private static void Operand(BindingHash hash, object operand, IDictionary<Instruction, int> indices)
        {
            if (operand == null) { hash.Add("none"); return; }
            var instruction = operand as Instruction;
            if (instruction != null) { hash.Add("branch"); Position(hash, instruction, indices); return; }
            var targets = operand as IList<Instruction>;
            if (targets != null) { hash.Add("switch"); hash.Add(targets.Count); foreach (var target in targets) Position(hash, target, indices); return; }
            var method = operand as IMethod;
            if (method != null) { hash.Add("method"); Method(hash, method); return; }
            var field = operand as IField;
            if (field != null) { hash.Add("field"); TypeReference(hash, field.DeclaringType); hash.Add(field.Name.String); TypeSignature(hash, field.FieldSig.Type); return; }
            var type = operand as ITypeDefOrRef;
            if (type != null) { hash.Add("type"); TypeReference(hash, type); return; }
            var signature = operand as CallingConventionSig;
            if (signature != null) { hash.Add("signature"); Signature(hash, signature); return; }
            Primitive(hash, operand);
        }

        private static void Position(BindingHash hash, Instruction instruction, IDictionary<Instruction, int> indices)
        {
            if (instruction == null) { hash.Add(-1); return; }
            int index; BindingChecks.Require(indices.TryGetValue(instruction, out index), "InvalidBranch", "Branch/EH target is outside this body."); hash.Add(index);
        }

        private static void Method(BindingHash hash, IMethod method)
        {
            var spec = method as MethodSpec;
            if (spec != null)
            {
                hash.Add("method-spec"); Method(hash, spec.Method); hash.Add(spec.GenericInstMethodSig.GenericArguments.Count);
                foreach (var argument in spec.GenericInstMethodSig.GenericArguments) TypeSignature(hash, argument); return;
            }
            BindingChecks.Require(method != null && method.MethodSig != null, "UnsupportedOperand", "Method signature missing.");
            hash.Add("method"); TypeReference(hash, method.DeclaringType); hash.Add(method.Name.String); Signature(hash, method.MethodSig);
        }

        private static void Signature(BindingHash hash, CallingConventionSig signature)
        {
            var method = signature as MethodSig;
            BindingChecks.Require(method != null, "UnsupportedSignature", "Only complete method/calli signatures are supported.");
            hash.Add((int)method.CallingConvention); hash.Add(method.GenParamCount); TypeSignature(hash, method.RetType);
            hash.Add(method.Params.Count); foreach (var parameter in method.Params) TypeSignature(hash, parameter);
            hash.Add(method.ParamsAfterSentinel == null ? -1 : method.ParamsAfterSentinel.Count);
            if (method.ParamsAfterSentinel != null) foreach (var parameter in method.ParamsAfterSentinel) TypeSignature(hash, parameter);
        }

        private static void TypeReference(BindingHash hash, ITypeDefOrRef type)
        {
            if (type == null) { hash.Add(null); return; }
            var spec = type as TypeSpec;
            if (spec != null) { hash.Add("type-spec"); TypeSignature(hash, spec.TypeSig); return; }
            hash.Add("type"); hash.Add(type.FullName);
            var assembly = type.DefinitionAssembly;
            BindingChecks.Require(assembly != null, "UnsupportedTypeScope", type.FullName);
            hash.Add(hash.TypeScope == null ? assembly.FullName : hash.TypeScope(type));
        }

        private static void TypeSignature(BindingHash hash, TypeSig type)
        {
            if (type == null) { hash.Add(null); return; }
            hash.Add((int)type.ElementType);
            var generic = type as GenericInstSig;
            if (generic != null)
            {
                TypeSignature(hash, generic.GenericType); hash.Add(generic.GenericArguments.Count);
                foreach (var argument in generic.GenericArguments) TypeSignature(hash, argument); return;
            }
            var variable = type as GenericSig;
            if (variable != null) { hash.Add(variable.Number); return; }
            var modifier = type as ModifierSig;
            if (modifier != null) { TypeReference(hash, modifier.Modifier); TypeSignature(hash, modifier.Next); return; }
            var pointer = type as FnPtrSig;
            if (pointer != null) { Signature(hash, pointer.Signature); return; }
            var array = type as ArraySig;
            if (array != null)
            {
                hash.Add(array.Rank); hash.Add(array.Sizes.Count); foreach (var size in array.Sizes) hash.Add(size);
                hash.Add(array.LowerBounds.Count); foreach (var bound in array.LowerBounds) hash.Add(bound);
                TypeSignature(hash, array.Next); return;
            }
            var reference = type as TypeDefOrRefSig;
            if (reference != null) { TypeReference(hash, reference.TypeDefOrRef); return; }
            BindingChecks.Require(type is PtrSig || type is ByRefSig || type is SZArraySig || type is PinnedSig,
                "UnsupportedTypeSignature", type.GetType().FullName);
            TypeSignature(hash, type.Next);
        }

        private static void Attributes(BindingHash hash, IList<CustomAttribute> attributes)
        {
            hash.Add(attributes.Count);
            foreach (var attribute in attributes)
            {
                BindingChecks.Require(!attribute.IsRawBlob, "UnsupportedAttribute", "Opaque custom attributes are unsupported.");
                Method(hash, attribute.Constructor); hash.Add(attribute.ConstructorArguments.Count);
                foreach (var argument in attribute.ConstructorArguments) AttributeArgument(hash, argument);
                hash.Add(attribute.NamedArguments.Count);
                foreach (var argument in attribute.NamedArguments)
                { hash.Add(argument.IsField); hash.Add(argument.Name.String); TypeSignature(hash, argument.Type); AttributeArgument(hash, argument.Argument); }
            }
        }

        private static void AttributeArgument(BindingHash hash, CAArgument argument)
        {
            TypeSignature(hash, argument.Type);
            var array = argument.Value as IList<CAArgument>;
            if (array != null) { hash.Add("array"); hash.Add(array.Count); foreach (var value in array) AttributeArgument(hash, value); return; }
            var type = argument.Value as TypeSig;
            if (type != null) { hash.Add("type"); TypeSignature(hash, type); return; }
            if (argument.Value is CAArgument) { hash.Add("boxed"); AttributeArgument(hash, (CAArgument)argument.Value); return; }
            Primitive(hash, argument.Value);
        }

        private static void Primitive(BindingHash hash, object value)
        {
            if (value == null) { hash.Add("null"); return; }
            if (value is UTF8String) { hash.Add("string"); hash.Add(((UTF8String)value).String); return; }
            if (value is string) { hash.Add("string"); hash.Add(value); return; }
            if (value is float) { hash.Add("single-bits"); hash.Add(BitConverter.ToString(BitConverter.GetBytes((float)value))); return; }
            if (value is double) { hash.Add("double-bits"); hash.Add(BitConverter.ToString(BitConverter.GetBytes((double)value))); return; }
            BindingChecks.Require(value is bool || value is char || value is byte || value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong, "UnsupportedOperand", value.GetType().FullName);
            hash.Add(value.GetType().FullName); hash.Add(value);
        }
    }
}
