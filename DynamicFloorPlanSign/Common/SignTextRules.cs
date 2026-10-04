using System;
using System.Text;
using DynamicFloorPlanSign.Common.Fonts;

namespace DynamicFloorPlanSign.Common
{
    internal enum SignTextAlignment
    {
        Left,
        Center,
        Right,
        CenterFullWidth
    }

    internal sealed class SignMarker
    {
        public int Angle;
        public bool Arrow;
    }

    internal sealed class SignTextSpec
    {
        public string Text;
        public SignMarker Left;
        public SignMarker Right;
        public bool RemoveMarker;

        public SignTextAlignment Alignment
        {
            get
            {
                if (RemoveMarker)
                    return SignTextAlignment.CenterFullWidth;
                if (Left != null && Right != null)
                    return SignTextAlignment.Center;
                if (Left != null)
                    return SignTextAlignment.Right;
                return SignTextAlignment.Left;
            }
        }
    }

    internal static class SignTextRules
    {
        public const int MAX_LINE_LENGTH = 32;
        public const int MAX_LENGTH = MAX_LINE_LENGTH; // Backward-compatible alias for the original single-line limit.
        public const int MAX_VISIBLE_LENGTH = MAX_LINE_LENGTH * 2;
        // Storage form may contain one newline plus optional marker controls in addition to visible text.
        public const int MAX_SERIALIZED_LENGTH = MAX_VISIBLE_LENGTH + 1 + 32;

        public static string Normalize(string input)
        {
            SignTextSpec spec = Parse(input);
            if (spec == null || string.IsNullOrEmpty(spec.Text))
                return string.Empty;

            StringBuilder result = new StringBuilder(MAX_SERIALIZED_LENGTH);
            if (spec.Left != null)
                AppendMarker(result, spec.Left);

            result.Append(spec.Text);

            if (spec.RemoveMarker)
            {
                result.Append(" -[]");
            }
            else if (spec.Right != null)
            {
                // A plain right-side chevron at 0 degrees is exactly vanilla; canonicalize it away.
                if (spec.Left != null || spec.Right.Angle != 0 || spec.Right.Arrow)
                    AppendMarker(result, spec.Right);
            }

            return result.ToString();
        }

        public static SignTextSpec Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new SignTextSpec { Text = string.Empty };

            string source = input.Trim().ToUpperInvariant();
            SignTextSpec spec = new SignTextSpec();

            // Explicit removal wins over the implicit vanilla right marker.
            if (source.EndsWith("-[]", StringComparison.Ordinal))
            {
                spec.RemoveMarker = true;
                source = source.Substring(0, source.Length - 3).TrimEnd();
            }

            SignMarker marker;
            int consumed;
            if (TryParseMarkerAtStart(source, out marker, out consumed))
            {
                spec.Left = marker;
                source = source.Substring(consumed).TrimStart();
            }

            if (TryParseMarkerAtEnd(source, out marker, out consumed))
            {
                spec.Right = marker;
                source = source.Substring(0, source.Length - consumed).TrimEnd();
            }

            spec.Text = NormalizeVisibleText(source);
            if (spec.Text.Length == 0)
                return spec;

            // No explicit directional control means the normal vanilla right [^].
            if (!spec.RemoveMarker && spec.Left == null && spec.Right == null)
                spec.Right = new SignMarker { Angle = 0, Arrow = false };

            return spec;
        }

        static bool TryParseMarkerAtStart(string source, out SignMarker marker, out int consumed)
        {
            marker = null;
            consumed = 0;
            if (string.IsNullOrEmpty(source) || source[0] != '[')
                return false;

            int close = source.IndexOf(']');
            if (close <= 1)
                return false;

            string body = source.Substring(1, close - 1);
            if (!TryParseMarkerBody(body, out marker))
                return false;

            consumed = close + 1;
            return true;
        }

        static bool TryParseMarkerAtEnd(string source, out SignMarker marker, out int consumed)
        {
            marker = null;
            consumed = 0;
            if (string.IsNullOrEmpty(source) || source[source.Length - 1] != ']')
                return false;

            int open = source.LastIndexOf('[');
            if (open < 0 || open >= source.Length - 2)
                return false;

            string body = source.Substring(open + 1, source.Length - open - 2);
            if (!TryParseMarkerBody(body, out marker))
                return false;

            consumed = source.Length - open;
            return true;
        }

        static bool TryParseMarkerBody(string body, out SignMarker marker)
        {
            marker = null;
            if (string.IsNullOrEmpty(body))
                return false;

            bool arrow = false;
            if (body.EndsWith("A", StringComparison.Ordinal))
            {
                arrow = true;
                body = body.Substring(0, body.Length - 1);
            }

            int rawAngle;
            if (!int.TryParse(body, out rawAngle))
                return false;

            marker = new SignMarker
            {
                Angle = NormalizeAngle(rawAngle),
                Arrow = arrow
            };
            return true;
        }

