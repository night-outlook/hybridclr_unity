using System;
using System.Collections.Generic;
using UnityEngine.Scripting;

namespace HybridCLR
{
    /// <summary>Schema 1 state based recovery disposition returned by the runtime.</summary>
    [Serializable, Preserve]
    public sealed class AssemblyShadowRecoveryInfo
    {
        [Preserve] public int schemaVersion;
        [Preserve] public bool enabled;
        [Preserve] public int capabilityVersion;
        [Preserve] public int stateCode;
        [Preserve] public string state;
        [Preserve] public bool published;
        [Preserve] public bool abortAllowed;
        [Preserve] public int dispositionCode;
        [Preserve] public string disposition;
        [Preserve] public int terminalFailureCode;
        [Preserve] public string reason;
        [Preserve] public ulong retainedBytes;
        [Preserve] public bool baselineEligibilityRequiresStartupValidation;

        /// <summary>Parses the complete schema 1 recovery report without defaulting missing fields.</summary>
        public static AssemblyShadowRecoveryInfo Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("Recovery info JSON must not be null or empty.", nameof(json));

            var reader = new AssemblyShadowStrictJsonReader(json);
            var result = new AssemblyShadowRecoveryInfo();
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
                        case "capabilityVersion": result.capabilityVersion = reader.Integer(); break;
                        case "stateCode": result.stateCode = reader.Integer(); break;
                        case "state": result.state = reader.String(); break;
                        case "published": result.published = reader.Boolean(); break;
                        case "abortAllowed": result.abortAllowed = reader.Boolean(); break;
                        case "dispositionCode": result.dispositionCode = reader.Integer(); break;
                        case "disposition": result.disposition = reader.String(); break;
                        case "terminalFailureCode": result.terminalFailureCode = reader.Integer(); break;
                        case "reason": result.reason = reader.String(); break;
                        case "retainedBytes": result.retainedBytes = reader.Unsigned(); break;
                        case "baselineEligibilityRequiresStartupValidation": result.baselineEligibilityRequiresStartupValidation = reader.Boolean(); break;
                        default: throw Invalid("Unknown recovery info field: " + field);
                    }
                } while (reader.Take(','));
                reader.Expect('}');
            }
            reader.EndDocument();
            if (fields.Count != 13)
                throw Invalid("Incomplete recovery info JSON.");
            Validate(result);
            return result;
        }

        /// <summary>Returns false for malformed, incomplete, or incompatible reports.</summary>
        public static bool TryParse(string json, out AssemblyShadowRecoveryInfo info)
        {
            info = null;
            try
            {
                info = Parse(json);
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (FormatException) { return false; }
            catch (Exception) { return false; }
        }

        private static void Validate(AssemblyShadowRecoveryInfo value)
        {
            if (value.schemaVersion != 1)
                throw Invalid("Unsupported recovery info schema.");
            string[] states = { "Disabled", "CandidatesRegistered", "Staging", "Staged", "Validated", "Committing", "Committed", "Aborted", "Failed", "FailedAfterCommit" };
            if (value.stateCode < 0 || value.stateCode >= states.Length || value.state != states[value.stateCode])
                throw Invalid("Recovery state code and name are inconsistent.");
            switch (value.dispositionCode)
            {
                case 0: RequireDisposition(value, "RestartRequired"); break;
                case 1: RequireDisposition(value, "CorrectInputOrAbort"); break;
                case 2: RequireDisposition(value, "AbortRequired"); break;
                case 3: RequireDisposition(value, "BaselineEligibleAfterAbort"); break;
                case 4: RequireDisposition(value, "ActiveShadow"); break;
                case 5: RequireDisposition(value, "BaselineUnselected"); break;
                default: throw Invalid("Unknown recovery disposition.");
            }

            bool requiresStartupValidation = value.dispositionCode != 4;
            if (value.baselineEligibilityRequiresStartupValidation != requiresStartupValidation)
                throw Invalid("Recovery startup-validation flag is inconsistent with disposition.");
            ValidateDispositionSemantics(value);

            if (!value.enabled)
            {
                if (value.capabilityVersion != 0 || value.stateCode != 0 || value.published || value.abortAllowed ||
                    value.dispositionCode != 0 || (value.terminalFailureCode != 0 && value.terminalFailureCode != 1) || value.retainedBytes != 0)
                    throw Invalid("Disabled recovery report contains fabricated capability values.");
                return;
            }
            if (value.capabilityVersion != 1)
                throw Invalid("Unsupported recovery capability version.");
        }

        private static void ValidateDispositionSemantics(AssemblyShadowRecoveryInfo value)
        {
            switch (value.dispositionCode)
            {
                case 0:
                    if (value.abortAllowed)
                        throw Invalid("RestartRequired cannot advertise Abort.");
                    return;
                case 1:
                case 2:
                    if (value.stateCode < 2 || value.stateCode > 4 || value.published || !value.abortAllowed || value.terminalFailureCode != 0)
                        throw Invalid("Abort recovery is only legal before publication in staging states.");
                    return;
                case 3:
                    if (value.stateCode != 7 || value.published || value.abortAllowed || value.terminalFailureCode != 0)
                        throw Invalid("BaselineEligibleAfterAbort requires an unpublished Aborted state.");
                    return;
                case 4:
                    if (value.stateCode != 6 || !value.published || value.abortAllowed || value.terminalFailureCode != 0)
                        throw Invalid("ActiveShadow requires a published Committed state.");
                    return;
                case 5:
                    if ((value.stateCode != 0 && value.stateCode != 1) || value.published || value.abortAllowed || value.terminalFailureCode != 0)
                        throw Invalid("BaselineUnselected requires an unpublished startup state.");
                    return;
                default:
                    throw Invalid("Unknown recovery disposition.");
            }
        }

        private static void RequireDisposition(AssemblyShadowRecoveryInfo value, string expected)
        {
            if (!string.Equals(value.disposition, expected, StringComparison.Ordinal))
                throw Invalid("Recovery disposition code and name are inconsistent.");
        }

        private static FormatException Invalid(string message) { return new FormatException(message); }
    }
}
