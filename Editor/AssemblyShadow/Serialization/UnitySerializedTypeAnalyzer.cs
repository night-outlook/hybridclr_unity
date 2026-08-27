using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace HybridCLR.Editor.AssemblyShadow
{
    /// <summary>Unity 2022 serialization analysis using only the explicit target metadata set.</summary>
    public static class UnitySerializedTypeAnalyzer
    {
        public static ResourceAbiDescriptor Analyze(CompiledAssemblySet set, IEnumerable<string> assemblyNames)
        {
            if (set == null) throw new ArgumentNullException("set");
            return new Analysis(set).Run(assemblyNames);
        }

        private sealed class Analysis
        {
            private readonly CompiledAssemblySet set;
            private readonly List<TypeDef> allTypes;
            private readonly HashSet<string> requestedAssemblies = new HashSet<string>(StringComparer.Ordinal);
            private static readonly HashSet<string> UnityValues = new HashSet<string>(new[] {
                "UnityEngine.Vector2", "UnityEngine.Vector3", "UnityEngine.Vector4", "UnityEngine.Vector2Int", "UnityEngine.Vector3Int",
                "UnityEngine.Rect", "UnityEngine.RectInt", "UnityEngine.Bounds", "UnityEngine.BoundsInt", "UnityEngine.Quaternion",
                "UnityEngine.Color", "UnityEngine.Color32", "UnityEngine.Matrix4x4", "UnityEngine.LayerMask",
                "UnityEngine.AnimationCurve", "UnityEngine.Gradient", "UnityEngine.RectOffset", "UnityEngine.Hash128"
            }, StringComparer.Ordinal);

            public Analysis(CompiledAssemblySet set)
            {
                this.set = set;
                allTypes = set.Modules.Values.SelectMany(m => m.GetTypes()).OrderBy(Key, StringComparer.Ordinal).ToList();
            }

            public ResourceAbiDescriptor Run(IEnumerable<string> assemblyNames)
            {
                var requested = new HashSet<string>((assemblyNames ?? Enumerable.Empty<string>()).Select(AssemblyIdentityUtil.CanonicalName), StringComparer.Ordinal);
                requestedAssemblies.UnionWith(requested);
                var pending = new Queue<TypeDef>();
                foreach (var type in allTypes.Where(t => !IsEngine(t) && (requested.Count == 0 || requested.Contains(Assembly(t)))))
                {
                    bool incomplete;
                    bool root = IsObject(type, out incomplete);
                    // An unresolved base may hide a Unity root; do not silently classify it code-only.
                    if (root || incomplete) pending.Enqueue(type);
                }
                var result = new Dictionary<string, ResourceAbiTypeDescriptor>(StringComparer.Ordinal);
                while (pending.Count != 0)
                {
                    var type = pending.Dequeue();
                    if (result.ContainsKey(Key(type)) || IsEngine(type) || IsSystem(type)) continue;
                    var related = new HashSet<TypeDef>();
                    var descriptor = Describe(type, related);
                    result.Add(descriptor.typeKey, descriptor);
                    foreach (var child in related) pending.Enqueue(child);
                }
                var descriptors = result.Values.OrderBy(t => t.typeKey, StringComparer.Ordinal).ToArray();
                return new ResourceAbiDescriptor(descriptors, descriptors.SelectMany(t => t.unknownReasons.Select(r => t.typeKey + ":" + r))
                    .Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToArray());
            }

            private ResourceAbiTypeDescriptor Describe(TypeDef type, ISet<TypeDef> related)
            {
                var unknown = new HashSet<string>(StringComparer.Ordinal);
                var chain = Chain(type, unknown).ToList();
                var fields = new List<ResourceAbiFieldDescriptor>();
                var candidates = new HashSet<string>(StringComparer.Ordinal);
                foreach (var current in chain)
                {
                    if (IsEngine(current) || IsSystem(current)) continue;
                    if (current != type) related.Add(current);
                    if (current.HasGenericParameters) unknown.Add("generic serialized type is not proven supported: " + Key(current));
                    foreach (var field in current.Fields.Where(IsSerializedField))
                    {
                        bool managed = Has(field, "UnityEngine.SerializeReference");
                        var fieldUnknown = new HashSet<string>(StringComparer.Ordinal);
                        string fieldType = DescribeFieldType(field.FieldType, managed, false, related, candidates, fieldUnknown);
                        foreach (var reason in fieldUnknown) unknown.Add(field.Name + ":" + reason);
                        fields.Add(new ResourceAbiFieldDescriptor {
                            declaringType = Key(current), name = field.Name, type = fieldType, shape = Shape(field.FieldType),
                            flags = (field.IsPublic ? "public;" : "") + (Has(field, "UnityEngine.SerializeField") ? "SerializeField;" : "") + (managed ? "SerializeReference" : ""),
                            formerNames = field.CustomAttributes.Where(a => AttributeName(a) == "UnityEngine.Serialization.FormerlySerializedAsAttribute")
                                .Select(a => a.ConstructorArguments.Count == 1 ? Convert.ToString(a.ConstructorArguments[0].Value, CultureInfo.InvariantCulture) : string.Empty)
                                .Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                            managedReferenceMode = managed ? "SerializeReference" : string.Empty, unknown = fieldUnknown.Count != 0,
                        });
                    }
                }
                bool callback = Implements(type, "UnityEngine.ISerializationCallbackReceiver", new HashSet<string>(), unknown);
                string callbackHash = callback ? CallbackHash(chain, unknown) : string.Empty;
                return new ResourceAbiTypeDescriptor {
                    typeKey = Key(type), assembly = Assembly(type), @namespace = Namespace(type), type = NestedName(type),
                    baseChain = string.Join("<", chain.Select(Key).ToArray()),
                    interfaces = chain.SelectMany(t => t.Interfaces.Select(i => Key(i.Interface))).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                    fields = fields.OrderBy(f => f.declaringType, StringComparer.Ordinal).ThenBy(f => f.name, StringComparer.Ordinal).ToArray(),
                    serializationCallback = callback, callbackSemanticHash = callbackHash,
                    serializeReferenceCandidates = candidates.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                    referencedTypeKeys = related.Where(t => t != type && !IsEngine(t) && !IsSystem(t)).Select(Key).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                    hasUnknown = unknown.Count != 0, unknownReasons = unknown.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                };
            }

            private string DescribeFieldType(TypeSig sig, bool managed, bool inContainer, ISet<TypeDef> related,
                ISet<string> candidates, ISet<string> unknown)
            {
                if (sig == null) { unknown.Add("missing field signature"); return "Unknown"; }
                var generic = sig as GenericInstSig;
                bool list = generic != null && generic.GenericType.FullName == "System.Collections.Generic.List`1" && IsCore(generic.GenericType.ToTypeDefOrRef());
                if (sig is SZArraySig || list)
                {
                    if (inContainer) unknown.Add("nested containers are unsupported");
                    if (list && generic.GenericArguments.Count != 1) { unknown.Add("invalid List signature"); return sig.FullName; }
                    var child = list ? generic.GenericArguments[0] : sig.Next;
                    return (list ? "List<" : "Array<") + DescribeFieldType(child, managed, true, related, candidates, unknown) + ">";
                }
                if (sig is ArraySig || generic != null || sig is GenericVar || sig is GenericMVar || sig is PtrSig || sig is ByRefSig)
                { unknown.Add("unsupported array, generic or pointer shape: " + sig.FullName); return sig.FullName; }
                if (!managed && IsPrimitive(sig.ElementType)) return sig.FullName;
                var reference = sig.ToTypeDefOrRef();
                var type = set.ResolveType(reference);
                if (managed)
                {
                    bool objectTarget = sig.ElementType == ElementType.Object;
                    bool incomplete = false;
                    if (!objectTarget && (type == null || type.IsValueType || IsObject(type, out incomplete)))
                    { unknown.Add("invalid or unresolved managed reference target: " + sig.FullName); return Key(reference); }
                    if (incomplete) unknown.Add("unresolved managed reference ancestry: " + sig.FullName);
                    if (type != null && !IsSystem(type)) related.Add(type);
                    int count = 0;
                    foreach (var candidate in allTypes.Where(t => Serializable(t) && !t.IsAbstract && !t.IsInterface && !t.IsValueType && !t.HasGenericParameters && !IsSystem(t) && !IsEngine(t)))
                    {
                        bool objectUnknown;
                        if (IsObject(candidate, out objectUnknown)) continue;
                        if (objectUnknown) { unknown.Add("unresolved managed candidate ancestry: " + Key(candidate)); continue; }
                        var assignmentUnknown = new HashSet<string>(StringComparer.Ordinal);
                        if (!objectTarget && !Assignable(candidate, type, new HashSet<string>(), assignmentUnknown))
                        { foreach (var reason in assignmentUnknown) unknown.Add(reason); continue; }
                        related.Add(candidate); candidates.Add(Key(candidate)); ++count;
                    }
                    if (count == 0) unknown.Add("no proven serializable managed reference candidates: " + sig.FullName);
                    return objectTarget ? "System.Object" : Key(type);
                }
                if (type == null) { unknown.Add("unresolved serialized type: " + Key(reference)); return Key(reference); }
                bool objectIncomplete;
                if (IsObject(type, out objectIncomplete)) { if (!IsEngine(type)) related.Add(type); return Key(type); }
                if (objectIncomplete) unknown.Add("unresolved serialized ancestry: " + Key(type));
                if (IsEngine(type) && UnityValues.Contains(type.FullName)) return Key(type);
                if (type.IsEnum)
                {
                    var backing = type.Fields.FirstOrDefault(f => !f.IsStatic);
                    if (backing == null || (backing.FieldType.ElementType != ElementType.I4 && backing.FieldType.ElementType != ElementType.U4 &&
                        backing.FieldType.ElementType != ElementType.I2 && backing.FieldType.ElementType != ElementType.U2 &&
                        backing.FieldType.ElementType != ElementType.I1 && backing.FieldType.ElementType != ElementType.U1)) unknown.Add("unsupported enum storage: " + Key(type));
                    related.Add(type); return Key(type);
                }
                if (!Serializable(type) || type.IsInterface || type.IsAbstract || IsSystem(type) || IsEngine(type) || type.HasGenericParameters)
                    unknown.Add("unsupported inline serialized type: " + Key(type));
                else related.Add(type);
                return Key(type);
            }

            private IEnumerable<TypeDef> Chain(TypeDef type, ISet<string> unknown)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                for (var current = type; current != null;)
                {
                    if (!visited.Add(Key(current))) { unknown.Add("cyclic base chain"); yield break; }
                    yield return current;
                    var parent = current.BaseType;
                    if (parent == null || IsTerminal(parent)) yield break;
                    current = set.ResolveType(parent);
                    if (current == null) unknown.Add("unresolved base: " + Key(parent));
                }
            }

            private bool IsObject(TypeDef type, out bool incomplete)
            {
                var unknown = new HashSet<string>();
                bool found = Chain(type, unknown).Any(t => IsEngine(t) && t.FullName == "UnityEngine.Object");
                incomplete = unknown.Count != 0;
                return found;
            }

            private bool Assignable(TypeDef type, TypeDef target, ISet<string> visited, ISet<string> unknown)
            {
                if (type == null || target == null || !visited.Add(Key(type))) return false;
                if (Key(type) == Key(target)) return true;
                foreach (var reference in type.Interfaces.Select(i => i.Interface).Concat(type.BaseType == null ? Enumerable.Empty<ITypeDefOrRef>() : new[] { type.BaseType }))
                {
                    if (IsTerminal(reference)) continue;
                    var parent = set.ResolveType(reference);
                    if (parent == null) unknown.Add("unresolved managed candidate relation: " + Key(reference));
                    else if (Assignable(parent, target, visited, unknown)) return true;
                }
                return false;
            }

            private bool Implements(TypeDef type, string name, ISet<string> visited, ISet<string> unknown)
            {
                if (type == null || !visited.Add(Key(type))) return false;
                foreach (var reference in type.Interfaces.Select(i => i.Interface).Concat(type.BaseType == null ? Enumerable.Empty<ITypeDefOrRef>() : new[] { type.BaseType }))
                {
                    if (IsTerminal(reference)) continue;
                    if (reference.FullName == name && IsEngine(reference)) return true;
                    var parent = set.ResolveType(reference);
                    if (parent == null) unknown.Add("unresolved interface/base: " + Key(reference));
                    else if (Implements(parent, name, visited, unknown)) return true;
                }
                return false;
            }

            private string CallbackHash(IEnumerable<TypeDef> chain, ISet<string> unknown)
            {
                var types = chain.ToArray();
                var serializedInputs = new HashSet<string>(types.Where(t => !IsCore(t) && !IsEngine(t))
                    .SelectMany(t => t.Fields).Where(IsSerializedField).Select(AssemblySemanticHasher.FieldRefText), StringComparer.Ordinal);
                return new CallbackDependencyClosure(set, unknown, IsEngine, serializedInputs).Compute(types);
            }

            private sealed class CallbackDependencyClosure
            {
                private readonly CompiledAssemblySet set;
                private readonly ISet<string> unknown;
                private readonly Func<ITypeDefOrRef, bool> isEngine;
                private readonly ISet<string> serializedInstanceInputs;
                private readonly Queue<MethodDef> pending = new Queue<MethodDef>();
                private readonly HashSet<string> methods = new HashSet<string>(StringComparer.Ordinal);
                private readonly HashSet<string> initializedTypes = new HashSet<string>(StringComparer.Ordinal);
                private readonly SortedDictionary<string, string> entries = new SortedDictionary<string, string>(StringComparer.Ordinal);

                public CallbackDependencyClosure(CompiledAssemblySet set, ISet<string> unknown, Func<ITypeDefOrRef, bool> isEngine,
                    ISet<string> serializedInstanceInputs)
                {
                    this.set = set;
                    this.unknown = unknown;
                    this.isEngine = isEngine;
                    this.serializedInstanceInputs = serializedInstanceInputs;
                }

                private bool IsEngine(ITypeDefOrRef type) { return isEngine(type); }

                public string Compute(IEnumerable<TypeDef> chain)
                {
                    foreach (var type in chain)
                    {
                        foreach (var method in type.Methods.Where(m => m.Name == "OnBeforeSerialize" || m.Name == "OnAfterDeserialize" ||
                            m.Name.String.EndsWith(".OnBeforeSerialize", StringComparison.Ordinal) || m.Name.String.EndsWith(".OnAfterDeserialize", StringComparison.Ordinal)))
                            pending.Enqueue(method);
                    }
                    if (pending.Count == 0) unknown.Add("serialization callback implementation unresolved");
                    while (pending.Count != 0)
                    {
                        var method = pending.Dequeue();
                        string key = AssemblySemanticHasher.MethodRefText(method);
                        if (!methods.Add(key)) continue;
                        entries["method:" + key] = AssemblySemanticHasher.MethodSemanticText(method);
                        TrackInitialization(method.DeclaringType);
                        if (!method.HasBody || method.IsPinvokeImpl || method.NativeBody != null)
                        {
                            unknown.Add("serialization callback body unavailable: " + key);
                            continue;
                        }
                        foreach (var instruction in method.Body.Instructions)
                        {
                            if (instruction.OpCode.Code == Code.Calli || instruction.OpCode.Code == Code.Ldftn || instruction.OpCode.Code == Code.Ldvirtftn)
                                unknown.Add("callback indirect or delegate dispatch cannot be proven: " + key);
                            // MemberRef implements both interfaces, so inspect the signature
                            // before choosing a field or method dependency.
                            var member = instruction.Operand as MemberRef;
                            var field = member != null ? (member.IsFieldRef ? (IField)member : null) : instruction.Operand as IField;
                            if (field != null) { TrackField(field); continue; }
                            var called = member != null ? (member.IsMethodRef ? (IMethod)member : null) : instruction.Operand as IMethod;
                            if (called != null) TrackMethod(called, instruction.OpCode.Code);
                        }
                    }
                    var writer = new CanonicalSignatureWriter();
                    // Old weak callback hashes can never compare equal after this upgrade.
                    writer.Line("resource-callback-dependencies-v2");
                    foreach (var entry in entries) { writer.Line(entry.Key); writer.Line(entry.Value); }
                    return "sha256:" + ShadowHash.Text(writer.ToString());
                }

                private void TrackInitialization(TypeDef type)
                {
                    if (type == null || IsCore(type) || IsEngine(type) || !initializedTypes.Add(Key(type))) return;
                    // BeforeFieldInit and module initialization affect when callback-observed
                    // static state is populated even when IL never directly calls .cctor.
                    entries["initialization:" + Key(type)] = type.Attributes.ToString();
                    foreach (var initializer in type.Methods.Where(m => m.IsStaticConstructor)) pending.Enqueue(initializer);
                    if (type.Module != null && type.Module.GlobalType != null && type != type.Module.GlobalType)
                        TrackInitialization(type.Module.GlobalType);
                    if (type.BaseType == null || IsTerminal(type.BaseType)) return;
                    var parent = set.ResolveType(type.BaseType);
                    if (parent == null) unknown.Add("callback initialization base unresolved: " + Key(type.BaseType));
                    else TrackInitialization(parent);
                }

                private void TrackField(IField reference)
                {
                    string key = AssemblySemanticHasher.FieldRefText(reference);
                    var declaring = set.ResolveType(reference.DeclaringType);
                    var matches = declaring == null ? new FieldDef[0] : declaring.Fields.Where(f => f.Name == reference.Name &&
                        new SigComparer().Equals(f.FieldSig, reference.FieldSig)).ToArray();
                    if (matches.Length != 1) { unknown.Add("callback field dependency unresolved or ambiguous: " + key); return; }
                    var field = matches[0];
                    entries["field:" + key] = AssemblySemanticHasher.FieldSemanticText(field);
                    if (!field.IsStatic)
                    {
                        // Instance .ctors and other writers need not be called by the callback.
                        // A declaration fingerprint cannot prove their state unchanged. Only
                        // this serialized object's explicit root/base fields are proven inputs;
                        // readonly, excluded and foreign-instance state still require review.
                        if (!serializedInstanceInputs.Contains(AssemblySemanticHasher.FieldRefText(field)))
                            unknown.Add("callback unproven instance state requires review: " + key);
                        return;
                    }
                    TrackInitialization(declaring);
                    if (reference.DeclaringType is TypeSpec || declaring.HasGenericParameters)
                        unknown.Add("callback generic static initialization cannot be proven: " + key);
                    if (IsCore(declaring) || IsEngine(declaring))
                        unknown.Add("callback runtime static state cannot be proven: " + key);
                    if (!field.IsLiteral && !field.IsInitOnly)
                        unknown.Add("callback mutable static state cannot be proven: " + key);
                    else if (!field.IsLiteral && !IsPrimitive(field.FieldType.ElementType))
                        unknown.Add("callback mutable static object state cannot be proven: " + key);
                }

                private void TrackMethod(IMethod reference, Code operation)
                {
                    string key = AssemblySemanticHasher.MethodRefText(reference);
                    var specification = reference as MethodSpec;
                    var target = specification == null ? reference : specification.Method;
                    var declaring = set.ResolveType(target.DeclaringType);
                    var matches = declaring == null ? new MethodDef[0] : declaring.Methods.Where(m => m.Name == target.Name &&
                        new SigComparer().Equals(m.MethodSig, target.MethodSig)).ToArray();
                    if (matches.Length != 1) { unknown.Add("callback method dependency unresolved or ambiguous: " + key); return; }
                    var definition = matches[0];
                    if (operation == Code.Ldvirtftn || (operation == Code.Callvirt && definition.IsVirtual))
                        unknown.Add("callback virtual dispatch cannot be proven: " + key);
                    if (specification != null || target.DeclaringType is TypeSpec || definition.HasGenericParameters || declaring.HasGenericParameters)
                        unknown.Add("callback generic execution cannot be proven: " + key);
                    if (declaring.FullName.StartsWith("System.Reflection.", StringComparison.Ordinal) ||
                        declaring.FullName == "System.Type" || declaring.FullName == "System.Activator")
                        unknown.Add("callback reflection behavior cannot be proven: " + key);
                    if (IsCore(declaring) || IsEngine(declaring))
                    {
                        // A pinned runtime reference is not proof of deterministic callback
                        // effects (native calls, delegates, clocks, reflection, global state).
                        unknown.Add("callback external runtime behavior cannot be proven: " + key);
                        return;
                    }
                    TrackInitialization(declaring);
                    pending.Enqueue(definition);
                }
            }

            private bool IsSerializedField(FieldDef field)
            {
                return !field.IsStatic && !field.IsLiteral && !field.IsInitOnly && !field.IsNotSerialized && !Has(field, "System.NonSerializedAttribute") &&
                    (field.IsPublic || Has(field, "UnityEngine.SerializeField") || Has(field, "UnityEngine.SerializeReference"));
            }
            private bool Serializable(TypeDef type) { return type.IsSerializable || Has(type, "System.SerializableAttribute"); }
            private bool Has(IHasCustomAttribute value, string name)
            {
                return value.CustomAttributes.Any(a => AttributeName(a) == name &&
                    (name.StartsWith("UnityEngine.", StringComparison.Ordinal) ? IsEngine(a.AttributeType) : IsCore(a.AttributeType)));
            }
            private static string AttributeName(CustomAttribute attribute) { return attribute.AttributeType == null ? string.Empty : attribute.AttributeType.FullName; }
            private static bool IsPrimitive(ElementType type) { return type == ElementType.Boolean || type == ElementType.Char || type == ElementType.I1 || type == ElementType.U1 || type == ElementType.I2 || type == ElementType.U2 || type == ElementType.I4 || type == ElementType.U4 || type == ElementType.I8 || type == ElementType.U8 || type == ElementType.R4 || type == ElementType.R8 || type == ElementType.String; }
            private static bool IsTerminal(ITypeDefOrRef type) { return IsCore(type) && (type.FullName == "System.Object" || type.FullName == "System.ValueType" || type.FullName == "System.Enum"); }
            private static bool IsSystem(TypeDef type) { return IsCore(type); }
            private static bool IsCore(ITypeDefOrRef type) { string assembly = Assembly(type); return assembly == "mscorlib" || assembly == "netstandard" || assembly == "system.runtime" || assembly == "system.private.corelib"; }
            private bool IsEngine(ITypeDefOrRef type)
            {
                string assembly = Assembly(type);
                // A name is not framework provenance. Only explicitly supplied reference
                // modules can be a framework boundary; candidates/snapshot DLLs never are.
                return !requestedAssemblies.Contains(assembly) && !set.Assemblies.ContainsKey(assembly) && set.Modules.ContainsKey(assembly) &&
                    (assembly == "unityengine" || assembly.StartsWith("unityengine.", StringComparison.Ordinal));
            }
            private static string Assembly(ITypeDefOrRef type) { return type == null || type.DefinitionAssembly == null ? string.Empty : AssemblyIdentityUtil.CanonicalName(type.DefinitionAssembly.Name); }
            private static string Key(ITypeDefOrRef type) { return AssemblyIdentityUtil.TypeKey(type); }
            private static string Namespace(TypeDef type) { while (type.DeclaringType != null) type = type.DeclaringType; return type.Namespace; }
            private static string NestedName(TypeDef type) { return type.DeclaringType == null ? type.Name.String : NestedName(type.DeclaringType) + "/" + type.Name; }
            private static string Shape(TypeSig sig) { return sig is SZArraySig ? "array" : sig is ArraySig ? "array-rank-" + ((ArraySig)sig).Rank : sig is GenericInstSig ? "list" : "scalar"; }
        }
    }
}
