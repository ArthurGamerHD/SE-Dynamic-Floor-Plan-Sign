using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DynamicFloorPlanSign.Common;
using Sandbox.ModAPI;
using VRage.Library.Utils;

// Clanker warning:
// This section was heavily written by LLMs, have a problem with that? build me a better one, this mod was an evening project, started 13 pm and finished by 18pm
namespace DynamicFloorPlanSign.Client.Rendering
{
    internal static class RuntimeMwmBuilder
    {
        const string TEMPLATE_PATH = @"Models\Cubes\large\FloorPlanMaintenance_LOD0.mwm";
        const string SIGN_MATERIAL = "WarningSignsEaster";
        const int TEMPLATE_GLYPH_COUNT = 11; // "MAINTENANCE".Length
        const int INDEXED_TAG_VERSION = 1066002;
        const int READ_CHUNK_SIZE = 64 * 1024;
        const string DETECTOR_DUMMY_NAME = "detector_dynsign_text_1";
        const string HIGHLIGHT_SECTION_NAME = "DynSignTextArea_section_1";
        
        static readonly Dictionary<string, string> DetectorModelCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> TextModelCache = new Dictionary<string, string>(StringComparer.Ordinal);

        static readonly float[] DetectorMatrix =
        {
            1.2889947891235352f, 1.1268742383663266e-07f, -5.019530817662599e-07f, 0f,
            -2.677485966273707e-08f, 0.30626869201660156f, -4.989750479467148e-08f, 0f,
            9.021470503967066e-08f, 3.7743458136674235e-08f, 0.2316676378250122f, 0f,
            1.0496295033135539e-07f, 1.0351805686950684f, -1.1994400024414062f, 1f
        };

        static readonly int[] CornerSourceVertices = { 2971, 2970, 2969, 3002 };

        const float TEXT_X_MIN = -0.433349609375f;
        const float TEXT_X_MAX = 0.263916015625f;
        const float TEXT_Y_MIN = 0.98974609375f;
        const float TEXT_Y_MAX = 1.0693359375f;
        const float TEXT_Z = -1.1767578125f;
        const float ATLAS_SIZE = 1024f;
        const float LETTER_GAP = 0.001f;

        // Vanilla FloorPlanMaintenance_LOD0 WarningSignsEaster marker geometry.
        // 2493..2496 are the standalone chevron quad. 2517..2556 are the two bracket pieces.
        const int MARKER_VERTEX_START = 2493;
        const int MARKER_VERTEX_END = 2556;
        const int MARKER_VERTEX_COUNT = MARKER_VERTEX_END - MARKER_VERTEX_START + 1;
        const int MARKER_ARROW_VERTEX_COUNT = 4;
        const int MARKER_BRACKET_VERTEX_START = 2517;
        const float MARKER_LEFT_DX = -0.8291015625f;
        const float MIRRORED_TEXT_X_MIN = -TEXT_X_MAX;
        const float MIRRORED_TEXT_X_MAX = -TEXT_X_MIN;

        sealed class Glyph
        {
            public int X0, X1, Y0, Y1;

            public float Aspect
            {
                get
                {
                    float w = X1 - X0 + 1 + 2f;
                    float h = Y1 - Y0 + 1 + 2f;
                    return w / h;
                }
            }

            public void GetUv(out float u0, out float v0, out float u1, out float v1)
            {
                // One-pixel padding around the visible glyph, matching the vanilla quads closely.
                u0 = (X0 - 1f) / ATLAS_SIZE;
                u1 = (X1 + 2f) / ATLAS_SIZE;
                v0 = (Y0 - 1f) / ATLAS_SIZE;
                v1 = (Y1 + 2f) / ATLAS_SIZE;
            }
        }

        struct GlyphQuad
        {
            public float X0, X1, Y0, Y1;
            public float U0, U1, V0, V1;
            public float RotationDegrees;
        }

        sealed class TagEntry
        {
            public string Name;
            public int Offset;
            public byte[] Chunk;
            public int OffsetPatchPosition;
        }

        sealed class MeshPart
        {
            public int Header;
            public int OldHeader;
            public bool HasOldHeader;
            public int[] Indices;
            public bool HasMaterial;
            public string MaterialName;
            public byte[] MaterialDescriptor;
        }

        sealed class MwmContainer
        {
            public string DebugTag;
            public string[] DebugLines;
            public int Version;
            public readonly List<TagEntry> Tags = new List<TagEntry>();

            public TagEntry Find(string name)
            {
                for (int i = 0; i < Tags.Count; i++)
                    if (string.Equals(Tags[i].Name, name, StringComparison.Ordinal))
                        return Tags[i];
                return null;
            }
        }

        /// <summary>
        /// Small little-endian reader over a byte[] for the MWM structures we patch.
        /// It implements only the BinaryReader-compatible primitives needed by the indexed MWM tags we patch.
        /// </summary>
        sealed class ByteReader
        {
            readonly byte[] _data;
            int _position;

            public ByteReader(byte[] data)
            {
                if (data == null) throw new ArgumentNullException("data");
                _data = data;
            }

            public int Position => _position;
            public int Length => _data.Length;
            public int Remaining => _data.Length - _position;

            void Require(int count)
            {
                if (count < 0 || _position > _data.Length - count)
                    throw new InvalidOperationException("Unexpected end of in-memory MWM buffer.");
            }

            public byte ReadByte()
            {
                Require(1);
                return _data[_position++];
            }

            public bool ReadBoolean()
            {
                return ReadByte() != 0;
            }

            public int ReadInt32()
            {
                Require(4);
                int p = _position;
                _position += 4;
                return _data[p]
                    | (_data[p + 1] << 8)
                    | (_data[p + 2] << 16)
                    | (_data[p + 3] << 24);
            }

            public uint ReadUInt32()
            {
                Require(4);
                int p = _position;
                _position += 4;
                return (uint)(_data[p]
                    | (_data[p + 1] << 8)
                    | (_data[p + 2] << 16)
                    | (_data[p + 3] << 24));
            }

            public ulong ReadUInt64()
            {
                Require(8);
                int p = _position;
                _position += 8;
                return _data[p]
                    | ((ulong)_data[p + 1] << 8)
                    | ((ulong)_data[p + 2] << 16)
                    | ((ulong)_data[p + 3] << 24)
                    | ((ulong)_data[p + 4] << 32)
                    | ((ulong)_data[p + 5] << 40)
                    | ((ulong)_data[p + 6] << 48)
                    | ((ulong)_data[p + 7] << 56);
            }

            public string ReadString()
            {
                int byteCount = Read7BitEncodedInt();
                if (byteCount < 0)
                    throw new InvalidOperationException("Negative MWM string byte count.");
                Require(byteCount);
                string value = Encoding.UTF8.GetString(_data, _position, byteCount);
                _position += byteCount;
                return value;
            }

            int Read7BitEncodedInt()
            {
                int count = 0;
                int shift = 0;
                while (shift != 35)
                {
                    byte b = ReadByte();
                    count |= (b & 0x7F) << shift;
                    shift += 7;
                    if ((b & 0x80) == 0)
                        return count;
                }
                throw new FormatException("Invalid 7-bit encoded integer in MWM buffer.");
            }

            public byte[] ReadBytes(int count)
            {
                Require(count);
                byte[] result = new byte[count];
                if (count != 0)
                    Buffer.BlockCopy(_data, _position, result, 0, count);
                _position += count;
                return result;
            }

            public byte[] ReadRemainingBytes()
            {
                return ReadBytes(Remaining);
            }

            public void Skip(int count)
            {
                Require(count);
                _position += count;
            }
        }

