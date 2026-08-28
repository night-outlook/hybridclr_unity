using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Scripting;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class M03RuntimeApiTests
    {
        // AssemblyShadowDiagnostics.h: uint64_t plus the arm64 size_t fields.
        // This inventory is independent of the managed declarations under test.
        private static readonly Dictionary<Type, string[]> NativeUnsignedFields = new Dictionary<Type, string[]>
        {
            { typeof(AssemblyShadowDiagnostics), new[] { "generation", "expected", "staged", "retainedBytes", "enumerationGeneration", "classEnumerationGeneration" } },
            { typeof(AssemblyShadowDiagnosticEvent), new[] { "sequence", "generation", "stagedCount" } },
            { typeof(AssemblyShadowBaselineUse), new[] { "thread", "timestamp" } },
        };

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
            string json = @"{
                ""schemaVersion"":1,""enabled"":true,""runtimeAbiVersion"":1,""state"":""Staged"",""stateCode"":3,""lastError"":0,
                ""detail"":""diagnostic-detail"",""baselineBuildId"":""b"",""patchId"":""p"",""generation"":2,""expected"":1,""staged"":1,
                ""retainedBytes"":8,""enumerationGeneration"":3,""classEnumerationGeneration"":3,
                ""closureLoadOrder"":[""A""],""stableAotNames"":[""mscorlib""],""commitOrder"":[],
                ""assemblies"":[{""name"":""A"",""mvid"":""01234567-89ab-cdef-0123-456789abcdef"",""skeletonBuilt"":true,
                    ""runtimeMetadataInitialized"":false,""published"":false,""moduleInitializerAttempted"":false,""moduleInitializerRan"":false}],
                ""events"":[{""sequence"":4,""kind"":""stage"",""name"":""A"",""generation"":2,""stagedCount"":1}],
                ""baselineUses"":[{""name"":""A"",""kind"":""AssemblyReflection"",""detail"":""d"",""type"":""T"",""thread"":7,""timestamp"":9}],
                ""ordinaryAssemblies"":[{""name"":""A"",""isInterpreter"":false}],
                ""ordinaryClasses"":[{""assemblyName"":""mscorlib"",""typeName"":""System.Nullable`1"",""isInterpreter"":false,
                    ""isConstructedGeneric"":true,""usesStagedMetadata"":false}]}";
            AssemblyShadowDiagnostics value = AssemblyShadowDiagnostics.Parse(json);
            Assert.That(value.schemaVersion, Is.EqualTo(1));
            Assert.That(value.generation, Is.EqualTo(2));
            Assert.That(value.retainedBytes, Is.EqualTo(8));
            Assert.That(value.enumerationGeneration, Is.EqualTo(3));
            Assert.That(value.classEnumerationGeneration, Is.EqualTo(3));
            Assert.That(value.detail, Is.EqualTo("diagnostic-detail"));
            Assert.That(value.baselineBuildId, Is.EqualTo("b"));
            Assert.That(value.patchId, Is.EqualTo("p"));
            Assert.That(value.closureLoadOrder, Is.EqualTo(new[] { "A" }));
            Assert.That(value.stableAotNames, Is.EqualTo(new[] { "mscorlib" }));
            Assert.That(value.commitOrder, Is.Empty);
            Assert.That(value.assemblies[0].mvid, Is.EqualTo("01234567-89ab-cdef-0123-456789abcdef"));
            Assert.That(value.events[0].sequence, Is.EqualTo(4));
            Assert.That(value.events[0].name, Is.EqualTo("A"));
            Assert.That(value.events[0].generation, Is.EqualTo(2));
            Assert.That(value.baselineUses[0].thread, Is.EqualTo(7));
            Assert.That(value.baselineUses[0].timestamp, Is.EqualTo(9));
            Assert.That(value.baselineUses[0].detail, Is.EqualTo("d"));
            Assert.That(value.baselineUses[0].type, Is.EqualTo("T"));
            Assert.That(value.ordinaryAssemblies[0].name, Is.EqualTo("A"));
            Assert.That(value.ordinaryClasses[0].assemblyName, Is.EqualTo("mscorlib"));
            Assert.That(value.ordinaryClasses[0].isConstructedGeneric, Is.True);
            AssertSchemaValuesEqual(value, AssemblyShadowDiagnostics.Parse(JsonUtility.ToJson(value)), "native fixture");
        }

        [Test]
        public void DiagnosticsSchemaPreservesEveryReachableTypeAndField()
        {
            Type[] types = DiagnosticSchemaTypes().ToArray();
            Assert.That(types, Does.Contain(typeof(AssemblyShadowBaselineUse)), "Traversal must include nested DTO arrays.");
            foreach (Type type in types)
            {
                Assert.That(type.IsSerializable, Is.True, type.FullName);
                Assert.That(type.IsDefined(typeof(PreserveAttribute), false), Is.True, type.FullName + " constructor preservation");
                FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
                Assert.That(fields, Is.Not.Empty, type.FullName);
                foreach (FieldInfo field in fields)
                    Assert.That(field.IsDefined(typeof(PreserveAttribute), false), Is.True, type.FullName + "." + field.Name);
            }
        }

        [Test]
        public void DiagnosticsRoundTripPreservesEveryReachableField()
        {
            // Non-default sentinels expose lost fields; empty arrays alone cannot.
            var expected = (AssemblyShadowDiagnostics)CreateSchemaValue(typeof(AssemblyShadowDiagnostics), "diagnostics", new HashSet<Type>());
            AssertSchemaValuesEqual(expected, AssemblyShadowDiagnostics.Parse(JsonUtility.ToJson(expected)), "diagnostics");
        }

        [Test]
        public void DiagnosticsUnsignedFieldsMatchNativeContract()
        {
            var expected = new List<string>();
            foreach (var entry in NativeUnsignedFields)
            {
                foreach (string name in entry.Value)
                {
                    FieldInfo field = entry.Key.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                    string path = entry.Key.FullName + "." + name;
                    Assert.That(field, Is.Not.Null, path);
                    Assert.That(field.FieldType, Is.EqualTo(typeof(ulong)), path + " must match native unsigned width");
                    expected.Add(path);
                }
            }
            string[] actual = DiagnosticSchemaTypes()
                .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Where(field => field.FieldType == typeof(ulong))
                    .Select(field => type.FullName + "." + field.Name)).ToArray();
            Assert.That(expected.Count, Is.EqualTo(11));
            Assert.That(actual, Is.EquivalentTo(expected), "Every unsigned field must have an explicit native contract and boundary cases.");
        }

        private static IEnumerable<TestCaseData> UnsignedNumberCases()
        {
            string[] tokens = {
                "0", "1", "4294967295", "4294967296", "4294967297",
                "9007199254740991", "9007199254740992", "9007199254740993",
                "9223372036854775807", "9223372036854775808",
                "18199233411598851025", "18446744073709551615"
            };
            foreach (var entry in NativeUnsignedFields)
                foreach (string field in entry.Value)
                    foreach (string token in tokens)
                        yield return new TestCaseData(entry.Key, field, token)
                            .SetName("UnsignedToken_" + entry.Key.Name + "_" + field + "_" + token);
        }

        [TestCaseSource(nameof(UnsignedNumberCases))]
        public void DiagnosticsUnsignedNumericTokensParseAndSerializeExactly(Type ownerType, string fieldName, string token)
        {
            ulong expected = ulong.Parse(token, NumberStyles.None, CultureInfo.InvariantCulture);
            string container = ownerType == typeof(AssemblyShadowDiagnosticEvent) ? "events" :
                ownerType == typeof(AssemblyShadowBaselineUse) ? "baselineUses" : null;
            string member = "\"" + fieldName + "\":" + token;
            // Construct the native numeric token directly, not through a managed
            // serializer that might already have rounded or coerced the value.
            string json = "{\"schemaVersion\":1," + (container == null ? member : "\"" + container + "\":[{" + member + "}]") + "}";
            AssemblyShadowDiagnostics value = AssemblyShadowDiagnostics.Parse(json);
            AssertUnsignedFieldValue(value, ownerType, fieldName, container, expected);

            string serialized = JsonUtility.ToJson(value);
            string scope = serialized;
            if (container != null)
            {
                Match array = Regex.Match(serialized, "\"" + container + "\"\\s*:\\s*\\[\\s*(\\{[^{}]*\\})\\s*\\]");
                Assert.That(array.Success, Is.True, "Serialized DTO array is missing: " + container);
                scope = array.Groups[1].Value;
            }
            MatchCollection numbers = Regex.Matches(scope, "\"" + Regex.Escape(fieldName) + "\"\\s*:\\s*([^\\s,}\\]]+)");
            Assert.That(numbers.Count, Is.EqualTo(1), "Expected exactly one numeric field: " + fieldName);
            Assert.That(numbers[0].Groups[1].Value, Is.EqualTo(token), "Numeric token must not be a float, string, narrowed integer or rounded value.");
            AssertUnsignedFieldValue(AssemblyShadowDiagnostics.Parse(serialized), ownerType, fieldName, container, expected);
        }

        private static void AssertUnsignedFieldValue(AssemblyShadowDiagnostics value, Type ownerType, string fieldName, string container, ulong expected)
        {
            object owner = value;
            if (container != null)
            {
                var array = (Array)typeof(AssemblyShadowDiagnostics).GetField(container).GetValue(value);
                Assert.That(array, Is.Not.Null, container);
                Assert.That(array.Length, Is.EqualTo(1), container);
                owner = array.GetValue(0);
            }
            FieldInfo field = ownerType.GetField(fieldName);
            Assert.That(field.FieldType, Is.EqualTo(typeof(ulong)), ownerType.Name + "." + fieldName);
            Assert.That(field.GetValue(owner), Is.EqualTo(expected), ownerType.Name + "." + fieldName);
        }

        private static IEnumerable<Type> DiagnosticSchemaTypes()
        {
            var pending = new Queue<Type>();
            var visited = new HashSet<Type>();
            pending.Enqueue(typeof(AssemblyShadowDiagnostics));
            while (pending.Count != 0)
            {
                Type type = pending.Dequeue();
                while (type.IsArray) type = type.GetElementType();
                if (IsSchemaScalar(type) || !visited.Add(type)) continue;
                Assert.That(type.Assembly, Is.EqualTo(typeof(AssemblyShadowDiagnostics).Assembly), "Unexpected schema container: " + type);
                yield return type;
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    pending.Enqueue(field.FieldType);
            }
        }

        private static bool IsSchemaScalar(Type type)
        {
            return type == typeof(string) || type == typeof(bool) || type == typeof(int) || type == typeof(long) || type == typeof(ulong);
        }

        private static object CreateSchemaValue(Type type, string path, HashSet<Type> ancestors)
        {
            if (type == typeof(string)) return path + "-sentinel";
            if (type == typeof(bool)) return true;
            if (type == typeof(int)) return 17;
            if (type == typeof(long)) return 4294967297L;
            if (type == typeof(ulong)) return ulong.MaxValue;
            if (type.IsArray)
            {
                Array array = Array.CreateInstance(type.GetElementType(), 1);
                array.SetValue(CreateSchemaValue(type.GetElementType(), path + "[0]", ancestors), 0);
                return array;
            }
            Assert.That(type.Assembly, Is.EqualTo(typeof(AssemblyShadowDiagnostics).Assembly), "Unexpected schema type: " + type);
            Assert.That(ancestors.Add(type), Is.True, "Cyclic schema cannot roundtrip: " + path);
            object value = Activator.CreateInstance(type);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                field.SetValue(value, CreateSchemaValue(field.FieldType, path + "." + field.Name, ancestors));
            ancestors.Remove(type);
            return value;
        }

        private static void AssertSchemaValuesEqual(object expected, object actual, string path)
        {
            Assert.That(actual, Is.Not.Null, path);
            Assert.That(actual.GetType(), Is.EqualTo(expected.GetType()), path);
            if (IsSchemaScalar(expected.GetType()))
                Assert.That(actual, Is.EqualTo(expected), path);
            else if (expected is Array expectedArray)
            {
                var actualArray = (Array)actual;
                Assert.That(actualArray.Length, Is.EqualTo(expectedArray.Length), path);
                for (int index = 0; index < expectedArray.Length; index++)
                    AssertSchemaValuesEqual(expectedArray.GetValue(index), actualArray.GetValue(index), path + "[" + index + "]");
            }
            else
            {
                foreach (FieldInfo field in expected.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    AssertSchemaValuesEqual(field.GetValue(expected), field.GetValue(actual), path + "." + field.Name);
            }
        }
    }
}
