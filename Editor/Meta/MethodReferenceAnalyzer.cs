using dnlib.DotNet;
using HybridCLR.Editor.ABI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HybridCLR.Editor.Meta
{
    public class MethodReferenceAnalyzer
    {
        private readonly Action<MethodDef, List<TypeSig>, List<TypeSig>, GenericMethod> _onNewMethod;

        private readonly ConcurrentDictionary<MethodDef, List<IMethod>> _methodEffectInsts = new ConcurrentDictionary<MethodDef, List<IMethod>>();

        public MethodReferenceAnalyzer(Action<MethodDef, List<TypeSig>, List<TypeSig>, GenericMethod> onNewMethod)
        {
            _onNewMethod = onNewMethod;
        }

        public void WalkMethod(MethodDef method, List<TypeSig> klassGenericInst, List<TypeSig> methodGenericInst)
        {
            foreach (var resolved in CollectMethods(method, klassGenericInst, methodGenericInst))
                _onNewMethod(method, klassGenericInst, methodGenericInst, resolved);
        }

        public IReadOnlyList<GenericMethod> CollectMethods(MethodDef method, List<TypeSig> klassGenericInst, List<TypeSig> methodGenericInst)
        {
            List<IMethod> effectInsts;
            if (!_methodEffectInsts.TryGetValue(method, out effectInsts))
            {
                var discovered = new List<IMethod>();
                var body = method.Body;
                if (body != null && body.HasInstructions)
                {
                    foreach (var inst in body.Instructions)
                    {
                        var met = inst.Operand as IMethod;
                        if (met != null && met.IsMethod) discovered.Add(met);
                    }
                }
                effectInsts = _methodEffectInsts.GetOrAdd(method, discovered);
            }
            var ctx = new GenericArgumentContext(klassGenericInst, methodGenericInst);
            var result = new List<GenericMethod>(effectInsts.Count);
            foreach (var met in effectInsts)
            {
                var resolved = GenericMethod.ResolveMethod(met, ctx)?.ToGenericShare();
                if (resolved != null) result.Add(resolved);
            }
            return result;
        }
    }
}
