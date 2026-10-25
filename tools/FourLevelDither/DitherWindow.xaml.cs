using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using DitherProcessor = DiscImageStudio.Imaging.GrayscaleDither;

namespace DiscImageStudio.DitherTool;

public partial class DitherWindow : Window
{
    private BitmapSource? _source;
    private BitmapSource? _result;
    private string? _inputPath;
    private string? _requestedPath;
    private DitherMode _mode;
    private bool _linearizeSrgb;
    private bool _initialized;
    private CancellationTokenSource? _cancellation;
    private int _revision;
    private bool _closed;
    private bool _saving;
    private bool _closeAfterSave;

    public DitherWindow(string? initialImage = null, DitherMode initialMode = DitherMode.Dvd, bool initialLinearizeSrgb = false)
    {
        _mode = initialMode;
        _linearizeSrgb = initialLinearizeSrgb;
        InitializeComponent();
        ModePicker.SelectedIndex = _mode == DitherMode.Dvd ? 0 : 1;
        GammaToggle.IsChecked = _linearizeSrgb;
        UpdateModeDisplay();
        UpdateGammaDisplay();
        _initialized = true;
        if (initialImage is not null) Loaded += async (_, _) => await LoadImageAsync(initialImage);
        Closing += (_, e) =>
        {
            if (!_saving) return;
            e.Cancel = true;
            _closeAfterSave = true;
            StatusText.Text = "正在完成 PNG 保存，保存后将关闭…";
        };
        Closed += (_, _) => { _closed = true; _cancellation?.Cancel(); };
    }

    internal bool HasResult => _result is not null;

    internal Task LoadImageAsync(string path) => ProcessImageAsync(path, null);

