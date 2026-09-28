using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Scripting;

namespace HybridCLR
{
    /// <summary>Strict schema 1, optionally carrying the versioned R02 diagnostics extension.</summary>
    /// <remarks>
    /// Registry execution mode and physical image kind are independent: ordinary
    /// hot-update types may be Interpreter images without being shadow types.
    /// Addresses are optional transient diagnostics, never persisted identities.
    /// </remarks>
    [Serializable, Preserve]
    public sealed class AssemblyShadowTypeResolutionInfo
    {
        [Preserve] public int schemaVersion;
        [Preserve] public string logicalAssembly;
        [Preserve] public int executionModeCode;
        [Preserve] public string executionMode;
        [Preserve] public bool isActive;
        [Preserve] public string physicalImageKind;
        [Preserve] public string typeKey;
        [Preserve] public string inputTypePointer;
        [Preserve] public string activeTypePointer;
        [Preserve] public string baselineTypePointer;
        [Preserve] public bool pointerDetailsAvailable;
        [Preserve] public bool baselinePointerAvailable;
        [Preserve] public bool containsShadowTypes;
        [Preserve] public ulong definitionCacheHits;
        [Preserve] public ulong definitionCacheMisses;
        [Preserve] public ulong compositeRebuilds;
        [Preserve] public ulong allocationRemaps;
        [Preserve] public ulong guardFailures;

        /// <summary>Null for a legacy producer; absence never means measured zero.</summary>
        [Preserve] public R02Diagnostics r02;

        /// <summary>Exact 33-field R02 diagnostics schema 1, independent of the outer schema.</summary>
        /// <remarks>Live counters are not a globally atomic snapshot. Coverage describes
        /// bounded observation, not correctness or performance acceptance.</remarks>
        [Serializable, Preserve]
        public sealed class R02Diagnostics
        {
            [Preserve] public int schemaVersion;
            [Preserve] public int diagnosticsLevel;
            [Preserve] public ulong definitionSearches;
            [Preserve] public ulong definitionRowsScanned;
            [Preserve] public ulong admissionCacheHits;
            [Preserve] public ulong admissionCacheMisses;
            [Preserve] public ulong admissionProofAttempts;
            [Preserve] public ulong admissionProofRejections;
            [Preserve] public ulong admissionEntries;
            [Preserve] public ulong admissionRetainedBytes;
            [Preserve] public ulong admissionUnready;
            [Preserve] public ulong baselineStateChecks;
            [Preserve] public ulong fieldWorkspaceBuilds;
            [Preserve] public ulong interfaceWorkspaceBuilds;
            [Preserve] public ulong layoutCheckCalls;
            [Preserve] public ulong counterpartCacheHits;
            [Preserve] public ulong counterpartCacheMisses;
            [Preserve] public ulong counterpartEntries;
            [Preserve] public ulong absentCounterpartEntries;
            [Preserve] public ulong cacheFixedBytes;
            [Preserve] public ulong counterpartRetainedBytes;
            [Preserve] public ulong genericContextChecks;
            [Preserve] public ulong observationLockContentions;
            [Preserve] public ulong observationMemoHits;
            [Preserve] public ulong observationMemoTlsBytesPerThread;
            [Preserve] public ulong counterStorageBytes;
            [Preserve] public ulong counterThreadCapacity;
            [Preserve] public ulong droppedCounterThreads;
            [Preserve] public bool counterSaturated;
            [Preserve] public string counterCoverage;
            [Preserve] public string classesCoverage;
            [Preserve] public bool memoryAccountingAvailable;
            [Preserve] public string memoryAccountingScope;
        }

