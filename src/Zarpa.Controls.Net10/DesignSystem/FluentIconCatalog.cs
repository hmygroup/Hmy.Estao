using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ZarpaSuite.Controls
{
    public sealed class FluentIconInfo
    {
        internal FluentIconInfo(string key, int codePoint)
        {
            Key = key;
            CodePoint = codePoint;
            DisplayName = FormatDisplayName(key);
        }

        public string Key { get; private set; }
        public int CodePoint { get; private set; }
        public string DisplayName { get; private set; }

        public override string ToString()
        {
            return DisplayName;
        }

        private static string FormatDisplayName(string key)
        {
            string value = key;
            const string prefix = "ic_fluent_";
            const string suffix = "_regular";
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                value = value.Substring(prefix.Length);
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.Length - suffix.Length);
            return value.Replace('_', ' ');
        }
    }

    public static class FluentIconCatalog
    {
        private const string FontResource = "TestRibbon.Assets.FluentSystemIcons-Regular.ttf";
        private const string CatalogResource = "TestRibbon.Assets.FluentSystemIcons-Regular.json";
        private static readonly object syncRoot = new object();
        private static List<FluentIconInfo> icons;
        private static Dictionary<string, FluentIconInfo> iconsByKey;
        private static readonly Dictionary<IconCacheKey, Bitmap> renderedIcons =
            new Dictionary<IconCacheKey, Bitmap>();
        private static PrivateFontCollection fontCollection;
        private static IntPtr fontMemory;

        public static IList<FluentIconInfo> Icons
        {
            get
            {
                EnsureLoaded();
                return icons.AsReadOnly();
            }
        }

        public static IEnumerable<FluentIconInfo> Search(string text)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(text))
                return icons;

            string[] words = text.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return icons.Where(icon => words.All(word =>
                icon.DisplayName.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        public static bool TryDraw(Graphics graphics, string key, Rectangle bounds, Color color, float size)
        {
            if (graphics == null || string.IsNullOrEmpty(key) || bounds.Width <= 0 || bounds.Height <= 0)
                return false;

            EnsureLoaded();
            FluentIconInfo icon;
            if (!iconsByKey.TryGetValue(key, out icon) || fontCollection.Families.Length == 0)
                return false;

            IconCacheKey cacheKey = new IconCacheKey(key, bounds.Width, bounds.Height, color.ToArgb(), size);
            Bitmap bitmap;
            lock (syncRoot)
            {
                if (!renderedIcons.TryGetValue(cacheKey, out bitmap))
                {
                    if (renderedIcons.Count >= 512)
                    {
                        foreach (Bitmap cached in renderedIcons.Values)
                            cached.Dispose();
                        renderedIcons.Clear();
                    }
                    bitmap = RenderIcon(icon, bounds.Size, color, size);
                    renderedIcons.Add(cacheKey, bitmap);
                }
            }
            graphics.DrawImageUnscaled(bitmap, bounds.Location);
            return true;
        }

        private static Bitmap RenderIcon(FluentIconInfo icon, Size bitmapSize, Color color, float size)
        {
            Bitmap bitmap = new Bitmap(bitmapSize.Width, bitmapSize.Height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Font font = new Font(fontCollection.Families[0], size, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush brush = new SolidBrush(color))
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
            {
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags |= StringFormatFlags.NoClip;
                graphics.DrawString(char.ConvertFromUtf32(icon.CodePoint), font, brush,
                    new Rectangle(Point.Empty, bitmapSize), format);
            }
            return bitmap;
        }

        private struct IconCacheKey : IEquatable<IconCacheKey>
        {
            private readonly string key;
            private readonly int width;
            private readonly int height;
            private readonly int argb;
            private readonly int sizeBits;

            internal IconCacheKey(string key, int width, int height, int argb, float size)
            {
                this.key = key;
                this.width = width;
                this.height = height;
                this.argb = argb;
                sizeBits = BitConverter.ToInt32(BitConverter.GetBytes(size), 0);
            }

            public bool Equals(IconCacheKey other)
            {
                return width == other.width && height == other.height && argb == other.argb &&
                    sizeBits == other.sizeBits && string.Equals(key, other.key, StringComparison.OrdinalIgnoreCase);
            }

            public override bool Equals(object obj)
            {
                return obj is IconCacheKey && Equals((IconCacheKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(key);
                    hash = hash * 397 ^ width;
                    hash = hash * 397 ^ height;
                    hash = hash * 397 ^ argb;
                    return hash * 397 ^ sizeBits;
                }
            }
        }

        private static void EnsureLoaded()
        {
            if (icons != null)
                return;

            lock (syncRoot)
            {
                if (icons != null)
                    return;

                Assembly assembly = typeof(FluentIconCatalog).Assembly;
                Dictionary<string, int> raw;
                using (Stream stream = assembly.GetManifestResourceStream(CatalogResource))
                {
                    if (stream == null)
                        throw new InvalidOperationException("No se encuentra el catálogo embebido de Fluent Icons.");
                    using (StreamReader reader = new StreamReader(stream))
                        raw = ReadCatalog(reader);
                }

                icons = raw.Select(pair => new FluentIconInfo(pair.Key, pair.Value))
                    .OrderBy(icon => icon.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                iconsByKey = icons.ToDictionary(icon => icon.Key, StringComparer.OrdinalIgnoreCase);
                LoadFont(assembly);
            }
        }

        private static Dictionary<string, int> ReadCatalog(StreamReader reader)
        {
            Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string line;
            int lineNumber = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                line = line.Trim();
                if (line.Length == 0 || line == "{" || line == "}")
                    continue;
                if (line.EndsWith(",", StringComparison.Ordinal))
                    line = line.Substring(0, line.Length - 1).TrimEnd();

                int separator = line.LastIndexOf(':');
                if (separator <= 2 || line[0] != '"' || line[separator - 1] != '"')
                    throw new FormatException("Entrada no válida en el catálogo de iconos, línea " + lineNumber + ".");

                string key = line.Substring(1, separator - 2);
                int codePoint;
                if (!int.TryParse(line.Substring(separator + 1).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out codePoint))
                    throw new FormatException("Código de icono no válido en la línea " + lineNumber + ".");
                result.Add(key, codePoint);
            }
            return result;
        }

        private static void LoadFont(Assembly assembly)
        {
            using (Stream stream = assembly.GetManifestResourceStream(FontResource))
            {
                if (stream == null)
                    throw new InvalidOperationException("No se encuentra la fuente embebida de Fluent Icons.");
                byte[] data = new byte[stream.Length];
                int offset = 0;
                while (offset < data.Length)
                {
                    int read = stream.Read(data, offset, data.Length - offset);
                    if (read == 0)
                        break;
                    offset += read;
                }

                fontMemory = Marshal.AllocCoTaskMem(data.Length);
                Marshal.Copy(data, 0, fontMemory, data.Length);
                fontCollection = new PrivateFontCollection();
                fontCollection.AddMemoryFont(fontMemory, data.Length);
            }
        }
    }
}

