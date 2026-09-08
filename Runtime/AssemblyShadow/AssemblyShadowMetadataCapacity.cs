using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Scripting;

namespace HybridCLR
{
    /// <summary>Schema 1 metadata budget report returned by the runtime.</summary>
    [Serializable, Preserve]
    public sealed class AssemblyShadowMetadataCapacity
    {
        [Preserve] public int schemaVersion;
        [Preserve] public bool enabled;
        [Preserve] public int profileVersion;
        [Preserve] public int indexBits;
        [Preserve] public int kindBits;
        [Preserve] public uint[] cursors;
        [Preserve] public uint[] remainingSlots;
        [Preserve] public uint requiredImages;
        [Preserve] public uint acceptedImages;
        [Preserve] public int firstFailingIndex;
        [Preserve] public ulong firstFailingSize;
        [Preserve] public string failureReason;
        [Preserve] public bool fits;
        [Preserve] public AssemblyShadowMetadataAllocation[] allocations;
        [Preserve] public uint[] finalCursors;
        [Preserve] public ulong ordinaryAllocatedCount;
        [Preserve] public ulong shadowAllocatedCount;
        [Preserve] public ulong reservedImageCount;

        /// <summary>Parses the complete schema 1 report without defaulting missing fields.</summary>
        public static AssemblyShadowMetadataCapacity Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("Metadata capacity JSON must not be null or empty.", nameof(json));

            var reader = new AssemblyShadowStrictJsonReader(json);
            var result = new AssemblyShadowMetadataCapacity();
            var fields = new HashSet<string>(StringComparer.Ordinal);
            reader.Expect('{');
            if (!reader.Take('}'))
            {
                do
                {
                    string field = reader.Field(fields);
                    switch (field)
                    {
                        case "schemaVersion": result.schemaVersion = reader.Integer(); break;
                        case "enabled": result.enabled = reader.Boolean(); break;
                        case "profileVersion": result.profileVersion = reader.Integer(); break;
                        case "indexBits": result.indexBits = reader.Integer(); break;
                        case "kindBits": result.kindBits = reader.Integer(); break;
                        case "cursors": result.cursors = UIntArray(reader, "cursors"); break;
                        case "remainingSlots": result.remainingSlots = UIntArray(reader, "remainingSlots"); break;
                        case "requiredImages": result.requiredImages = ToUInt(reader.Unsigned(), "requiredImages"); break;
                        case "acceptedImages": result.acceptedImages = ToUInt(reader.Unsigned(), "acceptedImages"); break;
                        case "firstFailingIndex": result.firstFailingIndex = reader.Integer(); break;
                        case "firstFailingSize": result.firstFailingSize = reader.Unsigned(); break;
                        case "failureReason": result.failureReason = reader.String(); break;
                        case "fits": result.fits = reader.Boolean(); break;
                        case "allocations": result.allocations = Allocations(reader); break;
                        case "finalCursors": result.finalCursors = UIntArray(reader, "finalCursors"); break;
                        case "ordinaryAllocatedCount": result.ordinaryAllocatedCount = reader.Unsigned(); break;
                        case "shadowAllocatedCount": result.shadowAllocatedCount = reader.Unsigned(); break;
                        case "reservedImageCount": result.reservedImageCount = reader.Unsigned(); break;
                        default: throw Invalid("Unknown metadata capacity field: " + field);
                    }
                } while (reader.Take(','));
                reader.Expect('}');
            }
            reader.EndDocument();
            if (fields.Count != 18)
                throw Invalid("Incomplete metadata capacity JSON.");
            Validate(result);
            return result;
        }

