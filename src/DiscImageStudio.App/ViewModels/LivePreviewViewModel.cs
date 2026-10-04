using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Cd;
using DiscImageStudio.Dvd;
using DiscImageStudio.Imaging;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// Live read-back preview pipeline: 450ms debounce, revision-based stale-result discard,
/// running/pending coalescing, cancel-on-change. Drawn on STA worker threads.
///
/// Two input modes share that pipeline:
/// - Calibration preview (default): the input is the source picture and two parameter sets
///   describe it — the generated geometry lays it out, the measured geometry reads it back.
///   Rendering never touches the multi-hundred-megabyte artifact, which is what makes parameter
///   iteration cheap; the price is that the picture is a projection of the source, not a
///   read-back of the bytes that were actually written.
/// - Read-back simulation: the input is the generated burn artifact (CD track or DVD ISO,
///   matching the cdimage calibration model), so the picture shows what the disc really
///   carries. One parameter set describes the measured disc geometry.
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
    private bool _suppressDiscTypeAutoUpdate;
    private bool _suppressPreviewModeAutoUpdate;
    private int _livePreviewRevision;
    private string? _lastLivePreviewPath;
    // True only while the canvas shows a picture the export action can write out: the
    // read-back simulation or the calibration projection. A job's own PNG output takes the
    // canvas over without a file of its own to export, so the action falls back to disabled.
    private bool _downloadablePreview;
    private string? _calibratedTrackPath;
    private string? _calibratedIsoPath;

    [ObservableProperty]
    private bool _isDvdSelected;

    /// <summary>False renders the generated artifact (read-back simulation), true renders the
    /// source picture through the generated/measured parameter pair.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadbackPreview))]
    private bool _isCalibrationPreview = true;

    /// <summary>Inverse of <see cref="IsCalibrationPreview"/>, so the view can trigger its
    /// read-back panels without an inverse-binding converter. Settable because the input switch
    /// puts this mode on its right-hand option, and the switch's <c>IsSecondSelected</c> is
    /// two-way: a write lands here and flips the single source of truth.</summary>
    public bool IsReadbackPreview
    {
        get => !IsCalibrationPreview;
        set => IsCalibrationPreview = !value;
    }

    [ObservableProperty]
    private string _headingText = "实时预览";

    [ObservableProperty]
    private string _resultPreviewTitle = "实时预览 - CD";

    [ObservableProperty]
    private string _resultPreviewPath = string.Empty;

    [ObservableProperty]
    private string _resultPreviewPlaceholderText = "请先选择生成的 CD 音轨。";

    [ObservableProperty]
    private string _statusText = "等待生成的 CD 音轨";

    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapSource? _resultImage;

    [ObservableProperty]
    private bool _hasResultImage;

    public LivePreviewViewModel(Dispatcher dispatcher, ShellViewModel shell, DiscParametersState state)
    {
        _shell = shell;
        _state = state;
        _state.PropertyChanged += State_PropertyChanged;
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
        DownloadPreviewCommand = new RelayCommand(DownloadPreview, CanDownloadPreview);
    }

    internal bool IsLivePreviewReady => _isLivePreviewReady;

    public RelayCommand RefreshCommand { get; }

    /// <summary>Writes the current preview next to the picture it was derived from.</summary>
    public RelayCommand DownloadPreviewCommand { get; }

    /// <summary>Shared browsing commands, identical to the CD/DVD page pickers.</summary>
    public RelayCommand BrowseCdImageCommand => _shell.BrowseCdImageCommand;

    public RelayCommand BrowseDvdImageCommand => _shell.BrowseDvdImageCommand;

    /// <summary>Picks the generated artifact to read back, matching the CD/DVD page pickers.</summary>
    public RelayCommand BrowseCdTrackCommand => _shell.BrowseCdTrackCommand;

    public RelayCommand BrowseDvdIsoCommand => _shell.BrowseDvdIsoCommand;

    /// <summary>Shared with the disc-production pages: the preset catalog actions. The
    /// calibration panels edit the same generation geometry a preset writes, so the catalog
    /// has to be reachable without leaving the page.</summary>
    public RelayCommand OpenPresetsJsonCommand => _shell.OpenPresetsJsonCommand;

    public RelayCommand ReloadPresetsCommand => _shell.ReloadPresetsCommand;

    /// <summary>
    /// Switching to another artifact adopts the geometry it was generated with, so an imported
    /// track or ISO starts from its own parameters instead of whatever was fitted before.
    /// </summary>
    private void State_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiscParametersState.CdTrackPath))
        {
            AdoptTrackMetadata(_state.CdTrackPath.Trim());
            DownloadPreviewCommand.NotifyCanExecuteChanged();
        }
        else if (e.PropertyName == nameof(DiscParametersState.DvdIsoPath))
        {
            AdoptIsoMetadata(_state.DvdIsoPath.Trim());
            DownloadPreviewCommand.NotifyCanExecuteChanged();
        }
        else if (e.PropertyName is nameof(DiscParametersState.CdImagePath)
            or nameof(DiscParametersState.DvdImagePath))
        {
            // The calibration preview exports next to its source picture, so editing that path
            // can change whether the export has a destination at all.
            DownloadPreviewCommand.NotifyCanExecuteChanged();
        }
    }

    private void AdoptTrackMetadata(string trackPath)
    {
        if (trackPath.Length == 0
            || string.Equals(trackPath, _calibratedTrackPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _calibratedTrackPath = trackPath;
        CdTrackMetadata? metadata = CdTrackMetadata.TryLoad(trackPath);
        if (metadata is null)
        {
            return;
        }

        DiscParametersState state = _state;
        string sectors = metadata.Sectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string innerRadius = FormatNumber(metadata.InnerRadiusMm);
        string outerRadius = FormatNumber(metadata.OuterRadiusMm);
        if (state.CdSectors.Trim() == sectors
            && state.CdInnerRadius.Trim() == innerRadius
            && state.CdOuterRadius.Trim() == outerRadius
            && state.CdInterleave == metadata.Interleaved)
        {
            return;
        }

        state.CdSectors = sectors;
        state.CdInnerRadius = innerRadius;
        state.CdOuterRadius = outerRadius;
        state.CdInterleave = metadata.Interleaved;
        _shell.AppendLog(
            $"[{DateTime.Now:HH:mm:ss}] 已按 {System.IO.Path.GetFileName(trackPath)} 的生成参数载入标定几何："
            + $"r0={innerRadius} mm, r1={outerRadius} mm, {sectors} 扇区，交织={metadata.Interleaved}。\n");
    }

    private void AdoptIsoMetadata(string isoPath)
    {
        if (isoPath.Length == 0
            || string.Equals(isoPath, _calibratedIsoPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _calibratedIsoPath = isoPath;
        DvdImageMetadata? metadata = DvdImageMetadata.TryLoad(isoPath);
        if (metadata is null)
        {
            return;
        }

        DiscParametersState state = _state;
        string totalSectors = metadata.TotalSectors.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        string innerRadius = FormatNumber(metadata.InnerRadiusMm);
        string outerRadius = FormatNumber(metadata.OuterRadiusMm);
        if (state.DvdTotalSectors.Trim() == totalSectors
            && state.DvdInnerRadius.Trim() == innerRadius
            && state.DvdOuterRadius.Trim() == outerRadius
            && state.DvdPitchLinear.Trim() == FormatNumber(metadata.PitchLinear)
            && state.DvdPitchQuadratic.Trim() == FormatNumber(metadata.PitchQuadratic)
            && state.DvdPitchCubic.Trim() == FormatNumber(metadata.PitchCubic))
        {
            return;
        }

        state.DvdTotalSectors = totalSectors;
        state.DvdInnerRadius = innerRadius;
        state.DvdOuterRadius = outerRadius;
        state.DvdPitchLinear = FormatNumber(metadata.PitchLinear);
        state.DvdPitchQuadratic = FormatNumber(metadata.PitchQuadratic);
        state.DvdPitchCubic = FormatNumber(metadata.PitchCubic);
        _shell.AppendLog(
            $"[{DateTime.Now:HH:mm:ss}] 已按 {System.IO.Path.GetFileName(isoPath)} 的生成参数载入标定几何："
            + $"r0={innerRadius} mm, r1={outerRadius} mm, {totalSectors} 总扇区；{state.DvdPitchSummary}\n");
    }

    private static string FormatNumber(double value)
        => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Disc-type radio semantics: clear the stale canvas and reschedule.</summary>
    partial void OnIsDvdSelectedChanged(bool value)
    {
        if (_suppressDiscTypeAutoUpdate)
        {
            return;
        }

        UpdateMode(clearResult: true);
        Schedule();
    }

    /// <summary>Configures the disc type without clearing the canvas (snapshot entry point).</summary>
    internal void SetDiscType(bool dvd, bool clearResult)
    {
        _suppressDiscTypeAutoUpdate = true;
        try
        {
            IsDvdSelected = dvd;
        }
        finally
        {
            _suppressDiscTypeAutoUpdate = false;
        }

        UpdateMode(clearResult);
    }

    /// <summary>Preview-mode semantics: the input changes, so the stale canvas goes and the
    /// new pipeline runs.</summary>
    partial void OnIsCalibrationPreviewChanged(bool value)
    {
        if (_suppressPreviewModeAutoUpdate)
        {
            return;
        }

        UpdateMode(clearResult: true);
        Schedule();
    }

    /// <summary>Configures the preview mode without clearing the canvas (snapshot entry point).</summary>
    internal void SetPreviewMode(bool calibration, bool clearResult)
    {
        _suppressPreviewModeAutoUpdate = true;
        try
        {
            IsCalibrationPreview = calibration;
        }
        finally
        {
            _suppressPreviewModeAutoUpdate = false;
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
        string inputName = IsCalibrationPreview
            ? $"{discName} 源图片"
            : IsDvdSelected ? "生成的 DVD ISO" : "生成的 CD 音轨";
        ResultPreviewTitle = $"实时预览 - {discName}";
        ResultPreviewPlaceholderText = $"请先选择 {inputName}。";
        StatusText = $"等待 {inputName}";

        if (!clearResult)
        {
            return;
        }

        ResultImage = null;
        HasResultImage = false;
        ResultPreviewPath = string.Empty;
        _downloadablePreview = false;
        DownloadPreviewCommand.NotifyCanExecuteChanged();
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

        if (IsCalibrationPreview)
        {
            await RefreshCdCalibrationAsync(revision, cancellation.Token);
        }
        else
        {
            await RefreshCdReadbackAsync(revision, cancellation.Token);
        }
    }

    /// <summary>Read-back simulation: undo the generation's delay interleave, then map the
    /// logical bytes onto the spiral described by the calibration geometry.</summary>
    private async Task RefreshCdReadbackAsync(int revision, CancellationToken cancellationToken)
    {
        if (!TryReadCdSettings(
                out string trackPath,
                out CdDiscParameters parameters,
                out int outputSize,
                out int samplesPerSector,
                out string message))
        {
            StatusText = message;
            if (!HasResultImage)
            {
                ResultPreviewPlaceholderText = message;
            }

            return;
        }

        bool deinterleave = _state.CdInterleave;
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
                CdTrackGenerator.PreviewTrack(
                    trackPath,
                    outputPath,
                    parameters,
                    outputSize,
                    CdDiscParameters.BytesPerSector / samplesPerSector,
                    deinterleave,
                    cancellationToken);
                return 0;
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, trackPath, "实时预览 - CD");
            keepOutput = true;
            StatusText = $"已实时更新 · 读回模拟 · {(deinterleave ? "逆交织" : "纯音轨")}"
                + $" · {outputSize} px · 每扇区 {samplesPerSector} 个采样";
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

    /// <summary>Calibration projection: the source picture is sampled where the generated
    /// geometry puts it and drawn where the measured geometry reads it. Only the source image
    /// is read, so a refresh costs the picture's size rather than the track's.</summary>
    private async Task RefreshCdCalibrationAsync(int revision, CancellationToken cancellationToken)
    {
        if (!TryReadCdWarpSettings(
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
            $"cd-live-warp-{revision}.png");
        StatusText = $"正在刷新 {outputSize} px 标定预览…";
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
                        "cd-live-warp",
                        cancellationToken,
                        writeLog: false);
                CdTrackGenerator.PreviewWarp(
                    preparedImage.Path,
                    outputPath,
                    generated,
                    actual,
                    outputSize,
                    samplesPerSector,
                    cancellationToken);
                return 0;
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, imagePath, "实时预览 - CD");
            keepOutput = true;
            StatusText = $"已实时更新 · 标定预览 · 灰度 · {outputSize} px"
                + $" · 每扇区 {samplesPerSector} 个采样";
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

        if (IsCalibrationPreview)
        {
            await RefreshDvdCalibrationAsync(revision, cancellation.Token);
        }
        else
        {
            await RefreshDvdReadbackAsync(revision, cancellation.Token);
        }
    }

    /// <summary>Read-back simulation: the solver classifies the generated ISO's payload
    /// bytes and splats them under the measured disc geometry.</summary>
    private async Task RefreshDvdReadbackAsync(int revision, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RingImagePreparation.LivePreviewDirectory);
        string outputPath = Path.Combine(
            RingImagePreparation.LivePreviewDirectory,
            $"dvd-live-preview-{revision}.png");
        string summaryJsonPath = outputPath + ".json";
        if (!TryBuildDvdCommand(
                outputPath,
                out string isoPath,
                out string[] arguments,
                out int outputSize,
                out int samplesPerSector,
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
            int exitCode = await StaWorker.RunAsync(() => UnifiedCommandRunner.Run(arguments));
            if (exitCode != 0)
            {
                throw new InvalidOperationException("DVD 读回模拟引擎未能完成渲染。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, isoPath, "实时预览 - DVD");
            keepOutput = true;
            StatusText = $"已实时更新 · 读回模拟 · CW · {outputSize} px · 每扇区 {samplesPerSector} 个采样";
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
            TryDeleteFile(summaryJsonPath);
            if (!keepOutput)
            {
                RingImagePreparation.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    /// <summary>Calibration projection: the solver renders the source picture at the
    /// generated radii and projects it onto the measured ones.</summary>
    private async Task RefreshDvdCalibrationAsync(int revision, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RingImagePreparation.LivePreviewDirectory);
        string outputPath = Path.Combine(
            RingImagePreparation.LivePreviewDirectory,
            $"dvd-live-warp-{revision}.png");
        string summaryJsonPath = outputPath + ".json";
        if (!TryBuildDvdCalibrateCommand(
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

        StatusText = $"正在刷新 {outputSize} px DVD 标定预览…";
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
                        "dvd-live-warp",
                        cancellationToken,
                        writeLog: false);
                string[] effectiveArguments = (string[])arguments.Clone();
                SetOptionValue(effectiveArguments, "--image", preparedImage.Path);
                return UnifiedCommandRunner.Run(effectiveArguments);
            });
            if (exitCode != 0)
            {
                throw new InvalidOperationException("DVD 标定预览引擎未能完成渲染。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, imagePath, "实时预览 - DVD");
            keepOutput = true;
            StatusText = $"已实时更新 · 标定预览 · CW · {outputSize} px · 每扇区 {samplesPerSector} 个采样";
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
            TryDeleteFile(summaryJsonPath);
            if (!keepOutput)
            {
                RingImagePreparation.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    /// <summary>Adopts a rendered preview as the canvas content and the export source, and
    /// retires the previous temporary file.</summary>
    private void PublishPreview(string outputPath, string inputPath, string title)
    {
        string? previousPath = _lastLivePreviewPath;
        _lastLivePreviewPath = outputPath;
        _downloadablePreview = true;
        ShowResultPreview(outputPath);
        _isLivePreviewReady = true;
        ResultPreviewTitle = title;
        ResultPreviewPath = Path.GetFileName(inputPath);
        RingImagePreparation.TryDeleteTemporaryFile(previousPath);
    }

    private bool TryReadCdSettings(
        out string trackPath,
        out CdDiscParameters parameters,
        out int outputSize,
        out int samplesPerSector,
        out string message)
    {
        trackPath = _state.CdTrackPath.Trim();
        parameters = null!;
        outputSize = 0;
        samplesPerSector = 0;
        if (trackPath.Length == 0)
        {
            message = "请先选择生成的 CD 音轨（制作页生成后自动填入）。";
            return false;
        }

        if (!File.Exists(trackPath))
        {
            message = "CD 音轨文件不存在。";
            return false;
        }

        try
        {
            long sectors = ParameterParser.ParsePositiveLong(_state.CdSectors, "扇区数");
            parameters = new CdDiscParameters(
                ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, "内半径"),
                ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, "外半径"),
                sectors);
            parameters.Validate();

            int configuredSize = ParameterParser.ParsePositiveInt(_state.CdPreviewSize, "预览尺寸");
            int configuredSamples = ParameterParser.ParsePositiveInt(_state.CdSamplesPerSector, "每扇区采样");
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

    /// <summary>
    /// Calibration variables of the CD projection. Both parameter sets share the program's
    /// sector count; velocity, start angle and the image outer radius stay the physical
    /// constants of the scheme, as in the read-back path.
    /// </summary>
    private bool TryReadCdWarpSettings(
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
            message = "请先选择 CD 源图片（制作页的源图片将自动同步）。";
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
            generated = new CdDiscParameters(
                ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, "生成内半径"),
                ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, "生成外半径"),
                sectors);
            actual = new CdDiscParameters(
                ParameterParser.ParsePositiveDouble(_state.CdActualInnerRadius, "实测内半径"),
                ParameterParser.ParsePositiveDouble(_state.CdActualOuterRadius, "实测外半径"),
                sectors);
            generated.Validate();
            actual.Validate();

            int configuredSize = ParameterParser.ParsePositiveInt(_state.CdPreviewSize, "预览尺寸");
            int configuredSamples = ParameterParser.ParsePositiveInt(_state.CdSamplesPerSector, "每扇区采样");
            outputSize = Math.Clamp(configuredSize, 256, 900);
            samplesPerSector = Math.Clamp(configuredSamples, 1, 2);

            // The projection must start from the picture the generator would consume, so the
            // production page's image processing mode applies here unchanged.
            if (_state.CdImageProcessingModeIndex == 1)
            {
                ringLayout = ParameterParser.CreateRingLayoutOptions(
                    CdDiscParameters.StandardImageOuterRadiusMm,
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
        out string isoPath,
        out string[] arguments,
        out int outputSize,
        out int samplesPerSector,
        out string message)
    {
        isoPath = _state.DvdIsoPath.Trim();
        arguments = [];
        outputSize = 0;
        samplesPerSector = 0;
        if (isoPath.Length == 0)
        {
            message = "请先选择生成的 DVD ISO（制作页生成后自动填入）。";
            return false;
        }

        if (!File.Exists(isoPath))
        {
            message = "DVD ISO 文件不存在。";
            return false;
        }

        try
        {
            long totalSectorsValue = ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, "总扇区数");
            if (totalSectorsValue > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException("总扇区数", "总扇区数超出 DVD 引擎支持范围。");
            }

            double innerRadius = ParameterParser.ParsePositiveDouble(_state.DvdInnerRadius, "内半径");
            double outerRadius = ParameterParser.ParsePositiveDouble(_state.DvdOuterRadius, "外半径");
            if (innerRadius >= outerRadius)
            {
                throw new ArgumentOutOfRangeException("半径", "内半径必须小于外半径。");
            }

            outputSize = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.DvdPreviewSize, "预览尺寸"),
                256,
                900);
            samplesPerSector = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.DvdSamplesPerSector, "每扇区采样"),
                1,
                2);

            // The simulate command falls back to the ISO's solve sidecar JSON for the
            // drawing range, so the app only passes the measured calibration geometry.
            arguments =
            [
                "simulate",
                "--iso", isoPath,
                "--output", outputPath,
                "--total-sectors", totalSectorsValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--inner-radius-mm", innerRadius.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--outer-radius-mm", outerRadius.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--pitch-linear", _state.DvdPitchLinear.Trim(),
                "--pitch-quadratic", _state.DvdPitchQuadratic.Trim(),
                "--pitch-cubic", _state.DvdPitchCubic.Trim(),
                "--spiral-direction", "cw",
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

    /// <summary>
    /// Calibration variables of the DVD projection. Unlike the read-back path, the command
    /// carries both radii pairs: the solver lays the picture out at the generated radii and
    /// projects it onto the measured ones. Channel bit (133.3 nm) and start angle (0°) stay
    /// the physical constants of the scheme.
    /// </summary>
    private bool TryBuildDvdCalibrateCommand(
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
            message = "请先选择 DVD 源图片（制作页的源图片将自动同步）。";
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
                "--pitch-linear", _state.DvdPitchLinear.Trim(),
                "--pitch-quadratic", _state.DvdPitchQuadratic.Trim(),
                "--pitch-cubic", _state.DvdPitchCubic.Trim(),
                "--actual-pitch-linear", _state.DvdActualPitchLinear.Trim(),
                "--actual-pitch-quadratic", _state.DvdActualPitchQuadratic.Trim(),
                "--actual-pitch-cubic", _state.DvdActualPitchCubic.Trim(),
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

    /// <summary>Replaces an option's value so the ring-composed intermediate replaces the
    /// source picture the command was validated with.</summary>
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    internal void PrepareResultPreview(string outputPath)
    {
        ResultImage = null;
        HasResultImage = false;
        _downloadablePreview = false;
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

    partial void OnHasResultImageChanged(bool value) => DownloadPreviewCommand.NotifyCanExecuteChanged();

    private bool CanDownloadPreview()
        => HasResultImage && _downloadablePreview && PreviewArtifactDirectory() is not null;

    /// <summary>
    /// Exports the live preview next to the picture it was derived from, named after that
    /// picture (<c>cd-track-preview.png</c>): the read-back picture belongs with the track or
    /// ISO the user keeps, and the projection belongs with its source picture, not in the
    /// preview's temporary directory.
    /// </summary>
    private void DownloadPreview()
    {
        string? previewPath = _lastLivePreviewPath;
        string? directory = PreviewArtifactDirectory();
        if (!_downloadablePreview || previewPath is null || directory is null || !File.Exists(previewPath))
        {
            return;
        }

        string artifactPath = PreviewInputPath();
        string baseName = Path.GetFileNameWithoutExtension(artifactPath);
        string destination = Path.Combine(
            directory,
            (baseName.Length == 0 ? "live-preview" : baseName) + "-preview.png");
        try
        {
            File.Copy(previewPath, destination, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _shell.ShowToast("导出失败", exception.Message, ToastKind.Error);
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] 预览导出失败：{exception.Message}\n");
            return;
        }

        _shell.SetLastOutputPath(destination);
        _shell.ShowToast("预览已导出", destination, ToastKind.Success);
        _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] 预览已导出：{destination}\n");
    }

    /// <summary>The picture the preview is derived from: the artifact being read back, or the
    /// source picture being projected.</summary>
    private string PreviewInputPath()
    {
        if (IsDvdSelected)
        {
            return (IsCalibrationPreview ? _state.DvdImagePath : _state.DvdIsoPath).Trim();
        }

        return (IsCalibrationPreview ? _state.CdImagePath : _state.CdTrackPath).Trim();
    }

    /// <summary>Folder holding that picture, or null when unknown.</summary>
    private string? PreviewArtifactDirectory()
    {
        string path = PreviewInputPath();
        if (path.Length == 0)
        {
            return null;
        }

        string? directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) || !Directory.Exists(directory) ? null : directory;
    }
}
