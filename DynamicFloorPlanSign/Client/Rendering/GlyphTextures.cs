using System;
using System.Collections.Generic;
using System.IO;
using Adk.Image;
using Adk.Image.Dds;
using Adk.Utils;
using DynamicFloorPlanSign.Common.Fonts;
using Sandbox.ModAPI;
using VRage.Utils;

namespace DynamicFloorPlanSign.Client.Rendering
{
    internal static class GlyphTextures
    {
        const string COLOR_METAL_FILE = "SignGlyph_cm.dds";
        const string ADD_MAPS_FILE = "SignGlyph_add.dds";
        const string NORMAL_GLOSS_FILE = "SignGlyph_ng.dds";
        const string MASK_PREFIX = "SignFontMask_";

        sealed class MaskJob
        {
            public SignFontBitmap Bitmap;
            public Type StorageType;
            public string FileName;
            public Exception Error;
            public readonly List<Action> Waiters = new List<Action>();
        }

        static readonly Dictionary<SignFontBitmap, string> Masks = new Dictionary<SignFontBitmap, string>();
        static readonly Dictionary<SignFontBitmap, MaskJob> Pending = new Dictionary<SignFontBitmap, MaskJob>();
        static readonly HashSet<SignFontBitmap> Failed = new HashSet<SignFontBitmap>();
        static readonly Dictionary<string, string> Solids = new Dictionary<string, string>(StringComparer.Ordinal);

        public static string ColorMetal(Type storageType)
        {
            return EnsureSolid(COLOR_METAL_FILE, 0, 0, 0, 33, SpaceEngineersTextureKind.ColorMetal, storageType);
        }

        public static string AddMaps(Type storageType)
        {
            return EnsureSolid(ADD_MAPS_FILE, 255, 0, 255, 0, SpaceEngineersTextureKind.Add, storageType);
        }

        public static string NormalGloss(Type storageType)
        {
            return EnsureSolid(NORMAL_GLOSS_FILE, 128, 128, 255, 200, SpaceEngineersTextureKind.NormalGloss, storageType);
        }

        /// <summary>
        /// Returns the alphamask for <paramref name="bitmap"/> when it is ready. Otherwise starts (or joins) its
        /// background generation, calls <paramref name="onReady"/> on the main thread once it finishes, and
        /// returns false. Bitmaps whose generation failed stay unavailable for the session.
        /// </summary>
        public static bool TryGetMask(SignFontBitmap bitmap, Type storageType, Action onReady, out string path)
        {
            if (Masks.TryGetValue(bitmap, out path))
                return true;

            path = null;
            if (Failed.Contains(bitmap))
                return false;

            MaskJob job;
            if (!Pending.TryGetValue(bitmap, out job))
            {
                job = new MaskJob { Bitmap = bitmap, StorageType = storageType };
                Pending[bitmap] = job;
                MyAPIGateway.Parallel.StartBackground(() => GenerateMask(job), () => CompleteMask(job));
            }

            if (onReady != null)
                job.Waiters.Add(onReady);
            return false;
        }

        public static bool IsUnavailable(SignFontBitmap bitmap)
        {
            return Failed.Contains(bitmap);
        }

        /// <summary>Forgets this session's textures; the files themselves are deleted with the models.</summary>
        public static void ClearSession()
        {
            Masks.Clear();
            Pending.Clear();
            Failed.Clear();
            Solids.Clear();
        }

        static void GenerateMask(MaskJob job)
        {
            try
            {
                SignFontBitmap bitmap = job.Bitmap;
                job.FileName = MASK_PREFIX + SafeFileName(FileStem(bitmap.Path)) + "_d" + bitmap.Font.MaskDilation
                    + "_" + SafeFileName(bitmap.Font.Name) + ".dds";

                byte[] source = ReadBitmapBytes(bitmap);
                RawRgbaBitmap decoded = DdsDecoder.Decode(source);
                byte[] pixels = decoded.Pixels;
                byte[] mask = new byte[decoded.Width * decoded.Height];
                for (int i = 0; i < mask.Length; i++)
                    mask[i] = pixels[i * 4 + 3];

                mask = Dilate(mask, decoded.Width, decoded.Height, job.Bitmap.Font.MaskDilation);

                for (int i = 0; i < mask.Length; i++)
                {
                    pixels[i * 4] = mask[i];
                    pixels[i * 4 + 1] = mask[i];
                    pixels[i * 4 + 2] = mask[i];
                    pixels[i * 4 + 3] = 255;
                }

                DdsEncodeOptions options = new DdsEncodeOptions { Format = DdsOutputFormat.Bc7Typeless };
                WriteStorageFile(job.FileName, DdsEncoder.Encode(decoded, options), job.StorageType);
            }
            catch (Exception e)
            {
                job.Error = e;
            }
        }

