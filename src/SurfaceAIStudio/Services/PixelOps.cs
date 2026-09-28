using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace SurfaceAIStudio;

internal readonly record struct AdjustSettings(
    float Brightness,
    float Contrast,
    float Saturation,
    float Temperature,
    float Fade,
    float Vignette,
    float Sharpness)
{
    public static AdjustSettings FromSliders(
        double brightness,
        double contrast,
        double saturation,
        double temperature,
        double fade,
        double vignette,
        double sharpness) =>
        new(
            (float)(brightness / 100.0),
            (float)(contrast / 100.0),
            (float)(saturation / 100.0),
            (float)(temperature / 100.0),
            (float)(fade / 100.0),
            (float)(vignette / 100.0),
            (float)(sharpness / 100.0));

    public bool IsNeutral =>
        Brightness == 0 && Contrast == 0 && Saturation == 0 && Temperature == 0 &&
        Fade == 0 && Vignette == 0 && Sharpness == 0;
}

internal static class PixelOps
{
    public static SoftwareBitmap Apply(SoftwareBitmap source, AdjustSettings settings)
    {
        var pixels = BitmapImages.CopyPixels(source);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        Tone(pixels, width, height, settings);
        if (settings.Sharpness > 0.01f)
        {
            Sharpen(pixels, width, height, settings.Sharpness);
        }

        return BitmapImages.FromPixels(pixels, width, height);
    }

    public static SoftwareBitmap Crop(SoftwareBitmap source, int x, int y, int width, int height)
    {
        var pixels = BitmapImages.CopyPixels(source);
        var srcW = source.PixelWidth;
        x = Math.Clamp(x, 0, srcW - 1);
        y = Math.Clamp(y, 0, source.PixelHeight - 1);
        width = Math.Clamp(width, 1, srcW - x);
        height = Math.Clamp(height, 1, source.PixelHeight - y);
        var cropped = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(pixels, ((y + row) * srcW + x) * 4, cropped, row * width * 4, width * 4);
        }

