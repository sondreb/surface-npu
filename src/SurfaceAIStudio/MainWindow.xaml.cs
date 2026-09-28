using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace SurfaceAIStudio;

public sealed partial class MainWindow : Window
{
    readonly PhotoDocument _document = new();
    readonly List<PointInt32> _includePoints = [];
    readonly List<PointInt32> _excludePoints = [];
    readonly ImagingService _imaging;

    SoftwareBitmap? _preview;
    SoftwareBitmapSource? _shown;
    byte[]? _mask;
    int _maskWidth;
    int _maskHeight;
    string _tool = "";
    bool _ready;
    bool _busy;
    bool _suppressSliders;
    bool _painting;
    bool _cropping;
    bool _selecting;
    bool _selectRight;
    bool _hasCrop;
    int _cropX;
    int _cropY;
    int _cropWidth;
    int _cropHeight;
    int _adjustGeneration;
    int _selectStartX;
    int _selectStartY;
    Windows.Foundation.Point _cropStart;
    RectInt32? _selectRect;

    public MainWindow()
    {
        var selfTest = File.Exists(StudioLog.SelfTestFlag);
        _imaging = new ImagingService(selfTest ? (_, _) => Task.FromResult(false) : ConfirmModelAsync);
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDrag);
        AppWindow.Resize(new SizeInt32(1440, 920));
    }

    async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (File.Exists(StudioLog.SelfTestFlag))
        {
            try
            {
                File.Delete(StudioLog.SelfTestFlag);
            }
            catch (Exception ex)
            {
                StudioLog.Write(ex.Message);
            }

            var report = await SelfTest.RunAsync(_imaging);
            Directory.CreateDirectory(StudioLog.DirectoryPath);
            await File.WriteAllTextAsync(StudioLog.SelfTestReport, report);
            StudioLog.Write(report);
            Environment.Exit(report.StartsWith("RESULT PASS", StringComparison.Ordinal) ? 0 : 1);
            return;
        }

        _ready = true;
        RefreshModels();
        UpdateChrome();
    }

    void RefreshModels()
    {
        try
        {
            var rows = _imaging.Probe();
            ModelList.ItemsSource = rows;
            foreach (var row in rows)
            {
                StudioLog.Write($"model {row.Name}: {row.State}");
            }
        }
        catch (Exception ex)
        {
            StudioLog.Write(ex.ToString());
            ShowError(ex.Message);
        }
    }

    void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton button)
        {
            return;
        }

        _tool = button.Tag as string ?? "";
        DiscardPreview();
        foreach (var panel in new UIElement[] { PanelHome, PanelAdjust, PanelCrop, PanelUpscale, PanelCutout, PanelSelect, PanelDescribe, PanelText, PanelCreate, PanelMotion, PanelErase })
        {
            panel.Visibility = Visibility.Collapsed;
        }

        var active = _tool switch
        {
            "Adjust" => PanelAdjust,
            "Crop" => PanelCrop,
            "Upscale" => PanelUpscale,
            "Cutout" => PanelCutout,
            "Select" => PanelSelect,
            "Erase" => PanelErase,
            "Describe" => PanelDescribe,
            "Text" => PanelText,
            "Create" => PanelCreate,
            "Motion" => PanelMotion,
            _ => PanelHome,
        };
        active.Visibility = Visibility.Visible;
        CropRect.Visibility = _tool == "Crop" && _hasCrop ? Visibility.Visible : Visibility.Collapsed;
        if (_document.Current is not null)
        {
            _ = ShowAsync(_document.Current);
        }

        DrawOverlay();
        UpdateChrome();
    }

    async void Open_Click(object sender, RoutedEventArgs e) => await OpenAsync();

    void OpenShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = OpenAsync();
    }

    async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    void SaveShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = SaveAsync();
    }

    void UndoShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = UndoAsync();
    }

    void RedoShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = RedoAsync();
    }

    async void Undo_Click(object sender, RoutedEventArgs e) => await UndoAsync();

    async void Redo_Click(object sender, RoutedEventArgs e) => await RedoAsync();

    async void Compare_Click(object sender, RoutedEventArgs e)
    {
        var bitmap = CompareButton.IsChecked == true ? _document.Original : _preview ?? _document.Current;
        if (bitmap is not null)
        {
            await ShowAsync(bitmap);
        }
    }

    void Adjust_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => ScheduleAdjust();

    void Preset_Click(object sender, RoutedEventArgs e)
    {
        var name = (sender as FrameworkElement)?.Tag as string ?? "Neutral";
        _suppressSliders = true;
        BrightnessSlider.Value = 0;
        ContrastSlider.Value = 0;
        SaturationSlider.Value = 0;
        TemperatureSlider.Value = 0;
        FadeSlider.Value = 0;
        VignetteSlider.Value = 0;
        SharpnessSlider.Value = 0;
        switch (name)
        {
            case "Mono":
                SaturationSlider.Value = -100;
                break;
            case "Warm":
                TemperatureSlider.Value = 42;
                ContrastSlider.Value = 8;
                break;
            case "Cool":
                TemperatureSlider.Value = -36;
                ContrastSlider.Value = 6;
                break;
            case "Fade":
                FadeSlider.Value = 55;
                ContrastSlider.Value = -12;
                SaturationSlider.Value = -15;
                break;
            case "Punch":
                ContrastSlider.Value = 24;
                SaturationSlider.Value = 18;
                VignetteSlider.Value = 22;
                break;
        }

        _suppressSliders = false;
        ScheduleAdjust();
    }

    async void ApplyAdjust_Click(object sender, RoutedEventArgs e)
    {
        CommitPreview();
        ResetSliders();
        if (_document.Current is not null)
        {
            await ShowAsync(_document.Current);
        }

        UpdateChrome();
    }

    async void Rotate_Click(object sender, RoutedEventArgs e) => await TransformAsync(PixelOps.RotateClockwise);

    async void FlipH_Click(object sender, RoutedEventArgs e) => await TransformAsync(PixelOps.FlipHorizontal);

    async void FlipV_Click(object sender, RoutedEventArgs e) => await TransformAsync(PixelOps.FlipVertical);

    async void ApplyCrop_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireImage() || !_hasCrop)
        {
            ShowError("Drag a crop on the photo first.");
            return;
        }

        await Guard("Cropping…", async () =>
        {
            var next = PixelOps.Crop(_document.Current!, _cropX, _cropY, _cropWidth, _cropHeight);
            ClearMask();
            _hasCrop = false;
            CropRect.Visibility = Visibility.Collapsed;
            _document.Replace(next);
            await ShowAsync(next);
        });
    }

    async void Sharpen_Click(object sender, RoutedEventArgs e) => await ScaleAsync(1);

    async void Scale2_Click(object sender, RoutedEventArgs e) => await ScaleAsync(2);

    async void Scale4_Click(object sender, RoutedEventArgs e) => await ScaleAsync(4);

    async void Cutout_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireImage())
        {
            return;
        }

        await Guard("Finding the subject on the NPU…", async () =>
        {
            var mask = await _imaging.ForegroundMaskAsync(_document.Current!);
            var choice = BackgroundChoice.SelectedItem as string ?? "Transparent";
            var next = choice switch
            {
                "White" => PixelOps.Composite(_document.Current!, mask, 255, 255, 255, false),
                "Black" => PixelOps.Composite(_document.Current!, mask, 0, 0, 0, false),
                "Warm gray" => PixelOps.Composite(_document.Current!, mask, 232, 224, 214, false),
                "Blur" => PixelOps.BlurBackground(_document.Current!, mask, 16),
                _ => PixelOps.Composite(_document.Current!, mask, 0, 0, 0, true),
            };
            mask.Dispose();
            _document.Replace(next);
            await ShowAsync(next);
        });
    }

    async void FindObject_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireImage())
        {
            return;
        }

        if (_includePoints.Count == 0 && _selectRect is null)
        {
            ShowError("Click the object, or drag a box around it.");
            return;
        }

        await Guard("Selecting on the NPU…", async () =>
        {
            RectInt32[] rects = _selectRect is RectInt32 rect ? [rect] : [];
            var mask = await _imaging.SelectMaskAsync(_document.Current!, _includePoints, _excludePoints, rects);
            AdoptMask(mask);
            mask.Dispose();
        });
    }

    async void CutSelection_Click(object sender, RoutedEventArgs e) => await UseSelectionAsync(erase: false);

    async void EraseSelection_Click(object sender, RoutedEventArgs e) => await UseSelectionAsync(erase: true);

    void ClearSelect_Click(object sender, RoutedEventArgs e)
    {
        _includePoints.Clear();
        _excludePoints.Clear();
        _selectRect = null;
        ClearMask();
        SelectReadout.Text = "No hints yet";
    }

    async void ErasePaint_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireImage() || !MaskHasPaint())
        {
            ShowError("Paint over the object you want to remove.");
            return;
        }

        await Guard("Erasing on the NPU…", async () =>
        {
            using var gray = BitmapImages.GrayFrom(_mask!, _maskWidth, _maskHeight);
            var next = await _imaging.EraseAsync(_document.Current!, gray);
            ClearMask();
            _document.Replace(next);
            await ShowAsync(next);
        });
    }

    void ClearPaint_Click(object sender, RoutedEventArgs e) => ClearMask();

    async void Describe_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireImage())
        {
            return;
        }

        await Guard("Describing on the NPU…", async () =>
        {
            var kind = (DescribeKind.SelectedItem as string) switch
            {
                "Detailed" => Microsoft.Windows.AI.Imaging.ImageDescriptionKind.DetailedDescription,
                "Diagram" => Microsoft.Windows.AI.Imaging.ImageDescriptionKind.DiagramDescription,
                "Accessible" => Microsoft.Windows.AI.Imaging.ImageDescriptionKind.AccessibleDescription,
                _ => Microsoft.Windows.AI.Imaging.ImageDescriptionKind.BriefDescription,
            };
            DescribeOutput.Text = await _imaging.DescribeAsync(_document.Current!, kind);
        });
    }

    void CopyDescribe_Click(object sender, RoutedEventArgs e) => Copy(DescribeOutput.Text);

    async void ReadText_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireImage())
        {
            return;
        }

        await Guard("Reading text on the NPU…", async () =>
        {
            TextOutput.Text = await _imaging.ReadTextAsync(_document.Current!);
        });
    }

    void CopyText_Click(object sender, RoutedEventArgs e) => Copy(TextOutput.Text);

    async void Generate_Click(object sender, RoutedEventArgs e) => await CreateAsync(coloringBook: false, restyle: false, fill: false);

    async void Restyle_Click(object sender, RoutedEventArgs e) => await CreateAsync(coloringBook: false, restyle: true, fill: false);

    async void Coloring_Click(object sender, RoutedEventArgs e) => await CreateAsync(coloringBook: true, restyle: false, fill: false);

    async void FillPaint_Click(object sender, RoutedEventArgs e) => await CreateAsync(coloringBook: false, restyle: false, fill: true);

    void Stage_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    async void Stage_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count > 0 && items[0] is StorageFile file)
        {
            await LoadFileAsync(file);
        }
    }

    void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => Fit();

    void Gesture_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (_document.Current is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(GestureLayer);
        var (x, y) = ToImage(point.Position);
        if (x < 0)
        {
            return;
        }

        GestureLayer.CapturePointer(e.Pointer);
        if (_tool == "Erase")
        {
            EnsureMask();
            _painting = true;
            PixelOps.Stamp(_mask!, _maskWidth, _maskHeight, x, y, BrushRadius(), 255);
            DrawOverlay();
        }
        else if (_tool == "Crop")
        {
            _cropping = true;
            _cropStart = point.Position;
        }
        else if (_tool == "Select")
        {
            _selecting = true;
            _selectRight = point.Properties.IsRightButtonPressed;
            _selectStartX = x;
            _selectStartY = y;
        }
    }

    void Gesture_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_document.Current is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(GestureLayer);
        if (_painting)
        {
            var (x, y) = ToImage(point.Position);
            if (x >= 0)
            {
                PixelOps.Stamp(_mask!, _maskWidth, _maskHeight, x, y, BrushRadius(), 255);
                DrawOverlay();
            }
        }
        else if (_cropping)
        {
            PlaceCropRect(_cropStart, point.Position);
        }
    }

    void Gesture_Released(object sender, PointerRoutedEventArgs e)
    {
        if (_cropping)
        {
            _cropping = false;
            var end = e.GetCurrentPoint(GestureLayer).Position;
            StoreCrop(_cropStart, end);
        }

        if (_selecting)
        {
            _selecting = false;
            var (x, y) = ToImage(e.GetCurrentPoint(GestureLayer).Position);
            FinishSelect(x, y);
        }

        _painting = false;
        if (GestureLayer.PointerCaptures is { Count: > 0 })
        {
            GestureLayer.ReleasePointerCaptures();
        }
    }

    async Task OpenAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".heic", ".webp" })
        {
            picker.FileTypeFilter.Add(ext);
        }

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await LoadFileAsync(file);
        }
    }

    async Task LoadFileAsync(StorageFile file)
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        await Guard("Opening…", async () =>
        {
            var bitmap = await BitmapImages.LoadAsync(file);
            DiscardPreview();
            ClearMask();
            ResetSliders();
            _document.Open(bitmap, file.DisplayName);
            await ShowAsync(bitmap);
        });
    }

    async Task<bool> SaveAsync()
    {
        CommitPreview();
        if (_document.Current is null)
        {
            return false;
        }

        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.SuggestedFileName = _document.Title;
        picker.FileTypeChoices.Add("PNG", [".png"]);
        picker.FileTypeChoices.Add("JPEG", [".jpg"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return false;
        }

        var saved = false;
        await Guard("Saving…", async () =>
        {
            await BitmapImages.SaveAsync(_document.Current, file);
            _document.NoteSaved(file.DisplayName);
            saved = true;
        });
        return saved;
    }

    async Task UndoAsync()
    {
        DiscardPreview();
        if (_document.Undo() && _document.Current is not null)
        {
            ClearMask();
            await ShowAsync(_document.Current);
        }

        UpdateChrome();
    }

    async Task RedoAsync()
    {
        DiscardPreview();
        if (_document.Redo() && _document.Current is not null)
        {
            ClearMask();
            await ShowAsync(_document.Current);
        }

        UpdateChrome();
    }

    async Task TransformAsync(Func<SoftwareBitmap, SoftwareBitmap> transform)
    {
        if (!RequireImage())
        {
            return;
        }

        await Guard("Updating the photo…", async () =>
        {
            CommitPreview();
            var next = transform(_document.Current!);
            ClearMask();
            _document.Replace(next);
            await ShowAsync(next);
        });
    }

    async Task ScaleAsync(int factor)
    {
        if (!RequireImage())
        {
            return;
        }

        var width = _document.Width * factor;
        var height = _document.Height * factor;
        if ((long)width * height > 48_000_000)
        {
            ShowError($"A {factor}× result would be {width}×{height}. That is too large for this 16 GB laptop.");
            return;
        }

        await Guard(factor == 1 ? "Sharpening on the NPU…" : $"Scaling {factor}× on the NPU…", async () =>
        {
            var next = await _imaging.UpscaleAsync(_document.Current!, width, height);
            _document.Replace(next);
            await ShowAsync(next);
        });
    }

    async Task UseSelectionAsync(bool erase)
    {
        if (!RequireImage() || !MaskHasPaint())
        {
            ShowError("Find an object before using the selection.");
            return;
        }

        await Guard(erase ? "Erasing the selection on the NPU…" : "Cutting out the selection…", async () =>
        {
            using var gray = BitmapImages.GrayFrom(_mask!, _maskWidth, _maskHeight);
            var next = erase
                ? await _imaging.EraseAsync(_document.Current!, gray)
                : PixelOps.Composite(_document.Current!, gray, 0, 0, 0, true);
            _document.Replace(next);
            await ShowAsync(next);
        });
    }

    async Task CreateAsync(bool coloringBook, bool restyle, bool fill)
    {
        var prompt = PromptBox.Text.Trim();
        if (prompt.Length == 0)
        {
            ShowError("Write a prompt first.");
            return;
        }

        if ((restyle || fill) && !RequireImage())
        {
            return;
        }

        if (fill && !MaskHasPaint())
        {
            ShowError("Paint the area to fill with the Erase brush, then come back to Create.");
            return;
        }

        if (!restyle && !fill && _document.IsDirty && !await ConfirmDiscardAsync())
        {
            return;
        }

        var steps = (int)StepsSlider.Value;
        var creativity = CreativitySlider.Value;
        var seed = int.TryParse(SeedBox.Text, out var parsed) ? parsed : Random.Shared.Next();
        SeedBox.Text = seed.ToString();
        await Guard("Generating on the NPU…", async () =>
        {
            SoftwareBitmap created;
            if (fill)
            {
                using var gray = BitmapImages.GrayFrom(_mask!, _maskWidth, _maskHeight);
                created = await _imaging.FillAsync(_document.Current!, gray, prompt, steps, creativity, seed);
                _document.Replace(created);
            }
            else if (restyle)
            {
                created = await _imaging.RestyleAsync(_document.Current!, prompt, steps, creativity, seed, (float)ColorKeepSlider.Value);
                _document.Replace(created);
            }
            else
            {
                var style = coloringBook
                    ? Microsoft.Windows.AI.Imaging.ImageFromTextGenerationStyle.ColoringBook
                    : Microsoft.Windows.AI.Imaging.ImageFromTextGenerationStyle.Default;
                created = await _imaging.GenerateAsync(prompt, steps, creativity, seed, style);
                DiscardPreview();
                ClearMask();
                _document.Open(created, coloringBook ? "Coloring book" : "Generated");
            }

            await ShowAsync(created);
        });
    }

    void ScheduleAdjust()
    {
        if (!_ready || _suppressSliders || _tool != "Adjust" || _document.Current is null)
        {
            return;
        }

        var generation = ++_adjustGeneration;
        _ = ApplyAdjustPreviewAsync(generation);
    }

    async Task ApplyAdjustPreviewAsync(int generation)
    {
        await Task.Delay(90);
        if (generation != _adjustGeneration || _document.Current is null)
        {
            return;
        }

        var settings = CurrentAdjust();
        if (settings.IsNeutral)
        {
            DiscardPreview();
            await ShowAsync(_document.Current);
            return;
        }

        var preview = PixelOps.Apply(_document.Current, settings);
        if (generation != _adjustGeneration)
        {
            preview.Dispose();
            return;
        }

        _preview?.Dispose();
        _preview = preview;
        await ShowAsync(preview);
    }

    AdjustSettings CurrentAdjust() => AdjustSettings.FromSliders(
        BrightnessSlider.Value,
        ContrastSlider.Value,
        SaturationSlider.Value,
        TemperatureSlider.Value,
        FadeSlider.Value,
        VignetteSlider.Value,
        SharpnessSlider.Value);

    void ResetSliders()
    {
        _suppressSliders = true;
        BrightnessSlider.Value = 0;
        ContrastSlider.Value = 0;
        SaturationSlider.Value = 0;
        TemperatureSlider.Value = 0;
        FadeSlider.Value = 0;
        VignetteSlider.Value = 0;
        SharpnessSlider.Value = 0;
        _suppressSliders = false;
    }

    void CommitPreview()
    {
        if (_preview is null)
        {
            return;
        }

        var next = _preview;
        _preview = null;
        _document.Replace(next);
    }

    void DiscardPreview()
    {
        _preview?.Dispose();
        _preview = null;
        _adjustGeneration++;
    }

    async Task ShowAsync(SoftwareBitmap bitmap)
    {
        var source = await BitmapImages.ToSourceAsync(bitmap);
        PhotoView.Source = source;
        _shown = source;
        EmptyState.Visibility = Visibility.Collapsed;
        Frame.Visibility = Visibility.Visible;
        Fit();
        DrawOverlay();
        UpdateChrome();
    }

    void Fit()
    {
        if (_document.Current is null || Stage.ActualWidth < 8 || Stage.ActualHeight < 8)
        {
            return;
        }

        var scale = Math.Min((Stage.ActualWidth - 36) / _document.Width, (Stage.ActualHeight - 36) / _document.Height);
        if (scale <= 0 || double.IsInfinity(scale) || double.IsNaN(scale))
        {
            return;
        }

        Frame.Width = Math.Max(1, _document.Width * scale);
        Frame.Height = Math.Max(1, _document.Height * scale);
    }

    void UpdateChrome()
    {
        var hasPhoto = _document.Current is not null;
        SaveButton.IsEnabled = hasPhoto;
        UndoButton.IsEnabled = _document.CanUndo;
        RedoButton.IsEnabled = _document.CanRedo;
        CompareButton.IsEnabled = _document.Original is not null;
        TitleText.Text = hasPhoto
            ? $"{_document.Title}  ·  {_document.Width} × {_document.Height}" + (_document.IsDirty ? "  ·  edited" : "")
            : "Offline on the Hexagon NPU";
        StatusText.Text = hasPhoto
            ? $"{_document.Width} × {_document.Height}   ·   Hexagon NPU   ·   offline"
            : "Hexagon NPU   ·   Snapdragon X Elite   ·   offline";
        if (hasPhoto)
        {
            UpscaleReadout.Text = $"Now {_document.Width} × {_document.Height}.  2× becomes {_document.Width * 2} × {_document.Height * 2}.  4× becomes {_document.Width * 4} × {_document.Height * 4}.";
        }
    }

    (int X, int Y) ToImage(Windows.Foundation.Point point)
    {
        if (_document.Current is null || Frame.Width < 1 || Frame.Height < 1)
        {
            return (-1, -1);
        }

        var x = (int)(point.X / Frame.Width * _document.Width);
        var y = (int)(point.Y / Frame.Height * _document.Height);
        if (x < 0 || y < 0 || x >= _document.Width || y >= _document.Height)
        {
            return (-1, -1);
        }

        return (x, y);
    }

    int BrushRadius()
    {
        if (_document.Current is null || Frame.Width < 1)
        {
            return 12;
        }

        return Math.Max(2, (int)(BrushSlider.Value / Frame.Width * _document.Width));
    }

    void EnsureMask()
    {
        if (_document.Current is null)
        {
            return;
        }

        if (_mask is not null && _maskWidth == _document.Width && _maskHeight == _document.Height)
        {
            return;
        }

        _maskWidth = _document.Width;
        _maskHeight = _document.Height;
        _mask = new byte[_maskWidth * _maskHeight];
    }

    void AdoptMask(SoftwareBitmap mask)
    {
        EnsureMask();
        var gray = mask.BitmapPixelFormat == BitmapPixelFormat.Gray8
            ? mask
            : SoftwareBitmap.Convert(mask, BitmapPixelFormat.Gray8, BitmapAlphaMode.Ignore);
        var bytes = new byte[gray.PixelWidth * gray.PixelHeight];
        gray.CopyToBuffer(bytes.AsBuffer());
        if (_mask is null)
        {
            return;
        }

        for (var y = 0; y < _maskHeight; y++)
        {
            var sourceY = Math.Clamp(y * gray.PixelHeight / _maskHeight, 0, gray.PixelHeight - 1);
            for (var x = 0; x < _maskWidth; x++)
            {
                var sourceX = Math.Clamp(x * gray.PixelWidth / _maskWidth, 0, gray.PixelWidth - 1);
                _mask[y * _maskWidth + x] = bytes[sourceY * gray.PixelWidth + sourceX];
            }
        }

        if (!ReferenceEquals(gray, mask))
        {
            gray.Dispose();
        }

        DrawOverlay();
    }

    bool MaskHasPaint() => _mask is not null && _mask.Any(value => value > 8);

    void ClearMask()
    {
        _mask = null;
        _maskWidth = 0;
        _maskHeight = 0;
        MaskView.Source = null;
    }

    void DrawOverlay()
    {
        if (_mask is null || Frame.Width < 2 || Frame.Height < 2 || _tool is not ("Erase" or "Select" or "Create"))
        {
            MaskView.Source = null;
            return;
        }

        var width = Math.Max(1, (int)Frame.Width);
        var height = Math.Max(1, (int)Frame.Height);
        var bitmap = new WriteableBitmap(width, height);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Clamp((int)(y / (double)height * _maskHeight), 0, _maskHeight - 1);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Clamp((int)(x / (double)width * _maskWidth), 0, _maskWidth - 1);
                var coverage = _mask[sourceY * _maskWidth + sourceX];
                if (coverage < 8)
                {
                    continue;
                }

                var index = (y * width + x) * 4;
                pixels[index] = 107;
                pixels[index + 1] = 165;
                pixels[index + 2] = 226;
                pixels[index + 3] = (byte)Math.Min(170, (int)coverage);
            }
        }

        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }

        bitmap.Invalidate();
        MaskView.Source = bitmap;
    }

    void PlaceCropRect(Windows.Foundation.Point start, Windows.Foundation.Point end)
    {
        var x = Math.Min(start.X, end.X);
        var y = Math.Min(start.Y, end.Y);
        Canvas.SetLeft(CropRect, x);
        Canvas.SetTop(CropRect, y);
        CropRect.Width = Math.Max(1, Math.Abs(end.X - start.X));
        CropRect.Height = Math.Max(1, Math.Abs(end.Y - start.Y));
        CropRect.Visibility = Visibility.Visible;
    }

    void StoreCrop(Windows.Foundation.Point start, Windows.Foundation.Point end)
    {
        var (x0, y0) = ToImage(start);
        var (x1, y1) = ToImage(end);
        if (x0 < 0 || x1 < 0)
        {
            return;
        }

        _cropX = Math.Min(x0, x1);
        _cropY = Math.Min(y0, y1);
        _cropWidth = Math.Abs(x1 - x0);
        _cropHeight = Math.Abs(y1 - y0);
        _hasCrop = _cropWidth > 4 && _cropHeight > 4;
        CropReadout.Text = _hasCrop ? $"{_cropWidth} × {_cropHeight} from {_cropX}, {_cropY}" : "Drag a larger crop.";
    }

    void FinishSelect(int x, int y)
    {
        if (x < 0)
        {
            return;
        }

        var distance = Math.Abs(x - _selectStartX) + Math.Abs(y - _selectStartY);
        if (distance < 8)
        {
            var list = _selectRight ? _excludePoints : _includePoints;
            if (_includePoints.Count + _excludePoints.Count + (_selectRect is null ? 0 : 2) >= 32)
            {
                ShowError("That is as many hints as the model accepts.");
                return;
            }

            list.Add(new PointInt32(_selectStartX, _selectStartY));
        }
        else
        {
            _selectRect = new RectInt32(
                Math.Min(_selectStartX, x),
                Math.Min(_selectStartY, y),
                Math.Abs(x - _selectStartX),
                Math.Abs(y - _selectStartY));
        }

        var box = _selectRect is RectInt32 rect ? $" Box {rect.Width}×{rect.Height}." : "";
        SelectReadout.Text = $"{_includePoints.Count} include, {_excludePoints.Count} exclude.{box}";
    }

    bool RequireImage()
    {
        if (_document.Current is not null)
        {
            return true;
        }

        ShowError("Open a photo first.");
        return false;
    }

    async Task<bool> ConfirmModelAsync(string feature, string state)
    {
        var dialog = new ContentDialog
        {
            Title = "Download on-device model",
            Content = $"{feature} is {state.ToLowerInvariant()}. Windows will download it through Windows Update and keep it on this PC. Image generation is a few gigabytes and can take several minutes. The model runs on the Hexagon NPU, offline.",
            PrimaryButtonText = "Download",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    async Task<bool> ConfirmDiscardAsync()
    {
        if (!_document.IsDirty)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            Title = "Save this photo?",
            Content = "You have edits that are not saved.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None)
        {
            return false;
        }

        if (choice == ContentDialogResult.Primary)
        {
            return await SaveAsync();
        }

        return true;
    }

    async Task Guard(string busy, Func<Task> work)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        BusyText.Text = busy;
        BusyLayer.Visibility = Visibility.Visible;
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StudioLog.Write(ex.ToString());
            ShowError(ex.Message);
        }
        finally
        {
            _busy = false;
            BusyLayer.Visibility = Visibility.Collapsed;
            UpdateChrome();
        }
    }

    void ShowError(string message)
    {
        Notice.Title = "Surface AI Studio";
        Notice.Message = message;
        Notice.Severity = InfoBarSeverity.Warning;
        Notice.IsOpen = true;
    }

    void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        StatusText.Text = "Copied";
    }
}