        /// <summary>
        /// Parses the 18 required native fields and only the optional known r02 object. Missing, duplicate, unknown or
        /// mistyped fields are errors, including omitted zero/false values.
        /// Unsigned counters are read directly from integer tokens without a
        /// floating-point or signed-integer intermediate.
        /// </summary>
        public static AssemblyShadowTypeResolutionInfo Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("Type-resolution JSON must not be null or empty.", nameof(json));
            var value = new Reader(json).Read();
            if (value.schemaVersion != 1) throw Invalid("Unsupported type-resolution schema.");
            if (string.IsNullOrWhiteSpace(value.logicalAssembly) || string.IsNullOrWhiteSpace(value.typeKey))
                throw Invalid("Logical assembly and type key must not be empty.");
            if (!((value.executionModeCode == 0 && value.executionMode == "AotBaseline") ||
                  (value.executionModeCode == 1 && value.executionMode == "InterpreterShadow")))
                throw Invalid("Execution mode code and name are inconsistent.");
            if (value.physicalImageKind != "Aot" && value.physicalImageKind != "Interpreter")
                throw Invalid("Unknown physical image kind.");
            if (!value.pointerDetailsAvailable)
            {
                if (value.inputTypePointer.Length != 0 || value.activeTypePointer.Length != 0 ||
                    value.baselineTypePointer.Length != 0 || value.baselinePointerAvailable)
                    throw Invalid("Unavailable pointer details must be empty.");
            }
            else if (!IsPointer(value.inputTypePointer) || !IsPointer(value.activeTypePointer) ||
                (value.baselinePointerAvailable ? !IsPointer(value.baselineTypePointer) : value.baselineTypePointer.Length != 0))
                throw Invalid("Pointer tokens and availability flags are inconsistent.");
            if (value.r02 != null) ValidateR02(value.r02);
            return value;
        }

        private static void ValidateR02(R02Diagnostics value)
        {
            if (value.schemaVersion != 1 || value.diagnosticsLevel < 0 || value.diagnosticsLevel > 2 ||
                value.counterThreadCapacity != 128 ||
                value.memoryAccountingScope != "R02StructuresExcludingAllocatorOverhead")
                throw Invalid("Unsupported R02 diagnostics contract.");
            if (!Coverage(value.counterCoverage) || !Coverage(value.classesCoverage))
                throw Invalid("Unknown R02 observation coverage.");
            // Validate immutable profile rules, not cross-counter equalities:
            // each native metric/flag is sampled independently during execution.
            if (value.diagnosticsLevel == 0)
            {
                if (value.counterCoverage != "Disabled" || value.classesCoverage != "Disabled" ||
                    value.memoryAccountingAvailable)
                    throw Invalid("Disabled R02 diagnostics must report unavailable coverage.");
            }
            else if (value.counterCoverage == "Disabled" ||
                (value.diagnosticsLevel == 1 ? value.classesCoverage != "Disabled" : value.classesCoverage == "Disabled"))
                throw Invalid("R02 coverage disagrees with the compiled diagnostics profile.");
        }

        private static bool Coverage(string text)
        {
            return text == "Disabled" || text == "Truncated" || text == "Saturated" || text == "BoundedComplete";
        }

        /// <summary>Returns false and a null result for invalid JSON or schema data.</summary>
        public static bool TryParse(string json, out AssemblyShadowTypeResolutionInfo info)
        {
            info = null;
            try { info = Parse(json); return true; }
            catch (ArgumentException) { return false; }
            catch (FormatException) { return false; }
        }

        private static FormatException Invalid(string message) { return new FormatException(message); }

        private static bool IsPointer(string text)
        {
            if (text.Length < 3 || text.Length > 18 || text[0] != '0' || text[1] != 'x') return false;
            bool nonzero = false;
            for (int index = 2; index < text.Length; ++index)
            {
                int digit = Hex(text[index]);
                if (digit < 0) return false;
                nonzero |= digit != 0;
            }
            return nonzero;
        }

        private static int Hex(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            return value >= 'A' && value <= 'F' ? value - 'A' + 10 : -1;
        }

