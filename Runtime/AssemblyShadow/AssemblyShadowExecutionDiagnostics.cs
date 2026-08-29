using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Scripting;

namespace HybridCLR
{
    /// <summary>The separate, strict schema 1 returned by GetExecutionDiagnosticsJson.</summary>
    /// <remarks>
    /// Guard and transformation totals are process-lifetime observations. The
    /// bounded class inventory begins with registered candidate observations;
    /// it is not a census of classes used before ConfigureCandidates. Startup
    /// policy must independently exclude early business execution. Reading this
    /// object does not initialize a class. Pointer tokens are never identities.
    /// </remarks>
    [Serializable, Preserve]
    public sealed class AssemblyShadowExecutionDiagnostics
    {
        [Preserve] public int schemaVersion;
        [Preserve] public bool enabled;
        [Preserve] public int stateCode;
        [Preserve] public string state;
        [Preserve] public ulong generation;
        [Preserve] public ulong methodChecks;
        [Preserve] public ulong shadowMethodChecks;
        [Preserve] public ulong rejectedBaselineMethods;
        [Preserve] public ulong baselineClassCctorStarted;
        [Preserve] public ulong shadowClassCctorStarted;
        [Preserve] public ulong interpreterTransformations;
        [Preserve] public ulong shadowInterpreterTransformations;
        [Preserve] public ulong droppedClassObservations;
        [Preserve] public AssemblyShadowExecutionClassInfo[] classes;

        /// <summary>
        /// Requires every native field with its exact JSON token type. Missing,
        /// duplicate and unknown fields, coercions and UInt64 overflow fail.
        /// </summary>
        public static AssemblyShadowExecutionDiagnostics Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("Execution diagnostics JSON must not be null or empty.", nameof(json));
            var value = new Reader(json).Read();
            string[] states = { "Disabled", "CandidatesRegistered", "Staging", "Staged", "Validated", "Committing", "Committed", "Aborted", "Failed", "FailedAfterCommit" };
            if (value.schemaVersion != 1) throw Invalid("Unsupported execution diagnostics schema.");
            if (value.stateCode < 0 || value.stateCode >= states.Length || value.state != states[value.stateCode])
                throw Invalid("Execution state code and name are inconsistent.");
            if (value.shadowMethodChecks > value.methodChecks || value.rejectedBaselineMethods > value.methodChecks ||
                value.shadowInterpreterTransformations > value.interpreterTransformations)
                throw Invalid("Execution subset counters exceed their totals.");
            foreach (AssemblyShadowExecutionClassInfo row in value.classes)
            {
                if (string.IsNullOrWhiteSpace(row.logicalAssembly) || string.IsNullOrWhiteSpace(row.typeKey))
                    throw Invalid("Observed class identity must not be empty.");
                if (!((row.executionModeCode == 0 && row.executionMode == "AotBaseline") ||
                    (row.executionModeCode == 1 && row.executionMode == "InterpreterShadow")))
                    throw Invalid("Class execution mode code and name are inconsistent.");
                if (row.physicalImageKind != "Aot" && row.physicalImageKind != "Interpreter")
                    throw Invalid("Unknown class physical image kind.");
                if (!row.pointerDetailsAvailable || !row.staticStorageAvailable)
                {
                    if (row.staticStoragePointer.Length != 0)
                        throw Invalid("Unavailable static-storage addresses must be empty.");
                }
                else if (!IsPointer(row.staticStoragePointer))
                    throw Invalid("Available static storage needs a nonzero pointer token.");
            }
            return value;
        }

        /// <summary>Returns false and a null result for invalid JSON or schema data.</summary>
        public static bool TryParse(string json, out AssemblyShadowExecutionDiagnostics diagnostics)
        {
            diagnostics = null;
            try { diagnostics = Parse(json); return true; }
            catch (ArgumentException) { return false; }
            catch (FormatException) { return false; }
        }

        private static FormatException Invalid(string message) { return new FormatException(message); }

        private static int Hex(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            return value >= 'A' && value <= 'F' ? value - 'A' + 10 : -1;
        }

