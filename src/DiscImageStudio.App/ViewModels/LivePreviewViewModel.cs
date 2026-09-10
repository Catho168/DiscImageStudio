using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Cd;
using DiscImageStudio.Imaging;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// Live preview pipeline: 450ms debounce, revision-based stale-result discard,
/// running/pending coalescing, cancel-on-change. Drawn on STA worker threads.
public partial class LivePreviewViewModel : ObservableObject
{
    private readonly DispatcherTimer _livePreviewTimer;
    private readonly DiscParametersState _state;
    private readonly ShellViewModel _shell;

    /// <summary>Exposed for the view's two-way bindings to the shared parameters.</summary>
    public DiscParametersState State => _state;
    private CancellationTokenSource? _livePreviewCancellation;
    private bool _livePreviewRefreshRunning;
    private bool _livePreviewRefreshPending;
    private bool _isLivePreviewReady;
    private bool _suppressModeAutoUpdate;
    private int _livePreviewRevision;
    private string? _lastLivePreviewPath;

    [ObservableProperty]
    private bool _isDvdSelected;

    [ObservableProperty]
    private string _headingText = "实时预览 - CD";

    [ObservableProperty]
    private string _resultPreviewTitle = "实时预览 - CD";

    [ObservableProperty]
    private string _resultPreviewPath = string.Empty;

    [ObservableProperty]
    private string _resultPreviewPlaceholderText = "请先选择 CD 源图片。";

    [ObservableProperty]
    private string _statusText = "等待 CD 源图片";

    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapSource? _resultImage;

    [ObservableProperty]
    private bool _hasResultImage;