        // Only the versioned r02 object may be nested. The token reader rejects
        // arbitrary nesting, numeric coercions and unknown fields instead of letting a
        // general serializer silently manufacture defaults. It does not alter
        // the separate M03 transaction-diagnostics parser.
        private sealed class Reader
        {
            private readonly string json;
            private int position;
            internal Reader(string json) { this.json = json; }

            internal AssemblyShadowTypeResolutionInfo Read()
            {
                var result = new AssemblyShadowTypeResolutionInfo();
                var fields = new HashSet<string>(StringComparer.Ordinal);
                Expect('{');
                if (!Take('}'))
                {
                    do
                    {
                        string field = String();
                        if (!fields.Add(field)) throw Invalid("Duplicate type-resolution field: " + field);
                        Expect(':');
                        switch (field)
                        {
                            case "schemaVersion": result.schemaVersion = Integer(); break;
                            case "logicalAssembly": result.logicalAssembly = String(); break;
                            case "executionModeCode": result.executionModeCode = Integer(); break;
                            case "executionMode": result.executionMode = String(); break;
                            case "isActive": result.isActive = Boolean(); break;
                            case "physicalImageKind": result.physicalImageKind = String(); break;
                            case "typeKey": result.typeKey = String(); break;
                            case "inputTypePointer": result.inputTypePointer = String(); break;
                            case "activeTypePointer": result.activeTypePointer = String(); break;
                            case "baselineTypePointer": result.baselineTypePointer = String(); break;
                            case "pointerDetailsAvailable": result.pointerDetailsAvailable = Boolean(); break;
                            case "baselinePointerAvailable": result.baselinePointerAvailable = Boolean(); break;
                            case "containsShadowTypes": result.containsShadowTypes = Boolean(); break;
                            case "definitionCacheHits": result.definitionCacheHits = Unsigned(); break;
                            case "definitionCacheMisses": result.definitionCacheMisses = Unsigned(); break;
                            case "compositeRebuilds": result.compositeRebuilds = Unsigned(); break;
                            case "allocationRemaps": result.allocationRemaps = Unsigned(); break;
                            case "guardFailures": result.guardFailures = Unsigned(); break;
                            case "r02": result.r02 = ReadR02(); break;
                            default: throw Invalid("Unknown type-resolution field: " + field);
                        }
                    } while (Take(','));
                    Expect('}');
                }
                WhiteSpace();
                if (position != json.Length || fields.Count != (result.r02 == null ? 18 : 19)) throw Invalid("Incomplete or trailing type-resolution JSON.");
                return result;
            }

