using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCLR.Editor.MethodBridge
{
    public sealed class GeneratedBridgeSignature
    {
        public string Key { get; private set; }
        public string Abi { get; private set; }
        public int Capacity { get; private set; }
        internal GeneratedBridgeSignature(string key, string abi, int capacity = 1)
        { Key = key; Abi = abi; Capacity = capacity; }
    }

    // The emitted sN identifiers are local to one run. Abi is the optimizer's
    // structural layout expression and is the cross-plan coverage key.
    public sealed class GenerationInventory
    {
        public IReadOnlyList<GeneratedBridgeSignature> ManagedToNative { get; private set; }
        public IReadOnlyList<GeneratedBridgeSignature> NativeToManaged { get; private set; }
        public IReadOnlyList<GeneratedBridgeSignature> AdjustThunks { get; private set; }
        public IReadOnlyList<GeneratedBridgeSignature> ReversePInvoke { get; private set; }
        public IReadOnlyList<GeneratedBridgeSignature> Calli { get; private set; }
        public IReadOnlyList<GeneratedBridgeSignature> StructMappings { get; private set; }
        public bool NativePointerDispatchHasMethodInfo { get { return false; } }

        internal GenerationInventory(IEnumerable<GeneratedBridgeSignature> managedToNative,
            IEnumerable<GeneratedBridgeSignature> nativeToManaged, IEnumerable<GeneratedBridgeSignature> adjustThunks,
            IEnumerable<GeneratedBridgeSignature> reversePInvoke, IEnumerable<GeneratedBridgeSignature> calli, IEnumerable<GeneratedBridgeSignature> structMappings)
        {
            ManagedToNative = Freeze(managedToNative); NativeToManaged = Freeze(nativeToManaged);
            AdjustThunks = Freeze(adjustThunks); ReversePInvoke = Freeze(reversePInvoke); Calli = Freeze(calli);
            StructMappings = Freeze(structMappings);
        }
        private static IReadOnlyList<GeneratedBridgeSignature> Freeze(IEnumerable<GeneratedBridgeSignature> source)
        { return Array.AsReadOnly(source.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray()); }
    }
}
