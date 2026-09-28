using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SurfaceAIStudio;

internal static class BitmapImages
{
    public static SoftwareBitmap ToBgra(SoftwareBitmap source)
    {
        if (source.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
            source.BitmapAlphaMode == BitmapAlphaMode.Premultiplied &&
            !source.IsReadOnly)
        {
            return SoftwareBitmap.Copy(source);
        }

        return SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    public static async Task<SoftwareBitmap> LoadAsync(StorageFile file)
    {
        using IRandomAccessStream stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        return bitmap.IsReadOnly ? SoftwareBitmap.Copy(bitmap) : bitmap;
    }

    public static async Task SaveAsync(SoftwareBitmap bitmap, StorageFile file)
    {
        var ext = Path.GetExtension(file.Name).ToLowerInvariant();
        var jpeg = ext is ".jpg" or ".jpeg";
        var owned = ToBgra(bitmap);
        var source = jpeg ? FlattenOnWhite(owned) : owned;
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        stream.Size = 0;
        var encoderId = jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId;
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetSoftwareBitmap(source);
        try
        {
            var properties = new BitmapPropertySet
            {
                ["System.Comment"] = new BitmapTypedValue("Edited offline in Surface AI Studio", PropertyType.String),
            };
            await encoder.BitmapProperties.SetPropertiesAsync(properties);
        }
        catch (Exception ex)
        {
            StudioLog.Write("Metadata skipped: " + ex.Message);
        }

        await encoder.FlushAsync();
        if (!ReferenceEquals(source, bitmap))
        {
            source.Dispose();
        }
    }

    public static async Task<SoftwareBitmapSource> ToSourceAsync(SoftwareBitmap bitmap)
    {
        var display = ToBgra(bitmap);
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(display);
        return source;
    }

    public static byte[] CopyPixels(SoftwareBitmap bitmap)
    {
        var bgra = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? bitmap : ToBgra(bitmap);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyToBuffer(pixels.AsBuffer());
        if (!ReferenceEquals(bgra, bitmap))
        {
            bgra.Dispose();
        }

        return pixels;
    }

    public static SoftwareBitmap FromPixels(byte[] pixels, int width, int height)
    {
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(pixels.AsBuffer());
        return bitmap;
    }

    public static SoftwareBitmap GrayFrom(byte[] mask, int width, int height)
    {
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Gray8, width, height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(mask.AsBuffer());
        return bitmap;
    }

    static SoftwareBitmap FlattenOnWhite(SoftwareBitmap bgra)
    {
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var pixels = CopyPixels(bgra);
        bgra.Dispose();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3] / 255f;
            pixels[i] = (byte)(pixels[i] * alpha + 255 * (1 - alpha));
            pixels[i + 1] = (byte)(pixels[i + 1] * alpha + 255 * (1 - alpha));
            pixels[i + 2] = (byte)(pixels[i + 2] * alpha + 255 * (1 - alpha));
            pixels[i + 3] = 255;
        }

        return FromPixels(pixels, width, height);
    }
}
