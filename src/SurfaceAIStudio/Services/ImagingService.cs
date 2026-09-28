using Microsoft.Graphics.Imaging;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.ContentSafety;
using Microsoft.Windows.AI.Imaging;
using Microsoft.Windows.AI.Video;
using Windows.Graphics;
using Windows.Graphics.Imaging;

namespace SurfaceAIStudio;

internal sealed record ModelRow(string Name, string State);

internal sealed class ImagingService : IDisposable
{
    readonly Func<string, string, Task<bool>> _consent;

    ImageScaler? _scaler;
    ImageObjectRemover? _remover;
    ImageDescriptionGenerator? _describer;
    TextRecognizer? _recognizer;
    ImageForegroundExtractor? _foreground;
    ImageGenerator? _generator;

    public ImagingService(Func<string, string, Task<bool>> consent)
    {
        _consent = consent;
    }

    public IReadOnlyList<ModelRow> Probe()
    {
        return
        [
            Row("Super resolution", SafeState(ImageScaler.GetReadyState)),
            Row("Background cutout", SafeState(ImageForegroundExtractor.GetReadyState)),
            Row("Object select", SafeState(ImageObjectExtractor.GetReadyState)),
            Row("Object erase", SafeState(ImageObjectRemover.GetReadyState)),
            Row("Image description", SafeState(ImageDescriptionGenerator.GetReadyState)),
            Row("Text recognition", SafeState(TextRecognizer.GetReadyState)),
            Row("Image generation", SafeState(ImageGenerator.GetReadyState)),
            Row("Video super resolution", SafeState(VideoScaler.GetReadyState)),
        ];
    }

    public async Task<SoftwareBitmap> UpscaleAsync(SoftwareBitmap source, int width, int height)
    {
        var scaler = await ReadyAsync(
            "image super resolution",
            ImageScaler.GetReadyState,
            ImageScaler.EnsureReadyAsync,
            async () => _scaler ??= await ImageScaler.CreateAsync());
        var factor = scaler.MaxSupportedScaleFactor;
        var requested = Math.Max(width / (double)source.PixelWidth, height / (double)source.PixelHeight);
        if (requested > factor + 0.01)
        {
            throw new InvalidOperationException($"This model scales up to {factor:0.#}×. Ask for a smaller size.");
        }

        var input = BitmapImages.ToBgra(source);
        try
        {
            return await Offload(() => scaler.ScaleSoftwareBitmap(input, width, height));
        }
        finally
        {
            input.Dispose();
        }
    }

    public async Task<SoftwareBitmap> ForegroundMaskAsync(SoftwareBitmap source)
    {
        var extractor = await ReadyAsync(
            "background cutout",
            ImageForegroundExtractor.GetReadyState,
            ImageForegroundExtractor.EnsureReadyAsync,
            async () => _foreground ??= await ImageForegroundExtractor.CreateAsync());
        var input = BitmapImages.ToBgra(source);
        try
        {
            return await Offload(() => extractor.GetMaskFromSoftwareBitmap(input));
        }
        finally
        {
            input.Dispose();
        }
    }

    public async Task<SoftwareBitmap> SelectMaskAsync(
        SoftwareBitmap source,
        IReadOnlyList<PointInt32> includePoints,
        IReadOnlyList<PointInt32> excludePoints,
        IReadOnlyList<RectInt32> includeRects)
    {
        await ReadyAsync(
            "object select",
            ImageObjectExtractor.GetReadyState,
            ImageObjectExtractor.EnsureReadyAsync,
            () => Task.FromResult(true));
        var input = BitmapImages.ToBgra(source);
        try
        {
            var session = await ImageObjectExtractor.CreateWithSoftwareBitmapAsync(input);
            using (session)
            {
                var hint = new ImageObjectExtractorHint(
                    includeRects.ToList(),
                    includePoints.ToList(),
                    excludePoints.ToList());
                return session.GetSoftwareBitmapObjectMask(hint);
            }
        }
        finally
        {
            input.Dispose();
        }
    }

    public async Task<SoftwareBitmap> EraseAsync(SoftwareBitmap source, SoftwareBitmap grayMask)
    {
        var remover = await ReadyAsync(
            "object erase",
            ImageObjectRemover.GetReadyState,
            ImageObjectRemover.EnsureReadyAsync,
            async () => _remover ??= await ImageObjectRemover.CreateAsync());
        var input = BitmapImages.ToBgra(source);
        var mask = grayMask.BitmapPixelFormat == BitmapPixelFormat.Gray8
            ? SoftwareBitmap.Copy(grayMask)
            : SoftwareBitmap.Convert(grayMask, BitmapPixelFormat.Gray8, BitmapAlphaMode.Ignore);
        try
        {
            return await Offload(() => remover.RemoveFromSoftwareBitmap(input, mask));
        }
        finally
        {
            input.Dispose();
            mask.Dispose();
        }
    }