            private R02Diagnostics ReadR02()
            {
                var result = new R02Diagnostics();
                var fields = new HashSet<string>(StringComparer.Ordinal);
                Expect('{');
                if (!Take('}'))
                {
                    do
                    {
                        string field = String();
                        if (!fields.Add(field)) throw Invalid("Duplicate R02 diagnostics field: " + field);
                        Expect(':');
                        switch (field)
                        {
                            case "schemaVersion": result.schemaVersion = Integer(); break;
                            case "diagnosticsLevel": result.diagnosticsLevel = Integer(); break;
                            case "definitionSearches": result.definitionSearches = Unsigned(); break;
                            case "definitionRowsScanned": result.definitionRowsScanned = Unsigned(); break;
                            case "admissionCacheHits": result.admissionCacheHits = Unsigned(); break;
                            case "admissionCacheMisses": result.admissionCacheMisses = Unsigned(); break;
                            case "admissionProofAttempts": result.admissionProofAttempts = Unsigned(); break;
                            case "admissionProofRejections": result.admissionProofRejections = Unsigned(); break;
                            case "admissionEntries": result.admissionEntries = Unsigned(); break;
                            case "admissionRetainedBytes": result.admissionRetainedBytes = Unsigned(); break;
                            case "admissionUnready": result.admissionUnready = Unsigned(); break;
                            case "baselineStateChecks": result.baselineStateChecks = Unsigned(); break;
                            case "fieldWorkspaceBuilds": result.fieldWorkspaceBuilds = Unsigned(); break;
                            case "interfaceWorkspaceBuilds": result.interfaceWorkspaceBuilds = Unsigned(); break;
                            case "layoutCheckCalls": result.layoutCheckCalls = Unsigned(); break;
                            case "counterpartCacheHits": result.counterpartCacheHits = Unsigned(); break;
                            case "counterpartCacheMisses": result.counterpartCacheMisses = Unsigned(); break;
                            case "counterpartEntries": result.counterpartEntries = Unsigned(); break;
                            case "absentCounterpartEntries": result.absentCounterpartEntries = Unsigned(); break;
                            case "cacheFixedBytes": result.cacheFixedBytes = Unsigned(); break;
                            case "counterpartRetainedBytes": result.counterpartRetainedBytes = Unsigned(); break;
                            case "genericContextChecks": result.genericContextChecks = Unsigned(); break;
                            case "observationLockContentions": result.observationLockContentions = Unsigned(); break;
                            case "observationMemoHits": result.observationMemoHits = Unsigned(); break;
                            case "observationMemoTlsBytesPerThread": result.observationMemoTlsBytesPerThread = Unsigned(); break;
                            case "counterStorageBytes": result.counterStorageBytes = Unsigned(); break;
                            case "counterThreadCapacity": result.counterThreadCapacity = Unsigned(); break;
                            case "droppedCounterThreads": result.droppedCounterThreads = Unsigned(); break;
                            case "counterSaturated": result.counterSaturated = Boolean(); break;
                            case "counterCoverage": result.counterCoverage = String(); break;
                            case "classesCoverage": result.classesCoverage = String(); break;
                            case "memoryAccountingAvailable": result.memoryAccountingAvailable = Boolean(); break;
                            case "memoryAccountingScope": result.memoryAccountingScope = String(); break;
                            default: throw Invalid("Unknown R02 diagnostics field: " + field);
                        }
                    } while (Take(','));
                    Expect('}');
                }
                if (fields.Count != 33) throw Invalid("Incomplete R02 diagnostics object.");
                return result;
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
                if (position == json.Length || json[position] < '0' || json[position] > '9') throw Invalid("Expected an unsigned integer token.");
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
                var text = new StringBuilder();
                while (position < json.Length)
                {
                    char value = json[position++];
                    if (value == '"')
                    {
                        string result = text.ToString();
                        for (int index = 0; index < result.Length; ++index)
                        {
                            if (char.IsHighSurrogate(result[index]))
                            { if (++index == result.Length || !char.IsLowSurrogate(result[index])) throw Invalid("Unpaired JSON surrogate."); }
                            else if (char.IsLowSurrogate(result[index])) throw Invalid("Unpaired JSON surrogate.");
                        }
                        return result;
                    }
                    if (value < ' ') throw Invalid("Unescaped control character in JSON string.");
                    if (value != '\\') { text.Append(value); continue; }
                    if (position == json.Length) throw Invalid("Incomplete JSON escape.");
                    switch (json[position++])
                    {
                        case '"': text.Append('"'); break;
                        case '\\': text.Append('\\'); break;
                        case '/': text.Append('/'); break;
                        case 'b': text.Append('\b'); break;
                        case 'f': text.Append('\f'); break;
                        case 'n': text.Append('\n'); break;
                        case 'r': text.Append('\r'); break;
                        case 't': text.Append('\t'); break;
                        case 'u':
                            int code = 0;
                            for (int index = 0; index < 4; ++index)
                            {
                                if (position == json.Length) throw Invalid("Incomplete Unicode escape.");
                                int digit = Hex(json[position++]);
                                if (digit < 0) throw Invalid("Invalid Unicode escape.");
                                code = code * 16 + digit;
                            }
                            text.Append((char)code); break;
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
}
