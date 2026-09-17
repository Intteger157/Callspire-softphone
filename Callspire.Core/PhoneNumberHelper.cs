using System;
using System.Linq;

namespace Softphone
{
    /// <summary>
    /// Outbound dial-string normalization (desktop, gateway docs, CRM variants).
    /// </summary>
    public static class PhoneNumberHelper
    {
        /// <summary>
        /// ITU country codes that produce an 11-digit digit string starting with '8'
        /// (e.g. Hong Kong +852 …, Japan +81 …). Must not be treated as Russian trunk "8".
        /// </summary>
        private static readonly string[] InternationalPrefixesElevenDigitsStartingWith8 =
        {
            "852", // Hong Kong
            "853", // Macau
            "81",  // Japan
            "82",  // South Korea
            "84",  // Vietnam
            "86",  // China (some 11-digit dial strings)
            "850", // North Korea
            "855", // Cambodia
            "856", // Laos
            "880", // Bangladesh
            "886", // Taiwan
        };

        public static string DigitsOnly(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return new string(value.Where(char.IsDigit).ToArray());
        }

        /// <summary>
        /// Compact destination for SIP / WebRTC / PBX Originate.
        /// Strips formatting; adds leading + for E.164 (≥11 digits or user typed +).
        /// Short internal extensions (no +, ≤6 digits) are returned unchanged.
        /// Russian domestic 8XXXXXXXXXX (11 digits) → +7XXXXXXXXXX when clearly RU, not HK/JP/…
        /// </summary>
        public static string NormalizeForDial(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            var trimmed = raw.Trim();
            var digits = DigitsOnly(trimmed);
            if (digits.Length == 0)
                return trimmed;

            bool userTypedPlus = trimmed.StartsWith("+", StringComparison.Ordinal);

            if (ShouldConvertRussianTrunkEightToSeven(digits, userTypedPlus))
                digits = "7" + digits[1..];

            if (userTypedPlus || digits.Length >= 11)
                return "+" + digits;

            return digits;
        }

        /// <summary>
        /// CRM / contact search: digit-only form with the same safe 8→7 rule as dial normalization.
        /// </summary>
        public static string NormalizeDigitsForSearch(string? phoneNumber)
        {
            var digits = DigitsOnly(phoneNumber);
            if (digits.Length == 0)
                return string.Empty;

            if (ShouldConvertRussianTrunkEightToSeven(digits, userTypedPlus: false))
                return "7" + digits[1..];

            return digits;
        }

        /// <summary>
        /// True when <paramref name="digits"/> is Russian national format 8 + 10 digits (not +852, +81, …).
        /// </summary>
        public static bool ShouldConvertRussianTrunkEightToSeven(string digits, bool userTypedPlus)
        {
            if (userTypedPlus)
                return false;

            if (digits.Length != 11 || digits[0] != '8')
                return false;

            foreach (var prefix in InternationalPrefixesElevenDigitsStartingWith8)
            {
                if (digits.StartsWith(prefix, StringComparison.Ordinal))
                    return false;
            }

            // Russian NSN (after trunk 8) starts with 3–9 (mobile 9xx, geographic 3xx–8xx).
            char nsnFirst = digits[1];
            return nsnFirst >= '3' && nsnFirst <= '9';
        }
    }
}