    public async Task<string> DescribeAsync(SoftwareBitmap source, ImageDescriptionKind kind)
    {
        var describer = await ReadyAsync(
            "image description",
            ImageDescriptionGenerator.GetReadyState,
            ImageDescriptionGenerator.EnsureReadyAsync,
            async () => _describer ??= await ImageDescriptionGenerator.CreateAsync());
        var input = BitmapImages.ToBgra(source);
        using var buffer = ImageBuffer.CreateForSoftwareBitmap(input);
        try
        {
            var response = await describer.DescribeAsync(buffer, kind, new ContentFilterOptions());
            if (response.Status != ImageDescriptionResultStatus.Complete || string.IsNullOrWhiteSpace(response.Description))
            {
                return $"No description ({response.Status}).";
            }

            return response.Description;
        }
        finally
        {
            input.Dispose();
        }
    }

    public async Task<string> ReadTextAsync(SoftwareBitmap source)
    {
        var recognizer = await ReadyAsync(
            "text recognition",
            TextRecognizer.GetReadyState,
            TextRecognizer.EnsureReadyAsync,
            async () => _recognizer ??= await TextRecognizer.CreateAsync());
        var input = BitmapImages.ToBgra(source);
        using var buffer = ImageBuffer.CreateForSoftwareBitmap(input);
        try
        {
            var text = await Offload(() => recognizer.RecognizeTextFromImage(buffer));
            var lines = text.Lines?.Select(line => line.Text).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray() ?? [];
            return lines.Length == 0 ? "No text found." : string.Join(Environment.NewLine, lines);
        }
        finally
        {
            input.Dispose();
        }
    }

    public async Task<SoftwareBitmap> GenerateAsync(string prompt, int steps, double creativity, int seed, ImageFromTextGenerationStyle style)
    {
        var generator = await GeneratorAsync();
        var options = Options(steps, creativity, seed);
        var textOptions = new ImageFromTextGenerationOptions { Style = style };
        var result = await Offload(() => generator.GenerateImageFromTextPrompt(prompt, options, textOptions));
        if (IsImageBlocked(result))
        {
            options.Seed = unchecked(seed + 1);
            result = await Offload(() => generator.GenerateImageFromTextPrompt(prompt, options, textOptions));
        }

        return Finish(result);
    }

    public async Task<SoftwareBitmap> RestyleAsync(SoftwareBitmap source, string prompt, int steps, double creativity, int seed, float colorPreservation)
    {
        var generator = await GeneratorAsync();
        var input = BitmapImages.ToBgra(source);
        using var buffer = ImageBuffer.CreateForSoftwareBitmap(input);
        try
        {
            var options = Options(steps, creativity, seed);
            var style = new ImageFromImageGenerationOptions
            {
                Style = ImageFromImageGenerationStyle.Restyle,
                ColorPreservation = colorPreservation,
            };
            var result = await Offload(() => generator.GenerateImageFromImageBuffer(buffer, prompt, options, style));
            if (IsImageBlocked(result))
            {
                options.Seed = unchecked(seed + 1);
                result = await Offload(() => generator.GenerateImageFromImageBuffer(buffer, prompt, options, style));
            }

            return Finish(result);
        }
        finally
        {
            input.Dispose();
        }
    }

    public async Task<SoftwareBitmap> FillAsync(SoftwareBitmap source, SoftwareBitmap grayMask, string prompt, int steps, double creativity, int seed)
    {
        var generator = await GeneratorAsync();
        var inputBitmap = BitmapImages.ToBgra(source);
        var maskBitmap = grayMask.BitmapPixelFormat == BitmapPixelFormat.Gray8
            ? SoftwareBitmap.Copy(grayMask)
            : SoftwareBitmap.Convert(grayMask, BitmapPixelFormat.Gray8, BitmapAlphaMode.Ignore);
        using var image = ImageBuffer.CreateForSoftwareBitmap(inputBitmap);
        using var mask = ImageBuffer.CreateForSoftwareBitmap(maskBitmap);
        try
        {
            var options = Options(steps, creativity, seed);
            var result = await Offload(() => generator.GenerateImageFromImageBufferAndMask(image, mask, prompt, options));
            if (IsImageBlocked(result))
            {
                options.Seed = unchecked(seed + 1);
                result = await Offload(() => generator.GenerateImageFromImageBufferAndMask(image, mask, prompt, options));
            }

            return Finish(result);
        }
        finally
        {
            inputBitmap.Dispose();
            maskBitmap.Dispose();
        }
    }

    public void Dispose()
    {
        _scaler?.Dispose();
        _remover?.Dispose();
        _describer?.Dispose();
        _recognizer?.Dispose();
        _foreground?.Dispose();
        _generator?.Dispose();
    }