        private static bool IsPointer(string value)
        {
            if (value.Length < 3 || value.Length > 18 || value[0] != '0' || value[1] != 'x') return false;
            bool nonzero = false;
            for (int index = 2; index < value.Length; ++index)
            {
                int digit = Hex(value[index]);
                if (digit < 0) return false;
                nonzero |= digit != 0;
            }
            return nonzero;
        }

        // The grammar has one root object and a flat array of class objects.
        // Read integer tokens directly: no floating-point conversion and no
        // JsonUtility defaults that could disguise a missing false/zero field.
        private sealed class Reader
        {
            private readonly string json;
            private int position;
            internal Reader(string json) { this.json = json; }

            internal AssemblyShadowExecutionDiagnostics Read()
            {
                var result = new AssemblyShadowExecutionDiagnostics();
                var fields = new HashSet<string>(StringComparer.Ordinal);
                Expect('{');
                if (!Take('}'))
                {
                    do
                    {
                        string field = Field(fields);
                        switch (field)
                        {
                            case "schemaVersion": result.schemaVersion = Integer(); break;
                            case "enabled": result.enabled = Boolean(); break;
                            case "stateCode": result.stateCode = Integer(); break;
                            case "state": result.state = String(); break;
                            case "generation": result.generation = Unsigned(); break;
                            case "methodChecks": result.methodChecks = Unsigned(); break;
                            case "shadowMethodChecks": result.shadowMethodChecks = Unsigned(); break;
                            case "rejectedBaselineMethods": result.rejectedBaselineMethods = Unsigned(); break;
                            case "baselineClassCctorStarted": result.baselineClassCctorStarted = Unsigned(); break;
                            case "shadowClassCctorStarted": result.shadowClassCctorStarted = Unsigned(); break;
                            case "interpreterTransformations": result.interpreterTransformations = Unsigned(); break;
                            case "shadowInterpreterTransformations": result.shadowInterpreterTransformations = Unsigned(); break;
                            case "droppedClassObservations": result.droppedClassObservations = Unsigned(); break;
                            case "classes": result.classes = Classes(); break;
                            default: throw Invalid("Unknown execution diagnostics field: " + field);
                        }
                    } while (Take(','));
                    Expect('}');
                }
                WhiteSpace();
                if (position != json.Length || fields.Count != 14)
                    throw Invalid("Incomplete or trailing execution diagnostics JSON.");
                return result;
            }

            private AssemblyShadowExecutionClassInfo[] Classes()
            {
                var rows = new List<AssemblyShadowExecutionClassInfo>();
                Expect('[');
                if (!Take(']'))
                {
                    do
                    {
                        var row = new AssemblyShadowExecutionClassInfo();
                        var fields = new HashSet<string>(StringComparer.Ordinal);
                        Expect('{');
                        if (!Take('}'))
                        {
                            do
                            {
                                string field = Field(fields);
                                switch (field)
                                {
                                    case "logicalAssembly": row.logicalAssembly = String(); break;
                                    case "typeKey": row.typeKey = String(); break;
                                    case "executionModeCode": row.executionModeCode = Integer(); break;
                                    case "executionMode": row.executionMode = String(); break;
                                    case "physicalImageKind": row.physicalImageKind = String(); break;
                                    case "isActive": row.isActive = Boolean(); break;
                                    case "cctorStarted": row.cctorStarted = Boolean(); break;
                                    case "cctorFinished": row.cctorFinished = Boolean(); break;
                                    case "hasInitializationException": row.hasInitializationException = Boolean(); break;
                                    case "staticStoragePointer": row.staticStoragePointer = String(); break;
                                    case "pointerDetailsAvailable": row.pointerDetailsAvailable = Boolean(); break;
                                    case "staticStorageAvailable": row.staticStorageAvailable = Boolean(); break;
                                    default: throw Invalid("Unknown execution class field: " + field);
                                }
                            } while (Take(','));
                            Expect('}');
                        }
                        if (fields.Count != 12) throw Invalid("Incomplete execution class object.");
                        rows.Add(row);
                    } while (Take(','));
                    Expect(']');
                }
                return rows.ToArray();
            }