        /// <summary>
        /// Resizable little-endian writer backed only by byte[]. Supports direct patching by array offset,
        /// which lets the builder rewrite indexed offsets without seeking.
        /// </summary>
        sealed class ByteWriter
        {
            byte[] _buffer;
            int _length;

            public ByteWriter(int initialCapacity = 256)
            {
                if (initialCapacity < 1) initialCapacity = 1;
                _buffer = new byte[initialCapacity];
            }

            public int Position => _length;

            void Ensure(int additional)
            {
                int required = _length + additional;
                if (required <= _buffer.Length)
                    return;

                int capacity = _buffer.Length;
                while (capacity < required)
                {
                    int next = capacity << 1;
                    if (next <= capacity)
                    {
                        capacity = required;
                        break;
                    }
                    capacity = next;
                }
                Array.Resize(ref _buffer, capacity);
            }

            public void Write(byte value)
            {
                Ensure(1);
                _buffer[_length++] = value;
            }

            public void Write(bool value)
            {
                Write((byte)(value ? 1 : 0));
            }

            public void Write(int value)
            {
                Ensure(4);
                _buffer[_length++] = (byte)value;
                _buffer[_length++] = (byte)(value >> 8);
                _buffer[_length++] = (byte)(value >> 16);
                _buffer[_length++] = (byte)(value >> 24);
            }

            public void Write(uint value)
            {
                Ensure(4);
                _buffer[_length++] = (byte)value;
                _buffer[_length++] = (byte)(value >> 8);
                _buffer[_length++] = (byte)(value >> 16);
                _buffer[_length++] = (byte)(value >> 24);
            }

            public void Write(float value)
            {
                // BitConverter is used only to encode one IEEE754 value into our byte[] writer;
                // no seekable stream object is used by the in-memory MWM codec.
                byte[] bytes = BitConverter.GetBytes(value);
                Write(bytes, 0, 4);
            }

            public void Write(ulong value)
            {
                Ensure(8);
                _buffer[_length++] = (byte)value;
                _buffer[_length++] = (byte)(value >> 8);
                _buffer[_length++] = (byte)(value >> 16);
                _buffer[_length++] = (byte)(value >> 24);
                _buffer[_length++] = (byte)(value >> 32);
                _buffer[_length++] = (byte)(value >> 40);
                _buffer[_length++] = (byte)(value >> 48);
                _buffer[_length++] = (byte)(value >> 56);
            }

            public void Write(string value)
            {
                if (value == null) throw new ArgumentNullException("value");
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                Write7BitEncodedInt(bytes.Length);
                Write(bytes);
            }

            void Write7BitEncodedInt(int value)
            {
                uint v = (uint)value;
                while (v >= 0x80)
                {
                    Write((byte)(v | 0x80));
                    v >>= 7;
                }
                Write((byte)v);
            }

            public void Write(byte[] data)
            {
                if (data == null) throw new ArgumentNullException("data");
                Write(data, 0, data.Length);
            }

            public void Write(byte[] data, int offset, int count)
            {
                if (data == null) throw new ArgumentNullException("data");
                if (offset < 0 || count < 0 || offset > data.Length - count)
                    throw new ArgumentException();
                Ensure(count);
                if (count != 0)
                    Buffer.BlockCopy(data, offset, _buffer, _length, count);
                _length += count;
            }

            public void PatchInt32(int offset, int value)
            {
                if (offset < 0 || offset > _length - 4)
                    throw new ArgumentException("offset");
                _buffer[offset] = (byte)value;
                _buffer[offset + 1] = (byte)(value >> 8);
                _buffer[offset + 2] = (byte)(value >> 16);
                _buffer[offset + 3] = (byte)(value >> 24);
            }

            public byte[] ToArray()
            {
                byte[] result = new byte[_length];
                if (_length != 0)
                    Buffer.BlockCopy(_buffer, 0, result, 0, _length);
                return result;
            }
        }

        static readonly Dictionary<char, Glyph> Glyphs = BuildGlyphTable();

        public static string BuildModel(string text, Type storageType, VRage.Game.MyObjectBuilder_Checkpoint.ModItem modItem)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("Sign text is empty.");

            string cached;
            if (TextModelCache.TryGetValue(text, out cached))
                return cached;

            byte[] templateBytes;
            using (BinaryReader reader = MyAPIGateway.Utilities.ReadBinaryFileInGameContent(TEMPLATE_PATH))
            {
                if (reader == null)
                    throw new FileNotFoundException("Could not open runtime sign template: " + TEMPLATE_PATH);
                templateBytes = ReadAllBytes(reader);
            }

            MwmContainer model = ParseContainer(templateBytes);
            if (model.Version < INDEXED_TAG_VERSION)
                throw new InvalidOperationException("Template MWM is not an indexed-tag model.");

            SignTextSpec spec = SignTextRules.Parse(text);
            if (spec == null || string.IsNullOrEmpty(spec.Text))
                throw new InvalidOperationException("Sign text is empty after parsing controls.");

            List<GlyphQuad> quads = LayoutText(spec.Text, spec.Alignment);

            TagEntry verticesTag = Required(model, "Vertices");
            TagEntry normalsTag = Required(model, "Normals");
            TagEntry texCoordsTag = Required(model, "TexCoords0");
            TagEntry binormalsTag = Required(model, "Binormals");
            TagEntry tangentsTag = Required(model, "Tangents");
            TagEntry meshPartsTag = Required(model, "MeshParts");

            int oldVertexCount;
            ulong[] oldVertices = ReadUInt64Stream(verticesTag.Chunk, "Vertices", out oldVertexCount);
            if (oldVertexCount <= CornerSourceVertices[3])
                throw new InvalidOperationException("Template vertex layout changed; glyph source vertices are unavailable.");

            int oldUvCount;
            uint[] oldUvs = ReadUInt32Stream(texCoordsTag.Chunk, "TexCoords0", out oldUvCount);
            if (oldUvCount != oldVertexCount)
                throw new InvalidOperationException("Template TexCoords0 count does not match Vertices.");

            if (oldVertexCount <= MARKER_VERTEX_END)
                throw new InvalidOperationException("Template vertex layout changed; vanilla marker vertices are unavailable.");

            ulong[] markerCloneVertices = null;
            uint[] markerCloneUvs = null;
            bool cloneMarkerToLeft = spec.Left != null && spec.Right != null && !spec.RemoveMarker;
            bool removeMarker = spec.RemoveMarker;

            if (!removeMarker)
            {
                if (cloneMarkerToLeft)
                {
                    markerCloneVertices = Slice(oldVertices, MARKER_VERTEX_START, MARKER_VERTEX_COUNT);
                    markerCloneUvs = Slice(oldUvs, MARKER_VERTEX_START, MARKER_VERTEX_COUNT);
                    ApplyMarkerGeometry(oldVertices, oldUvs, MARKER_VERTEX_START, spec.Right, 0f);
                    ApplyMarkerGeometry(markerCloneVertices, markerCloneUvs, 0, spec.Left, MARKER_LEFT_DX);
                }
                else if (spec.Left != null)
                {
                    ApplyMarkerGeometry(oldVertices, oldUvs, MARKER_VERTEX_START, spec.Left, MARKER_LEFT_DX);
                }
                else if (spec.Right != null)
                {
                    ApplyMarkerGeometry(oldVertices, oldUvs, MARKER_VERTEX_START, spec.Right, 0f);
                }
            }

            int markerCloneCount = markerCloneVertices == null ? 0 : markerCloneVertices.Length;
            int newVertexCount = oldVertexCount + markerCloneCount + quads.Count * 4;