    private async Task ProcessImageAsync(string path, BitmapSource? cachedSource)
    {
        if (_saving || _closed) return;
        _cancellation?.Cancel();
        using CancellationTokenSource cancellation = new();
        _cancellation = cancellation;
        int revision = ++_revision;
        DitherMode mode = _mode;
        bool linearizeSrgb = _linearizeSrgb;
        _requestedPath = path;
        _source = cachedSource;
        _result = null;
        _inputPath = null;
        SourceImage.Source = cachedSource;
        ResultImage.Source = null;
        SourcePlaceholder.Visibility = cachedSource is null ? Visibility.Visible : Visibility.Collapsed;
        ResultPlaceholder.Visibility = Visibility.Visible;
        SaveButton.IsEnabled = false;
        FileText.Text = Path.GetFileName(path);
        FileText.ToolTip = null;
        SourceInfo.Text = string.Empty;
        StatusText.ToolTip = null;
        StatusText.Text = $"正在处理 {DitherModes.Label(mode)}抖动（{DitherModes.PaletteLabel(mode)}）· {GammaStatus(linearizeSrgb)}…";
        BusyBar.Visibility = Visibility.Visible;
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            string fullPath = Path.GetFullPath(path);
            (BitmapSource source, BitmapSource result) = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                BitmapSource loaded = cachedSource ?? DitherProcessor.LoadImage(fullPath);
                return (loaded, DitherProcessor.Process(loaded, DitherModes.Levels(mode), cancellation.Token, linearizeSrgb));
            }, cancellation.Token);
            if (_closed || revision != _revision) return;
            _source = source;
            _result = result;
            _inputPath = fullPath;
            SourceImage.Source = source;
            ResultImage.Source = result;
            SourcePlaceholder.Visibility = Visibility.Collapsed;
            ResultPlaceholder.Visibility = Visibility.Collapsed;
            SourceInfo.Text = $"{source.PixelWidth:N0} × {source.PixelHeight:N0} 像素";
            FileText.Text = fullPath;
            FileText.ToolTip = fullPath;
            SaveButton.IsEnabled = true;
            StatusText.Text = $"{DitherModes.Label(mode)}处理完成 · {GammaStatus(linearizeSrgb)} · {source.PixelWidth:N0} × {source.PixelHeight:N0} · {stopwatch.Elapsed.TotalSeconds:F2} 秒";
            UpdatePreviewSizes();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_closed && revision == _revision) StatusText.Text = $"处理失败：{exception.Message}";
        }
        finally
        {
            if (revision == _revision)
            {
                _cancellation = null;
                BusyBar.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _saving || _closed) return;
        _mode = ModePicker.SelectedIndex == 0 ? DitherMode.Dvd : DitherMode.Cd;
        UpdateModeDisplay();
        if (_requestedPath is not null) await ProcessImageAsync(_requestedPath, _source);
    }

    private void UpdateModeDisplay()
    {
        ResultHeading.Text = DitherModes.Label(_mode) + "抖动结果";
        SaveButton.Content = "保存黑白 PNG";
        ModeHint.Text = _mode == DitherMode.Dvd
            ? "A5 → 黑色 0 · 92 → 白色 255"
            : "0D → 黑色 0 · AA → 白色 255";
    }

    private async void Gamma_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _saving || _closed) return;
        _linearizeSrgb = GammaToggle.IsChecked == true;
        UpdateGammaDisplay();
        if (_requestedPath is not null) await ProcessImageAsync(_requestedPath, _source);
    }

    private void UpdateGammaDisplay()
    {
        GammaHint.Text = _linearizeSrgb
            ? "已开启：按线性亮度分配黑白像素，中间调通常更深"
            : "已关闭：保持现有明暗分配";
        ResultDetails.Text = $"蛇形 Floyd–Steinberg · {GammaStatus(_linearizeSrgb)} · 无损 PNG";
        if (_requestedPath is null) StatusText.Text = "就绪 · " + GammaStatus(_linearizeSrgb);
    }

    private static string GammaStatus(bool linearizeSrgb) => linearizeSrgb ? "sRGB 伽马：开" : "sRGB 伽马：关";

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "选择原图",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) await LoadImageAsync(dialog.FileName);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null || _inputPath is null || _saving) return;
        SaveFileDialog dialog = new()
        {
            Title = "保存" + DitherModes.Suffix(_mode) + " PNG",
            Filter = "PNG 图片|*.png",
            DefaultExt = ".png", AddExtension = true, OverwritePrompt = true,
            FileName = Path.GetFileNameWithoutExtension(_inputPath) + "-" + DitherModes.Suffix(_mode)
                + (_linearizeSrgb ? "-sRGB" : string.Empty) + ".png",
            InitialDirectory = Path.GetDirectoryName(_inputPath),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            Program.ValidateOutputPath(_inputPath, dialog.FileName);
            BitmapSource result = _result;
            DitherMode mode = _mode;
            bool linearizeSrgb = _linearizeSrgb;
            _saving = true;
            OpenButton.IsEnabled = false;
            SaveButton.IsEnabled = false;
            ModePicker.IsEnabled = false;
            GammaToggle.IsEnabled = false;
            BusyBar.Visibility = Visibility.Visible;
            StatusText.Text = "正在保存 PNG · " + GammaStatus(linearizeSrgb) + "…";
            await Task.Run(() => DitherProcessor.SavePng(result, dialog.FileName, DitherModes.Levels(mode)));
            StatusText.Text = $"已保存 · {GammaStatus(linearizeSrgb)}：{dialog.FileName}";
            StatusText.ToolTip = dialog.FileName;
        }
        catch (Exception exception)
        {
            _closeAfterSave = false;
            StatusText.Text = $"保存失败：{exception.Message}";
        }
        finally
        {
            _saving = false;
            OpenButton.IsEnabled = true;
            ModePicker.IsEnabled = true;
            GammaToggle.IsEnabled = true;
            SaveButton.IsEnabled = _result is not null;
            BusyBar.Visibility = Visibility.Collapsed;
            if (_closeAfterSave) Close();
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_saving && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_saving && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            await LoadImageAsync(paths[0]);
    }

    private void PreviewMode_Changed(object sender, RoutedEventArgs e) => UpdatePreviewSizes();
    private void Preview_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewSizes();

    private void UpdatePreviewSizes()
    {
        if (_source is null || _result is null) return;
        ResizePreview(SourceImage, SourceScroll, _source);
        ResizePreview(ResultImage, ResultScroll, _result);
    }

    private void ResizePreview(Image image, ScrollViewer scroll, BitmapSource bitmap)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double width = bitmap.PixelWidth / dpi.DpiScaleX;
        double height = bitmap.PixelHeight / dpi.DpiScaleY;
        double scale = ActualSizeToggle.IsChecked == true ? 1 : Math.Min(1, Math.Min(
            Math.Max(1, scroll.ActualWidth - 24) / width,
            Math.Max(1, scroll.ActualHeight - 24) / height));
        image.Width = width * scale;
        image.Height = height * scale;
    }
}