            private string Field(HashSet<string> fields)
            {
                string field = String();
                if (!fields.Add(field)) throw Invalid("Duplicate execution diagnostics field: " + field);
                Expect(':');
                return field;
            }

            private int Integer()
            {
                ulong value = Unsigned();
                if (value > int.MaxValue) throw Invalid("Integer token is outside Int32 range.");
                return (int)value;
            }

            private ulong Unsigned()
            {
                WhiteSpace();
                if (position == json.Length || json[position] < '0' || json[position] > '9')
                    throw Invalid("Expected an unsigned integer token.");
                bool leadingZero = json[position] == '0';
                int start = position;
                ulong value = 0;
                while (position < json.Length && json[position] >= '0' && json[position] <= '9')
                {
                    uint digit = (uint)(json[position++] - '0');
                    if (value > (ulong.MaxValue - digit) / 10) throw Invalid("Unsigned integer token overflows UInt64.");
                    value = value * 10 + digit;
                }
                if (leadingZero && position - start != 1) throw Invalid("Leading zero in integer token.");
                return value;
            }

            private bool Boolean()
            {
                WhiteSpace();
                if (Literal("true")) return true;
                if (Literal("false")) return false;
                throw Invalid("Expected a Boolean token.");
            }

            private bool Literal(string token)
            {
                if (json.Length - position < token.Length || string.CompareOrdinal(json, position, token, 0, token.Length) != 0) return false;
                position += token.Length;
                return true;
            }

            private string String()
            {
                Expect('"');
                var value = new StringBuilder();
                while (position < json.Length)
                {
                    char next = json[position++];
                    if (next == '"')
                    {
                        string result = value.ToString();
                        for (int index = 0; index < result.Length; ++index)
                        {
                            if (char.IsHighSurrogate(result[index]))
                            { if (++index == result.Length || !char.IsLowSurrogate(result[index])) throw Invalid("Unpaired JSON surrogate."); }
                            else if (char.IsLowSurrogate(result[index])) throw Invalid("Unpaired JSON surrogate.");
                        }
                        return result;
                    }
                    if (next < ' ') throw Invalid("Unescaped control character in JSON string.");
                    if (next != '\\') { value.Append(next); continue; }
                    if (position == json.Length) throw Invalid("Incomplete JSON escape.");
                    switch (json[position++])
                    {
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case '/': value.Append('/'); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'u':
                            int code = 0;
                            for (int index = 0; index < 4; ++index)
                            {
                                if (position == json.Length) throw Invalid("Incomplete Unicode escape.");
                                int digit = Hex(json[position++]);
                                if (digit < 0) throw Invalid("Invalid Unicode escape.");
                                code = code * 16 + digit;
                            }
                            value.Append((char)code); break;
                        default: throw Invalid("Invalid JSON escape.");
                    }
                }
                throw Invalid("Unterminated JSON string.");
            }

            private void Expect(char token)
            { if (!Take(token)) throw Invalid("Expected JSON token: " + token); }

            private bool Take(char token)
            {
                WhiteSpace();
                if (position == json.Length || json[position] != token) return false;
                ++position;
                return true;
            }

            private void WhiteSpace()
            {
                while (position < json.Length && (json[position] == ' ' || json[position] == '\t' || json[position] == '\r' || json[position] == '\n')) ++position;
            }
        }
    }

    /// <summary>Already-existing native class/static state, without initialization.</summary>
    [Serializable, Preserve]
    public sealed class AssemblyShadowExecutionClassInfo
    {
        [Preserve] public string logicalAssembly;
        [Preserve] public string typeKey;
        [Preserve] public int executionModeCode;
        [Preserve] public string executionMode;
        [Preserve] public string physicalImageKind;
        [Preserve] public bool isActive;
        [Preserve] public bool cctorStarted;
        [Preserve] public bool cctorFinished;
        [Preserve] public bool hasInitializationException;
        [Preserve] public string staticStoragePointer;
        [Preserve] public bool pointerDetailsAvailable;
        [Preserve] public bool staticStorageAvailable;
    }
}