        /// <summary>Returns false for malformed, incomplete, or incompatible reports.</summary>
        public static bool TryParse(string json, out AssemblyShadowMetadataCapacity capacity)
        {
            capacity = null;
            try
            {
                capacity = Parse(json);
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (FormatException) { return false; }
            catch (Exception) { return false; }
        }

        private static void Validate(AssemblyShadowMetadataCapacity value)
        {
            if (value.cursors == null || value.cursors.Length != 4 || value.remainingSlots == null || value.remainingSlots.Length != 4 ||
                value.finalCursors == null || value.finalCursors.Length != 4 || value.allocations == null)
                throw Invalid("Metadata capacity arrays are incomplete.");
            if (value.schemaVersion != 1)
                throw Invalid("Unsupported metadata capacity schema.");
            if (!value.enabled)
            {
                if (value.profileVersion != 0 || value.indexBits != 0 || value.kindBits != 0 || value.requiredImages != 0 ||
                    value.acceptedImages != 0 || value.firstFailingIndex != -1 || value.firstFailingSize != 0 || !value.fits ||
                    value.allocations.Length != 0 || !AllZero(value.cursors) || !AllZero(value.remainingSlots) || !AllZero(value.finalCursors))
                    throw Invalid("Disabled metadata capacity report contains fabricated budget values.");
                return;
            }

            if (value.profileVersion != 1 || value.indexBits != 22 || value.kindBits != 2 || value.acceptedImages > value.requiredImages)
                throw Invalid("Unsupported metadata capacity profile.");
            ValidateCursor(value.cursors, "cursors");
            ValidateCursor(value.finalCursors, "finalCursors");
            ValidateRemainingSlots(value.cursors, value.remainingSlots);
            for (int kind = 0; kind != 4; ++kind)
                if (value.finalCursors[kind] < value.cursors[kind])
                    throw Invalid("Metadata cursors must be monotonic.");
            if (value.allocations.Length != value.acceptedImages)
                throw Invalid("Metadata allocation count does not match acceptedImages.");
            if (value.fits)
            {
                if (value.firstFailingIndex != -1 || value.acceptedImages != value.requiredImages || value.firstFailingSize != 0 || value.failureReason != "None")
                    throw Invalid("Successful metadata capacity report has a failure marker.");
            }
            else
            {
                if (value.requiredImages == 0 || value.firstFailingIndex < 0 || (uint)value.firstFailingIndex >= value.requiredImages ||
                    value.acceptedImages != (uint)value.firstFailingIndex || value.firstFailingSize == 0 ||
                    (value.failureReason != "Exhausted" && value.failureReason != "InvalidSizeOrProfileState"))
                    throw Invalid("Failed metadata capacity report has an invalid failure marker.");
            }
            foreach (AssemblyShadowMetadataAllocation allocation in value.allocations)
            {
                if (allocation == null || allocation.kind < 0 || allocation.kind > 3 || allocation.dllSize == 0)
                    throw Invalid("Metadata allocation contains an invalid value.");
                ValidateImageIndex(allocation.imageIndex, allocation.kind);
            }
        }

        private static void ValidateCursor(uint[] values, string name)
        {
            uint[] strides = { 64, 16, 4, 1 };
            uint[] terminals = { 256, 256, 256, 255 };
            for (int kind = 0; kind != 4; ++kind)
            {
                uint cursor = values[kind];
                if ((kind == 0 && cursor < 64) || cursor > terminals[kind] || cursor % strides[kind] != 0)
                    throw Invalid(name + " contains an invalid cursor.");
            }
        }

        private static void ValidateRemainingSlots(uint[] cursors, uint[] remainingSlots)
        {
            uint[] strides = { 64, 16, 4, 1 };
            uint[] terminals = { 256, 256, 256, 255 };
            for (int kind = 0; kind != 4; ++kind)
            {
                uint expected = (terminals[kind] - cursors[kind]) / strides[kind];
                if (remainingSlots[kind] != expected)
                    throw Invalid("remainingSlots does not match cursors.");
            }
        }

        private static void ValidateImageIndex(uint imageIndex, int kind)
        {
            uint[] strides = { 64, 16, 4, 1 };
            uint[] terminals = { 256, 256, 256, 255 };
            uint low = imageIndex & 0xff;
            if (imageIndex == 0 || imageIndex > 0x3ff || ((imageIndex >> 8) & 3) != (uint)kind ||
                low >= terminals[kind] || (kind == 0 && low < 64) || low % strides[kind] != 0)
                throw Invalid("Metadata allocation contains an invalid image index.");
        }

        private static bool AllZero(uint[] values)
        {
            foreach (uint value in values)
                if (value != 0) return false;
            return true;
        }

        private static uint[] UIntArray(AssemblyShadowStrictJsonReader reader, string name)
        {
            var values = new List<uint>();
            reader.Expect('[');
            if (!reader.Take(']'))
            {
                do { values.Add(ToUInt(reader.Unsigned(), name)); } while (reader.Take(','));
                reader.Expect(']');
            }
            if (values.Count != 4)
                throw Invalid(name + " must contain four cursor values.");
            return values.ToArray();
        }

        private static AssemblyShadowMetadataAllocation[] Allocations(AssemblyShadowStrictJsonReader reader)
        {
            var values = new List<AssemblyShadowMetadataAllocation>();
            reader.Expect('[');
            if (!reader.Take(']'))
            {
                do
                {
                    var allocation = new AssemblyShadowMetadataAllocation();
                    var fields = new HashSet<string>(StringComparer.Ordinal);
                    reader.Expect('{');
                    if (!reader.Take('}'))
                    {
                        do
                        {
                            string field = reader.Field(fields);
                            switch (field)
                            {
                                case "imageIndex": allocation.imageIndex = ToUInt(reader.Unsigned(), "imageIndex"); break;
                                case "kind": allocation.kind = reader.Integer(); break;
                                case "dllSize": allocation.dllSize = reader.Unsigned(); break;
                                default: throw Invalid("Unknown metadata allocation field: " + field);
                            }
                        } while (reader.Take(','));
                        reader.Expect('}');
                    }
                    if (fields.Count != 3)
                        throw Invalid("Incomplete metadata allocation.");
                    values.Add(allocation);
                } while (reader.Take(','));
                reader.Expect(']');
            }
            return values.ToArray();
        }

        private static uint ToUInt(ulong value, string name)
        {
            if (value > uint.MaxValue)
                throw Invalid(name + " is outside UInt32 range.");
            return (uint)value;
        }

        private static FormatException Invalid(string message) { return new FormatException(message); }
    }