        static void CompleteMask(MaskJob job)
        {
            MaskJob current;
            if (!Pending.TryGetValue(job.Bitmap, out current) || current != job)
                return; // The session was cleared while the job ran.
            Pending.Remove(job.Bitmap);

            if (job.Error != null)
            {
                Failed.Add(job.Bitmap);
                LogHelper.Log(MyLogSeverity.Error, "failed to generate the alphamask for sign font '"
                    + job.Bitmap.Font.Name + "' bitmap " + job.Bitmap.Path + ": " + job.Error);
            }
            else
            {
                Masks[job.Bitmap] = RuntimeMwmBuilder.GetLocalStorageAbsolutePath(job.FileName);
            }

            for (int i = 0; i < job.Waiters.Count; i++)
            {
                try
                {
                    job.Waiters[i]();
                }
                catch (Exception e)
                {
                    LogHelper.Log(MyLogSeverity.Error, "glyph texture callback failed: " + e);
                }
            }
        }

        static byte[] Dilate(byte[] values, int width, int height, int radius)
        {
            if (radius <= 0)
                return values;

            byte[] horizontal = new byte[values.Length];
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int x0 = Math.Max(0, x - radius);
                    int x1 = Math.Min(width - 1, x + radius);
                    byte max = 0;
                    for (int k = x0; k <= x1; k++)
                        if (values[row + k] > max)
                            max = values[row + k];
                    horizontal[row + x] = max;
                }
            }

            byte[] result = new byte[values.Length];
            for (int y = 0; y < height; y++)
            {
                int y0 = Math.Max(0, y - radius);
                int y1 = Math.Min(height - 1, y + radius);
                for (int x = 0; x < width; x++)
                {
                    byte max = 0;
                    for (int k = y0; k <= y1; k++)
                        if (horizontal[k * width + x] > max)
                            max = horizontal[k * width + x];
                    result[y * width + x] = max;
                }
            }
            return result;
        }

        static byte[] ReadBitmapBytes(SignFontBitmap bitmap)
        {
            BinaryReader reader = bitmap.InMod
                ? MyAPIGateway.Utilities.ReadBinaryFileInModLocation(bitmap.Path, bitmap.Font.ModItem)
                : MyAPIGateway.Utilities.ReadBinaryFileInGameContent(bitmap.Path);
            if (reader == null)
                throw new FileNotFoundException("Sign font bitmap not found: " + bitmap.Path);

            using (reader)
                return RuntimeMwmBuilder.ReadAllBytes(reader);
        }

        static string EnsureSolid(string fileName, byte r, byte g, byte b, byte a, SpaceEngineersTextureKind kind,
            Type storageType)
        {
            string path;
            if (Solids.TryGetValue(fileName, out path))
                return path;

            RawRgbaBitmap solid = new RawRgbaBitmap(4, 4);
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                    solid.SetPixel(x, y, r, g, b, a);
            WriteStorageFile(fileName, DdsEncoder.Encode(solid, SpaceEngineersTextureProfiles.CreateTexture(kind)), storageType);

            path = RuntimeMwmBuilder.GetLocalStorageAbsolutePath(fileName);
            Solids[fileName] = path;
            return path;
        }

        static void WriteStorageFile(string fileName, byte[] data, Type storageType)
        {
            using (BinaryWriter writer = MyAPIGateway.Utilities.WriteBinaryFileInLocalStorage(fileName, storageType))
            {
                writer.Write(data);
                writer.Flush();
            }
        }

        static string FileStem(string path)
        {
            int slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            string name = path.Substring(slash + 1);
            int dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        static string SafeFileName(string value)
        {
            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_'))
                    chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
