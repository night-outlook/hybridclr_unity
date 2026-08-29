using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Scripting;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class M06RuntimeApiTests
    {
        private static readonly string[] Counters = {
            "generation", "methodChecks", "shadowMethodChecks", "rejectedBaselineMethods",
            "baselineClassCctorStarted", "shadowClassCctorStarted", "interpreterTransformations",
            "shadowInterpreterTransformations", "droppedClassObservations"
        };

        private static readonly string[] RootFields = {
            "schemaVersion", "enabled", "stateCode", "state", "generation", "methodChecks",
            "shadowMethodChecks", "rejectedBaselineMethods", "baselineClassCctorStarted",
            "shadowClassCctorStarted", "interpreterTransformations", "shadowInterpreterTransformations",
            "droppedClassObservations", "classes"
        };

        private static readonly string[] ClassFields = {
            "logicalAssembly", "typeKey", "executionModeCode", "executionMode", "physicalImageKind",
            "isActive", "cctorStarted", "cctorFinished", "hasInitializationException",
            "staticStoragePointer", "pointerDetailsAvailable", "staticStorageAvailable"
        };

        [Test]
        public void PublicApiAddsExactlyOneOperationAndOneStableCode()
        {
            CollectionAssert.AreEquivalent(new[] {
                "ConfigureCandidates", "BeginTransaction", "StageAssembly", "ValidateTransaction",
                "CommitTransaction", "AbortTransaction", "GetState", "GetAssemblyExecutionMode",
                "GetDiagnosticsJson", "GetTypeResolutionInfo", "GetExecutionDiagnosticsJson"
            }, typeof(AssemblyShadowRuntime).GetMethods(BindingFlags.Public | BindingFlags.Static).Select(m => m.Name).ToArray());
            var method = typeof(AssemblyShadowRuntime).GetMethod("GetExecutionDiagnosticsJson");
            Assert.That(method.ReturnType, Is.EqualTo(typeof(AssemblyShadowErrorCode)));
            Assert.That(method.GetParameters().Length, Is.EqualTo(1));
            Assert.That(method.GetParameters()[0].IsOut, Is.True);
            Assert.That(method.GetParameters()[0].ParameterType, Is.EqualTo(typeof(string).MakeByRefType()));
            Assert.That((int)AssemblyShadowErrorCode.BaselineMethodExecution, Is.EqualTo(21));
            string json = "not-cleared";
            Assert.Throws<NotSupportedException>(() => AssemblyShadowRuntime.GetExecutionDiagnosticsJson(out json));
            Assert.That(json, Is.Null, "Editor must not manufacture native execution observations.");
        }

        [Test]
        public void ExactPreservedSchemaHasNoLegacyDiagnosticsFieldInjection()
        {
            CheckFields(typeof(AssemblyShadowExecutionDiagnostics), RootFields);
            CheckFields(typeof(AssemblyShadowExecutionClassInfo), ClassFields);
            foreach (string field in Counters)
                Assert.That(typeof(AssemblyShadowExecutionDiagnostics).GetField(field).FieldType, Is.EqualTo(typeof(ulong)), field);
            Assert.That(typeof(AssemblyShadowExecutionDiagnostics).GetField("classes").FieldType,
                Is.EqualTo(typeof(AssemblyShadowExecutionClassInfo[])));
            Assert.That(typeof(AssemblyShadowDiagnostics).GetField("methodChecks"), Is.Null);
            Assert.That(typeof(AssemblyShadowTypeResolutionInfo).GetField("methodChecks"), Is.Null);
        }

        [Test]
        public void NativeShapeParsesAndRoundTripsWithoutLosingRows()
        {
            var actual = AssemblyShadowExecutionDiagnostics.Parse(Json());
            Assert.That(actual.state, Is.EqualTo("Committed"));
            Assert.That(actual.methodChecks, Is.EqualTo(10UL));
            Assert.That(actual.classes.Length, Is.EqualTo(1));
            Assert.That(actual.classes[0].logicalAssembly, Is.EqualTo("AssemblyA.Implementation.Internal"));
            Assert.That(actual.classes[0].staticStoragePointer, Is.EqualTo("0x1234"));
            var roundtrip = AssemblyShadowExecutionDiagnostics.Parse(JsonUtility.ToJson(actual));
            Assert.That(roundtrip.classes[0].typeKey, Is.EqualTo(actual.classes[0].typeKey));
            Assert.That(roundtrip.shadowMethodChecks, Is.EqualTo(actual.shadowMethodChecks));
        }

        [TestCase("0")]
        [TestCase("4294967297")]
        [TestCase("9007199254740993")]
        [TestCase("9223372036854775808")]
        [TestCase("18446744073709551615")]
        public void AllUnsignedCountersRetainExactIntegerTokens(string token)
        {
            var fields = Root();
            foreach (string field in Counters) fields[field] = token;
            var parsed = AssemblyShadowExecutionDiagnostics.Parse(Object(fields));
            ulong expected = ulong.Parse(token, NumberStyles.None, CultureInfo.InvariantCulture);
            foreach (string field in Counters)
                Assert.That(typeof(AssemblyShadowExecutionDiagnostics).GetField(field).GetValue(parsed), Is.EqualTo(expected), field);
        }

        [TestCase("-1")]
        [TestCase("+1")]
        [TestCase("01")]
        [TestCase("1.0")]
        [TestCase("1e2")]
        [TestCase("\"1\"")]
        [TestCase("null")]
        [TestCase("true")]
        [TestCase("18446744073709551616")]
        public void UnsignedCounterCoercionsAndOverflowAreRejected(string token)
        {
            foreach (string field in Counters)
            {
                var fields = Root(); fields[field] = token;
                Reject(Object(fields), field + "=" + token);
            }
        }

        [Test]
        public void EveryRootFieldIsRequiredEvenWhenItsValueCouldDefault()
        {
            foreach (string field in RootFields)
            {
                var fields = Root(); fields.Remove(field);
                Reject(Object(fields), field);
            }
        }

        [Test]
        public void EveryClassFieldIsRequiredEvenWhenItsValueCouldDefault()
        {
            foreach (string field in ClassFields)
            {
                var fields = Class(); fields.Remove(field);
                Reject(Json(Object(fields)), field);
            }
        }

        [Test]
        public void DuplicateUnknownNullAndWrongContainerTokensAreRejected()
        {
            Reject(Json().Insert(1, "\"schemaVersion\":1,"), "duplicate root");
            Reject(Json().Insert(1, "\"futureField\":0,"), "unknown root");
            Reject(Json(Object(Class()).Insert(1, "\"isActive\":true,")), "duplicate class");
            Reject(Json(Object(Class()).Insert(1, "\"futureField\":0,")), "unknown class");
            foreach (string token in new[] { "null", "{}", "[null]", "[[]]", "[1]", "[{}]" })
            {
                var fields = Root(); fields["classes"] = token;
                Reject(Object(fields), token);
            }
            var empty = Root(); empty["classes"] = "[]";
            Assert.That(AssemblyShadowExecutionDiagnostics.Parse(Object(empty)).classes, Is.Empty);
        }

        [TestCase("schemaVersion", "2")]
        [TestCase("schemaVersion", "2147483648")]
        [TestCase("stateCode", "10")]
        [TestCase("state", "\"Staged\"")]
        [TestCase("enabled", "1")]
        [TestCase("enabled", "\"true\"")]
        [TestCase("shadowMethodChecks", "11")]
        [TestCase("rejectedBaselineMethods", "11")]
        [TestCase("shadowInterpreterTransformations", "11")]
        public void RootSemanticAndTokenContradictionsAreRejected(string field, string token)
        {
            var fields = Root(); fields[field] = token;
            Reject(Object(fields), field);
        }

        [TestCase("logicalAssembly", "\"\"")]
        [TestCase("logicalAssembly", "null")]
        [TestCase("typeKey", "\" \"")]
        [TestCase("executionModeCode", "2")]
        [TestCase("executionMode", "\"AotBaseline\"")]
        [TestCase("physicalImageKind", "\"Shadow\"")]
        [TestCase("isActive", "0")]
        [TestCase("staticStoragePointer", "\"0x0\"")]
        [TestCase("staticStoragePointer", "\"0X1234\"")]
        [TestCase("staticStoragePointer", "\"0x10000000000000000\"")]
        [TestCase("staticStoragePointer", "\"0xxyz\"")]
        [TestCase("staticStoragePointer", "null")]
        [TestCase("pointerDetailsAvailable", "false")]
        [TestCase("staticStorageAvailable", "false")]
        public void ClassSemanticAndTokenContradictionsAreRejected(string field, string token)
        {
            var fields = Class(); fields[field] = token;
            Reject(Json(Object(fields)), field);
        }

        [Test]
        public void ReleaseCanObserveStorageWithoutExposingItsAddress()
        {
            var fields = Class();
            fields["pointerDetailsAvailable"] = "false";
            fields["staticStoragePointer"] = "\"\"";
            var value = AssemblyShadowExecutionDiagnostics.Parse(Json(Object(fields))).classes[0];
            Assert.That(value.staticStorageAvailable, Is.True);
            Assert.That(value.staticStoragePointer, Is.Empty);
            fields["staticStorageAvailable"] = "false";
            Assert.That(AssemblyShadowExecutionDiagnostics.Parse(Json(Object(fields))).classes[0].staticStorageAvailable, Is.False);
        }

        [Test]
        public void NoCctorClassesMayHaveFinishedFlagWithoutAStartedCctor()
        {
            var fields = Class(); fields["cctorStarted"] = "false"; fields["cctorFinished"] = "true";
            Assert.That(AssemblyShadowExecutionDiagnostics.Parse(Json(Object(fields))).classes[0].cctorStarted, Is.False);
        }

        [Test]
        public void StrictStringGrammarAndTryParseFailureContractArePreserved()
        {
            foreach (string value in new[] { null, "", "{}", Json() + "x", Json().Replace("Committed", "bad\nstate"),
                Json().Replace("Committed", "\\uD800"), Json().Replace("Committed", "\\uDC00"),
                Json().Replace("Committed", "\\q"), Json().Replace("Committed", "\\uZZZZ") })
            {
                AssemblyShadowExecutionDiagnostics parsed;
                Assert.That(AssemblyShadowExecutionDiagnostics.TryParse(value, out parsed), Is.False);
                Assert.That(parsed, Is.Null);
            }
            var fields = Class(); fields["typeKey"] = "\"escaped\\tkey\\uD83D\\uDE00\"";
            Assert.That(AssemblyShadowExecutionDiagnostics.Parse(Json(Object(fields))).classes[0].typeKey, Is.EqualTo("escaped\tkey\ud83d\ude00"));
        }

        private static void CheckFields(Type type, string[] expected)
        {
            Assert.That(type.IsSerializable, Is.True);
            Assert.That(type.IsDefined(typeof(PreserveAttribute), false), Is.True);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            CollectionAssert.AreEquivalent(expected, fields.Select(field => field.Name).ToArray());
            foreach (var field in fields) Assert.That(field.IsDefined(typeof(PreserveAttribute), false), Is.True, field.Name);
        }

        private static void Reject(string json, string label)
        { Assert.Throws<FormatException>(() => AssemblyShadowExecutionDiagnostics.Parse(json), label); }

        private static string Json(string row = null)
        {
            var fields = Root();
            if (row != null) fields["classes"] = "[" + row + "]";
            return Object(fields);
        }

        private static Dictionary<string, string> Root()
        {
            var result = new Dictionary<string, string> {
                { "schemaVersion", "1" }, { "enabled", "true" }, { "stateCode", "6" }, { "state", "\"Committed\"" }
            };
            foreach (string field in Counters) result.Add(field, "10");
            result.Add("classes", "[" + Object(Class()) + "]");
            return result;
        }

        private static Dictionary<string, string> Class()
        {
            return new Dictionary<string, string> {
                { "logicalAssembly", "\"AssemblyA.Implementation.Internal\"" }, { "typeKey", "\"test-physical-type-key\"" },
                { "executionModeCode", "1" }, { "executionMode", "\"InterpreterShadow\"" }, { "physicalImageKind", "\"Interpreter\"" },
                { "isActive", "true" }, { "cctorStarted", "true" }, { "cctorFinished", "true" },
                { "hasInitializationException", "false" }, { "staticStoragePointer", "\"0x1234\"" },
                { "pointerDetailsAvailable", "true" }, { "staticStorageAvailable", "true" }
            };
        }

        private static string Object(Dictionary<string, string> fields)
        { return "{" + string.Join(",", fields.Select(pair => "\"" + pair.Key + "\":" + pair.Value)) + "}"; }
    }
}
