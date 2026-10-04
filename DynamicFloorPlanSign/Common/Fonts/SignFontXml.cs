using System.Collections.Generic;
using System.Xml.Serialization;

namespace DynamicFloorPlanSign.Common.Fonts
{
    [XmlRoot("font", Namespace = NAMESPACE)]
    public class SignFontXml
    {
        public const string NAMESPACE = "http://xna.microsoft.com/bitmapfont";

        [XmlAttribute("name")]
        public string Name;

        [XmlAttribute("height")]
        public int Height;

        [XmlArray("bitmaps", Namespace = NAMESPACE)]
        [XmlArrayItem("bitmap", Namespace = NAMESPACE)]
        public List<SignFontBitmapXml> Bitmaps = new List<SignFontBitmapXml>();

        [XmlArray("glyphs", Namespace = NAMESPACE)]
        [XmlArrayItem("glyph", Namespace = NAMESPACE)]
        public List<SignFontGlyphXml> Glyphs = new List<SignFontGlyphXml>();
    }

    public class SignFontBitmapXml
    {
        [XmlAttribute("id")]
        public int Id;

        [XmlAttribute("name")]
        public string Name;

        [XmlAttribute("size")]
        public string Size;
    }

    public class SignFontGlyphXml
    {
        [XmlAttribute("code")]
        public string Code;

        [XmlAttribute("bm")]
        public int Bitmap;

        [XmlAttribute("origin")]
        public string Origin;

        [XmlAttribute("size")]
        public string Size;

        [XmlAttribute("aw")]
        public int Advance;

        [XmlAttribute("lsb")]
        public int LeftBearing;
    }
}