    async Task<ImageGenerator> GeneratorAsync() =>
        await ReadyAsync(
            "image generation",
            ImageGenerator.GetReadyState,
            ImageGenerator.EnsureReadyAsync,
            async () => _generator ??= await ImageGenerator.CreateAsync());

    static async Task<T> Offload<T>(Func<T> work)
    {
        try
        {
            return await Task.Run(work);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x8001010E))
        {
            return work();
        }
    }

    static ImageGenerationOptions Options(int steps, double creativity, int seed)
    {
        var filters = new ContentFilterOptions();
        filters.PromptMaxAllowedSeverityLevel.Hate = SeverityLevel.Medium;
        filters.PromptMaxAllowedSeverityLevel.Sexual = SeverityLevel.Medium;
        filters.PromptMaxAllowedSeverityLevel.Violent = SeverityLevel.Medium;
        filters.PromptMaxAllowedSeverityLevel.SelfHarm = SeverityLevel.Medium;
        filters.ImageMaxAllowedSeverityLevel.AdultContentLevel = SeverityLevel.Medium;
        filters.ImageMaxAllowedSeverityLevel.RacyContentLevel = SeverityLevel.Medium;
        filters.ImageMaxAllowedSeverityLevel.GoryContentLevel = SeverityLevel.Medium;
        filters.ImageMaxAllowedSeverityLevel.ViolentContentLevel = SeverityLevel.Medium;
        return new ImageGenerationOptions
        {
            MaxInferenceSteps = Math.Clamp(steps, 1, 8),
            Creativity = creativity,
            Seed = seed,
            ContentFilterOptions = filters,
        };
    }

    static bool IsImageBlocked(ImageGeneratorResult result) =>
        result.Status == ImageGeneratorResultStatus.ImageBlockedByContentModeration;

    static SoftwareBitmap Finish(ImageGeneratorResult result)
    {
        if (result.Status != ImageGeneratorResultStatus.Success || result.Image is null)
        {
            throw new InvalidOperationException(result.Status switch
            {
                ImageGeneratorResultStatus.BlockedByPolicy =>
                    "Windows rejected the safety settings for this request. Generation is allowed only up to the medium filter.",
                ImageGeneratorResultStatus.ImageBlockedByContentModeration =>
                    "Windows blocked that picture, including a second try with a new seed. Rephrase the prompt or lower Creativity. The safety check stays on.",
                ImageGeneratorResultStatus.TextBlockedByContentModeration =>
                    "Windows blocked that prompt. Rephrase it. The safety check stays on.",
                _ => string.IsNullOrWhiteSpace(result.ExtendedError?.Message)
                    ? $"Image generation did not finish ({result.Status})."
                    : result.ExtendedError.Message,
            });
        }

        using (result.Image)
        {
            return result.Image.CopyToSoftwareBitmap();
        }
    }

    async Task<T> ReadyAsync<T>(
        string feature,
        Func<AIFeatureReadyState> getState,
        Func<Windows.Foundation.IAsyncOperationWithProgress<AIFeatureReadyResult, double>> ensure,
        Func<Task<T>> create)
    {
        var actual = getState();
        if (actual != AIFeatureReadyState.Ready)
        {
            if (actual is AIFeatureReadyState.NotSupportedOnCurrentSystem
                or AIFeatureReadyState.DisabledByUser
                or AIFeatureReadyState.NotCompatibleWithSystemHardware
                or AIFeatureReadyState.CapabilityMissing
                or AIFeatureReadyState.OSUpdateNeeded)
            {
                throw new InvalidOperationException($"{feature}: {Explain(actual)}");
            }

            if (!await _consent(feature, Explain(actual)))
            {
                throw new OperationCanceledException();
            }

            var operation = ensure();
            var result = await operation;
            if (result.Status != AIFeatureReadyResultState.Success)
            {
                var detail = result.ErrorDisplayText;
                if (string.IsNullOrWhiteSpace(detail))
                {
                    detail = result.ExtendedError?.Message ?? result.Status.ToString();
                }

                throw new InvalidOperationException($"{feature}: {detail}");
            }
        }

        return await create();
    }

    static ModelRow Row(string name, string state) => new(name, state);

    static string SafeState(Func<AIFeatureReadyState> read)
    {
        try
        {
            return Explain(read());
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static string Explain(AIFeatureReadyState state) => state switch
    {
        AIFeatureReadyState.Ready => "Ready",
        AIFeatureReadyState.NotReady => "Not downloaded",
        AIFeatureReadyState.DisabledByUser => "Turned off in Settings",
        AIFeatureReadyState.NotSupportedOnCurrentSystem => "Not supported",
        AIFeatureReadyState.CapabilityMissing => "Open the app from the Start menu",
        AIFeatureReadyState.NotCompatibleWithSystemHardware => "Hardware mismatch",
        AIFeatureReadyState.OSUpdateNeeded => "Update Windows",
        _ => state.ToString(),
    };
}