        static int NormalizeAngle(int angle)
        {
            // Normalize before canonicalization/optimization: 360 -> 0, -360 -> 0, -90 -> 270.
            int normalized = angle % 360;
            if (normalized < 0)
                normalized += 360;
            return normalized;
        }

        static void AppendMarker(StringBuilder result, SignMarker marker)
        {
            result.Append('[');
            result.Append(marker.Angle);
            if (marker.Arrow)
                result.Append('A');
            result.Append(']');
        }

        static string NormalizeVisibleText(string source)
        {
            StringBuilder first = new StringBuilder(MAX_LINE_LENGTH);
            StringBuilder second = null;
            StringBuilder current = first;
            bool pendingSpace = false;
            bool hasNewline = false;

            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];

                if (c == '\r')
                    continue;

                if (c == '\n')
                {
                    if (!hasNewline)
                    {
                        TrimTrailingSpace(current);
                        second = new StringBuilder(MAX_LINE_LENGTH);
                        current = second;
                        hasNewline = true;
                        pendingSpace = false;
                    }
                    else if (current.Length > 0)
                    {
                        pendingSpace = true;
                    }
                    continue;
                }

                if (c >= '0' && c <= '9')
                {
                    long value = 0;
                    while (i < source.Length && source[i] >= '0' && source[i] <= '9')
                    {
                        int digit = source[i] - '0';
                        if (value < 100000)
                            value = value * 10 + digit;
                        i++;
                    }
                    i--;

                    AppendVisibleToken(current, ToRoman(value), ref pendingSpace);
                    continue;
                }

                if (IsRuntimePrimitive(c) || (!char.IsWhiteSpace(c) && SignFonts.HasGlyph(c)))
                {
                    AppendVisibleToken(current, c.ToString(), ref pendingSpace);
                    continue;
                }

                if (!char.IsWhiteSpace(c))
                    continue;

                if (current.Length > 0)
                    pendingSpace = true;
            }

            TrimTrailingSpace(first);
            if (second != null)
                TrimTrailingSpace(second);

            if (!hasNewline)
                return first.ToString();

            // Do not preserve an empty second line; an empty first line is allowed only if the second has text.
            if (second == null || second.Length == 0)
                return first.ToString();
            if (first.Length == 0)
                return second.ToString();

            return first.ToString() + "\n" + second.ToString();
        }

        public static bool IsRuntimePrimitive(char c)
        {
            return c == '|' || c == '/' || c == '\\' || c == '+' || c == '-' || c == '=' || c == '_' || c == '<' || c == '>';
        }

        static void AppendVisibleToken(StringBuilder result, string token, ref bool pendingSpace)
        {
            if (string.IsNullOrEmpty(token) || result.Length >= MAX_LINE_LENGTH)
            {
                pendingSpace = false;
                return;
            }

            if (pendingSpace && result.Length > 0 && result.Length < MAX_LINE_LENGTH - 1)
                result.Append(' ');
            pendingSpace = false;

            int remaining = MAX_LINE_LENGTH - result.Length;
            if (remaining <= 0)
                return;

            if (token.Length <= remaining)
                result.Append(token);
            else
                result.Append(token, 0, remaining);
        }

        static void TrimTrailingSpace(StringBuilder result)
        {
            while (result.Length > 0 && result[result.Length - 1] == ' ')
                result.Length--;
        }

        static string ToRoman(long value)
        {
            if (value <= 0)
                return "";

            if (value > 99999)
                value = 99999;

            StringBuilder result = new StringBuilder();
            AppendRoman(result, ref value, 1000, "M");
            AppendRoman(result, ref value, 900, "CM");
            AppendRoman(result, ref value, 500, "D");
            AppendRoman(result, ref value, 400, "CD");
            AppendRoman(result, ref value, 100, "C");
            AppendRoman(result, ref value, 90, "XC");
            AppendRoman(result, ref value, 50, "L");
            AppendRoman(result, ref value, 40, "XL");
            AppendRoman(result, ref value, 10, "X");
            AppendRoman(result, ref value, 9, "IX");
            AppendRoman(result, ref value, 5, "V");
            AppendRoman(result, ref value, 4, "IV");
            AppendRoman(result, ref value, 1, "I");
            return result.ToString();
        }

        static void AppendRoman(StringBuilder result, ref long value, int unit, string token)
        {
            while (value >= unit)
            {
                result.Append(token);
                value -= unit;
            }
        }
    }
}
