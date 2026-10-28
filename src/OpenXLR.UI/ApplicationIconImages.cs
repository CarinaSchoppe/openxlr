using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace OpenXLR.UI;

/// <summary>
/// Application icons as pictures: the daemon's icon name found through the
/// desktop's icon theme and decoded at the size it is drawn, on a worker
/// thread. Recent results, missing icons included, share their lookup and
/// decoding task within a fixed cache budget.
/// </summary>
public static class ApplicationIconImages
{
    private const int MaxCachedImages = 128;
    private sealed class ImageCache(IconThemeLookup lookup)
    {
        public IconThemeLookup Lookup { get; } = lookup;
        public Dictionary<(string Icon, int Size), Task<Bitmap?>> Images { get; } = new();
    }
    private static readonly Lock Gate = new();
    private static ImageCache? _current;
    private static ImageCache Current => LazyInitializer.EnsureInitialized(ref _current, () => new(IconThemeLookup.ForDesktop()));

    /// <summary>The lookup in use; built for the desktop the first time an icon is wanted.</summary>
    internal static IconThemeLookup Lookup
    {
        get => Current.Lookup;
        set => Volatile.Write(ref _current, new ImageCache(value));
    }

    /// <summary>The picture for this icon at this many pixels square, or null when nothing draws it.</summary>
    public static Task<Bitmap?> Load(string icon, int pixels)
    {
        icon = icon.Trim();
        if (icon.Length is 0 or > IconThemeLookup.MaxIconLength) return Task.FromResult<Bitmap?>(null);
        ImageCache cache = Current;
        var key = (Icon: icon, Size: Math.Clamp(pixels, 8, 512));
        lock (Gate)
        {
            if (cache.Images.TryGetValue(key, out Task<Bitmap?>? known)) return known;
            // Drop only the cache's reference. Do not dispose a bitmap that
            // a row can still be drawing.
            if (cache.Images.Count >= MaxCachedImages) cache.Images.Clear();
            Task<Bitmap?> load = Task.Run(() => LoadNow(cache.Lookup, key.Icon, key.Size));
            cache.Images[key] = load;
            return load;
        }
    }

    /// <summary>The first of the lookup's files that decodes; this touches the disk.</summary>
    internal static Bitmap? LoadNow(IconThemeLookup lookup, string icon, int pixels)
    {
        foreach (string file in lookup.Find(icon, pixels))
            if (Decode(file, pixels) is { } picture) return picture;
        return null;
    }

    // Icons are small; a file this large is not one.
    private const long MaxFileBytes = 4 * 1024 * 1024;

    internal static Bitmap? Decode(string file, int pixels)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            if (file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return SvgRaster.Render(file, pixels);
            using FileStream stream = info.OpenRead();
            return Bitmap.DecodeToWidth(stream, pixels, BitmapInterpolationMode.HighQuality);
        }
        // A file that does not read or decode is no icon: the codec's
        // exceptions are not documented, and none of them should reach the row.
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// SVG icons drawn by the desktop's own image loader, GdkPixbuf with its
/// librsvg module, which every GTK desktop carries. Without it, or without
/// the SVG module, an SVG is not drawn and the lookup's next file is tried.
/// </summary>
internal static class SvgRaster
{
    private const string Pixbuf = "libgdk_pixbuf-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private const string GLib = "libglib-2.0.so.0";

    [DllImport(Pixbuf)]
    private static extern IntPtr gdk_pixbuf_new_from_file_at_size(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename, int width, int height, out IntPtr error);
    [DllImport(Pixbuf)] private static extern int gdk_pixbuf_get_width(IntPtr pixbuf);
    [DllImport(Pixbuf)] private static extern int gdk_pixbuf_get_height(IntPtr pixbuf);
    [DllImport(Pixbuf)] private static extern int gdk_pixbuf_get_rowstride(IntPtr pixbuf);
    [DllImport(Pixbuf)] private static extern int gdk_pixbuf_get_n_channels(IntPtr pixbuf);
    [DllImport(Pixbuf)] private static extern int gdk_pixbuf_get_bits_per_sample(IntPtr pixbuf);
    [DllImport(Pixbuf)] private static extern IntPtr gdk_pixbuf_get_pixels(IntPtr pixbuf);
    [DllImport(GObject)] private static extern void g_object_unref(IntPtr instance);
    [DllImport(GLib)] private static extern void g_error_free(IntPtr error);

    private static readonly Lock Gate = new();
    private static bool _unavailable;

    /// <summary>Whether SVG files can be drawn here; false once the loader is found missing.</summary>
    public static bool Available => !_unavailable;

    /// <summary>The SVG drawn to fit <paramref name="pixels"/> square, or null.</summary>
    public static Bitmap? Render(string file, int pixels)
    {
        if (_unavailable) return null;
        lock (Gate)
        {
            try
            {
                IntPtr pixbuf = gdk_pixbuf_new_from_file_at_size(file, pixels, pixels, out IntPtr error);
                if (error != IntPtr.Zero) g_error_free(error);
                if (pixbuf == IntPtr.Zero) return null;
                try { return Copy(pixbuf); }
                finally { g_object_unref(pixbuf); }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _unavailable = true;
                return null;
            }
        }
    }

    // GdkPixbuf rows are RGB or RGBA, straight alpha, padded to the row
    // stride except for the last one.
    private static WriteableBitmap? Copy(IntPtr pixbuf)
    {
        int width = gdk_pixbuf_get_width(pixbuf), height = gdk_pixbuf_get_height(pixbuf);
        int stride = gdk_pixbuf_get_rowstride(pixbuf), channels = gdk_pixbuf_get_n_channels(pixbuf);
        if (width <= 0 || height <= 0 || width > 1024 || height > 1024 ||
            gdk_pixbuf_get_bits_per_sample(pixbuf) != 8 || channels is not (3 or 4)) return null;
        IntPtr source = gdk_pixbuf_get_pixels(pixbuf);
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Rgba8888, AlphaFormat.Unpremul);
        using ILockedFramebuffer target = bitmap.Lock();
        byte[] row = new byte[width * channels];
        byte[] rgba = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(source + y * stride, row, 0, row.Length);
            if (channels == 4) Buffer.BlockCopy(row, 0, rgba, 0, rgba.Length);
            else
                for (int x = 0; x < width; x++)
                {
                    rgba[x * 4] = row[x * 3];
                    rgba[x * 4 + 1] = row[x * 3 + 1];
                    rgba[x * 4 + 2] = row[x * 3 + 2];
                    rgba[x * 4 + 3] = 255;
                }
            Marshal.Copy(rgba, 0, target.Address + y * target.RowBytes, rgba.Length);
        }
        return bitmap;
    }
}