    public LivePreviewViewModel(Dispatcher dispatcher, ShellViewModel shell, DiscParametersState state)
    {
        _shell = shell;
        _state = state;
        _livePreviewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450),
        };
        _livePreviewTimer.Tick += (_, _) =>
        {
            _livePreviewTimer.Stop();
            _ = RefreshAsync();
        };
        RefreshCommand = new RelayCommand(RefreshNow);
    }

    internal bool IsLivePreviewReady => _isLivePreviewReady;

    public RelayCommand RefreshCommand { get; }

    /// <summary>Shared browsing commands, identical to the CD/DVD page pickers.</summary>
    public RelayCommand BrowseCdImageCommand => _shell.BrowseCdImageCommand;

    public RelayCommand BrowseDvdImageCommand => _shell.BrowseDvdImageCommand;

    /// <summary>Disc-type radio semantics: clear the stale canvas and reschedule.</summary>
    partial void OnIsDvdSelectedChanged(bool value)
    {
        if (_suppressModeAutoUpdate)
        {
            return;
        }

        UpdateMode(clearResult: true);
        Schedule();
    }

    /// <summary>Configures the disc type without clearing the canvas (snapshot entry point).</summary>
    internal void SetDiscType(bool dvd, bool clearResult)
    {
        _suppressModeAutoUpdate = true;
        try
        {
            IsDvdSelected = dvd;
        }
        finally
        {
            _suppressModeAutoUpdate = false;
        }

        UpdateMode(clearResult);
    }

    internal void OnWindowLoaded()
    {
        UpdateMode(clearResult: false);
        Schedule();
    }

    internal void UpdateMode(bool clearResult)
    {
        string discName = IsDvdSelected ? "DVD" : "CD";
        HeadingText = $"实时预览 - {discName}";
        ResultPreviewTitle = $"实时预览 - {discName}";
        ResultPreviewPlaceholderText = $"请先选择 {discName} 源图片。";
        StatusText = $"等待 {discName} 源图片";

        if (!clearResult)
        {
            return;
        }

        ResultImage = null;
        HasResultImage = false;
        ResultPreviewPath = string.Empty;
        string? previousPath = _lastLivePreviewPath;
        _lastLivePreviewPath = null;
        RingImagePreparation.TryDeleteTemporaryFile(previousPath);
    }

    internal void Schedule()
    {
        if (!_shell.IsWindowLoaded)
        {
            return;
        }

        _livePreviewCancellation?.Cancel();
        _livePreviewTimer.Stop();
        _livePreviewRevision++;
        _isLivePreviewReady = false;
        StatusText = "参数已变化，准备刷新…";
        _livePreviewTimer.Start();
    }

    internal void RefreshNow()
    {
        _livePreviewTimer.Stop();
        _livePreviewCancellation?.Cancel();
        _livePreviewRevision++;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
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
                if (IsDvdSelected)
                {
                    await RefreshDvdAsync(revision);
                }
                else
                {
                    await RefreshCdAsync(revision);
                }

                if (revision != _livePreviewRevision)
                {
                    _livePreviewRefreshPending = true;
                }
            }
            while (_livePreviewRefreshPending && _shell.IsWindowLoaded);
        }
        finally
        {
            _livePreviewRefreshRunning = false;
        }
    }

    private async Task RefreshCdAsync(int revision)
    {
        _livePreviewCancellation?.Cancel();
        _livePreviewCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _livePreviewCancellation = cancellation;

        if (!TryReadCdSettings(
                out string imagePath,
                out CdDiscParameters generated,
                out CdDiscParameters actual,
                out int outputSize,
                out int samplesPerSector,
                out RingImageLayoutOptions? ringLayout,
                out string message))
        {
            StatusText = message;
            if (!HasResultImage)
            {
                ResultPreviewPlaceholderText = message;
            }

            return;
        }

        Directory.CreateDirectory(RingImagePreparation.LivePreviewDirectory);
        string outputPath = Path.Combine(
            RingImagePreparation.LivePreviewDirectory,
            $"cd-live-preview-{revision}.png");
        StatusText = $"正在刷新 {outputSize} px 实时预览…";
        bool keepOutput = false;
        try
        {
            await StaWorker.RunAsync(() =>
            {
                using RingImagePreparation.PreparedImage preparedImage = ringLayout is null
                    ? RingImagePreparation.PreparedImage.Original(imagePath)
                    : RingImagePreparation.PrepareRingImage(
                        imagePath,
                        ringLayout,
                        "cd-live",
                        cancellation.Token,
                        writeLog: false);
                CdTrackGenerator.PreviewWarp(
                    preparedImage.Path,
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
            ResultPreviewTitle = "实时预览 - CD";
            ResultPreviewPath = Path.GetFileName(imagePath);
            StatusText = $"已实时更新 · 灰度 · {outputSize} px · 每扇区 {samplesPerSector} 个快速采样";
            RingImagePreparation.TryDeleteTemporaryFile(previousPath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = $"实时预览失败：{exception.Message}";
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] 实时预览失败：{exception}\n");
        }
        finally
        {
            if (!keepOutput)
            {
                RingImagePreparation.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    private async Task RefreshDvdAsync(int revision)
    {
        _livePreviewCancellation?.Cancel();
        _livePreviewCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _livePreviewCancellation = cancellation;

        Directory.CreateDirectory(RingImagePreparation.LivePreviewDirectory);
        string outputPath = Path.Combine(
            RingImagePreparation.LivePreviewDirectory,
            $"dvd-live-preview-{revision}.png");
        if (!TryBuildDvdCommand(
                outputPath,
                out string imagePath,
                out string[] arguments,
                out int outputSize,
                out int samplesPerSector,
                out RingImageLayoutOptions? ringLayout,
                out string message))
        {
            StatusText = message;
            if (!HasResultImage)
            {
                ResultPreviewPlaceholderText = message;
            }

            return;
        }

        StatusText = $"正在刷新 {outputSize} px DVD 实时预览…";
        bool keepOutput = false;
        try
        {
            int exitCode = await StaWorker.RunAsync(() =>
            {
                using RingImagePreparation.PreparedImage preparedImage = ringLayout is null
                    ? RingImagePreparation.PreparedImage.Original(imagePath)
                    : RingImagePreparation.PrepareRingImage(
                        imagePath,
                        ringLayout,
                        "dvd-live",
                        cancellation.Token,
                        writeLog: false);
                string[] effectiveArguments = (string[])arguments.Clone();
                SetOptionValue(effectiveArguments, "--image", preparedImage.Path);
                return UnifiedCommandRunner.Run(effectiveArguments);
            });
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
            ResultPreviewTitle = "实时预览 - DVD";
            ResultPreviewPath = Path.GetFileName(imagePath);
            StatusText = $"已实时更新 · 灰度 · CW · {outputSize} px · 每扇区 {samplesPerSector} 个快速采样";
            RingImagePreparation.TryDeleteTemporaryFile(previousPath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = $"实时预览失败：{exception.Message}";
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] DVD 实时预览失败：{exception}\n");
        }
        finally
        {
            if (!keepOutput)
            {
                RingImagePreparation.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    private bool TryReadCdSettings(
        out string imagePath,
        out CdDiscParameters generated,
        out CdDiscParameters actual,
        out int outputSize,
        out int samplesPerSector,
        out RingImageLayoutOptions? ringLayout,
        out string message)
    {
        imagePath = _state.CdImagePath.Trim();
        generated = null!;
        actual = null!;
        outputSize = 0;
        samplesPerSector = 0;
        ringLayout = null;
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
            long sectors = ParameterParser.ParsePositiveLong(_state.CdSectors, "扇区数");
            double imageOuterRadius = ParameterParser.ParsePositiveDouble(_state.CdImageOuterRadius, "图片外半径");
            generated = new CdDiscParameters(
                ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, "生成内半径"),
                ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, "生成外半径"),
                sectors,
                ParameterParser.ParsePositiveDouble(_state.CdVelocity, "生成线速度"),
                ParameterParser.ParseDouble(_state.CdStartAngle, "生成起始角") * Math.PI / 180.0,
                imageOuterRadius);
            actual = new CdDiscParameters(
                ParameterParser.ParsePositiveDouble(_state.CdActualInnerRadius, "实测内半径"),
                ParameterParser.ParsePositiveDouble(_state.CdActualOuterRadius, "实测外半径"),
                sectors,
                ParameterParser.ParsePositiveDouble(_state.CdActualVelocity, "实测线速度"),
                ParameterParser.ParseDouble(_state.CdActualStartAngle, "实测起始角") * Math.PI / 180.0,
                imageOuterRadius);
            generated.Validate();
            actual.Validate();

            int configuredSize = ParameterParser.ParsePositiveInt(_state.CdPreviewSize, "预览尺寸");
            int configuredSamples = ParameterParser.ParsePositiveInt(_state.CdSamplesPerSector, "每扇区采样");
            outputSize = Math.Clamp(configuredSize, 256, 900);
            samplesPerSector = Math.Clamp(configuredSamples, 1, 2);
            if (_state.CdImageProcessingModeIndex == 1)
            {
                ringLayout = ParameterParser.CreateRingLayoutOptions(
                    imageOuterRadius,
                    generated.InnerRadiusMm,
                    generated.OuterRadiusMm,
                    _state.CdRingInnerMargin,
                    _state.CdRingOuterMargin,
                    Math.Max(RingImageQuality.LivePreviewSize, outputSize));
            }

            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            message = exception.Message;
            return false;
        }
    }

    private bool TryBuildDvdCommand(
        string outputPath,
        out string imagePath,
        out string[] arguments,
        out int outputSize,
        out int samplesPerSector,
        out RingImageLayoutOptions? ringLayout,
        out string message)
    {
        imagePath = _state.DvdImagePath.Trim();
        arguments = [];
        outputSize = 0;
        samplesPerSector = 0;
        ringLayout = null;
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
            long totalSectorsValue = ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, "总扇区数");
            if (totalSectorsValue > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException("总扇区数", "总扇区数超出 DVD 引擎支持范围。");
            }

            double generatedInner = ParameterParser.ParsePositiveDouble(_state.DvdInnerRadius, "生成内半径");
            double generatedOuter = ParameterParser.ParsePositiveDouble(_state.DvdOuterRadius, "生成外半径");
            double actualInner = ParameterParser.ParsePositiveDouble(_state.DvdActualInnerRadius, "实测内半径");
            double actualOuter = ParameterParser.ParsePositiveDouble(_state.DvdActualOuterRadius, "实测外半径");
            if (generatedInner >= generatedOuter || actualInner >= actualOuter)
            {
                throw new ArgumentOutOfRangeException("半径", "内半径必须小于外半径。");
            }

            double channelBit = ParameterParser.ParsePositiveDouble(_state.DvdChannelBit, "Channel bit");
            double startAngle = ParameterParser.ParseDouble(_state.DvdStartAngle, "起始角");
            outputSize = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.DvdPreviewSize, "预览尺寸"),
                256,
                900);
            samplesPerSector = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.DvdSamplesPerSector, "每扇区采样"),
                1,
                2);
            if (_state.DvdImageProcessingModeIndex == 1)
            {
                ringLayout = ParameterParser.CreateRingLayoutOptions(
                    generatedOuter,
                    generatedInner,
                    generatedOuter,
                    _state.DvdRingInnerMargin,
                    _state.DvdRingOuterMargin,
                    Math.Max(RingImageQuality.LivePreviewSize, outputSize));
            }

            string totalSectors = totalSectorsValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            arguments =
            [
                "calibrate",
                "--image", imagePath,
                "--output", outputPath,
                "--total-sectors", totalSectors,
                "--fill-sectors", totalSectors,
                "--generated-inner-radius-mm", generatedInner.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--generated-outer-radius-mm", generatedOuter.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--actual-inner-radius-mm", actualInner.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--actual-outer-radius-mm", actualOuter.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--channel-bit-nm", channelBit.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--start-angle-deg", startAngle.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--spiral-direction", "cw",
                "--image-threshold", "128",
                "--alpha-threshold", "1",
                "--preview-size", outputSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--samples-per-sector", samplesPerSector.ToString(System.Globalization.CultureInfo.InvariantCulture),
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

    private static void SetOptionValue(string[] arguments, string option, string value)
    {
        for (int index = 0; index + 1 < arguments.Length; index++)
        {
            if (arguments[index].Equals(option, StringComparison.OrdinalIgnoreCase))
            {
                arguments[index + 1] = value;
                return;
            }
        }

        throw new ArgumentException($"Missing command option {option}.", nameof(arguments));
    }

    internal void PrepareResultPreview(string outputPath)
    {
        ResultImage = null;
        HasResultImage = false;
        ResultPreviewTitle = "输出预览";
        ResultPreviewPath = string.Empty;
        ResultPreviewPlaceholderText = ShellViewModel.IsPngPath(outputPath)
            ? "正在生成图片预览…"
            : "当前任务生成文件，不包含可视图片预览。";
    }

    internal void ShowResultPreview(string outputPath)
    {
        if (!ShellViewModel.IsPngPath(outputPath))
        {
            return;
        }

        try
        {
            using FileStream stream = new(outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            System.Windows.Media.Imaging.BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            ResultImage = bitmap;
            HasResultImage = true;
            ResultPreviewPath = Path.GetFileName(outputPath);
        }
        catch (Exception exception)
        {
            ResultPreviewPlaceholderText = "图片已生成，但无法在界面中加载。";
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] 预览加载失败：{exception.Message}\n");
        }
    }

    internal void OnWindowClosed()
    {
        _livePreviewTimer.Stop();
        _livePreviewCancellation?.Cancel();
        _livePreviewCancellation?.Dispose();
        RingImagePreparation.TryDeleteTemporaryFile(_lastLivePreviewPath);
    }
}
