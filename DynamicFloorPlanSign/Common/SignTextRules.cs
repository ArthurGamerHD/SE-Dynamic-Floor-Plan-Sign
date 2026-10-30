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
        public const int MAX_FONT_NAME_LENGTH = 32;
        public const int MAX_FONT_SPANS_PER_LINE = 4;
        const string FONT_OPEN_TAG = "[FONT=\"";
        const string FONT_CLOSE_TAG = "[/FONT]";
        // [FONT="name"] + [/FONT]
        const int MAX_FONT_SPAN_LENGTH = 7 + MAX_FONT_NAME_LENGTH + 2 + 7;
        // Storage form may contain one newline plus optional marker controls and [font] spans in addition to visible text.
        public const int MAX_SERIALIZED_LENGTH = MAX_VISIBLE_LENGTH + 1 + 32
            + 2 * MAX_FONT_SPANS_PER_LINE * MAX_FONT_SPAN_LENGTH;

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

        sealed class LineBuilder
        {
            public readonly StringBuilder Text = new StringBuilder(MAX_LINE_LENGTH);
            public int VisibleLength;
            // Font of the span left open at the end of Text; null is the default font.
            public string Font;
            public int FontSpans;
        }

        static string NormalizeVisibleText(string source)
        {
            LineBuilder first = new LineBuilder();
            LineBuilder second = null;
            LineBuilder current = first;
            bool pendingSpace = false;
            bool hasNewline = false;
            string requestedFont = null;

            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];

                if (c == '\r')
                    continue;

                string tagFont;
                int tagLength;
                if (TryReadFontTag(source, i, out tagFont, out tagLength))
                {
                    // An unknown font name is dropped rather than kept as a span that silently renders in fallback fonts.
                    requestedFont = tagFont != null && SignFonts.IsKnownFont(tagFont) ? tagFont : null;
                    i += tagLength - 1;
                    continue;
                }

                if (c == '\n')
                {
                    if (!hasNewline)
                    {
                        // Spans never cross lines; the requested font reopens on the second line.
                        SwitchFont(current, null);
                        TrimTrailingSpace(current.Text);
                        second = new LineBuilder();
                        current = second;
                        hasNewline = true;
                        pendingSpace = false;
                    }
                    else if (current.VisibleLength > 0)
                    {
                        pendingSpace = true;
                    }
                    continue;
                }

                string font = EffectiveFont(current, requestedFont);

                // Digits become Roman numerals unless a [font] span supplies digit glyphs.
                if (c >= '0' && c <= '9' && (font == null || !SignFonts.HasGlyph(c, font)))
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

                    AppendVisibleToken(current, ToRoman(value), font, ref pendingSpace);
                    continue;
                }

                if (IsRuntimePrimitive(c) || (!char.IsWhiteSpace(c) && SignFonts.HasGlyph(c, font)))
                {
                    AppendVisibleToken(current, c.ToString(), font, ref pendingSpace);
                    continue;
                }

                if (!char.IsWhiteSpace(c))
                    continue;

                if (current.VisibleLength > 0)
                    pendingSpace = true;
            }

            SwitchFont(first, null);
            TrimTrailingSpace(first.Text);
            if (second != null)
            {
                SwitchFont(second, null);
                TrimTrailingSpace(second.Text);
            }

            if (!hasNewline)
                return first.Text.ToString();

            // Do not preserve an empty second line; an empty first line is allowed only if the second has text.
            if (second == null || second.VisibleLength == 0)
                return first.Text.ToString();
            if (first.VisibleLength == 0)
                return second.Text.ToString();

            return first.Text.ToString() + "\n" + second.Text.ToString();
        }

        /// <summary>Returns the first [font] tag name in <paramref name="input"/> that matches no registered font, or null.</summary>
        public static string FindUnknownFont(string input)
        {
            if (string.IsNullOrEmpty(input))
                return null;

            for (int i = 0; i < input.Length; i++)
            {
                string font;
                int length;
                if (!TryReadFontTag(input, i, out font, out length))
                    continue;

                if (font != null && !SignFonts.IsKnownFont(font))
                    return font;
                i += length - 1;
            }
            return null;
        }

        /// <summary>
        /// Reads a [font="name"] (quotes optional) or [/font] tag at <paramref name="index"/>; <paramref name="font"/>
        /// is null for the closing tag. Case-insensitive, so it also reads the canonical uppercase form.
        /// </summary>
        public static bool TryReadFontTag(string text, int index, out string font, out int length)
        {
            font = null;
            length = 0;
            if (index >= text.Length || text[index] != '[')
                return false;

            if (string.Compare(text, index, FONT_CLOSE_TAG, 0, FONT_CLOSE_TAG.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                length = FONT_CLOSE_TAG.Length;
                return true;
            }

            const string OPEN = "[FONT=";
            if (string.Compare(text, index, OPEN, 0, OPEN.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;

            int start = index + OPEN.Length;
            bool quoted = start < text.Length && text[start] == '"';
            if (quoted)
                start++;

            int end = start;
            while (end < text.Length && text[end] != '"' && text[end] != ']' && text[end] != '[' && text[end] != '\n')
                end++;

            int close = end;
            if (quoted)
            {
                if (close >= text.Length || text[close] != '"')
                    return false;
                close++;
            }
            if (close >= text.Length || text[close] != ']')
                return false;

            string name = text.Substring(start, end - start).Trim();
            if (name.Length == 0 || name.Length > MAX_FONT_NAME_LENGTH)
                return false;

            font = name;
            length = close + 1 - index;
            return true;
        }

        public static bool IsRuntimePrimitive(char c)
        {
            return c == '|' || c == '/' || c == '\\' || c == '+' || c == '-' || c == '=' || c == '_' || c == '<' || c == '>';
        }

        static string EffectiveFont(LineBuilder line, string requestedFont)
        {
            // Past the span limit a line keeps its current font instead of opening another span.
            if (requestedFont != null && line.FontSpans >= MAX_FONT_SPANS_PER_LINE
                && !string.Equals(line.Font, requestedFont, StringComparison.OrdinalIgnoreCase))
                return line.Font;
            return requestedFont;
        }

        static void SwitchFont(LineBuilder line, string font)
        {
            if (string.Equals(line.Font, font, StringComparison.OrdinalIgnoreCase))
                return;

            if (line.Font != null)
                line.Text.Append(FONT_CLOSE_TAG);
            if (font != null)
            {
                line.Text.Append(FONT_OPEN_TAG).Append(font).Append("\"]");
                line.FontSpans++;
            }
            line.Font = font;
        }

        static void AppendVisibleToken(LineBuilder line, string token, string font, ref bool pendingSpace)
        {
            if (string.IsNullOrEmpty(token) || line.VisibleLength >= MAX_LINE_LENGTH)
            {
                pendingSpace = false;
                return;
            }

            // Font tags go before the separating space so re-normalizing yields the same string.
            SwitchFont(line, font);

            if (pendingSpace && line.VisibleLength > 0 && line.VisibleLength < MAX_LINE_LENGTH - 1)
            {
                line.Text.Append(' ');
                line.VisibleLength++;
            }
            pendingSpace = false;

            int count = Math.Min(token.Length, MAX_LINE_LENGTH - line.VisibleLength);
            line.Text.Append(token, 0, count);
            line.VisibleLength += count;
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
