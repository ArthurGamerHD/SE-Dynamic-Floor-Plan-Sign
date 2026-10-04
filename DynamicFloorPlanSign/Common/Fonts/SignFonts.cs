using System;
using System.Collections.Generic;
using System.IO;
using Adk.Utils;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Utils;

namespace DynamicFloorPlanSign.Common.Fonts
{
    internal sealed class SignFontBitmap
    {
        public SignFont Font;
        public int Id;
        public string Path;
        public bool InMod;
        public int Width;
        public int Height;
    }

    internal sealed class SignFontGlyph
    {
        public SignFont Font;
        public SignFontBitmap Bitmap;
        public int X, Y, Width, Height;
        public int Advance;
        public int LeftBearing;

        public void GetUv(out float u0, out float v0, out float u1, out float v1)
        {
            u0 = (float)X / Bitmap.Width;
            u1 = (float)(X + Width) / Bitmap.Width;
            v0 = (float)Y / Bitmap.Height;
            v1 = (float)(Y + Height) / Bitmap.Height;
        }
    }

    internal sealed class SignFont
    {
        public string Name;
        public int Priority;
        public string XmlPath;
        public int MaskDilation;
        public MyObjectBuilder_Checkpoint.ModItem ModItem;
        public readonly Dictionary<int, SignFontBitmap> Bitmaps = new Dictionary<int, SignFontBitmap>();
        public readonly Dictionary<char, SignFontGlyph> Glyphs = new Dictionary<char, SignFontGlyph>();
        internal int Order;
    }

    internal static class SignFonts
    {
        public const long CHANNEL = 3811572476L;
        public const int MAX_MASK_DILATION = 8;
        const string REQUEST_FONTS = "DynamicFloorPlanSign.RequestFonts";

        static readonly List<SignFont> Fonts = new List<SignFont>();
        static readonly Dictionary<char, SignFontGlyph> Resolved = new Dictionary<char, SignFontGlyph>();
        static int _nextOrder;
        static bool _listening;

        public static void Load()
        {
            if (_listening || MyAPIGateway.Utilities == null)
                return;

            MyAPIGateway.Utilities.RegisterMessageHandler(CHANNEL, OnModMessage);
            _listening = true;
            MyAPIGateway.Utilities.SendModMessage(CHANNEL, REQUEST_FONTS);
        }

        public static void Unload()
        {
            if (_listening && MyAPIGateway.Utilities != null)
                MyAPIGateway.Utilities.UnregisterMessageHandler(CHANNEL, OnModMessage);
            _listening = false;
            Fonts.Clear();
            Resolved.Clear();
            _nextOrder = 0;
        }

        static void OnModMessage(object message)
        {
            if (!(message is MyTuple<int, string, int, string, MyObjectBuilder_Checkpoint.ModItem>))
                return; // Our own font request, or an unrelated payload.

            var registration = (MyTuple<int, string, int, string, MyObjectBuilder_Checkpoint.ModItem>)message;
            AddFont(registration.Item1, registration.Item2, registration.Item3, registration.Item4, registration.Item5);
        }

        /// <summary>
        /// Loads a bitmap font XML from <paramref name="modItem"/> and registers it. Registering the same mod and
        /// XML path again replaces the previous registration.
        /// </summary>
        /// <param name="maskDilation">
        /// Pixels to thicken strokes by in the generated alphamask, clamped to 0..<see cref="MAX_MASK_DILATION"/>.
        /// The shader clips at alpha 0.5, so thin strokes vanish in distant mips; fonts drawn on the vanilla
        /// sign atlas ignore it.
        /// </param>
        /// <param name="characters">
        /// Comma-separated hex code points or ranges this font provides, e.g. "3130-318F,AC00-D7A3"; glyphs
        /// outside them are ignored. Null or empty takes every glyph in the XML.
        /// </param>
        public static bool AddFont(int priority, string xmlPath, int maskDilation, string characters,
            MyObjectBuilder_Checkpoint.ModItem modItem)
        {
            SignFont font;
            try
            {
                font = LoadFont(xmlPath, ParseCharacterRanges(characters), modItem);
            }
            catch (Exception e)
            {
                LogHelper.Log(MyLogSeverity.Error, "failed to load sign font '" + xmlPath + "' from " + modItem.Name + ": " + e);
                return false;
            }

            font.Priority = priority;
            font.MaskDilation = Math.Max(0, Math.Min(MAX_MASK_DILATION, maskDilation));

            int existing = Fonts.FindIndex(f => f.ModItem.Name == modItem.Name
                && string.Equals(f.XmlPath, xmlPath, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                font.Order = Fonts[existing].Order;
                Fonts[existing] = font;
            }
            else
            {
                font.Order = _nextOrder++;
                Fonts.Add(font);
            }

            Fonts.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Order.CompareTo(b.Order));
            Resolved.Clear();

            LogHelper.LogInfo("registered sign font '" + font.Name + "' (" + font.Glyphs.Count + " glyphs, priority "
                + priority + ") from " + modItem.Name);
            return true;
        }

        public static bool TryGetGlyph(char c, out SignFontGlyph glyph)
        {
            if (Resolved.TryGetValue(c, out glyph))
                return glyph != null;

            for (int i = 0; i < Fonts.Count; i++)
            {
                if (Fonts[i].Glyphs.TryGetValue(c, out glyph))
                    break;
            }

            Resolved[c] = glyph;
            return glyph != null;
        }