        return BitmapImages.FromPixels(cropped, width, height);
    }

    public static SoftwareBitmap RotateClockwise(SoftwareBitmap source)
    {
        var pixels = BitmapImages.CopyPixels(source);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var rotated = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var destX = height - 1 - y;
                var destY = x;
                Buffer.BlockCopy(pixels, (y * width + x) * 4, rotated, (destY * height + destX) * 4, 4);
            }
        }

        return BitmapImages.FromPixels(rotated, height, width);
    }

    public static SoftwareBitmap RotateCounterClockwise(SoftwareBitmap source)
    {
        var pixels = BitmapImages.CopyPixels(source);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var rotated = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var destX = y;
                var destY = width - 1 - x;
                Buffer.BlockCopy(pixels, (y * width + x) * 4, rotated, (destY * height + destX) * 4, 4);
            }
        }

        return BitmapImages.FromPixels(rotated, height, width);
    }

    public static SoftwareBitmap FlipHorizontal(SoftwareBitmap source)
    {
        var pixels = BitmapImages.CopyPixels(source);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var flipped = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Buffer.BlockCopy(pixels, (y * width + x) * 4, flipped, (y * width + (width - 1 - x)) * 4, 4);
            }
        }

        return BitmapImages.FromPixels(flipped, width, height);
    }

    public static SoftwareBitmap FlipVertical(SoftwareBitmap source)
    {
        var pixels = BitmapImages.CopyPixels(source);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var flipped = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(pixels, y * width * 4, flipped, (height - 1 - y) * width * 4, width * 4);
        }

        return BitmapImages.FromPixels(flipped, width, height);
    }

    public static SoftwareBitmap Composite(SoftwareBitmap source, SoftwareBitmap grayMask, byte backgroundR, byte backgroundG, byte backgroundB, bool transparent)
    {
        var pixels = BitmapImages.CopyPixels(source);
        var mask = CopyGray(grayMask, source.PixelWidth, source.PixelHeight);
        for (var i = 0; i < pixels.Length / 4; i++)
        {
            var coverage = mask[i] / 255f;
            var p = i * 4;
            if (transparent)
            {
                pixels[p + 3] = mask[i];
                continue;
            }

            pixels[p] = Mix(pixels[p], backgroundB, coverage);
            pixels[p + 1] = Mix(pixels[p + 1], backgroundG, coverage);
            pixels[p + 2] = Mix(pixels[p + 2], backgroundR, coverage);
            pixels[p + 3] = 255;
        }

        return BitmapImages.FromPixels(pixels, source.PixelWidth, source.PixelHeight);
    }

    public static SoftwareBitmap BlurBackground(SoftwareBitmap source, SoftwareBitmap grayMask, int radius)
    {
        var pixels = BitmapImages.CopyPixels(source);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var blurred = BoxBlur(pixels, width, height, Math.Clamp(radius, 2, 28));
        var mask = CopyGray(grayMask, width, height);
        for (var i = 0; i < mask.Length; i++)
        {
            var coverage = mask[i] / 255f;
            var p = i * 4;
            pixels[p] = Mix(blurred[p], pixels[p], coverage);
            pixels[p + 1] = Mix(blurred[p + 1], pixels[p + 1], coverage);
            pixels[p + 2] = Mix(blurred[p + 2], pixels[p + 2], coverage);
            pixels[p + 3] = 255;
        }

        return BitmapImages.FromPixels(pixels, width, height);
    }

    public static void Stamp(byte[] mask, int width, int height, int cx, int cy, int radius, byte value)
    {
        var r2 = radius * radius;
        var x0 = Math.Max(0, cx - radius);
        var x1 = Math.Min(width - 1, cx + radius);
        var y0 = Math.Max(0, cy - radius);
        var y1 = Math.Min(height - 1, cy + radius);
        for (var y = y0; y <= y1; y++)
        {
            var dy = y - cy;
            for (var x = x0; x <= x1; x++)
            {
                var dx = x - cx;
                if (dx * dx + dy * dy <= r2)
                {
                    mask[y * width + x] = value;
                }
            }
        }
    }

    static void Tone(byte[] pixels, int width, int height, AdjustSettings settings)
    {
        var contrast = 1f + settings.Contrast;
        var saturation = 1f + settings.Saturation;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var b = pixels[i] / 255f + settings.Brightness;
                var g = pixels[i + 1] / 255f + settings.Brightness;
                var r = pixels[i + 2] / 255f + settings.Brightness;
                r = (r - 0.5f) * contrast + 0.5f;
                g = (g - 0.5f) * contrast + 0.5f;
                b = (b - 0.5f) * contrast + 0.5f;
                var luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                r = luma + (r - luma) * saturation;
                g = luma + (g - luma) * saturation;
                b = luma + (b - luma) * saturation;
                r += settings.Temperature * 0.18f;
                b -= settings.Temperature * 0.18f;
                if (settings.Vignette > 0)
                {
                    var dx = (x + 0.5f) / width - 0.5f;
                    var dy = (y + 0.5f) / height - 0.5f;
                    var distance = MathF.Sqrt(dx * dx + dy * dy) / 0.7071f;
                    var vig = 1f - settings.Vignette * distance * distance;
                    r *= vig;
                    g *= vig;
                    b *= vig;
                }

                if (settings.Fade > 0)
                {
                    var lift = 0.22f * settings.Fade;
                    r = r * (1f - lift) + lift;
                    g = g * (1f - lift) + lift;
                    b = b * (1f - lift) + lift;
                }

                pixels[i] = ToByte(b);
                pixels[i + 1] = ToByte(g);
                pixels[i + 2] = ToByte(r);
            }
        }
    }

    static void Sharpen(byte[] pixels, int width, int height, float amount)
    {
        var copy = (byte[])pixels.Clone();
        var strength = amount * 1.4f;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var i = (y * width + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var center = copy[i + c];
                    var neighbors =
                        copy[((y - 1) * width + x) * 4 + c] +
                        copy[((y + 1) * width + x) * 4 + c] +
                        copy[(y * width + (x - 1)) * 4 + c] +
                        copy[(y * width + (x + 1)) * 4 + c];
                    var value = center + strength * (center - neighbors / 4f);
                    pixels[i + c] = ToByte(value / 255f);
                }
            }
        }
    }

    static byte[] BoxBlur(byte[] pixels, int width, int height, int radius)
    {
        var horizontal = BlurHorizontal(pixels, width, height, radius);
        return BlurVertical(horizontal, width, height, radius);
    }

    static byte[] BlurHorizontal(byte[] source, int width, int height, int radius)
    {
        var dest = new byte[source.Length];
        var prefix = new int[width];
        for (var y = 0; y < height; y++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                var running = 0;
                for (var x = 0; x < width; x++)
                {
                    running += source[(y * width + x) * 4 + channel];
                    prefix[x] = running;
                }

                for (var x = 0; x < width; x++)
                {
                    var x0 = Math.Max(0, x - radius);
                    var x1 = Math.Min(width - 1, x + radius);
                    var sum = prefix[x1] - (x0 > 0 ? prefix[x0 - 1] : 0);
                    dest[(y * width + x) * 4 + channel] = (byte)(sum / (x1 - x0 + 1));
                }
            }

            for (var x = 0; x < width; x++)
            {
                dest[(y * width + x) * 4 + 3] = source[(y * width + x) * 4 + 3];
            }
        }

        return dest;
    }

    static byte[] BlurVertical(byte[] source, int width, int height, int radius)
    {
        var dest = new byte[source.Length];
        var prefix = new int[height];
        for (var x = 0; x < width; x++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                var running = 0;
                for (var y = 0; y < height; y++)
                {
                    running += source[(y * width + x) * 4 + channel];
                    prefix[y] = running;
                }

                for (var y = 0; y < height; y++)
                {
                    var y0 = Math.Max(0, y - radius);
                    var y1 = Math.Min(height - 1, y + radius);
                    var sum = prefix[y1] - (y0 > 0 ? prefix[y0 - 1] : 0);
                    dest[(y * width + x) * 4 + channel] = (byte)(sum / (y1 - y0 + 1));
                }
            }

            for (var y = 0; y < height; y++)
            {
                dest[(y * width + x) * 4 + 3] = 255;
            }
        }

        return dest;
    }

    static byte[] CopyGray(SoftwareBitmap mask, int width, int height)
    {
        var gray = mask.BitmapPixelFormat == BitmapPixelFormat.Gray8
            ? mask
            : SoftwareBitmap.Convert(mask, BitmapPixelFormat.Gray8, BitmapAlphaMode.Ignore);
        var bytes = new byte[gray.PixelWidth * gray.PixelHeight];
        gray.CopyToBuffer(bytes.AsBuffer());
        if (!ReferenceEquals(gray, mask))
        {
            gray.Dispose();
        }

        if (gray.PixelWidth == width && gray.PixelHeight == height)
        {
            return bytes;
        }

        var fitted = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp(y * gray.PixelHeight / height, 0, gray.PixelHeight - 1);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp(x * gray.PixelWidth / width, 0, gray.PixelWidth - 1);
                fitted[y * width + x] = bytes[sy * gray.PixelWidth + sx];
            }
        }

        return fitted;
    }

    static byte Mix(byte foreground, byte background, float coverage) =>
        ToByte((foreground * coverage + background * (1f - coverage)) / 255f);

    static byte ToByte(float unit) => (byte)Math.Clamp((int)MathF.Round(unit * 255f), 0, 255);
}
