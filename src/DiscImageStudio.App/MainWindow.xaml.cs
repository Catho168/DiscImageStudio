using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Cd;
using Microsoft.Win32;

namespace DiscImageStudio;

public partial class MainWindow : Window
{
    private const int PreviewTabIndex = 3;
    private const int LogTabIndex = 4;

    private static readonly string LivePreviewDirectory = Path.Combine(
        Path.GetTempPath(),
        "DiscImageStudio");

    private readonly DispatcherTimer _livePreviewTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(450),
    };
    private readonly List<(TextBox Primary, TextBox Live)> _livePreviewBindings = [];
    private bool _isBusy;
    private bool _isLivePreviewReady;
    private bool _livePreviewBindingsInitialized;
    private bool _isSynchronizingLivePreview;
    private bool _livePreviewRefreshRunning;
    private bool _livePreviewRefreshPending;
    private int _livePreviewRevision;
    private CancellationTokenSource? _livePreviewCancellation;
    private string? _lastLivePreviewPath;
    private string? _lastOutputPath;

    public MainWindow()
    {
        InitializeComponent();
        Version version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);
        VersionText.Text = $"版本 {version.Major}.{version.Minor}.{version.Build} · 本地处理";
        _livePreviewTimer.Tick += LivePreviewTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    internal bool IsLivePreviewReady => _isLivePreviewReady;

    internal void SetLivePreviewAngleForSnapshot(string angle)
    {
        if (LiveDvdRadio.IsChecked == true)
        {
            LiveDvdStartAngle.Text = angle;
        }
        else
        {
            LiveCdActualStartAngle.Text = angle;
        }
    }

    internal void ConfigureSnapshot(
        int selectedTab,
        string? previewPath,
        string? liveInputPath = null,
        string? liveDisc = null)
    {
        if (selectedTab < 0 || selectedTab >= ContentTabs.Items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedTab));
        }

        ContentTabs.SelectedIndex = selectedTab;
        if (!string.IsNullOrWhiteSpace(previewPath))
        {
            string fullPath = Path.GetFullPath(previewPath);
            ContentTabs.SelectedIndex = PreviewTabIndex;
            PrepareResultPreview(fullPath);
            ShowResultPreview(fullPath);
        }
        if (!string.IsNullOrWhiteSpace(liveInputPath))
        {
            bool dvd = string.Equals(liveDisc, "dvd", StringComparison.OrdinalIgnoreCase);
            LiveCdRadio.IsChecked = !dvd;
            LiveDvdRadio.IsChecked = dvd;
            UpdateLivePreviewMode(clearResult: false);
            if (dvd)
            {
                DvdImagePath.Text = Path.GetFullPath(liveInputPath);
            }
            else
            {
                CdImagePath.Text = Path.GetFullPath(liveInputPath);
            }
            ContentTabs.SelectedIndex = PreviewTabIndex;
        }
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int index))
        {
            ContentTabs.SelectedIndex = index;
            if (index == PreviewTabIndex)
            {
                ScheduleLivePreview();
            }
        }
    }

    private void BrowseDvdImage_Click(object sender, RoutedEventArgs e)
        => BrowseImage(DvdImagePath, DvdOutputPath, DvdPreviewPath, "dvd-image.iso", "dvd-preview.png");

    private void BrowseCdImage_Click(object sender, RoutedEventArgs e)
        => BrowseImage(CdImagePath, CdOutputPath, CdPreviewPath, "cd-track.raw", "cd-preview.png");

    private void BrowseLiveCdImage_Click(object sender, RoutedEventArgs e)
        => BrowseImage(CdImagePath, CdOutputPath, CdPreviewPath, "cd-track.raw", "cd-preview.png");

    private void BrowseLiveDvdImage_Click(object sender, RoutedEventArgs e)
        => BrowseImage(DvdImagePath, DvdOutputPath, DvdPreviewPath, "dvd-image.iso", "dvd-preview.png");

    private void BrowseDvdOutput_Click(object sender, RoutedEventArgs e)
        => BrowseSave(DvdOutputPath, "ISO 镜像|*.iso|所有文件|*.*", ".iso", "dvd-image.iso");

    private void BrowseDvdPreview_Click(object sender, RoutedEventArgs e)
        => BrowseSave(DvdPreviewPath, "PNG 图片|*.png", ".png", "dvd-preview.png");

    private void BrowseCdOutput_Click(object sender, RoutedEventArgs e)
        => BrowseSave(CdOutputPath, "CD 原始音轨|*.raw|所有文件|*.*", ".raw", "cd-track.raw");

    private void BrowseCdPreview_Click(object sender, RoutedEventArgs e)
        => BrowseSave(CdPreviewPath, "PNG 图片|*.png", ".png", "cd-preview.png");

    private void BrowseDvdData_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "选择要放入 DVD 内圈的文件夹",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            DvdDataDirectory.Text = dialog.FolderName;
        }
    }

    private async void StartDvd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string image = RequirePath(DvdImagePath, "请选择 DVD 源图片。");
            string output = RequirePath(DvdOutputPath, "请选择 DVD ISO 输出位置。");
            List<string> arguments =
            [
                "solve",
                "--image", image,
                "--iso-output", output,
                "--total-sectors", DvdTotalSectors.Text.Trim(),
                "--inner-radius-mm", DvdInnerRadius.Text.Trim(),
                "--outer-radius-mm", DvdOuterRadius.Text.Trim(),
                "--channel-bit-nm", DvdChannelBit.Text.Trim(),
                "--start-angle-deg", DvdStartAngle.Text.Trim(),
                "--spiral-direction", "cw",
                "--image-threshold", "128",
                "--alpha-threshold", "1",
                "--algorithm", "dispersion",
                "--iterations-per-block", "1",
                "--fast-output", "true",
                "--volume-label", string.IsNullOrWhiteSpace(DvdVolumeLabel.Text) ? "DISC_IMAGE" : DvdVolumeLabel.Text.Trim(),
            ];
            if (string.IsNullOrWhiteSpace(DvdDataDirectory.Text))
            {
                arguments.Add("--fill-sectors");
                arguments.Add(DvdTotalSectors.Text.Trim());
            }
            else
            {
                arguments.Add("--data-dir");
                arguments.Add(DvdDataDirectory.Text.Trim());
            }

            await RunCommandAsync("正在生成 DVD ISO…", arguments.ToArray(), output);
        }
        catch (Exception exception)
        {
            ShowValidationError(exception);
        }
    }

    private async void PreviewDvd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string image = RequirePath(DvdImagePath, "请选择 DVD 源图片。");
            string output = RequirePath(DvdPreviewPath, "请选择校准预览输出位置。");
            string[] arguments =
            [
                "calibrate",
                "--image", image,
                "--output", output,
                "--total-sectors", DvdTotalSectors.Text.Trim(),
                "--fill-sectors", DvdTotalSectors.Text.Trim(),
                "--generated-inner-radius-mm", DvdInnerRadius.Text.Trim(),
                "--generated-outer-radius-mm", DvdOuterRadius.Text.Trim(),
                "--actual-inner-radius-mm", DvdActualInnerRadius.Text.Trim(),
                "--actual-outer-radius-mm", DvdActualOuterRadius.Text.Trim(),
                "--channel-bit-nm", DvdChannelBit.Text.Trim(),
                "--start-angle-deg", DvdStartAngle.Text.Trim(),
                "--spiral-direction", "cw",
                "--image-threshold", "128",
                "--alpha-threshold", "1",
                "--preview-size", DvdPreviewSize.Text.Trim(),
                "--samples-per-sector", DvdSamplesPerSector.Text.Trim(),
            ];
            await RunCommandAsync("正在生成 DVD 校准预览…", arguments, output);
        }
        catch (Exception exception)
        {
            ShowValidationError(exception);
        }
    }

    private async void StartCd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string input = RequirePath(CdImagePath, "请选择 CD 源图片。");
            string output = RequirePath(CdOutputPath, "请选择 CD 原始音轨输出位置。");
            List<string> arguments = ["cd-generate", "--input", input, "--output", output];
            AddCdGeometry(arguments, string.Empty, actual: false);
            arguments.Add("--interleave");
            arguments.Add((CdInterleave.IsChecked == true).ToString().ToLowerInvariant());
            await RunCommandAsync("正在生成 CD 原始音轨…", arguments.ToArray(), output);
        }
        catch (Exception exception)
        {
            ShowValidationError(exception);
        }
    }

    private async void PreviewCdWarp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string input = RequirePath(CdImagePath, "请选择 CD 源图片。");
            string output = RequirePath(CdPreviewPath, "请选择 CD 预览输出位置。");
            List<string> arguments = ["cd-preview-warp", "--input", input, "--output", output];
            AddCdGeometry(arguments, "gen-", actual: false);
            AddCdGeometry(arguments, "actual-", actual: true);
            arguments.AddRange(["--size", CdPreviewSize.Text.Trim(), "--samples-per-sector", CdSamplesPerSector.Text.Trim()]);
            await RunCommandAsync("正在生成 CD 几何预览…", arguments.ToArray(), output);
        }
        catch (Exception exception)
        {
            ShowValidationError(exception);
        }
    }

    private async void PreviewCdTrack_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string track = RequirePath(CdOutputPath, "请选择或生成 CD 原始音轨。");
            string output = RequirePath(CdPreviewPath, "请选择 CD 预览输出位置。");
            List<string> arguments = ["cd-preview-track", "--track", track, "--output", output];
            AddCdGeometry(arguments, string.Empty, actual: true);
            arguments.AddRange(["--size", CdPreviewSize.Text.Trim(), "--byte-step", "48"]);
            await RunCommandAsync("正在预览 CD 音轨…", arguments.ToArray(), output);
        }
        catch (Exception exception)
        {
            ShowValidationError(exception);
        }
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastOutputPath))
        {
            return;
        }

        string? directory = Directory.Exists(_lastOutputPath)
            ? _lastOutputPath
            : Path.GetDirectoryName(_lastOutputPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
    }

    private async Task RunCommandAsync(string status, string[] arguments, string outputPath)
    {
        if (_isBusy)
        {
            MessageBox.Show(this, "已有任务正在运行。", "Disc Image Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _isBusy = true;
        SetActionButtonsEnabled(false);
        BusyProgress.Visibility = Visibility.Visible;
        StatusText.Text = status;
        ContentTabs.SelectedIndex = LogTabIndex;
        if (IsPngPath(outputPath))
        {
            PrepareResultPreview(outputPath);
        }
        LogTextBox.AppendText($"\n[{DateTime.Now:HH:mm:ss}] {status}\n");
        DispatcherTextWriter writer = new(Dispatcher, AppendLog);
        TextWriter previousOutput = Console.Out;
        TextWriter previousError = Console.Error;
        int exitCode;
        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            exitCode = await RunOnStaThreadAsync(() => UnifiedCommandRunner.Run(arguments));
            writer.Flush();
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
            BusyProgress.Visibility = Visibility.Collapsed;
            SetActionButtonsEnabled(true);
            _isBusy = false;
        }

        if (exitCode == 0)
        {
            _lastOutputPath = Path.GetFullPath(outputPath);
            OpenOutputButton.IsEnabled = true;
            StatusText.Text = "完成";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 完成：{_lastOutputPath}\n");
            if (IsPngPath(_lastOutputPath))
            {
                ShowResultPreview(_lastOutputPath);
                ContentTabs.SelectedIndex = PreviewTabIndex;
            }
        }
        else
        {
            StatusText.Text = "未完成，请查看日志";
            MessageBox.Show(this, "任务未完成，请查看运行日志中的错误信息。", "Disc Image Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddCdGeometry(List<string> arguments, string prefix, bool actual)
    {
        arguments.AddRange(
        [
            "--" + prefix + "r0", actual ? CdActualInnerRadius.Text.Trim() : CdInnerRadius.Text.Trim(),
            "--" + prefix + "r1", actual ? CdActualOuterRadius.Text.Trim() : CdOuterRadius.Text.Trim(),
            "--" + prefix + "sectors", CdSectors.Text.Trim(),
            "--" + prefix + "velocity", actual ? CdActualVelocity.Text.Trim() : CdVelocity.Text.Trim(),
            "--" + prefix + "theta0", actual ? CdActualStartAngle.Text.Trim() : CdStartAngle.Text.Trim(),
            "--" + prefix + "outer", CdImageOuterRadius.Text.Trim(),
        ]);
    }

    private void BrowseImage(TextBox target, TextBox output, TextBox preview, string outputName, string previewName)
    {
        OpenFileDialog dialog = new()
        {
            Title = "选择源图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        target.Text = dialog.FileName;
        string directory = Path.GetDirectoryName(dialog.FileName) ?? Environment.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(output.Text))
        {
            output.Text = Path.Combine(directory, outputName);
        }

        if (string.IsNullOrWhiteSpace(preview.Text))
        {
            preview.Text = Path.Combine(directory, previewName);
        }
    }

    private void BrowseSave(TextBox target, string filter, string extension, string fileName)
    {
        SaveFileDialog dialog = new()
        {
            Title = "选择输出位置",
            Filter = filter,
            DefaultExt = extension,
            AddExtension = true,
            FileName = fileName,
        };
        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FileName;
        }
    }

    private static string RequirePath(TextBox textBox, string message)
    {
        string value = textBox.Text.Trim();
        return value.Length == 0 ? throw new ArgumentException(message) : value;
    }

    private void ShowValidationError(Exception exception)
        => MessageBox.Show(this, exception.Message, "请检查输入", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void SetActionButtonsEnabled(bool enabled)
    {
        StartDvdButton.IsEnabled = enabled;
        PreviewDvdButton.IsEnabled = enabled;
        StartCdButton.IsEnabled = enabled;
        PreviewCdWarpButton.IsEnabled = enabled;
        PreviewCdTrackButton.IsEnabled = enabled;
    }

    private void AppendLog(string text)
    {
        LogTextBox.AppendText(text);
        LogTextBox.ScrollToEnd();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        InitializeLivePreviewBindings();
        UpdateLivePreviewMode(clearResult: false);
        ScheduleLivePreview();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _livePreviewTimer.Stop();
        _livePreviewCancellation?.Cancel();
        _livePreviewCancellation?.Dispose();
        TryDeleteLivePreview(_lastLivePreviewPath);
    }

    private void InitializeLivePreviewBindings()
    {
        _livePreviewBindings.AddRange(
        [
            (CdImagePath, LiveCdImagePath),
            (CdInnerRadius, LiveCdInnerRadius),
            (CdOuterRadius, LiveCdOuterRadius),
            (CdSectors, LiveCdSectors),
            (CdVelocity, LiveCdVelocity),
            (CdStartAngle, LiveCdStartAngle),
            (CdImageOuterRadius, LiveCdImageOuterRadius),
            (CdActualInnerRadius, LiveCdActualInnerRadius),
            (CdActualOuterRadius, LiveCdActualOuterRadius),
            (CdActualVelocity, LiveCdActualVelocity),
            (CdActualStartAngle, LiveCdActualStartAngle),
            (CdPreviewSize, LiveCdPreviewSize),
            (CdSamplesPerSector, LiveCdSamplesPerSector),
            (DvdImagePath, LiveDvdImagePath),
            (DvdTotalSectors, LiveDvdTotalSectors),
            (DvdInnerRadius, LiveDvdInnerRadius),
            (DvdOuterRadius, LiveDvdOuterRadius),
            (DvdChannelBit, LiveDvdChannelBit),
            (DvdStartAngle, LiveDvdStartAngle),
            (DvdActualInnerRadius, LiveDvdActualInnerRadius),
            (DvdActualOuterRadius, LiveDvdActualOuterRadius),
            (DvdPreviewSize, LiveDvdPreviewSize),
            (DvdSamplesPerSector, LiveDvdSamplesPerSector),
        ]);

        foreach ((TextBox primary, TextBox live) in _livePreviewBindings)
        {
            live.Text = primary.Text;
            primary.TextChanged += (_, _) => SynchronizeLivePreviewText(primary, live);
            live.TextChanged += (_, _) => SynchronizeLivePreviewText(live, primary);
        }

        _livePreviewBindingsInitialized = true;
    }

    private void SynchronizeLivePreviewText(TextBox source, TextBox target)
    {
        if (_isSynchronizingLivePreview)
        {
            return;
        }

        try
        {
            _isSynchronizingLivePreview = true;
            if (!string.Equals(target.Text, source.Text, StringComparison.Ordinal))
            {
                target.Text = source.Text;
            }
        }
        finally
        {
            _isSynchronizingLivePreview = false;
        }

        ScheduleLivePreview();
    }

    private void LivePreviewDiscType_Click(object sender, RoutedEventArgs e)
    {
        UpdateLivePreviewMode(clearResult: true);
        ScheduleLivePreview();
    }

    private void UpdateLivePreviewMode(bool clearResult)
    {
        bool dvd = LiveDvdRadio.IsChecked == true;
        LiveCdControls.Visibility = dvd ? Visibility.Collapsed : Visibility.Visible;
        LiveDvdControls.Visibility = dvd ? Visibility.Visible : Visibility.Collapsed;
        string discName = dvd ? "DVD" : "CD";
        LivePreviewHeading.Text = $"{discName} 实时预览";
        ResultPreviewTitle.Text = $"{discName} 实时预览";
        ResultPreviewPlaceholderText.Text = $"请先选择 {discName} 源图片。";
        LivePreviewStatusText.Text = $"等待 {discName} 源图片";

        if (!clearResult)
        {
            return;
        }

        ResultPreviewImage.Source = null;
        ResultPreviewImage.Visibility = Visibility.Collapsed;
        ResultPreviewPlaceholder.Visibility = Visibility.Visible;
        ResultPreviewPath.Text = string.Empty;
        string? previousPath = _lastLivePreviewPath;
        _lastLivePreviewPath = null;
        TryDeleteLivePreview(previousPath);
    }

    private void ScheduleLivePreview()
    {
        if (!_livePreviewBindingsInitialized || !IsLoaded)
        {
            return;
        }

        _livePreviewCancellation?.Cancel();
        _livePreviewTimer.Stop();
        _livePreviewRevision++;
        _isLivePreviewReady = false;
        LivePreviewStatusText.Text = "参数已变化，准备刷新…";
        _livePreviewTimer.Start();
    }

    private async void LivePreviewTimer_Tick(object? sender, EventArgs e)
    {
        _livePreviewTimer.Stop();
        await RefreshLivePreviewAsync();
    }

    private async void RefreshLivePreview_Click(object sender, RoutedEventArgs e)
    {
        _livePreviewTimer.Stop();
        _livePreviewCancellation?.Cancel();
        _livePreviewRevision++;
        await RefreshLivePreviewAsync();
    }

    private async Task RefreshLivePreviewAsync()
    {
        if (_livePreviewRefreshRunning)
        {
            _livePreviewRefreshPending = true;
            return;
        }

        _livePreviewRefreshRunning = true;
        try
        {
            do
            {
                _livePreviewRefreshPending = false;
                int revision = _livePreviewRevision;
                if (LiveDvdRadio.IsChecked == true)
                {
                    await RefreshDvdLivePreviewAsync(revision);
                }
                else
                {
                    await RefreshCdLivePreviewAsync(revision);
                }

                if (revision != _livePreviewRevision)
                {
                    _livePreviewRefreshPending = true;
                }
            }
            while (_livePreviewRefreshPending && IsLoaded);
        }
        finally
        {
            _livePreviewRefreshRunning = false;
        }
    }

    private async Task RefreshCdLivePreviewAsync(int revision)
    {
        _livePreviewCancellation?.Cancel();
        _livePreviewCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _livePreviewCancellation = cancellation;

        if (!TryReadCdLivePreviewSettings(
                out string imagePath,
                out CdDiscParameters generated,
                out CdDiscParameters actual,
                out int outputSize,
                out int samplesPerSector,
                out string message))
        {
            LivePreviewStatusText.Text = message;
            if (ResultPreviewImage.Source is null)
            {
                ResultPreviewPlaceholderText.Text = message;
            }
            return;
        }

        Directory.CreateDirectory(LivePreviewDirectory);
        string outputPath = Path.Combine(
            LivePreviewDirectory,
            $"cd-live-preview-{revision}.png");
        LivePreviewStatusText.Text = $"正在刷新 {outputSize} px 实时预览…";
        bool keepOutput = false;

        try
        {
            await RunOnStaThreadAsync(() =>
            {
                CdTrackGenerator.PreviewWarp(
                    imagePath,
                    outputPath,
                    generated,
                    actual,
                    outputSize,
                    samplesPerSector,
                    cancellation.Token);
                return 0;
            });
            cancellation.Token.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            string? previousPath = _lastLivePreviewPath;
            _lastLivePreviewPath = outputPath;
            ShowResultPreview(outputPath);
            keepOutput = true;
            _isLivePreviewReady = true;
            ResultPreviewTitle.Text = "CD 实时预览";
            ResultPreviewPath.Text = Path.GetFileName(imagePath);
            LivePreviewStatusText.Text =
                $"已实时更新 · 灰度 · {outputSize} px · 每扇区 {samplesPerSector} 个快速采样";
            TryDeleteLivePreview(previousPath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LivePreviewStatusText.Text = $"实时预览失败：{exception.Message}";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 实时预览失败：{exception}\n");
        }
        finally
        {
            if (!keepOutput)
            {
                TryDeleteLivePreview(outputPath);
            }
        }
    }

    private async Task RefreshDvdLivePreviewAsync(int revision)
    {
        _livePreviewCancellation?.Cancel();
        _livePreviewCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _livePreviewCancellation = cancellation;

        Directory.CreateDirectory(LivePreviewDirectory);
        string outputPath = Path.Combine(
            LivePreviewDirectory,
            $"dvd-live-preview-{revision}.png");
        if (!TryBuildDvdLivePreviewCommand(
                outputPath,
                out string imagePath,
                out string[] arguments,
                out int outputSize,
                out int samplesPerSector,
                out string message))
        {
            LivePreviewStatusText.Text = message;
            if (ResultPreviewImage.Source is null)
            {
                ResultPreviewPlaceholderText.Text = message;
            }
            return;
        }

        LivePreviewStatusText.Text = $"正在刷新 {outputSize} px DVD 实时预览…";
        bool keepOutput = false;
        try
        {
            int exitCode = await RunOnStaThreadAsync(() => UnifiedCommandRunner.Run(arguments));
            if (exitCode != 0)
            {
                throw new InvalidOperationException("DVD 预览引擎未能完成渲染。");
            }

            cancellation.Token.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            string? previousPath = _lastLivePreviewPath;
            _lastLivePreviewPath = outputPath;
            ShowResultPreview(outputPath);
            keepOutput = true;
            _isLivePreviewReady = true;
            ResultPreviewTitle.Text = "DVD 实时预览";
            ResultPreviewPath.Text = Path.GetFileName(imagePath);
            LivePreviewStatusText.Text =
                $"已实时更新 · 灰度 · CW · {outputSize} px · 每扇区 {samplesPerSector} 个快速采样";
            TryDeleteLivePreview(previousPath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LivePreviewStatusText.Text = $"实时预览失败：{exception.Message}";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] DVD 实时预览失败：{exception}\n");
        }
        finally
        {
            if (!keepOutput)
            {
                TryDeleteLivePreview(outputPath);
            }
        }
    }

    private bool TryReadCdLivePreviewSettings(
        out string imagePath,
        out CdDiscParameters generated,
        out CdDiscParameters actual,
        out int outputSize,
        out int samplesPerSector,
        out string message)
    {
        imagePath = CdImagePath.Text.Trim();
        generated = null!;
        actual = null!;
        outputSize = 0;
        samplesPerSector = 0;
        if (imagePath.Length == 0)
        {
            message = "请先选择 CD 源图片。";
            return false;
        }
        if (!File.Exists(imagePath))
        {
            message = "CD 源图片不存在。";
            return false;
        }

        try
        {
            long sectors = ParsePositiveLong(CdSectors, "扇区数");
            double imageOuterRadius = ParsePositiveDouble(CdImageOuterRadius, "图片外半径");
            generated = new CdDiscParameters(
                ParsePositiveDouble(CdInnerRadius, "生成内半径"),
                ParsePositiveDouble(CdOuterRadius, "生成外半径"),
                sectors,
                ParsePositiveDouble(CdVelocity, "生成线速度"),
                ParseDouble(CdStartAngle, "生成起始角") * Math.PI / 180.0,
                imageOuterRadius);
            actual = new CdDiscParameters(
                ParsePositiveDouble(CdActualInnerRadius, "实测内半径"),
                ParsePositiveDouble(CdActualOuterRadius, "实测外半径"),
                sectors,
                ParsePositiveDouble(CdActualVelocity, "实测线速度"),
                ParseDouble(CdActualStartAngle, "实测起始角") * Math.PI / 180.0,
                imageOuterRadius);
            generated.Validate();
            actual.Validate();

            int configuredSize = ParsePositiveInt(CdPreviewSize, "预览尺寸");
            int configuredSamples = ParsePositiveInt(CdSamplesPerSector, "每扇区采样");
            outputSize = Math.Clamp(configuredSize, 256, 900);
            samplesPerSector = Math.Clamp(configuredSamples, 1, 2);
            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            message = exception.Message;
            return false;
        }
    }

    private bool TryBuildDvdLivePreviewCommand(
        string outputPath,
        out string imagePath,
        out string[] arguments,
        out int outputSize,
        out int samplesPerSector,
        out string message)
    {
        imagePath = DvdImagePath.Text.Trim();
        arguments = [];
        outputSize = 0;
        samplesPerSector = 0;
        if (imagePath.Length == 0)
        {
            message = "请先选择 DVD 源图片。";
            return false;
        }
        if (!File.Exists(imagePath))
        {
            message = "DVD 源图片不存在。";
            return false;
        }

        try
        {
            long totalSectorsValue = ParsePositiveLong(DvdTotalSectors, "总扇区数");
            if (totalSectorsValue > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException("总扇区数", "总扇区数超出 DVD 引擎支持范围。");
            }

            double generatedInner = ParsePositiveDouble(DvdInnerRadius, "生成内半径");
            double generatedOuter = ParsePositiveDouble(DvdOuterRadius, "生成外半径");
            double actualInner = ParsePositiveDouble(DvdActualInnerRadius, "实测内半径");
            double actualOuter = ParsePositiveDouble(DvdActualOuterRadius, "实测外半径");
            if (generatedInner >= generatedOuter || actualInner >= actualOuter)
            {
                throw new ArgumentOutOfRangeException("半径", "内半径必须小于外半径。");
            }

            double channelBit = ParsePositiveDouble(DvdChannelBit, "Channel bit");
            double startAngle = ParseDouble(DvdStartAngle, "起始角");
            outputSize = Math.Clamp(ParsePositiveInt(DvdPreviewSize, "预览尺寸"), 256, 900);
            samplesPerSector = Math.Clamp(ParsePositiveInt(DvdSamplesPerSector, "每扇区采样"), 1, 2);
            string totalSectors = totalSectorsValue.ToString(CultureInfo.InvariantCulture);
            arguments =
            [
                "calibrate",
                "--image", imagePath,
                "--output", outputPath,
                "--total-sectors", totalSectors,
                "--fill-sectors", totalSectors,
                "--generated-inner-radius-mm", generatedInner.ToString("R", CultureInfo.InvariantCulture),
                "--generated-outer-radius-mm", generatedOuter.ToString("R", CultureInfo.InvariantCulture),
                "--actual-inner-radius-mm", actualInner.ToString("R", CultureInfo.InvariantCulture),
                "--actual-outer-radius-mm", actualOuter.ToString("R", CultureInfo.InvariantCulture),
                "--channel-bit-nm", channelBit.ToString("R", CultureInfo.InvariantCulture),
                "--start-angle-deg", startAngle.ToString("R", CultureInfo.InvariantCulture),
                "--spiral-direction", "cw",
                "--image-threshold", "128",
                "--alpha-threshold", "1",
                "--preview-size", outputSize.ToString(CultureInfo.InvariantCulture),
                "--samples-per-sector", samplesPerSector.ToString(CultureInfo.InvariantCulture),
            ];
            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            message = exception.Message;
            return false;
        }
    }

    private static double ParseDouble(TextBox textBox, string fieldName)
    {
        string value = textBox.Text.Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result))
        {
            return result;
        }

        throw new FormatException($"{fieldName}不是有效数字。");
    }

    private static double ParsePositiveDouble(TextBox textBox, string fieldName)
    {
        double result = ParseDouble(textBox, fieldName);
        return double.IsFinite(result) && result > 0
            ? result
            : throw new ArgumentOutOfRangeException(fieldName, $"{fieldName}必须大于 0。");
    }

    private static int ParsePositiveInt(TextBox textBox, string fieldName)
    {
        string value = textBox.Text.Trim();
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) && result > 0
            ? result
            : throw new FormatException($"{fieldName}必须是正整数。");
    }

    private static long ParsePositiveLong(TextBox textBox, string fieldName)
    {
        string value = textBox.Text.Trim();
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) && result > 0
            ? result
            : throw new FormatException($"{fieldName}必须是正整数。");
    }

    private static void TryDeleteLivePreview(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void PrepareResultPreview(string outputPath)
    {
        ResultPreviewImage.Source = null;
        ResultPreviewImage.Visibility = Visibility.Collapsed;
        ResultPreviewPlaceholder.Visibility = Visibility.Visible;
        ResultPreviewTitle.Text = "输出预览";
        ResultPreviewPath.Text = string.Empty;
        ResultPreviewPlaceholderText.Text = IsPngPath(outputPath)
            ? "正在生成图片预览…"
            : "当前任务生成文件，不包含可视图片预览。";
    }

    private void ShowResultPreview(string outputPath)
    {
        if (!IsPngPath(outputPath))
        {
            return;
        }

        try
        {
            using FileStream stream = new(outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            ResultPreviewImage.Source = bitmap;
            ResultPreviewImage.Visibility = Visibility.Visible;
            ResultPreviewPlaceholder.Visibility = Visibility.Collapsed;
            ResultPreviewPath.Text = Path.GetFileName(outputPath);
        }
        catch (Exception exception)
        {
            ResultPreviewPlaceholderText.Text = "图片已生成，但无法在界面中加载。";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 预览加载失败：{exception.Message}\n");
        }
    }

    private static bool IsPngPath(string path)
        => string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase);

    private static Task<int> RunOnStaThreadAsync(Func<int> action)
    {
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Disc image worker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class DispatcherTextWriter : TextWriter
    {
        private readonly Dispatcher _dispatcher;
        private readonly Action<string> _append;
        private readonly StringBuilder _buffer = new();
        private readonly object _gate = new();

        internal DispatcherTextWriter(Dispatcher dispatcher, Action<string> append)
        {
            _dispatcher = dispatcher;
            _append = append;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            string? completed = null;
            lock (_gate)
            {
                _buffer.Append(value);
                if (value == '\n')
                {
                    completed = _buffer.ToString();
                    _buffer.Clear();
                }
            }

            if (completed is not null)
            {
                Dispatch(completed);
            }
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            foreach (char character in value)
            {
                Write(character);
            }
        }

        public override void Flush()
        {
            string remaining;
            lock (_gate)
            {
                remaining = _buffer.ToString();
                _buffer.Clear();
            }

            if (remaining.Length != 0)
            {
                Dispatch(remaining);
            }
        }

        private void Dispatch(string text)
            => _dispatcher.BeginInvoke(() => _append(text), DispatcherPriority.Background);
    }
}