    [Serializable, Preserve]
    public sealed class AssemblyShadowMetadataAllocation
    {
        [Preserve] public uint imageIndex;
        [Preserve] public int kind;
        [Preserve] public ulong dllSize;
    }

    // The budget and recovery DTOs deliberately use a small strict reader rather
    // than JsonUtility: a missing zero/false field must never become a fake report.
    internal sealed class AssemblyShadowStrictJsonReader
    {
        private readonly string json;
        private int position;

        internal AssemblyShadowStrictJsonReader(string json) { this.json = json; }

        internal void Expect(char token)
        {
            if (!Take(token)) throw Invalid("Expected '" + token + "'.");
        }

        internal bool Take(char token)
        {
            WhiteSpace();
            if (position < json.Length && json[position] == token)
            {
                position++;
                return true;
            }
            return false;
        }

        internal string Field(HashSet<string> fields)
        {
            string field = String();
            if (!fields.Add(field)) throw Invalid("Duplicate JSON field: " + field);
            Expect(':');
            return field;
        }

        internal bool Boolean()
        {
            WhiteSpace();
            if (Match("true")) return true;
            if (Match("false")) return false;
            throw Invalid("Expected a Boolean token.");
        }

        internal int Integer()
        {
            WhiteSpace();
            bool negative = position < json.Length && json[position] == '-';
            if (negative)
            {
                position++;
                if (position == json.Length || json[position] < '0' || json[position] > '9')
                    throw Invalid("Expected a digit immediately after the minus sign.");
            }
            ulong value = Unsigned();
            if ((!negative && value > int.MaxValue) || (negative && value > 2147483648UL))
                throw Invalid("Integer token is outside Int32 range.");
            if (!negative) return (int)value;
            return value == 2147483648UL ? int.MinValue : -(int)value;
        }

        internal ulong Unsigned()
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
                if (value > (ulong.MaxValue - digit) / 10)
                    throw Invalid("Unsigned integer token overflows UInt64.");
                value = value * 10 + digit;
            }
            if (leadingZero && position - start != 1)
                throw Invalid("Leading zero in integer token.");
            return value;
        }

        internal string String()
        {
            WhiteSpace();
            if (position == json.Length || json[position++] != '"')
                throw Invalid("Expected a JSON string.");
            var value = new StringBuilder();
            while (position < json.Length)
            {
                char token = json[position++];
                if (token == '"') return value.ToString();
                if (token < 0x20) throw Invalid("Control character in JSON string.");
                if (token != '\\') { value.Append(token); continue; }
                if (position == json.Length) throw Invalid("Unterminated JSON escape.");
                char escaped = json[position++];
                switch (escaped)
                {
                    case '"': value.Append('"'); break;
                    case '\\': value.Append('\\'); break;
                    case '/': value.Append('/'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'u': value.Append(ReadUnicode()); break;
                    default: throw Invalid("Unknown JSON escape.");
                }
            }
            throw Invalid("Unterminated JSON string.");
        }

        internal void EndDocument()
        {
            WhiteSpace();
            if (position != json.Length)
                throw Invalid("Trailing JSON content.");
        }

        private bool Match(string token)
        {
            if (position + token.Length > json.Length || string.CompareOrdinal(json, position, token, 0, token.Length) != 0)
                return false;
            position += token.Length;
            return true;
        }

        private char ReadUnicode()
        {
            if (position + 4 > json.Length) throw Invalid("Incomplete Unicode escape.");
            int value = 0;
            for (int i = 0; i < 4; ++i)
            {
                int digit = Hex(json[position++]);
                if (digit < 0) throw Invalid("Invalid Unicode escape.");
                value = (value << 4) | digit;
            }
            return (char)value;
        }

        private static int Hex(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            if (value >= 'A' && value <= 'F') return value - 'A' + 10;
            return -1;
        }

        private void WhiteSpace()
        {
            while (position < json.Length && (json[position] == ' ' || json[position] == '\t' || json[position] == '\r' || json[position] == '\n'))
                position++;
        }

        private static FormatException Invalid(string message) { return new FormatException(message); }
    }
}
