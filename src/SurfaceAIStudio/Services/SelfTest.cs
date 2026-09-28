using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Imaging;
using Windows.Graphics.Imaging;

namespace SurfaceAIStudio;

internal static class SelfTest
{
    public static async Task<string> RunAsync(ImagingService imaging)
    {
        var lines = new List<string>();
        try
        {
            var source = Gradient(8, 4);
            var bright = PixelOps.Apply(source, new AdjustSettings(0.4f, 0, 0, 0, 0, 0, 0));
            var before = BitmapImages.CopyPixels(source);
            var after = BitmapImages.CopyPixels(bright);
            lines.Add(after[2] > before[2] ? "PASS brightness raises red" : "FAIL brightness");

            var cropped = PixelOps.Crop(source, 1, 1, 3, 2);
            lines.Add(cropped.PixelWidth == 3 && cropped.PixelHeight == 2 ? "PASS crop" : "FAIL crop");

            var turned = PixelOps.RotateClockwise(source);
            lines.Add(turned.PixelWidth == 4 && turned.PixelHeight == 8 ? "PASS rotate" : $"FAIL rotate {turned.PixelWidth}x{turned.PixelHeight}");

            var flipped = PixelOps.FlipHorizontal(source);
            var srcPx = BitmapImages.CopyPixels(source);
            var flipPx = BitmapImages.CopyPixels(flipped);
            var left = srcPx[2];
            var right = srcPx[(source.PixelWidth - 1) * 4 + 2];
            var flippedLeft = flipPx[2];
            lines.Add(flippedLeft == right && left != right ? "PASS flip" : "FAIL flip");

            source.Dispose();
            bright.Dispose();
            cropped.Dispose();
            turned.Dispose();
            flipped.Dispose();
        }
        catch (Exception ex)
        {
            lines.Add("FAIL pixels " + ex.Message);
        }

        try
        {
            foreach (var row in imaging.Probe())
            {
                lines.Add($"MODEL {row.Name}: {row.State}");
            }
        }
        catch (Exception ex)
        {
            lines.Add("FAIL probe " + ex);
        }

        if (ImageDescriptionGenerator.GetReadyState() == AIFeatureReadyState.Ready)
        {
            try
            {
                using var photo = Gradient(320, 200);
                var description = await imaging.DescribeAsync(photo, ImageDescriptionKind.BriefDescription);
                lines.Add(string.IsNullOrWhiteSpace(description) ? "FAIL describe empty" : "PASS describe: " + description.ReplaceLineEndings(" "));
            }
            catch (Exception ex)
            {
                lines.Add("FAIL describe " + ex.Message);
            }
        }

        if (TextRecognizer.GetReadyState() == AIFeatureReadyState.Ready)
        {
            try
            {
                using var photo = Gradient(320, 80);
                var text = await imaging.ReadTextAsync(photo);
                lines.Add("PASS text: " + text.ReplaceLineEndings(" "));
            }
            catch (Exception ex)
            {
                lines.Add("FAIL text " + ex.Message);
            }
        }

        var failed = lines.Any(line => line.StartsWith("FAIL", StringComparison.Ordinal));
        lines.Insert(0, failed ? "RESULT FAIL" : "RESULT PASS");
        return string.Join(Environment.NewLine, lines);
    }

    static SoftwareBitmap Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = 40;
                pixels[i + 1] = 80;
                pixels[i + 2] = (byte)(20 + x * 20);
                pixels[i + 3] = 255;
            }
        }

        return BitmapImages.FromPixels(pixels, width, height);
    }
}