            verticesTag.Chunk = BuildVerticesChunk(oldVertices, markerCloneVertices, quads);
            texCoordsTag.Chunk = BuildTexCoordsChunk(oldUvs, markerCloneUvs, quads);
            normalsTag.Chunk = AppendClonedVertexStream(normalsTag.Chunk, "Normals", oldVertexCount, 4,
                markerCloneCount, MARKER_VERTEX_START, quads.Count);
            binormalsTag.Chunk = AppendClonedVertexStream(binormalsTag.Chunk, "Binormals", oldVertexCount, 4,
                markerCloneCount, MARKER_VERTEX_START, quads.Count);
            tangentsTag.Chunk = AppendClonedVertexStream(tangentsTag.Chunk, "Tangents", oldVertexCount, 4,
                markerCloneCount, MARKER_VERTEX_START, quads.Count);

            int oldTriangleCount;
            int newTriangleCount;
            meshPartsTag.Chunk = BuildMeshPartsChunk(meshPartsTag.Chunk, model.Version, oldVertexCount,
                markerCloneCount, quads.Count, removeMarker, cloneMarkerToLeft,
                out oldTriangleCount, out newTriangleCount);

            TagEntry modelInfo = model.Find("ModelInfo");
            if (modelInfo != null)
                modelInfo.Chunk = PatchModelInfo(modelInfo.Chunk, newTriangleCount, newVertexCount);

            // Prevent switching back to the vanilla LOD chain at distance. I know this is bad for performance, but i don't want to deal with LOD now
            TagEntry lods = model.Find("LODs");
            if (lods != null)
                lods.Chunk = BuildEmptyLodsChunk();

            // Every generated custom-text model must remain directly clickable and use
            // the modern mesh-section outline highlight rather than the legacy dummy overlay.
            EnsureHighlightSection(model, GetMaterialIndexCount(meshPartsTag.Chunk, model.Version, SIGN_MATERIAL));
            EnsureDetectorDummy(model);

            byte[] generated = RebuildContainer(model);
            string fileName = "DynamicFloorPlan_" + Fnv1A(text).ToString("X8") + ".mwm";

            using (BinaryWriter writer = MyAPIGateway.Utilities.WriteBinaryFileInLocalStorage(fileName, storageType))
            {
                writer.Write(generated);
                writer.Flush();
            }

            string absolutePath = GetLocalStorageAbsolutePath(fileName);
            TextModelCache[text] = absolutePath;
            return absolutePath;
        }

        /// <summary>
        /// Creates a visually identical clone of a vanilla model with detector_dynsign_text_1
        /// appended to its Dummies tag. This is used to make untouched vanilla FloorPlan signs
        /// interactive before the player has assigned any custom text.
        /// </summary>
        public static string BuildDetectorModel(string sourceModelPath, Type storageType)
        {
            if (string.IsNullOrWhiteSpace(sourceModelPath))
                throw new ArgumentException("sourceModelPath");

            string cached;
            if (DetectorModelCache.TryGetValue(sourceModelPath, out cached))
                return cached;

            byte[] sourceBytes;
            using (BinaryReader reader = MyAPIGateway.Utilities.ReadBinaryFileInGameContent(sourceModelPath))
            {
                if (reader == null)
                    throw new FileNotFoundException("Could not open vanilla sign model: " + sourceModelPath);
                sourceBytes = ReadAllBytes(reader);
            }

            MwmContainer model = ParseContainer(sourceBytes);
            if (model.Version < INDEXED_TAG_VERSION)
                throw new InvalidOperationException("Vanilla sign MWM is not an indexed-tag model: " + sourceModelPath);

            int signMaterialIndexCount = ResolveSignMaterialIndexCount(model);
            EnsureHighlightSection(model, signMaterialIndexCount);
            EnsureDetectorDummy(model);
            byte[] generated = RebuildContainer(model);

            string fileName = "InteractiveFloorPlan_" + Fnv1A(sourceModelPath.ToUpperInvariant()).ToString("X8") + ".mwm";
            using (BinaryWriter writer = MyAPIGateway.Utilities.WriteBinaryFileInLocalStorage(fileName, storageType))
            {
                writer.Write(generated);
                writer.Flush();
            }

            string absolutePath = GetLocalStorageAbsolutePath(fileName);
            DetectorModelCache[sourceModelPath] = absolutePath;
            return absolutePath;
        }

        public static void ClearSessionCache(Type storageType)
        {
            TextModelCache.Clear();
            DetectorModelCache.Clear();

            if (MyAPIGateway.Utilities == null || MyAPIGateway.Utilities.GamePaths == null)
                return;

            // Do not trust only this session's in-memory list. Old game sessions, mod versions,
            // crashes, or disconnected clients may have left generated models behind. Scan this
            // mod's entire LocalStorage scope for MWM cache files and delete every one.
            string storageRoot = GetLocalStorageRoot();
            string[] files;
            try
            {
                files = PathUtils.GetFilesRecursively(storageRoot, "*.mwm");
            }
            catch (Exception e)
            {
                VRage.Utils.MyLog.Default.WriteLineAndConsole(
                    "[DynamicFloorPlanSign] failed to enumerate cached models: " + e);
                return;
            }

            if (files == null)
                return;

            for (int i = 0; i < files.Length; i++)
            {
                string fullPath = files[i];
                if (string.IsNullOrWhiteSpace(fullPath))
                    continue;

                string relativePath = MakeLocalStorageRelativePath(storageRoot, fullPath);
                if (string.IsNullOrWhiteSpace(relativePath))
                    continue;

                try
                {
                    MyAPIGateway.Utilities.DeleteFileInLocalStorage(relativePath, storageType);
                }
                catch (Exception e)
                {
                    VRage.Utils.MyLog.Default.WriteLineAndConsole(
                        "[DynamicFloorPlanSign] failed to delete cached model '" + relativePath + "': " + e);
                }
            }
        }

        static string GetLocalStorageRoot()
        {
            return Path.Combine(
                MyAPIGateway.Utilities.GamePaths.UserDataPath,
                "Storage",
                MyAPIGateway.Utilities.GamePaths.ModScopeName);
        }

        static string GetLocalStorageAbsolutePath(string fileName)
        {
            return Path.Combine(GetLocalStorageRoot(), fileName);
        }

        static string MakeLocalStorageRelativePath(string storageRoot, string fullPath)
        {
            if (string.IsNullOrEmpty(storageRoot) || string.IsNullOrEmpty(fullPath))
                return null;

            string root = PathUtils.Normalize(storageRoot);
            string path = PathUtils.Normalize(fullPath);

            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return null;

            int start = root.Length;
            while (start < path.Length && (path[start] == '\\' || path[start] == '/'))
                start++;

            return start < path.Length ? path.Substring(start) : null;
        }

        static byte[] ReadAllBytes(BinaryReader reader)
        {
            byte[] result = new byte[READ_CHUNK_SIZE];
            int length = 0;

            while (true)
            {
                if (result.Length - length < READ_CHUNK_SIZE)
                {
                    int next = result.Length << 1;
                    if (next < result.Length + READ_CHUNK_SIZE)
                        next = result.Length + READ_CHUNK_SIZE;
                    Array.Resize(ref result, next);
                }

                int read = reader.Read(result, length, READ_CHUNK_SIZE);
                if (read <= 0)
                    break;
                length += read;
            }

            if (length != result.Length)
                Array.Resize(ref result, length);
            return result;
        }