        public static bool HasGlyph(char c)
        {
            SignFontGlyph glyph;
            return TryGetGlyph(c, out glyph);
        }

        static SignFont LoadFont(string xmlPath, List<int[]> characters, MyObjectBuilder_Checkpoint.ModItem modItem)
        {
            string xml;
            using (TextReader reader = MyAPIGateway.Utilities.ReadFileInModLocation(xmlPath, modItem))
            {
                if (reader == null)
                    throw new FileNotFoundException("Sign font XML not found: " + xmlPath);
                xml = reader.ReadToEnd();
            }

            SignFontXml data = MyAPIGateway.Utilities.SerializeFromXML<SignFontXml>(xml.TrimStart('\uFEFF'));
            if (data == null)
                throw new InvalidOperationException("Sign font XML is empty: " + xmlPath);

            SignFont font = new SignFont
            {
                Name = string.IsNullOrEmpty(data.Name) ? xmlPath : data.Name,
                XmlPath = xmlPath,
                ModItem = modItem
            };

            int slash = Math.Max(xmlPath.LastIndexOf('\\'), xmlPath.LastIndexOf('/'));
            string folder = slash < 0 ? string.Empty : xmlPath.Substring(0, slash + 1);
            foreach (SignFontBitmapXml b in data.Bitmaps)
            {
                int width, height;
                ParsePair(b.Size, 'x', out width, out height);
                string path = NormalizeContentPath(folder + b.Name);

                bool inMod = MyAPIGateway.Utilities.FileExistsInModLocation(path, modItem);
                if (!inMod && !MyAPIGateway.Utilities.FileExistsInGameContent(path))
                {
                    LogHelper.Log(MyLogSeverity.Warning, "sign font '" + font.Name + "' bitmap " + b.Id + " not found in "
                        + modItem.Name + " or game content: " + path + "; its glyphs are unavailable");
                    continue;
                }

                font.Bitmaps[b.Id] = new SignFontBitmap
                {
                    Font = font,
                    Id = b.Id,
                    Path = path,
                    InMod = inMod,
                    Width = width,
                    Height = height
                };
            }

            foreach (SignFontGlyphXml g in data.Glyphs)
            {
                int code;
                if (string.IsNullOrEmpty(g.Code)
                    || !int.TryParse(g.Code, System.Globalization.NumberStyles.HexNumber, null, out code)
                    || code > char.MaxValue
                    || !InRanges(characters, code))
                    continue;

                SignFontBitmap bitmap;
                if (!font.Bitmaps.TryGetValue(g.Bitmap, out bitmap))
                    continue;

                SignFontGlyph glyph = new SignFontGlyph
                {
                    Font = font,
                    Bitmap = bitmap,
                    Advance = g.Advance,
                    LeftBearing = g.LeftBearing
                };
                ParsePair(g.Origin, ',', out glyph.X, out glyph.Y);
                ParsePair(g.Size, 'x', out glyph.Width, out glyph.Height);
                if (glyph.Height <= 0)
                    continue;

                font.Glyphs[(char)code] = glyph;
            }

            return font;
        }

        static List<int[]> ParseCharacterRanges(string characters)
        {
            if (string.IsNullOrWhiteSpace(characters))
                return null;

            List<int[]> ranges = new List<int[]>();
            foreach (string entry in characters.Split(','))
            {
                string trimmed = entry.Trim();
                if (trimmed.Length == 0)
                    continue;

                int dash = trimmed.IndexOf('-');
                string first = dash < 0 ? trimmed : trimmed.Substring(0, dash).Trim();
                string last = dash < 0 ? trimmed : trimmed.Substring(dash + 1).Trim();
                int lo, hi;
                if (!int.TryParse(first, System.Globalization.NumberStyles.HexNumber, null, out lo)
                    || !int.TryParse(last, System.Globalization.NumberStyles.HexNumber, null, out hi)
                    || hi < lo)
                    throw new InvalidOperationException("Invalid sign font character range '" + trimmed + "'.");
                ranges.Add(new[] { lo, hi });
            }
            return ranges;
        }

        static bool InRanges(List<int[]> ranges, int code)
        {
            if (ranges == null)
                return true;
            for (int i = 0; i < ranges.Count; i++)
                if (code >= ranges[i][0] && code <= ranges[i][1])
                    return true;
            return false;
        }

        static string NormalizeContentPath(string path)
        {
            List<string> segments = new List<string>();
            foreach (string segment in path.Split('\\', '/'))
            {
                if (segment.Length == 0 || segment == ".")
                    continue;
                if (segment == "..")
                {
                    if (segments.Count == 0)
                        throw new InvalidOperationException("Sign font path leaves its content root: " + path);
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                }
                segments.Add(segment);
            }
            return string.Join("\\", segments);
        }

        static void ParsePair(string value, char separator, out int a, out int b)
        {
            int split = value == null ? -1 : value.IndexOf(separator);
            if (split < 0
                || !int.TryParse(value.Substring(0, split), out a)
                || !int.TryParse(value.Substring(split + 1), out b))
                throw new InvalidOperationException("Invalid sign font value '" + value + "'.");
        }
    }
}
