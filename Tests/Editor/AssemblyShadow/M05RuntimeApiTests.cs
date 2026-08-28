using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Scripting;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class M05RuntimeApiTests
    {
        // Independent literal schema inventory, not generated from DTO defaults.
        private static readonly string[] Members =
        {
            "\"schemaVersion\":1", "\"logicalAssembly\":\"AssemblyA.Contracts\"", "\"executionModeCode\":0",
            "\"executionMode\":\"AotBaseline\"", "\"isActive\":false", "\"physicalImageKind\":\"Aot\"",
            "\"typeKey\":\"AssemblyA.Contracts::Fixture.Payload`0\"", "\"inputTypePointer\":\"\"",
            "\"activeTypePointer\":\"\"", "\"baselineTypePointer\":\"\"", "\"pointerDetailsAvailable\":false",
            "\"baselinePointerAvailable\":false", "\"containsShadowTypes\":false", "\"definitionCacheHits\":0",
            "\"definitionCacheMisses\":0", "\"compositeRebuilds\":0", "\"allocationRemaps\":0", "\"guardFailures\":0",
        };
        private static readonly string[] Counters = { "definitionCacheHits", "definitionCacheMisses", "compositeRebuilds", "allocationRemaps", "guardFailures" };
        private static readonly string[] UnsignedTokens =
        {
            "0", "1", "4294967295", "4294967296", "4294967297", "9007199254740991", "9007199254740992", "9007199254740993",
            "9223372036854775807", "9223372036854775808", "9223372036854775809", "18446744073709551615",
        };

        [Test] public void TypeResolutionApiHasExactSignatureAndDoesNotSimulateEditorOrMono()
        {
            MethodInfo method = typeof(AssemblyShadowRuntime).GetMethod("GetTypeResolutionInfo", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method); Assert.AreEqual(typeof(AssemblyShadowErrorCode), method.ReturnType);
            var parameters = method.GetParameters(); Assert.AreEqual(2, parameters.Length);
            Assert.AreEqual(typeof(Type), parameters[0].ParameterType); Assert.AreEqual("type", parameters[0].Name);
            Assert.AreEqual(typeof(string).MakeByRefType(), parameters[1].ParameterType); Assert.IsTrue(parameters[1].IsOut);
            Assert.AreEqual("json", parameters[1].Name); Assert.IsFalse(parameters.Any(parameter => parameter.IsOptional));
            foreach (Type input in new[] { typeof(int), null })
            {
                string json = "must be cleared";
                Assert.Throws<NotSupportedException>(() => AssemblyShadowRuntime.GetTypeResolutionInfo(input, out json));
                Assert.IsNull(json);
            }
        }

        [Test] public void TypeResolutionDtoPreservesExactlyTheEighteenDeclaredFields()
        {
            Type type = typeof(AssemblyShadowTypeResolutionInfo);
            Assert.IsTrue(type.IsSerializable); Assert.IsTrue(type.IsDefined(typeof(PreserveAttribute), false));
            var expected = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (string name in new[] { "schemaVersion", "executionModeCode" }) expected.Add(name, typeof(int));
            foreach (string name in new[] { "logicalAssembly", "executionMode", "physicalImageKind", "typeKey", "inputTypePointer", "activeTypePointer", "baselineTypePointer" }) expected.Add(name, typeof(string));
            foreach (string name in new[] { "isActive", "pointerDetailsAvailable", "baselinePointerAvailable", "containsShadowTypes" }) expected.Add(name, typeof(bool));
            foreach (string name in Counters) expected.Add(name, typeof(ulong));
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            Assert.AreEqual(18, fields.Length); CollectionAssert.AreEquivalent(expected.Keys, fields.Select(field => field.Name));
            foreach (FieldInfo field in fields)
            { Assert.AreEqual(expected[field.Name], field.FieldType, field.Name); Assert.IsTrue(field.IsDefined(typeof(PreserveAttribute), false), field.Name); }
        }

        [Test] public void RequiredFalseAndZeroValuesParseWithoutDefaultManufacturing()
        {
            var value = AssemblyShadowTypeResolutionInfo.Parse(Json());
            Assert.AreEqual(1, value.schemaVersion); Assert.AreEqual("AssemblyA.Contracts", value.logicalAssembly);
            Assert.AreEqual(0, value.executionModeCode); Assert.AreEqual("AotBaseline", value.executionMode);
            Assert.AreEqual("Aot", value.physicalImageKind); Assert.AreEqual("AssemblyA.Contracts::Fixture.Payload`0", value.typeKey);
            Assert.IsFalse(value.isActive); Assert.IsFalse(value.pointerDetailsAvailable); Assert.IsFalse(value.baselinePointerAvailable); Assert.IsFalse(value.containsShadowTypes);
            Assert.AreEqual("", value.inputTypePointer); Assert.AreEqual("", value.activeTypePointer); Assert.AreEqual("", value.baselineTypePointer);
            foreach (string field in Counters) Assert.AreEqual(0UL, typeof(AssemblyShadowTypeResolutionInfo).GetField(field).GetValue(value));
            AssemblyShadowTypeResolutionInfo parsed; Assert.IsTrue(AssemblyShadowTypeResolutionInfo.TryParse(Json(), out parsed)); Assert.IsNotNull(parsed);
            for (int omitted = 0; omitted < Members.Length; ++omitted)
                Reject("{" + string.Join(",", Members.Where((member, index) => index != omitted).ToArray()) + "}");
        }

        [Test] public void UnsignedCounterTokensAndBclRoundTripsRetainTheFullRange()
        {
            foreach (string field in Counters)
            foreach (string token in UnsignedTokens)
            {
                var value = AssemblyShadowTypeResolutionInfo.Parse(Json(field, token));
                Assert.AreEqual(ulong.Parse(token, NumberStyles.None, CultureInfo.InvariantCulture), typeof(AssemblyShadowTypeResolutionInfo).GetField(field).GetValue(value), field + "=" + token);
                string serialized = Serialize(value);
                StringAssert.Contains("\"" + field + "\":" + token, serialized);
                Equal(value, AssemblyShadowTypeResolutionInfo.Parse(serialized));
            }
        }

        [Test] public void EveryFieldRejectsWrongPrimitiveTypesAndEveryCounterRejectsOverflow()
        {
            foreach (FieldInfo field in typeof(AssemblyShadowTypeResolutionInfo).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                string[] wrong = field.FieldType == typeof(string) ? new[] { "null", "false", "1", "{}", "[]" } :
                    field.FieldType == typeof(bool) ? new[] { "null", "\"false\"", "0", "1", "{}", "[]", "False", "TRUE" } :
                    new[] { "null", "\"0\"", "true", "-1", "-0", "+1", "01", "1.0", "1e0", "1E+1", "{}", "[]", "NaN", "Infinity", "18446744073709551616" };
                foreach (string token in wrong) Reject(Json(field.Name, token));
                if (field.FieldType == typeof(int)) Reject(Json(field.Name, "2147483648"));
            }
        }

        [Test] public void DuplicateUnknownMissingAndMalformedObjectFormsAreRejected()
        {
            foreach (string member in Members) Reject(Json().Insert(Json().Length - 1, "," + member));
            Reject(Json().Insert(Json().Length - 1, ",\"schema\\u0056ersion\":1"));
            foreach (string unknown in new[] { "mvid", "SchemaVersion", "schema_version", "futureCounter" }) Reject(Json().Insert(Json().Length - 1, ",\"" + unknown + "\":0"));
            foreach (string malformed in new[] { null, "", " ", "null", "{}", "[]", "[" + Json() + "]", Json() + "x", Json() + Json(),
                Json().TrimEnd('}') + ",}", Json().Replace("\"schemaVersion\":", "\"schemaVersion\""), Json().Replace("false", "falsefalse"),
                "\u00a0" + Json(), "\ufeff" + Json(), Json().Replace("\"schemaVersion\"", "'schemaVersion'"), Json().Substring(0, Json().Length - 1) }) Reject(malformed);
        }

        [Test] public void FieldOrderWhitespaceAndValidJsonEscapesDoNotChangeValues()
        {
            Equal(AssemblyShadowTypeResolutionInfo.Parse(Json()), AssemblyShadowTypeResolutionInfo.Parse(" \r\n{\t" + string.Join(" ,\n", Members.Reverse().ToArray()) + "\r}\t"));
            string token = "\"Type\\\"\\\\\\/\\b\\f\\n\\r\\t\\u0041\\ud83d\\ude03\"";
            var value = AssemblyShadowTypeResolutionInfo.Parse(Json("typeKey", token));
            Assert.AreEqual("Type\"\\/\b\f\n\r\tA\ud83d\ude03", value.typeKey);
            Equal(value, AssemblyShadowTypeResolutionInfo.Parse(Serialize(value)));
            foreach (string invalid in new[] { "\"bad\\q\"", "\"bad\\u12\"", "\"bad\\uGGGG\"", "\"bad\ntext\"", "\"\\ud800\"", "\"\\udc00\"", "\"\\ud800x\"", "\"\ud800\"", "\"unterminated" }) Reject(Json("typeKey", invalid));
        }

        [Test] public void RegistryModeAndPhysicalInterpreterKindRemainSeparate()
        {
            foreach (string physical in new[] { "Aot", "Interpreter" })
            foreach (bool shadow in new[] { false, true })
            foreach (int mode in new[] { 0, 1 })
            {
                string json = Json(new Dictionary<string, string>
                { { "executionModeCode", mode.ToString(CultureInfo.InvariantCulture) }, { "executionMode", Quote(mode == 0 ? "AotBaseline" : "InterpreterShadow") },
                    { "physicalImageKind", Quote(physical) }, { "containsShadowTypes", shadow ? "true" : "false" } });
                var value = AssemblyShadowTypeResolutionInfo.Parse(json);
                Assert.AreEqual(physical, value.physicalImageKind); Assert.AreEqual(mode, value.executionModeCode); Assert.AreEqual(shadow, value.containsShadowTypes);
            }
            foreach (string token in new[] { "0", "2", "2147483647" }) Reject(Json("schemaVersion", token));
            Reject(Json("executionModeCode", "1")); Reject(Json("executionModeCode", "2")); Reject(Json("executionMode", "\"InterpreterShadow\""));
            foreach (string field in new[] { "executionMode", "physicalImageKind", "logicalAssembly", "typeKey" }) { Reject(Json(field, "\"\"")); Reject(Json(field, "\" \"")); }
            Reject(Json("physicalImageKind", "\"AOT\"")); Reject(Json("physicalImageKind", "\"Unknown\""));
        }

        [Test] public void PointerAvailabilityRequiresOnlyActualNonzeroBoundedTokens()
        {
            foreach (string field in new[] { "inputTypePointer", "activeTypePointer", "baselineTypePointer" }) Reject(Json(field, "\"0x1\""));
            Reject(Json("baselinePointerAvailable", "true")); Reject(Json("pointerDetailsAvailable", "true"));
            var details = new Dictionary<string, string>
            { { "pointerDetailsAvailable", "true" }, { "inputTypePointer", "\"0x1\"" }, { "activeTypePointer", "\"0xffffffffffffffff\"" } };
            var value = AssemblyShadowTypeResolutionInfo.Parse(Json(details)); Assert.IsFalse(value.baselinePointerAvailable); Assert.AreEqual("", value.baselineTypePointer);
            details["baselinePointerAvailable"] = "true"; Reject(Json(details));
            details["baselineTypePointer"] = "\"0xABCDEF01\"";
            Equal(AssemblyShadowTypeResolutionInfo.Parse(Json(details)), AssemblyShadowTypeResolutionInfo.Parse(Serialize(AssemblyShadowTypeResolutionInfo.Parse(Json(details)))));
            foreach (string field in new[] { "inputTypePointer", "activeTypePointer", "baselineTypePointer" })
            foreach (string token in new[] { "", "0", "0x", "0x0", "0x000", "0X1", "0xg", "0x10000000000000000", "0x00000000000000001", " 0x1", "0x1 ", "(nil)", "0x-1" })
            {
                var invalid = new Dictionary<string, string>(details); invalid[field] = Quote(token); Reject(Json(invalid));
            }
            details["baselinePointerAvailable"] = "false"; Reject(Json(details));
            details["baselineTypePointer"] = "\"\""; details["pointerDetailsAvailable"] = "false"; Reject(Json(details));
        }

        [Test] [Category("UnityEditorIntegration")]
        public void UnitySerializerRoundTripPreservesAllFieldsIncludingUnsignedExtremes()
        {
            var data = new Dictionary<string, string>
            { { "executionModeCode", "1" }, { "executionMode", "\"InterpreterShadow\"" }, { "isActive", "true" }, { "physicalImageKind", "\"Interpreter\"" },
                { "containsShadowTypes", "true" }, { "pointerDetailsAvailable", "true" }, { "baselinePointerAvailable", "true" },
                { "inputTypePointer", "\"0x111\"" }, { "activeTypePointer", "\"0x222\"" }, { "baselineTypePointer", "\"0x333\"" } };
            foreach (string token in UnsignedTokens)
            {
                foreach (string counter in Counters) data[counter] = token;
                var value = AssemblyShadowTypeResolutionInfo.Parse(Json(data));
                Equal(value, AssemblyShadowTypeResolutionInfo.Parse(JsonUtility.ToJson(value)));
            }
            var zero = AssemblyShadowTypeResolutionInfo.Parse(Json()); Equal(zero, AssemblyShadowTypeResolutionInfo.Parse(JsonUtility.ToJson(zero)));
        }

        private static string Quote(string value) { return "\"" + value + "\""; }
        private static string Json(string field, string token) { return Json(new Dictionary<string, string> { { field, token } }); }
        private static string Json(Dictionary<string, string> replacements = null)
        {
            return "{" + string.Join(",", Members.Select(member =>
            {
                string name = member.Substring(1, member.IndexOf('"', 1) - 1), replacement;
                return replacements != null && replacements.TryGetValue(name, out replacement) ? Quote(name) + ":" + replacement : member;
            }).ToArray()) + "}";
        }
        private static void Reject(string json)
        {
            var value = new AssemblyShadowTypeResolutionInfo();
            Assert.IsFalse(AssemblyShadowTypeResolutionInfo.TryParse(json, out value), json); Assert.IsNull(value, json);
            if (string.IsNullOrEmpty(json)) Assert.Throws<ArgumentException>(() => AssemblyShadowTypeResolutionInfo.Parse(json));
            else Assert.Throws<FormatException>(() => AssemblyShadowTypeResolutionInfo.Parse(json), json);
        }
        private static string Serialize(AssemblyShadowTypeResolutionInfo value)
        {
            using (var stream = new MemoryStream())
            { new DataContractJsonSerializer(typeof(AssemblyShadowTypeResolutionInfo)).WriteObject(stream, value); return Encoding.UTF8.GetString(stream.ToArray()); }
        }
        private static void Equal(AssemblyShadowTypeResolutionInfo expected, AssemblyShadowTypeResolutionInfo actual)
        {
            foreach (FieldInfo field in typeof(AssemblyShadowTypeResolutionInfo).GetFields(BindingFlags.Public | BindingFlags.Instance))
                Assert.AreEqual(field.GetValue(expected), field.GetValue(actual), field.Name);
        }
    }
}
