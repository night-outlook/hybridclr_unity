using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class M03RuntimeApiTests
    {
        [Test]
        public void ContractEnumsAndAbiAreStable()
        {
            Assert.That(Enum.GetValues(typeof(AssemblyShadowErrorCode)).Cast<AssemblyShadowErrorCode>().Select(value => (int)value).ToArray(),
                Is.EqualTo(Enumerable.Range(0, 21).ToArray()));
            Assert.That((int)AssemblyShadowState.Disabled, Is.EqualTo(0));
            Assert.That((int)AssemblyShadowState.FailedAfterCommit, Is.EqualTo(9));
            Assert.That((int)AssemblyExecutionMode.AotBaseline, Is.EqualTo(0));
            Assert.That((int)AssemblyExecutionMode.InterpreterShadow, Is.EqualTo(1));

            ParameterInfo abi = typeof(AssemblyShadowRuntime).GetMethod("BeginTransaction").GetParameters()[3];
            Assert.That(abi.DefaultValue, Is.EqualTo(1));
        }

        [Test]
        public void EditorApiDoesNotSimulateNativeRuntime()
        {
            Assert.Throws<NotSupportedException>(() => AssemblyShadowRuntime.ConfigureCandidates("baseline", new string[0], new string[0]));
            Assert.Throws<NotSupportedException>(() => AssemblyShadowRuntime.BeginTransaction("patch", "baseline", new string[0]));
            Assert.Throws<NotSupportedException>(() => AssemblyShadowRuntime.StageAssembly(new byte[] { 1 }, null));
            Assert.Throws<NotSupportedException>(() => AssemblyShadowRuntime.ValidateTransaction());
            Assert.Throws<NotSupportedException>(() => AssemblyShadowRuntime.CommitTransaction());
            Assert.Throws<NotSupportedException>(() => AssemblyShadowRuntime.AbortTransaction());
            AssemblyShadowState state;
            Assert.Throws<NotSupportedException>(() => AssemblyShadowRuntime.GetState(out state));
            AssemblyExecutionMode mode;
            Assert.Throws<NotSupportedException>(() => AssemblyShadowRuntime.GetAssemblyExecutionMode("name", out mode));
            string json;
            Assert.Throws<NotSupportedException>(() => AssemblyShadowRuntime.GetDiagnosticsJson(out json));
        }

        [Test]
        public void PublicSignaturesUseStableErrorCodeAndExplicitOutValues()
        {
            Assert.That(typeof(AssemblyShadowRuntime).GetMethod("ConfigureCandidates").ReturnType, Is.EqualTo(typeof(AssemblyShadowErrorCode)));
            Assert.That(typeof(AssemblyShadowRuntime).GetMethod("BeginTransaction").ReturnType, Is.EqualTo(typeof(AssemblyShadowErrorCode)));
            Assert.That(typeof(AssemblyShadowRuntime).GetMethod("StageAssembly").ReturnType, Is.EqualTo(typeof(AssemblyShadowErrorCode)));
            Assert.That(typeof(AssemblyShadowRuntime).GetMethod("ValidateTransaction").ReturnType, Is.EqualTo(typeof(AssemblyShadowErrorCode)));
            Assert.That(typeof(AssemblyShadowRuntime).GetMethod("CommitTransaction").ReturnType, Is.EqualTo(typeof(AssemblyShadowErrorCode)));
            Assert.That(typeof(AssemblyShadowRuntime).GetMethod("AbortTransaction").ReturnType, Is.EqualTo(typeof(AssemblyShadowErrorCode)));

            ParameterInfo[] state = typeof(AssemblyShadowRuntime).GetMethod("GetState").GetParameters();
            ParameterInfo[] mode = typeof(AssemblyShadowRuntime).GetMethod("GetAssemblyExecutionMode").GetParameters();
            ParameterInfo[] diagnostics = typeof(AssemblyShadowRuntime).GetMethod("GetDiagnosticsJson").GetParameters();
            Assert.That(state[0].ParameterType, Is.EqualTo(typeof(AssemblyShadowState).MakeByRefType()));
            Assert.That(mode[1].ParameterType, Is.EqualTo(typeof(AssemblyExecutionMode).MakeByRefType()));
            Assert.That(diagnostics[0].ParameterType, Is.EqualTo(typeof(string).MakeByRefType()));
        }

        [Test]
        public void DiagnosticsParserMirrorsSchemaOneFields()
        {
            string json = "{\"schemaVersion\":1,\"enabled\":true,\"runtimeAbiVersion\":1,\"state\":\"Staged\",\"stateCode\":3,\"lastError\":0,\"detail\":\"\",\"baselineBuildId\":\"b\",\"patchId\":\"p\",\"generation\":2,\"expected\":1,\"staged\":1,\"retainedBytes\":8,\"enumerationGeneration\":3,\"closureLoadOrder\":[\"A\"],\"stableAotNames\":[\"mscorlib\"],\"commitOrder\":[],\"assemblies\":[],\"events\":[{\"sequence\":4,\"kind\":\"stage\",\"name\":\"A\",\"generation\":2,\"stagedCount\":1}],\"baselineUses\":[{\"name\":\"A\",\"kind\":\"AssemblyReflection\",\"detail\":\"d\",\"type\":\"T\",\"thread\":7,\"timestamp\":9}],\"ordinaryAssemblies\":[{\"name\":\"A\",\"isInterpreter\":false}]}";
            AssemblyShadowDiagnostics value = AssemblyShadowDiagnostics.Parse(json);
            Assert.That(value.schemaVersion, Is.EqualTo(1));
            Assert.That(value.generation, Is.EqualTo(2));
            Assert.That(value.retainedBytes, Is.EqualTo(8));
            Assert.That(value.enumerationGeneration, Is.EqualTo(3));
            Assert.That(value.events[0].sequence, Is.EqualTo(4));
            Assert.That(value.baselineUses[0].thread, Is.EqualTo(7));
            Assert.That(value.ordinaryAssemblies[0].name, Is.EqualTo("A"));
        }
    }
}