        static List<GlyphQuad> LayoutText(string text, SignTextAlignment alignment)
        {
            int newline = text.IndexOf('\n');
            if (newline < 0)
                return LayoutTextLine(text, alignment, TEXT_Y_MIN, TEXT_Y_MAX);

            List<GlyphQuad> result = new List<GlyphQuad>();
            string first = text.Substring(0, newline);
            string second = text.Substring(newline + 1);

            // Keep a small gutter between lines. First logical line is physically above the second.
            float mid = (TEXT_Y_MIN + TEXT_Y_MAX) * 0.5f;
            float lineGap = (TEXT_Y_MAX - TEXT_Y_MIN) * 0.08f;
            result.AddRange(LayoutTextLine(first, alignment, mid + lineGap * 0.5f, TEXT_Y_MAX));
            result.AddRange(LayoutTextLine(second, alignment, TEXT_Y_MIN, mid - lineGap * 0.5f));
            return result;
        }

        static List<GlyphQuad> LayoutTextLine(string text, SignTextAlignment alignment, float yMin, float yMax)
        {
            float maxHeight = yMax - yMin;
            float naturalWidth = 0f;
            int drawableCount = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == ' ')
                {
                    naturalWidth += maxHeight * 0.45f;
                    continue;
                }

                naturalWidth += maxHeight * GetCharacterAspect(c);
                drawableCount++;
            }

            if (drawableCount > 1)
                naturalWidth += LETTER_GAP * (drawableCount - 1);

            float layoutMin = TEXT_X_MIN;
            float layoutMax = TEXT_X_MAX;
            if (alignment == SignTextAlignment.Right)
            {
                layoutMin = MIRRORED_TEXT_X_MIN;
                layoutMax = MIRRORED_TEXT_X_MAX;
            }
            else if (alignment == SignTextAlignment.Center)
            {
                layoutMin = MIRRORED_TEXT_X_MIN;
                layoutMax = TEXT_X_MAX;
            }
            else if (alignment == SignTextAlignment.CenterFullWidth)
            {
                // With no marker, use the outer text bounds on both sides of the sign.
                layoutMin = TEXT_X_MIN;
                layoutMax = MIRRORED_TEXT_X_MAX;
            }

            float availableWidth = layoutMax - layoutMin;
            float scale = naturalWidth > availableWidth ? availableWidth / naturalWidth : 1f;
            float height = maxHeight * scale;
            float gap = LETTER_GAP * scale;

            float totalWidth = 0f;
            drawableCount = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == ' ')
                {
                    totalWidth += height * 0.45f;
                    continue;
                }

                totalWidth += height * GetCharacterAspect(c);
                drawableCount++;
            }
            if (drawableCount > 1)
                totalWidth += gap * (drawableCount - 1);

            float x = layoutMin;
            if (alignment == SignTextAlignment.Right)
                x = layoutMax - totalWidth;
            else if (alignment == SignTextAlignment.Center || alignment == SignTextAlignment.CenterFullWidth)
                x = layoutMin + (availableWidth - totalWidth) * 0.5f;

            float lineY0 = (yMin + yMax - height) * 0.5f;
            float lineY1 = lineY0 + height;
            List<GlyphQuad> result = new List<GlyphQuad>();

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == ' ')
                {
                    x += height * 0.45f;
                    continue;
                }

                float w = height * GetCharacterAspect(c);
                AppendCharacterQuads(result, c, x, x + w, lineY0, lineY1);
                x += w + gap;
            }

            return result;
        }

        static float GetCharacterAspect(char c)
        {
            Glyph g;
            if (Glyphs.TryGetValue(c, out g))
                return g.Aspect;

            switch (c)
            {
                case '|':
                    return 0.24f;
                case '/':
                case '\\':
                case '+':
                case '-':
                case '=':
                case '_':
                case '<':
                case '>':
                    return 0.68f;
                default:
                    throw new InvalidOperationException("No atlas glyph or runtime primitive for '" + c + "'.");
            }
        }

        static void AppendCharacterQuads(List<GlyphQuad> result, char c, float x0, float x1, float y0, float y1)
        {
            Glyph g;
            if (Glyphs.TryGetValue(c, out g))
            {
                float u0, v0, u1, v1;
                g.GetUv(out u0, out v0, out u1, out v1);
                result.Add(MakeQuad(x0, x1, y0, y1, u0, u1, v0, v1, 0f));
                return;
            }

            if (c == '<' || c == '>')
            {
                Glyph chevron = Glyphs['^'];
                float u0, v0, u1, v1;
                chevron.GetUv(out u0, out v0, out u1, out v1);

                float boxWidth = x1 - x0;
                float boxHeight = y1 - y0;
                float cx = (x0 + x1) * 0.5f;
                float cy = (y0 + y1) * 0.5f;

                // Build a compact upright chevron quad then rotate only its geometry.
                float preWidth = Math.Min(boxHeight * 0.82f, boxWidth * chevron.Aspect);
                float preHeight = preWidth / chevron.Aspect;
                float angle = c == '>' ? 90f : -90f;
                result.Add(MakeQuad(
                    cx - preWidth * 0.5f, cx + preWidth * 0.5f,
                    cy - preHeight * 0.5f, cy + preHeight * 0.5f,
                    u0, u1, v0, v1, angle));
                return;
            }

            // Primitives are synthesized from narrow textured rectangles. Reusing the
            // atlas 'I' keeps the material/alpha behavior identical without new DDS assets.
            Glyph strokeGlyph = Glyphs['I'];
            float su0, sv0, su1, sv1;
            strokeGlyph.GetUv(out su0, out sv0, out su1, out sv1);

            float width = x1 - x0;
            float height = y1 - y0;
            float centerX = (x0 + x1) * 0.5f;
            float centerY = (y0 + y1) * 0.5f;
            float thickness = height * 0.28f;
            float strokeLength = height * 0.95f;
            float horizontalLength = width * 0.88f;

            if (c == '|')
            {
                AddStroke(result, centerX, centerY, thickness, strokeLength, 0f, su0, su1, sv0, sv1);
            }
            else if (c == '/')
            {
                AddStroke(result, centerX, centerY, thickness, strokeLength, 30f, su0, su1, sv0, sv1);
            }
            else if (c == '\\')
            {
                AddStroke(result, centerX, centerY, thickness, strokeLength, -30f, su0, su1, sv0, sv1);
            }
            else if (c == '-')
            {
                AddStroke(result, centerX, centerY, thickness, horizontalLength, 90f, su0, su1, sv0, sv1);
            }
            else if (c == '_')
            {
                AddStroke(result, centerX, y0 + height * 0.10f, thickness, horizontalLength, 90f, su0, su1, sv0, sv1);
            }
            else if (c == '+')
            {
                // Use three adjoining rectangles so the center has only one surface.
                // Remove the I's side padding while preserving its visible stroke width,
                // and crop the horizontal UVs so the joins sample the middle of the I.
                float visibleThickness = thickness * (strokeGlyph.X1 - strokeGlyph.X0 + 1f)
                    / (strokeGlyph.X1 - strokeGlyph.X0 + 3f);
                float strokeU0 = strokeGlyph.X0 / ATLAS_SIZE;
                float strokeU1 = (strokeGlyph.X1 + 1f) / ATLAS_SIZE;
                float armLength = (horizontalLength - visibleThickness) * 0.5f;
                float armOffset = (horizontalLength + visibleThickness) * 0.25f;
                float armUvLength = (sv1 - sv0) * armLength / horizontalLength;

                AddStroke(result, centerX, centerY, visibleThickness, horizontalLength, 0f,
                    strokeU0, strokeU1, sv0, sv1);
                AddStroke(result, centerX - armOffset, centerY, visibleThickness, armLength, 90f,
                    strokeU0, strokeU1, sv1 - armUvLength, sv1);
                AddStroke(result, centerX + armOffset, centerY, visibleThickness, armLength, 90f,
                    strokeU0, strokeU1, sv0, sv0 + armUvLength);
            }
            else if (c == '=')
            {
                AddStroke(result, centerX, y0 + height * 0.35f, thickness, horizontalLength, 90f, su0, su1, sv0, sv1);
                AddStroke(result, centerX, y0 + height * 0.67f, thickness, horizontalLength, 90f, su0, su1, sv0, sv1);
            }
            else
            {
                throw new InvalidOperationException("Unsupported runtime primitive '" + c + "'.");
            }
        }

        static void AddStroke(List<GlyphQuad> result, float cx, float cy, float thickness, float length,
            float angle, float u0, float u1, float v0, float v1)
        {
            result.Add(MakeQuad(
                cx - thickness * 0.5f, cx + thickness * 0.5f,
                cy - length * 0.5f, cy + length * 0.5f,
                u0, u1, v0, v1, angle));
        }

        static GlyphQuad MakeQuad(float x0, float x1, float y0, float y1,
            float u0, float u1, float v0, float v1, float rotationDegrees)
        {
            GlyphQuad q = new GlyphQuad();
            q.X0 = x0;
            q.X1 = x1;
            q.Y0 = y0;
            q.Y1 = y1;
            q.U0 = u0;
            q.U1 = u1;
            q.V0 = v0;
            q.V1 = v1;
            q.RotationDegrees = rotationDegrees;
            return q;
        }

        static Dictionary<char, Glyph> BuildGlyphTable()
        {
            Dictionary<char, Glyph> d = new Dictionary<char, Glyph>();
            // Bounds measured from WarningSignsEaster_alphamask.DDS mip displayed at 1024x1024.
            // The two atlas glyphs before A are exposed as printable aliases for sign text.
            Add(d, '~', 296, 316, 891, 909); // up arrow
            Add(d, '^', 320, 339, 892, 909); // chevron

            Add(d, 'A', 298, 314, 914, 930);
            Add(d, 'B', 317, 329, 914, 930);
            Add(d, 'C', 332, 346, 914, 930);
            Add(d, 'D', 350, 364, 914, 930);
            Add(d, 'E', 367, 378, 914, 930);
            Add(d, 'F', 382, 392, 914, 930);
            Add(d, 'G', 394, 409, 914, 930);
            Add(d, 'H', 413, 427, 914, 930);
            Add(d, 'I', 431, 434, 914, 930);
            Add(d, 'J', 437, 446, 914, 930);
            Add(d, 'K', 450, 463, 914, 930);

            Add(d, 'L', 298, 308, 938, 954);
            Add(d, 'M', 311, 328, 938, 954);
            Add(d, 'N', 332, 346, 938, 954);
            Add(d, 'O', 350, 366, 938, 954);
            Add(d, 'P', 370, 382, 938, 954);
            Add(d, 'Q', 384, 401, 938, 957);
            Add(d, 'R', 405, 417, 938, 954);
            Add(d, 'S', 420, 432, 938, 954);
            Add(d, 'T', 435, 447, 938, 954);

            Add(d, 'U', 297, 311, 964, 980);
            Add(d, 'V', 314, 329, 964, 979);
            Add(d, 'W', 331, 355, 964, 979);
            Add(d, 'X', 357, 371, 964, 979);
            Add(d, 'Y', 374, 388, 964, 979);
            Add(d, 'Z', 391, 403, 964, 979);
            return d;
        }

        static void Add(Dictionary<char, Glyph> d, char c, int x0, int x1, int y0, int y1)
        {
            Glyph g = new Glyph();
            g.X0 = x0;
            g.X1 = x1;
            g.Y0 = y0;
            g.Y1 = y1;
            d[c] = g;
        }

        static void EnsureDetectorDummy(MwmContainer model)
        {
            TagEntry dummies = model.Find("Dummies");
            if (dummies == null)
            {
                int maxOffset = 0;
                for (int i = 0; i < model.Tags.Count; i++)
                    if (model.Tags[i].Offset > maxOffset)
                        maxOffset = model.Tags[i].Offset;

                dummies = new TagEntry
                {
                    Name = "Dummies",
                    Offset = maxOffset + 1,
                    Chunk = BuildDetectorOnlyDummiesChunk()
                };
                model.Tags.Add(dummies);
                return;
            }

            if (ContainsAscii(dummies.Chunk, DETECTOR_DUMMY_NAME))
                return;

            ByteReader r = new ByteReader(dummies.Chunk);
            if (!string.Equals(r.ReadString(), "Dummies", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected Dummies payload.");

            int oldCount = r.ReadInt32();
            int oldEntriesOffset = r.Position;

            ByteWriter w = new ByteWriter(dummies.Chunk.Length + 192);
            w.Write("Dummies");
            w.Write(oldCount + 1);
            w.Write(dummies.Chunk, oldEntriesOffset, dummies.Chunk.Length - oldEntriesOffset);
            WriteDetectorDummy(w);
            dummies.Chunk = w.ToArray();
        }

        static byte[] BuildDetectorOnlyDummiesChunk()
        {
            ByteWriter w = new ByteWriter(192);
            w.Write("Dummies");
            w.Write(1);
            WriteDetectorDummy(w);
            return w.ToArray();
        }

        static void WriteDetectorDummy(ByteWriter w)
        {
            w.Write(DETECTOR_DUMMY_NAME);
            for (int i = 0; i < DetectorMatrix.Length; i++)
                w.Write(DetectorMatrix[i]);

            // Match the modern UseObject highlight path: MyCharacterDetectorComponent looks for
            // dummy.CustomData["highlight"], then resolves that value through the model's Sections tag.
            w.Write(5);
            w.Write("UserProperties");
            w.Write(string.Empty);
            w.Write("IsNull");
            w.Write("True");
            w.Write("highlight");
            w.Write(HIGHLIGHT_SECTION_NAME);
            w.Write("DefaultAttributeIndex");
            w.Write("0");
            w.Write("InheritType");
            w.Write("1");
        }

        static void EnsureHighlightSection(MwmContainer model, int signMaterialIndexCount)
        {
            if (signMaterialIndexCount <= 0)
                throw new InvalidOperationException("WarningSignsEaster mesh part contains no indices.");

            TagEntry sections = model.Find("Sections");
            if (sections == null)
            {
                int maxOffset = 0;
                for (int i = 0; i < model.Tags.Count; i++)
                    if (model.Tags[i].Offset > maxOffset)
                        maxOffset = model.Tags[i].Offset;

                sections = new TagEntry
                {
                    Name = "Sections",
                    Offset = maxOffset + 1,
                    Chunk = BuildHighlightSectionsChunk(signMaterialIndexCount)
                };
                model.Tags.Add(sections);
                return;
            }

            if (ContainsAscii(sections.Chunk, HIGHLIGHT_SECTION_NAME))
                return;

            ByteReader r = new ByteReader(sections.Chunk);
            if (!string.Equals(r.ReadString(), "Sections", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected Sections payload.");

            int oldCount = r.ReadInt32();
            int oldEntriesOffset = r.Position;

            ByteWriter w = new ByteWriter(sections.Chunk.Length + 128);
            w.Write("Sections");
            w.Write(oldCount + 1);
            w.Write(sections.Chunk, oldEntriesOffset, sections.Chunk.Length - oldEntriesOffset);
            WriteHighlightSection(w, signMaterialIndexCount);
            sections.Chunk = w.ToArray();
        }

        static byte[] BuildHighlightSectionsChunk(int signMaterialIndexCount)
        {
            ByteWriter w = new ByteWriter(128);
            w.Write("Sections");
            w.Write(1);
            WriteHighlightSection(w, signMaterialIndexCount);
            return w.ToArray();
        }

        static void WriteHighlightSection(ByteWriter w, int signMaterialIndexCount)
        {
            w.Write(HIGHLIGHT_SECTION_NAME);
            w.Write(1);
            w.Write(SIGN_MATERIAL);
            w.Write(0);
            w.Write(signMaterialIndexCount);
        }

        static int ResolveSignMaterialIndexCount(MwmContainer model)
        {
            TagEntry meshParts = model.Find("MeshParts");
            if (meshParts != null)
                return GetMaterialIndexCount(meshParts.Chunk, model.Version, SIGN_MATERIAL);

            TagEntry geometryDataAsset = model.Find("GeometryDataAsset");
            if (geometryDataAsset == null)
                throw new InvalidOperationException("Model has neither MeshParts nor GeometryDataAsset.");

            ByteReader pathReader = new ByteReader(geometryDataAsset.Chunk);
            if (!string.Equals(pathReader.ReadString(), "GeometryDataAsset", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected GeometryDataAsset payload.");

            string geometryPath = pathReader.ReadString();
            if (!geometryPath.EndsWith(".mwm", StringComparison.OrdinalIgnoreCase))
                geometryPath += ".mwm";

            byte[] geometryBytes;
            using (BinaryReader reader = MyAPIGateway.Utilities.ReadBinaryFileInGameContent(geometryPath))
            {
                if (reader == null)
                    throw new FileNotFoundException("Could not open geometry data asset: " + geometryPath);
                geometryBytes = ReadAllBytes(reader);
            }

            MwmContainer geometry = ParseContainer(geometryBytes);
            TagEntry geometryMeshParts = Required(geometry, "MeshParts");
            return GetMaterialIndexCount(geometryMeshParts.Chunk, geometry.Version, SIGN_MATERIAL);
        }

        static int GetMaterialIndexCount(byte[] meshPartsChunk, int version, string materialName)
        {
            List<MeshPart> parts = ParseMeshParts(meshPartsChunk, version);
            for (int i = 0; i < parts.Count; i++)
            {
                if (string.Equals(parts[i].MaterialName, materialName, StringComparison.OrdinalIgnoreCase))
                    return parts[i].Indices.Length;
            }

            throw new InvalidOperationException("Mesh part was not found for material: " + materialName);
        }

        static bool ContainsAscii(byte[] data, string text)
        {
            if (data == null || text == null || text.Length == 0)
                return false;

            byte[] needle = Encoding.UTF8.GetBytes(text);
            if (needle.Length > data.Length)
                return false;

            for (int i = 0; i <= data.Length - needle.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && data[i + j] == needle[j])
                    j++;
                if (j == needle.Length)
                    return true;
            }
            return false;
        }

        static TagEntry Required(MwmContainer model, string name)
        {
            TagEntry tag = model.Find(name);
            if (tag == null)
                throw new InvalidOperationException("Template missing required MWM tag: " + name);
            return tag;
        }

        static MwmContainer ParseContainer(byte[] bytes)
        {
            MwmContainer result = new MwmContainer();
            ByteReader reader = new ByteReader(bytes);

            result.DebugTag = reader.ReadString();
            if (!string.Equals(result.DebugTag, "Debug", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected MWM header.");

            int debugCount = reader.ReadInt32();
            result.DebugLines = new string[debugCount];
            for (int i = 0; i < debugCount; i++)
            {
                result.DebugLines[i] = reader.ReadString();
                if (result.DebugLines[i].StartsWith("Version:", StringComparison.Ordinal))
                    int.TryParse(result.DebugLines[i].Substring("Version:".Length), out result.Version);
            }

            int tagCount = reader.ReadInt32();
            for (int i = 0; i < tagCount; i++)
            {
                TagEntry tag = new TagEntry();
                tag.Name = reader.ReadString();
                tag.Offset = reader.ReadInt32();
                result.Tags.Add(tag);
            }

            List<TagEntry> physical = new List<TagEntry>(result.Tags);
            physical.Sort(delegate(TagEntry a, TagEntry b) { return a.Offset.CompareTo(b.Offset); });
            for (int i = 0; i < physical.Count; i++)
            {
                int start = physical[i].Offset;
                int end = i + 1 < physical.Count ? physical[i + 1].Offset : bytes.Length;
                if (start < 0 || end < start || end > bytes.Length)
                    throw new InvalidOperationException("Invalid MWM tag offsets.");
                byte[] chunk = new byte[end - start];
                Buffer.BlockCopy(bytes, start, chunk, 0, chunk.Length);
                physical[i].Chunk = chunk;
            }

            return result;
        }

        static byte[] RebuildContainer(MwmContainer model)
        {
            ByteWriter writer = new ByteWriter();
            writer.Write(model.DebugTag);
            writer.Write(model.DebugLines.Length);
            for (int i = 0; i < model.DebugLines.Length; i++)
                writer.Write(model.DebugLines[i]);

            writer.Write(model.Tags.Count);
            for (int i = 0; i < model.Tags.Count; i++)
            {
                writer.Write(model.Tags[i].Name);
                model.Tags[i].OffsetPatchPosition = writer.Position;
                writer.Write(0);
            }

            List<TagEntry> physical = new List<TagEntry>(model.Tags);
            physical.Sort(delegate(TagEntry a, TagEntry b) { return a.Offset.CompareTo(b.Offset); });
            Dictionary<string, int> newOffsets = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < physical.Count; i++)
            {
                newOffsets[physical[i].Name] = writer.Position;
                writer.Write(physical[i].Chunk);
            }

            for (int i = 0; i < model.Tags.Count; i++)
                writer.PatchInt32(model.Tags[i].OffsetPatchPosition, newOffsets[model.Tags[i].Name]);

            return writer.ToArray();
        }

        static ulong[] ReadUInt64Stream(byte[] chunk, string expectedTag, out int count)
        {
            ByteReader r = new ByteReader(chunk);
            if (r.ReadString() != expectedTag)
                throw new InvalidOperationException("Unexpected tag payload: " + expectedTag);
            count = r.ReadInt32();
            ulong[] values = new ulong[count];
            for (int i = 0; i < count; i++)
                values[i] = r.ReadUInt64();
            return values;
        }

        static uint[] ReadUInt32Stream(byte[] chunk, string expectedTag, out int count)
        {
            ByteReader r = new ByteReader(chunk);
            if (r.ReadString() != expectedTag)
                throw new InvalidOperationException("Unexpected tag payload: " + expectedTag);
            count = r.ReadInt32();
            uint[] values = new uint[count];
            for (int i = 0; i < count; i++)
                values[i] = r.ReadUInt32();
            return values;
        }

        static bool IsMarkerVertex(int index)
        {
            return (index >= MARKER_VERTEX_START && index < MARKER_VERTEX_START + MARKER_ARROW_VERTEX_COUNT)
                || (index >= MARKER_BRACKET_VERTEX_START && index <= MARKER_VERTEX_END);
        }

        static bool IsMarkerCloneOffset(int offset)
        {
            int sourceIndex = MARKER_VERTEX_START + offset;
            return IsMarkerVertex(sourceIndex);
        }

        static T[] Slice<T>(T[] source, int start, int count)
        {
            T[] result = new T[count];
            Array.Copy(source, start, result, 0, count);
            return result;
        }

        static void ApplyMarkerGeometry(ulong[] vertices, uint[] uvs, int markerStart, SignMarker marker, float dx)
        {
            if (marker == null)
                return;

            for (int i = 0; i < MARKER_VERTEX_COUNT; i++)
            {
                // The cloned buffer is intentionally contiguous so existing index remapping stays simple,
                // but 2497..2516 are outer-frame vertices and must remain untouched/unreferenced.
                bool markerVertex = markerStart == MARKER_VERTEX_START
                    ? IsMarkerVertex(markerStart + i)
                    : IsMarkerCloneOffset(i);
                if (!markerVertex)
                    continue;

                int index = markerStart + i;
                float x, y, z;
                ReadPosition(vertices[index], out x, out y, out z);
                vertices[index] = PackPosition(vertices[index], x + dx, y, z);
            }

            if (marker.Arrow)
            {
                Glyph glyph = Glyphs['~'];
                float u0, v0, u1, v1;
                glyph.GetUv(out u0, out v0, out u1, out v1);
                // Vanilla arrow quad order: TL, BR, TR, BL. Chevron keeps the original vanilla UVs.
                uvs[markerStart + 0] = PackHalf2(u0, v0);
                uvs[markerStart + 1] = PackHalf2(u1, v1);
                uvs[markerStart + 2] = PackHalf2(u1, v0);
                uvs[markerStart + 3] = PackHalf2(u0, v1);
            }

            if (marker.Angle == 0)
                return;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < MARKER_ARROW_VERTEX_COUNT; i++)
            {
                float x, y, z;
                ReadPosition(vertices[markerStart + i], out x, out y, out z);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            float cx = (minX + maxX) * 0.5f;
            float cy = (minY + maxY) * 0.5f;
            // Positive user angles are clockwise when looking at the sign.
            float radians = -marker.Angle * (float)Math.PI / 180f;
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);

            for (int i = 0; i < MARKER_ARROW_VERTEX_COUNT; i++)
            {
                int index = markerStart + i;
                float x, y, z;
                ReadPosition(vertices[index], out x, out y, out z);
                float ox = x - cx;
                float oy = y - cy;
                float rx = cx + ox * cos - oy * sin;
                float ry = cy + ox * sin + oy * cos;
                vertices[index] = PackPosition(vertices[index], rx, ry, z);
            }
        }

        static void ReadPosition(ulong packed, out float x, out float y, out float z)
        {
            x = HalfToFloat((ushort)(packed & 0xffff));
            y = HalfToFloat((ushort)((packed >> 16) & 0xffff));
            z = HalfToFloat((ushort)((packed >> 32) & 0xffff));
        }

        static ulong PackPosition(ulong source, float x, float y, float z)
        {
            ulong packed = source & 0xffff000000000000UL;
            packed |= FloatToHalf(x);
            packed |= (ulong)FloatToHalf(y) << 16;
            packed |= (ulong)FloatToHalf(z) << 32;
            return packed;
        }

        static byte[] BuildVerticesChunk(ulong[] oldVertices, ulong[] markerCloneVertices, List<GlyphQuad> quads)
        {
            int markerCloneCount = markerCloneVertices == null ? 0 : markerCloneVertices.Length;
            ByteWriter w = new ByteWriter();
            w.Write("Vertices");
            w.Write(oldVertices.Length + markerCloneCount + quads.Count * 4);
            for (int i = 0; i < oldVertices.Length; i++)
                w.Write(oldVertices[i]);
            if (markerCloneVertices != null)
                for (int i = 0; i < markerCloneVertices.Length; i++)
                    w.Write(markerCloneVertices[i]);

            for (int i = 0; i < quads.Count; i++)
            {
                GlyphQuad q = quads[i];
                WriteQuadPosition(w, q, q.X0, q.Y1, oldVertices[CornerSourceVertices[0]]); // TL
                WriteQuadPosition(w, q, q.X1, q.Y1, oldVertices[CornerSourceVertices[1]]); // TR
                WriteQuadPosition(w, q, q.X0, q.Y0, oldVertices[CornerSourceVertices[2]]); // BL
                WriteQuadPosition(w, q, q.X1, q.Y0, oldVertices[CornerSourceVertices[3]]); // BR
            }
            return w.ToArray();
        }

        static void WriteQuadPosition(ByteWriter w, GlyphQuad q, float x, float y, ulong source)
        {
            if (q.RotationDegrees != 0f)
            {
                float cx = (q.X0 + q.X1) * 0.5f;
                float cy = (q.Y0 + q.Y1) * 0.5f;
                float radians = -q.RotationDegrees * (float)Math.PI / 180f;
                float cos = (float)Math.Cos(radians);
                float sin = (float)Math.Sin(radians);
                float ox = x - cx;
                float oy = y - cy;
                float rx = cx + ox * cos - oy * sin;
                float ry = cy + ox * sin + oy * cos;
                x = rx;
                y = ry;
            }

            WritePosition(w, x, y, TEXT_Z, source);
        }

        static void WritePosition(ByteWriter w, float x, float y, float z, ulong source)
        {
            ulong packed = source & 0xffff000000000000UL;
            packed |= FloatToHalf(x);
            packed |= (ulong)FloatToHalf(y) << 16;
            packed |= (ulong)FloatToHalf(z) << 32;
            w.Write(packed);
        }

        static byte[] BuildTexCoordsChunk(uint[] oldUvs, uint[] markerCloneUvs, List<GlyphQuad> quads)
        {
            int markerCloneCount = markerCloneUvs == null ? 0 : markerCloneUvs.Length;
            ByteWriter w = new ByteWriter();
            w.Write("TexCoords0");
            w.Write(oldUvs.Length + markerCloneCount + quads.Count * 4);
            for (int i = 0; i < oldUvs.Length; i++)
                w.Write(oldUvs[i]);
            if (markerCloneUvs != null)
                for (int i = 0; i < markerCloneUvs.Length; i++)
                    w.Write(markerCloneUvs[i]);

            for (int i = 0; i < quads.Count; i++)
            {
                GlyphQuad q = quads[i];
                w.Write(PackHalf2(q.U0, q.V0)); // TL
                w.Write(PackHalf2(q.U1, q.V0)); // TR
                w.Write(PackHalf2(q.U0, q.V1)); // BL
                w.Write(PackHalf2(q.U1, q.V1)); // BR
            }
            return w.ToArray();
        }

        static byte[] AppendClonedVertexStream(byte[] chunk, string expectedTag, int expectedCount, int elementSize,
            int markerCloneCount, int markerSourceStart, int glyphCount)
        {
            ByteReader r = new ByteReader(chunk);
            if (r.ReadString() != expectedTag)
                throw new InvalidOperationException("Unexpected tag payload: " + expectedTag);
            int count = r.ReadInt32();
            if (count != expectedCount)
                throw new InvalidOperationException(expectedTag + " count does not match Vertices.");

            byte[] data = r.ReadBytes(count * elementSize);
            ByteWriter w = new ByteWriter(chunk.Length + (markerCloneCount + glyphCount * 4) * elementSize + 32);
            w.Write(expectedTag);
            w.Write(count + markerCloneCount + glyphCount * 4);
            w.Write(data);

            for (int i = 0; i < markerCloneCount; i++)
            {
                int sourceIndex = markerSourceStart + i;
                w.Write(data, sourceIndex * elementSize, elementSize);
            }

            for (int g = 0; g < glyphCount; g++)
            {
                for (int corner = 0; corner < 4; corner++)
                {
                    int sourceIndex = CornerSourceVertices[corner];
                    w.Write(data, sourceIndex * elementSize, elementSize);
                }
            }
            return w.ToArray();
        }

        static byte[] BuildMeshPartsChunk(byte[] chunk, int version, int oldVertexCount, int markerCloneCount,
            int glyphCount, bool removeMarker, bool cloneMarkerToLeft,
            out int oldTriangleCount, out int newTriangleCount)
        {
            List<MeshPart> parts = ParseMeshParts(chunk, version);
            oldTriangleCount = 0;
            newTriangleCount = 0;
            bool replaced = false;

            for (int i = 0; i < parts.Count; i++)
            {
                MeshPart p = parts[i];
                oldTriangleCount += p.Indices.Length / 3;

                if (string.Equals(p.MaterialName, SIGN_MATERIAL, StringComparison.OrdinalIgnoreCase))
                {
                    int removeIndexCount = TEMPLATE_GLYPH_COUNT * 2 * 3;
                    if (p.Indices.Length < removeIndexCount)
                        throw new InvalidOperationException("WarningSignsEaster mesh does not contain the expected template glyph tail.");

                    int prefixCount = p.Indices.Length - removeIndexCount;
                    List<int> replacement = new List<int>(prefixCount + markerCloneCount * 3 + glyphCount * 6);

                    for (int idx = 0; idx < prefixCount; idx += 3)
                    {
                        int a = p.Indices[idx];
                        int b = p.Indices[idx + 1];
                        int c = p.Indices[idx + 2];
                        bool markerTriangle = IsMarkerVertex(a) && IsMarkerVertex(b) && IsMarkerVertex(c);
                        if (!removeMarker || !markerTriangle)
                        {
                            replacement.Add(a);
                            replacement.Add(b);
                            replacement.Add(c);
                        }

                        if (cloneMarkerToLeft && markerTriangle)
                        {
                            replacement.Add(oldVertexCount + (a - MARKER_VERTEX_START));
                            replacement.Add(oldVertexCount + (b - MARKER_VERTEX_START));
                            replacement.Add(oldVertexCount + (c - MARKER_VERTEX_START));
                        }
                    }

                    int glyphVertexStart = oldVertexCount + markerCloneCount;
                    for (int g = 0; g < glyphCount; g++)
                    {
                        int b = glyphVertexStart + g * 4;
                        replacement.Add(b + 0);
                        replacement.Add(b + 1);
                        replacement.Add(b + 2);
                        replacement.Add(b + 1);
                        replacement.Add(b + 3);
                        replacement.Add(b + 2);
                    }

                    p.Indices = replacement.ToArray();
                    replaced = true;
                }

                newTriangleCount += p.Indices.Length / 3;
            }

            if (!replaced)
                throw new InvalidOperationException("WarningSignsEaster mesh part was not found.");

            ByteWriter w = new ByteWriter(chunk.Length + glyphCount * 24 + 128);
            w.Write("MeshParts");
            w.Write(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                MeshPart p = parts[i];
                w.Write(p.Header);
                if (p.HasOldHeader)
                    w.Write(p.OldHeader);
                w.Write(p.Indices.Length);
                for (int j = 0; j < p.Indices.Length; j++)
                    w.Write(p.Indices[j]);
                w.Write(p.HasMaterial);
                if (p.HasMaterial)
                    w.Write(p.MaterialDescriptor);
            }
            return w.ToArray();
        }

        static List<MeshPart> ParseMeshParts(byte[] chunk, int version)
        {
            List<MeshPart> parts = new List<MeshPart>();
            ByteReader r = new ByteReader(chunk);
            if (r.ReadString() != "MeshParts")
                throw new InvalidOperationException("Unexpected MeshParts payload.");

            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                MeshPart p = new MeshPart();
                p.Header = r.ReadInt32();
                if (version < 1052001)
                {
                    p.HasOldHeader = true;
                    p.OldHeader = r.ReadInt32();
                }

                int indexCount = r.ReadInt32();
                p.Indices = new int[indexCount];
                for (int j = 0; j < indexCount; j++)
                    p.Indices[j] = r.ReadInt32();
                p.HasMaterial = r.ReadBoolean();
                if (p.HasMaterial)
                {
                    int start = r.Position;
                    p.MaterialName = ReadMaterialDescriptor(r, version);
                    int end = r.Position;
                    p.MaterialDescriptor = new byte[end - start];
                    Buffer.BlockCopy(chunk, start, p.MaterialDescriptor, 0, p.MaterialDescriptor.Length);
                }
                parts.Add(p);
            }
            return parts;
        }

        static string ReadMaterialDescriptor(ByteReader reader, int version)
        {
            string materialName = reader.ReadString();
            if (version < 1052002)
            {
                reader.ReadString();
                reader.ReadString();
            }
            else
            {
                SkipStringDictionary(reader);
            }

            if (version >= 1068001)
                SkipStringDictionary(reader);

            if (version < 1157001)
                reader.Skip(7 * 4);

            string technique = version < 1052001 ? reader.ReadInt32().ToString() : reader.ReadString();
            if (technique == "GLASS")
            {
                if (version >= 1043001)
                {
                    reader.ReadString();
                    reader.ReadString();
                    reader.ReadBoolean();
                }
                else
                {
                    reader.Skip(4 * 4);
                }
            }
            return materialName;
        }

        static void SkipStringDictionary(ByteReader reader)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                reader.ReadString();
                reader.ReadString();
            }
        }

        static byte[] PatchModelInfo(byte[] chunk, int triangleCount, int vertexCount)
        {
            ByteReader r = new ByteReader(chunk);
            if (r.ReadString() != "ModelInfo")
                return chunk;

            r.ReadInt32(); // old triangle count
            r.ReadInt32(); // old vertex count
            byte[] tail = r.ReadRemainingBytes();

            ByteWriter w = new ByteWriter(chunk.Length);
            w.Write("ModelInfo");
            w.Write(triangleCount);
            w.Write(vertexCount);
            w.Write(tail);
            return w.ToArray();
        }

        static byte[] BuildEmptyLodsChunk()
        {
            ByteWriter w = new ByteWriter(16);
            w.Write("LODs");
            w.Write(0);
            return w.ToArray();
        }

        static uint PackHalf2(float x, float y)
        {
            return FloatToHalf(x) | ((uint)FloatToHalf(y) << 16);
        }

        static float HalfToFloat(ushort value)
        {
            uint sign = (uint)(value & 0x8000) << 16;
            int exponent = (value >> 10) & 0x1f;
            uint mantissa = (uint)value & 0x3ff;
            uint bits;

            if (exponent == 0)
            {
                if (mantissa == 0)
                {
                    bits = sign;
                }
                else
                {
                    int e = -14;
                    while ((mantissa & 0x400) == 0)
                    {
                        mantissa <<= 1;
                        e--;
                    }
                    mantissa &= 0x3ff;
                    bits = sign | (uint)((e + 127) << 23) | (mantissa << 13);
                }
            }
            else if (exponent == 31)
            {
                bits = sign | 0x7f800000u | (mantissa << 13);
            }
            else
            {
                bits = sign | (uint)((exponent - 15 + 127) << 23) | (mantissa << 13);
            }

            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        static ushort FloatToHalf(float value)
        {
            uint f = BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
            uint sign = (f >> 16) & 0x8000u;
            uint mantissa = f & 0x007fffffu;
            int exponent = (int)((f >> 23) & 0xffu) - 127 + 15;

            if (exponent <= 0)
            {
                if (exponent < -10) return (ushort)sign;
                mantissa = (mantissa | 0x00800000u) >> (1 - exponent);
                if ((mantissa & 0x00001000u) != 0) mantissa += 0x00002000u;
                return (ushort)(sign | (mantissa >> 13));
            }

            if (exponent >= 31)
                return (ushort)(sign | 0x7c00u);

            if ((mantissa & 0x00001000u) != 0)
            {
                mantissa += 0x00002000u;
                if ((mantissa & 0x00800000u) != 0)
                {
                    mantissa = 0;
                    exponent++;
                    if (exponent >= 31) return (ushort)(sign | 0x7c00u);
                }
            }

            return (ushort)(sign | ((uint)exponent << 10) | (mantissa >> 13));
        }

        static uint Fnv1A(string value)
        {
            const uint offset = 2166136261u;
            const uint prime = 16777619u;
            uint hash = offset;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                hash ^= (byte)(c & 0xff);
                hash *= prime;
                hash ^= (byte)(c >> 8);
                hash *= prime;
            }
            return hash;
        }
    }
}
