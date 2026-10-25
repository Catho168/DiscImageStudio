using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.Imaging;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class BurnViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private readonly IOpticalDiscBurner _opticalDiscBurner;
    private CancellationTokenSource? _burnCancellation;
    private int _burnWriteSpeedRefreshRevision;
    private int _burnPreparationRevision;
    private string? _sourceIdentity;
    private bool _isPreparingBurn;
    private bool _isFinalizingBurn;
    private bool _hasDeviceWriteProgress;
    private int _burnProgressRevision;
    private DispatcherTimer? _writeRateTimer;
    private long? _lastDeviceWriteTimestamp;
    private bool _deviceWriteProgressFinished;

    [ObservableProperty]
    private IReadOnlyList<OpticalBurnDevice>? _devices;

    [ObservableProperty]
    private OpticalBurnDevice? _selectedDevice;

    [ObservableProperty]
    private string _deviceStatusText = "进入此页面后会检测本机刻录机。";

    [ObservableProperty]
    private IReadOnlyList<BurnWriteSpeedOption> _speeds = [BurnWriteSpeedOption.Automatic];

    [ObservableProperty]
    private BurnWriteSpeedOption? _selectedSpeed = BurnWriteSpeedOption.Automatic;

    [ObservableProperty]
    private bool _isSpeedComboEnabled;

    [ObservableProperty]
    private bool _isSpeedRefreshEnabled;

    [ObservableProperty]
    private string _speedStatusText = "放入可写盘片后读取其支持的速度；默认自动使用最低可用速度，以降低写入错误率。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDvdBurn))]
    [NotifyPropertyChangedFor(nameof(IsBluRayBurn))]
    [NotifyPropertyChangedFor(nameof(SelectedMediaKind))]
    private int _selectedMediaIndex;

    [ObservableProperty]
    private bool _useCalibrationPattern;

    [ObservableProperty]
    private string _sourceTitle = "使用 CD 页面中的源图片与生成参数";

    [ObservableProperty]
    private string _sourceDetails = "请先在 CD 页面选择图片并完成预览。";

    [ObservableProperty]
    private bool _confirmChecked;

    [ObservableProperty]
    private string _burnStatusText = "等待开始";

    [ObservableProperty]
    private double _burnProgressValue;

    [ObservableProperty]
    private string _currentWriteRateText = "—";

    [ObservableProperty]
    private bool _isBurnRunning;

    public BurnViewModel(ShellViewModel shell, DiscParametersState state,
        IOpticalDiscBurner? opticalDiscBurner = null)
    {
        _shell = shell;
        _state = state;
        _opticalDiscBurner = opticalDiscBurner ?? new WindowsImapiBurner();
        RefreshDevicesCommand = new RelayCommand(() => _ = RefreshDevicesAsync(), () => !_shell.IsBusy);
        RefreshSpeedsCommand = new RelayCommand(() => _ = RefreshWriteSpeedsAsync(), () => !_shell.IsBusy);
        StartBurnCommand = new AsyncRelayCommand(() => StartStreamBurnAsync(), CanStartBurn);
        CancelBurnCommand = new RelayCommand(CancelStreamBurn, () =>
            !_isFinalizingBurn && (IsBurnRunning || _isPreparingBurn)
            && _burnCancellation is { IsCancellationRequested: false });
        OpenCdParametersCommand = new RelayCommand(() => shell.NavigateTo(ShellViewModel.CdTabIndex));
        OpenDvdParametersCommand = new RelayCommand(() => shell.NavigateTo(ShellViewModel.DvdTabIndex));
        OpenBluRayParametersCommand = new RelayCommand(() => shell.NavigateTo(ShellViewModel.BluRayTabIndex));
        _shell.BusyChanged += () =>
        {
            InvalidateConfirmation();
            OnPropertyChanged(nameof(IsSetupEnabled));
            RefreshDevicesCommand.NotifyCanExecuteChanged();
            RefreshSpeedsCommand.NotifyCanExecuteChanged();
            StartBurnCommand.NotifyCanExecuteChanged();
            CancelBurnCommand.NotifyCanExecuteChanged();
        };
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(IsBurnRunning))
            {
                CancelBurnCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public RelayCommand RefreshDevicesCommand { get; }

    public RelayCommand RefreshSpeedsCommand { get; }

    public IAsyncRelayCommand StartBurnCommand { get; }

    public RelayCommand CancelBurnCommand { get; }

    public RelayCommand OpenCdParametersCommand { get; }

    public RelayCommand OpenDvdParametersCommand { get; }

    public RelayCommand OpenBluRayParametersCommand { get; }

    public bool IsDvdBurn
    {
        get => SelectedMediaIndex == 1;
        set
        {
            if (value) SelectedMediaIndex = 1;
            else if (IsDvdBurn) SelectedMediaIndex = 0;
        }
    }

    public bool IsBluRayBurn
    {
        get => SelectedMediaIndex == 2;
        set
        {
            if (value) SelectedMediaIndex = 2;
            else if (IsBluRayBurn) SelectedMediaIndex = 0;
        }
    }

    public OpticalBurnMediaKind SelectedMediaKind => SelectedMediaIndex switch
    {
        0 => OpticalBurnMediaKind.CdAudio,
        1 => OpticalBurnMediaKind.DvdData,
        2 => OpticalBurnMediaKind.BluRayData,
        _ => throw new InvalidOperationException("请选择 CD、DVD 或蓝光刻录类型。"),
    };

    public bool IsSetupEnabled => !_shell.IsBusy && !_isPreparingBurn;

    /// <summary>Device→speeds→confirm gating: not busy, device chosen, checkbox ticked.</summary>
    private bool CanStartBurn()
        => !_shell.IsBusy
            && !_isPreparingBurn
            && SelectedDevice is not null
            && ConfirmChecked
            && (!UseCalibrationPattern || CalibrationMediaMatches());

    private bool CalibrationMediaMatches()
        => _shell.Calibration.Target is { } target
            && CalibrationMediaKind(target.Parameters.Kind) == SelectedMediaKind;

    internal static OpticalBurnMediaKind CalibrationMediaKind(CalibrationDiscKind kind) => kind switch
    {
        CalibrationDiscKind.Cd => OpticalBurnMediaKind.CdAudio,
        CalibrationDiscKind.Dvd => OpticalBurnMediaKind.DvdData,
        CalibrationDiscKind.BluRay => OpticalBurnMediaKind.BluRayData,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string MediaDisplayName(OpticalBurnMediaKind kind) => kind switch
    {
        OpticalBurnMediaKind.CdAudio => "CD",
        OpticalBurnMediaKind.DvdData => "DVD",
        OpticalBurnMediaKind.BluRayData => "蓝光",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private void InvalidateConfirmation()
    {
        _burnPreparationRevision++;
        ConfirmChecked = false;
        UpdateActionState();
    }

    partial void OnSelectedDeviceChanged(OpticalBurnDevice? value)
    {
        InvalidateConfirmation();
        _ = RefreshWriteSpeedsAsync();
    }

    partial void OnSelectedSpeedChanged(BurnWriteSpeedOption? value)
    {
        InvalidateConfirmation();
        UpdateActionState();
    }

    partial void OnConfirmCheckedChanged(bool value) => UpdateActionState();

    partial void OnSelectedMediaIndexChanging(int value)
    {
        if (value is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(value));
    }

    partial void OnSelectedMediaIndexChanged(int value)
    {
        InvalidateConfirmation();
        UpdateSourceSummary();
        UpdateActionState();
        _ = RefreshWriteSpeedsAsync();
    }

    partial void OnUseCalibrationPatternChanged(bool value)
    {
        InvalidateConfirmation();
        UpdateSourceSummary();
    }

    public void OnCalibrationTargetChanged()
    {
        InvalidateConfirmation();
        UpdateSourceSummary();
    }

    public void UpdateSourceSummary()
    {
        string identity;
        if (UseCalibrationPattern)
        {
            CalibrationTarget? target = _shell.Calibration.Target;
            if (target is null)
            {
                SourceTitle = "尚未创建标定图案";
                SourceDetails = "请先在辅助标定页面创建图案，再选择对应的 CD / DVD / 蓝光刻录类型。";
                identity = "calibration:none";
            }
            else
            {
                CalibrationParameters p = target.Parameters;
                string kind = MediaDisplayName(CalibrationMediaKind(p.Kind));
                SourceTitle = $"标定图案 · {kind} · {target.Id[..8]}";
                string encoding = p.Kind == CalibrationDiscKind.Cd
                    ? $"线速度 {ParameterNumberText.ForSummary(p.LinearDensity)} mm/s · "
                        + (_shell.Calibration.CdInterleave ? "延迟交织" : "未交织")
                    : p.Kind == CalibrationDiscKind.BluRay
                        ? $"3T / 6T 数量偏向 · {(CalibrationViewModel.HasBluRayFileSystem(target) ? "UDF" : "全数据区绘图")} · 无备用区 BD-R"
                        : $"位长 {ParameterNumberText.ForSummary(p.LinearDensity)} nm";
                SourceDetails = CalibrationRadiusDisplay.Format(target)
                    + $" · {p.Sectors} 扇区 · {encoding}\n"
                    + (CalibrationMediaMatches()
                        ? "使用创建图案时保存的参数；开始前保存标定记录。"
                        : $"盘片类型不一致，请将上方刻录类型切换为 {kind}。");
                identity = $"calibration:{target.Id}:{p}:{_shell.Calibration.CdInterleave}:{SelectedMediaKind}";
            }
        }
        else if (IsBluRayBurn)
        {
            BluRayViewModel bluRay = _shell.BluRay;
            SourceTitle = "使用蓝光页面中的源图片与生成参数";
            string source = bluRay.ImagePath.Trim();
            string suffix = string.IsNullOrWhiteSpace(bluRay.DataDirectory)
                ? "；全数据区绘图，不含文件系统"
                : "；将顺序写入内圈文件夹与外圈图案";
            suffix += "。使用全新未格式化的单层 BD-R，不预留备用区。";
            SourceDetails = source.Length == 0
                ? "请先在蓝光页面选择图片并检查环带布局。" + suffix
                : $"{Path.GetFileName(source)} · {bluRay.TotalSectors.Trim()} 扇区 · 3T / 6T"
                    + (bluRay.InvertMapping ? " · 已反转" : string.Empty) + suffix;
            SourceDetails += ImageProcessingSummary(bluRay.ImageProcessingEnabled, bluRay.GammaCorrectionEnabled);
            identity = string.Join("\u001f", "bluray", source, bluRay.TotalSectors,
                bluRay.InnerRadius, bluRay.OuterRadius, bluRay.InvertMapping,
                bluRay.ImageProcessingEnabled, bluRay.GammaCorrectionEnabled,
                bluRay.DataDirectory, bluRay.VolumeLabel, bluRay.SelectedPreset?.Id);
        }
        else if (IsDvdBurn)
        {
            SourceTitle = "使用 DVD 页面中的源图片与生成参数";
            string source = _state.DvdImagePath.Trim();
            string suffix = string.IsNullOrWhiteSpace(_state.DvdDataDirectory)
                ? string.Empty
                : "；将顺序写入内圈文件夹与外圈图案";
            SourceDetails = source.Length == 0
                ? "请先在 DVD 页面选择图片并完成实时预览。" + suffix
                : $"{Path.GetFileName(source)} · {_state.DvdTotalSectors.Trim()} 扇区 · 快速算法 · CW{suffix}";
            SourceDetails += ImageProcessingSummary(_state.DvdImageProcessingEnabled, _state.DvdGammaCorrectionEnabled);
            identity = string.Join("\u001f", "dvd", source, _state.DvdTotalSectors,
                _state.DvdInnerRadius, _state.DvdOuterRadius,
                _state.DvdPitchLinear, _state.DvdPitchQuadratic, _state.DvdPitchCubic,
                _state.DvdImageProcessingModeIndex, _state.DvdRingInnerMargin, _state.DvdRingOuterMargin,
                _state.DvdImageProcessingEnabled, _state.DvdGammaCorrectionEnabled,
                _state.DvdDataDirectory, _state.DvdVolumeLabel);
        }
        else
        {
            SourceTitle = "使用 CD 页面中的源图片与生成参数";
            string source = _state.CdImagePath.Trim();
            SourceDetails = source.Length == 0
                ? "请先在 CD 页面选择图片并完成实时预览。"
                : $"{Path.GetFileName(source)} · {_state.CdSectors.Trim()} 扇区 · "
                    + (_state.CdInterleave ? "延迟交织" : "未交织");
            SourceDetails += ImageProcessingSummary(_state.CdImageProcessingEnabled, _state.CdGammaCorrectionEnabled);
            identity = string.Join("\u001f", "cd", source, _state.CdSectors,
                _state.CdInnerRadius, _state.CdOuterRadius, _state.CdImageProcessingModeIndex,
                _state.CdImageProcessingEnabled, _state.CdGammaCorrectionEnabled,
                _state.CdRingInnerMargin, _state.CdRingOuterMargin, _state.CdInterleave);
        }

        if (!string.Equals(identity, _sourceIdentity, StringComparison.Ordinal))
        {
            _sourceIdentity = identity;
            InvalidateConfirmation();
        }
    }

    private static string ImageProcessingSummary(bool enabled, bool gammaCorrectionEnabled)
        => enabled ? $" · 黑白抖动 · sRGB 伽马：{(gammaCorrectionEnabled ? "开" : "关")}" : string.Empty;

    private void UpdateActionState() => StartBurnCommand.NotifyCanExecuteChanged();

    internal async Task RefreshDevicesAsync()
    {
        if (_shell.IsBusy)
        {
            return;
        }

        Devices = null;
        DeviceStatusText = "正在检测刻录机…";
        try
        {
            IReadOnlyList<OpticalBurnDevice> devices =
                await _opticalDiscBurner.GetDevicesAsync();
            Devices = devices;
            SelectedDevice = devices.Count == 0 ? null : devices[0];
            DeviceStatusText = devices.Count == 0
                ? "未检测到支持 IMAPI2 的 CD/DVD/蓝光刻录机。"
                : $"检测到 {devices.Count} 台刻录机";
        }
        catch (Exception exception)
        {
            Devices = null;
            SelectedDevice = null;
            DeviceStatusText = "检测刻录机失败：" + exception.Message;
        }
        finally
        {
            UpdateActionState();
        }
    }

    internal async Task RefreshWriteSpeedsAsync()
    {
        if (_shell.IsBusy)
        {
            return;
        }

        int revision = ++_burnWriteSpeedRefreshRevision;
        OpticalWriteSpeed? previousSpeed = SelectedSpeed?.Speed;
        Speeds = [BurnWriteSpeedOption.Automatic];
        SelectedSpeed = BurnWriteSpeedOption.Automatic;

        if (SelectedDevice is not OpticalBurnDevice device)
        {
            IsSpeedComboEnabled = false;
            IsSpeedRefreshEnabled = false;
            SpeedStatusText = "请先选择刻录机。";
            return;
        }

        OpticalBurnMediaKind mediaKind = SelectedMediaKind;
        IsSpeedComboEnabled = false;
        IsSpeedRefreshEnabled = false;
        SpeedStatusText = "正在读取当前盘片支持的刻录速度…";
        try
        {
            IReadOnlyList<OpticalWriteSpeed> speeds =
                await _opticalDiscBurner.GetSupportedWriteSpeedsAsync(
                    device.Id,
                    mediaKind);
            if (revision != _burnWriteSpeedRefreshRevision)
            {
                return;
            }

            List<BurnWriteSpeedOption> options = [BurnWriteSpeedOption.Automatic];
            options.AddRange(speeds.Select(speed => new BurnWriteSpeedOption(
                speed,
                FormatBurnWriteSpeed(speed, mediaKind))));
            Speeds = options;
            int selectedIndex = previousSpeed is null
                ? 0
                : options.FindIndex(option => option.Speed == previousSpeed);
            SelectedSpeed = selectedIndex < 0 ? options[0] : options[selectedIndex];
            SpeedStatusText = speeds.Count == 0
                ? "当前盘片未报告可选速度，将使用刻录接口的保守回退设置。"
                : $"当前盘片报告 {speeds.Count} 种写入配置；自动模式将使用最低值 "
                    + $"{FormatBurnWriteSpeed(speeds[0], mediaKind)}，最终以刻录机采用值为准。";
        }
        catch (Exception exception)
        {
            if (revision == _burnWriteSpeedRefreshRevision)
            {
                SpeedStatusText =
                    "未能读取当前盘片速度，将使用刻录接口的保守回退设置：" + exception.Message;
            }
        }
        finally
        {
            if (revision == _burnWriteSpeedRefreshRevision)
            {
                IsSpeedComboEnabled = true;
                IsSpeedRefreshEnabled = true;
            }
        }
    }

    private static string FormatBurnWriteSpeed(
        OpticalWriteSpeed speed,
        OpticalBurnMediaKind mediaKind)
    {
        string rotation = speed.RotationTypeIsPureCav ? " · CAV" : string.Empty;
        return $"{speed.GetMultiplier(mediaKind):0.#}×"
            + $" · {speed.GetMegabytesPerSecond(mediaKind):0.0} MB/s{rotation}";
    }

    internal void ResetBurnProgress()
    {
        StopWriteRateMonitor();
        _hasDeviceWriteProgress = false;
        _isFinalizingBurn = false;
        _deviceWriteProgressFinished = false;
        CurrentWriteRateText = "—";
        BurnProgressValue = 0;
    }

    internal void ApplyBurnProgress(OpticalBurnProgress value, OpticalBurnMediaKind mediaKind, long? timestamp = null)
    {
        // Settings and low-frequency timing diagnostics are log records, not
        // new write samples. In particular, they must not refresh a stale rate.
        if (value.Stage is "speed" or "diagnostic")
        {
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] {value.Message}\n");
            return;
        }
        // A late write/producer callback cannot restore a rate once writing has ended.
        if (_deviceWriteProgressFinished && (value.Stage is "write" or "transfer")) return;
        // Reading the producer stream can run ahead of physical writing. Once the
        // recorder reports its own position, only its updates drive this display.
        if (value.Stage == "transfer" && _hasDeviceWriteProgress) return;

        long now = timestamp ?? Stopwatch.GetTimestamp();
        if (value.Stage == "write")
        {
            _hasDeviceWriteProgress = true;
            _lastDeviceWriteTimestamp = now;
            CurrentWriteRateText = value.BytesPerSecond is double rate && double.IsFinite(rate) && rate >= 0
                ? FormatCurrentWriteRate(rate, mediaKind)
                : "测量中…";
        }
        else
        {
            CurrentWriteRateText = "—";
        }

        if (value.Stage == "finalize")
        {
            _isFinalizingBurn = true;
            CancelBurnCommand.NotifyCanExecuteChanged();
        }
        if (value.Stage is "finalize" or "complete")
        {
            _deviceWriteProgressFinished = true;
            StopWriteRateMonitor();
        }
        BurnStatusText = value.Message;
        // Stream reads are buffering activity, not confirmed writes to the disc.
        if (value.Stage != "transfer")
            BurnProgressValue = Math.Clamp(value.Fraction * 100.0, 0.0, 100.0);
        UpdateWriteRateAge(now);
    }

    // Clock-only methods let tests advance monotonic time without constructing a
    // Dispatcher, waiting for ticks, or contacting a recorder.
    internal void BeginWriteRateTracking(long timestamp)
    {
        if (IsBurnRunning && !_deviceWriteProgressFinished)
            _lastDeviceWriteTimestamp = timestamp;
    }

    internal void UpdateWriteRateAge(long timestamp)
    {
        if (!IsBurnRunning || _deviceWriteProgressFinished || _lastDeviceWriteTimestamp is not long last
            || timestamp < last) return;
        if (Stopwatch.GetElapsedTime(last, timestamp) >= TimeSpan.FromSeconds(2))
            CurrentWriteRateText = "等待写入进度…";
    }

    private void StartWriteRateMonitor()
    {
        StopWriteRateMonitor();
        if (!IsBurnRunning) return;
        BeginWriteRateTracking(Stopwatch.GetTimestamp());
        _writeRateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _writeRateTimer.Tick += WriteRateTimer_Tick;
        _writeRateTimer.Start();
    }

    private void WriteRateTimer_Tick(object? sender, EventArgs e)
        => UpdateWriteRateAge(Stopwatch.GetTimestamp());

    private void StopWriteRateMonitor()
    {
        if (_writeRateTimer is not null)
        {
            _writeRateTimer.Stop();
            _writeRateTimer.Tick -= WriteRateTimer_Tick;
            _writeRateTimer = null;
        }
        _lastDeviceWriteTimestamp = null;
    }

    private static string FormatCurrentWriteRate(double bytesPerSecond, OpticalBurnMediaKind mediaKind)
    {
        // Reuse the selected-speed model's media-specific 1x conversion without
        // rounding the measured rate to an integer number of sectors per second.
        OpticalWriteSpeed oneSectorPerSecond = new(1, false);
        double megabytes = bytesPerSecond / 1_000_000.0;
        double multiplier = megabytes / oneSectorPerSecond.GetMegabytesPerSecond(mediaKind)
            * oneSectorPerSecond.GetMultiplier(mediaKind);
        return $"{megabytes:0.00} MB/s · {multiplier:0.0}×";
    }

    private async Task StartStreamBurnAsync()
    {
        if (_shell.IsBusy || _isPreparingBurn)
        {
            _shell.ShowToast("操作未执行", "已有任务正在运行。", ToastKind.Info);
            return;
        }

        RingImagePreparation.PreparedImage? preparedImage = null;
        bool ownsBusyState = false;
        ResetBurnProgress();
        int progressRevision = ++_burnProgressRevision;
        _burnCancellation = new CancellationTokenSource();
        CancellationToken burnToken = _burnCancellation.Token;
        _isPreparingBurn = true;
        CancelBurnCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsSetupEnabled));
        UpdateActionState();
        try
        {
            if (SelectedDevice is not OpticalBurnDevice device)
            {
                throw new ArgumentException("请选择刻录机。");
            }

            if (!ConfirmChecked)
            {
                throw new ArgumentException("请先勾选刻录确认。");
            }

            bool calibration = UseCalibrationPattern;
            int preparationRevision = _burnPreparationRevision;
            OpticalBurnMediaKind mediaKind = SelectedMediaKind;
            BurnWriteSpeedOption selectedWriteSpeed = SelectedSpeed ?? BurnWriteSpeedOption.Automatic;
            OpticalBurnRequest request;
            string contentDescription;
            if (calibration)
            {
                CalibrationTarget target = _shell.Calibration.Target
                    ?? throw new ArgumentException("请先在辅助标定页面创建图案。");
                bool interleave = _shell.Calibration.CdInterleave;
                if (CalibrationMediaKind(target.Parameters.Kind) != mediaKind)
                {
                    throw new ArgumentException("刻录类型与标定图案不一致，请切换到图案对应的 CD / DVD / 蓝光。");
                }

                if (!_shell.Calibration.EnsureSessionSavedForBurn())
                {
                    return;
                }

                if (!ReferenceEquals(target, _shell.Calibration.Target)
                    || interleave != _shell.Calibration.CdInterleave)
                {
                    throw new InvalidOperationException("标定图案已变化，请重新保存记录并确认刻录。");
                }

                request = CreateCalibrationBurnRequest(target, interleave, device.Id, selectedWriteSpeed.Speed);
                contentDescription = $"{MediaDisplayName(mediaKind)} 标定图案 · {target.Id[..8]}"
                    + $" · {target.Parameters.Sectors} 扇区 · {request.ContentLength / (1024.0 * 1024.0):F1} MiB";
            }
            else if (mediaKind == OpticalBurnMediaKind.BluRayData)
            {
                BluRayViewModel bluRay = _shell.BluRay;
                string source = RequirePath(bluRay.ImagePath, "请先在蓝光页面选择源图片。");
                BluRayDiscParameters parameters = bluRay.ReadParameters();
                IBluRayLuminanceMapping luminanceMapping = bluRay.CreateLuminanceMapping();
                string dataDirectory = bluRay.DataDirectory.Trim();
                string volumeLabel = bluRay.VolumeLabel.Trim();
                bool imageProcessingEnabled = bluRay.ImageProcessingEnabled;
                bool gammaCorrectionEnabled = bluRay.GammaCorrectionEnabled;
                // Snapshot the raster and file layout before asking for confirmation.
                // The ISO writer also checks source file metadata during production.
                (BluRayGray8Raster raster, BluRayIsoPlan plan) = await Task.Run(() =>
                {
                    if (imageProcessingEnabled) BluRayImageLoader.ValidateSource(source, burnToken);
                    using RingImagePreparation.PreparedImage prepared = RingImagePreparation.PrepareImage(
                        source, null, "bd-burn", imageProcessingEnabled, gammaCorrectionEnabled,
                        burnToken, writeLog: false);
                    BluRayGray8Raster loaded = BluRayImageLoader.Load(prepared.Path, burnToken);
                    burnToken.ThrowIfCancellationRequested();
                    BluRayIsoPlan layout = BluRayIsoGenerator.PrepareDrawing(parameters,
                        dataDirectory.Length == 0 ? null : dataDirectory,
                        volumeLabel.Length == 0 ? "BD_IMAGE" : volumeLabel);
                    burnToken.ThrowIfCancellationRequested();
                    return (loaded, layout);
                }, burnToken);
                request = CreateBluRayBurnRequest(plan,
                    new BluRayImagePayloadMapping(parameters, raster, luminanceMapping),
                    device.Id, selectedWriteSpeed.Speed);
                contentDescription = $"蓝光 · {parameters.TotalSectors} 扇区 · {plan.FileCount} 个内圈文件"
                    + $" · {(plan.HasFileSystem ? "UDF" : "无文件系统，全区绘图")} · 绘图 LBA {plan.DrawingStartSector}–{plan.DrawingEndSectorExclusive - 1}"
                    + $" · {request.ContentLength / (1024.0 * 1024.0):F1} MiB";
            }
            else if (mediaKind == OpticalBurnMediaKind.DvdData)
            {
                string source = RequirePath(_state.DvdImagePath, "请先在 DVD 页面选择源图片。");
                RingImagePreparation.Request imagePreparation = CreateDvdImageRequest(source, RingImageQuality.GenerationSize);
                DvdStreamingOptions options = ReadDvdStreamingOptions();
                string dataDirectory = _state.DvdDataDirectory.Trim();
                DvdHybridStreamingPlan? hybridPlan = dataDirectory.Length == 0
                    ? null
                    : DvdStreamingGenerator.PrepareHybrid(
                        dataDirectory,
                        options,
                        string.IsNullOrWhiteSpace(_state.DvdVolumeLabel)
                            ? "DISC_IMAGE"
                            : _state.DvdVolumeLabel.Trim());
                BurnStatusText = "正在处理源图片…";
                preparedImage = await StaWorker.RunAsync(() => imagePreparation.Prepare(
                    burnToken, appendLog: _shell.AppendLog));
                string preparedPath = preparedImage.Path;
                request = new OpticalBurnRequest(
                    device.Id,
                    OpticalBurnMediaKind.DvdData,
                    options.ContentLength,
                    (output, cancellationToken) =>
                    {
                        if (hybridPlan is null)
                        {
                            DvdStreamingGenerator.Generate(
                                preparedPath,
                                output,
                                options,
                                cancellationToken: cancellationToken);
                        }
                        else
                        {
                            DvdStreamingGenerator.GenerateHybrid(
                                preparedPath,
                                output,
                                options,
                                hybridPlan,
                                cancellationToken: cancellationToken);
                        }
                    },
                    selectedWriteSpeed.Speed);
                contentDescription = hybridPlan is null
                    ? $"DVD · {options.TotalSectors} 扇区 · {options.ContentLength / (1024.0 * 1024.0):F1} MiB"
                    : $"DVD 混合盘 · {hybridPlan.FileCount} 个文件 · 绘图从 LBA {hybridPlan.DrawingStartLba} 开始"
                        + $" · {options.ContentLength / (1024.0 * 1024.0):F1} MiB";
            }
            else
            {
                string source = RequirePath(_state.CdImagePath, "请先在 CD 页面选择源图片。");
                RingImagePreparation.Request imagePreparation = CreateCdImageRequest(source, RingImageQuality.GenerationSize);
                CdDiscParameters parameters = ReadCdGeneratedParameters();
                bool interleave = _state.CdInterleave;
                BurnStatusText = "正在处理源图片…";
                preparedImage = await StaWorker.RunAsync(() => imagePreparation.Prepare(
                    burnToken, appendLog: _shell.AppendLog));
                string preparedPath = preparedImage.Path;
                request = new OpticalBurnRequest(
                    device.Id,
                    OpticalBurnMediaKind.CdAudio,
                    parameters.TotalBytes,
                    (output, cancellationToken) => CdTrackGenerator.GenerateToStream(
                        preparedPath,
                        output,
                        parameters,
                        interleave,
                        cancellationToken: cancellationToken,
                        audioByteOrder: CdAudioByteOrder.LittleEndian),
                    selectedWriteSpeed.Speed);
                contentDescription = $"CD · {parameters.Sectors} 扇区 · {parameters.TotalBytes / (1024.0 * 1024.0):F1} MiB";
            }

            burnToken.ThrowIfCancellationRequested();
            if (_shell.IsBusy || preparationRevision != _burnPreparationRevision || !ConfirmChecked)
            {
                BurnStatusText = "刻录来源或选项已变化，请重新确认后开始。";
                return;
            }
            if (mediaKind == OpticalBurnMediaKind.BluRayData)
                contentDescription += "\n无备用区顺序刻录；使用全新未格式化的单层 BD-R，保持所设总扇区数。";
            // In-app danger modal; default focus sits on the refusing button.
            bool confirmed = await _shell.ConfirmAsync(
                "确认开始流式刻录",
                $"即将写入：{device.DisplayName}\n{contentDescription}\n"
                    + $"请求速度：{selectedWriteSpeed.DisplayName}\n\n"
                    + "仅允许空白盘。开始后取消或断电可能使盘片报废。是否继续？",
                "确认刻录");
            burnToken.ThrowIfCancellationRequested();
            if (!confirmed)
            {
                return;
            }

            if (_shell.IsBusy || preparationRevision != _burnPreparationRevision || !ConfirmChecked)
            {
                BurnStatusText = "刻录来源或选项已变化，请重新确认后开始。";
                return;
            }

            ownsBusyState = true;
            _shell.SetBusy(true);
            IsBurnRunning = true;
            StartWriteRateMonitor();
            BurnProgressValue = 0;
            BurnStatusText = "正在准备流式刻录…";
            _shell.AppendLog(
                $"\n[{DateTime.Now:HH:mm:ss}] 开始流式刻录：{contentDescription}；"
                + $"设备={device.DisplayName}；请求速度={selectedWriteSpeed.DisplayName}。\n");
            Progress<OpticalBurnProgress> progress = new(value =>
            {
                if (progressRevision == _burnProgressRevision && IsBurnRunning)
                    ApplyBurnProgress(value, mediaKind);
            });
            OpticalBurnResult result = await _opticalDiscBurner.BurnAsync(
                request,
                progress,
                burnToken);
            BurnProgressValue = 100;
            BurnStatusText = "刻录完成，可以取出光盘。";
            _shell.AppendLog(
                $"[{DateTime.Now:HH:mm:ss}] 流式刻录完成：{result.BytesWritten} 字节，"
                + $"耗时 {result.Elapsed.TotalSeconds:F1} 秒"
                + (result.ActualWriteSpeed is null
                    ? "。\n"
                    : $"，设备设置速度={FormatBurnWriteSpeed(result.ActualWriteSpeed, mediaKind)}。\n"));
            ConfirmChecked = false;
            _shell.ShowToast("流式刻录完成", "流式刻录已经完成，可以取出光盘。", ToastKind.Success);
        }
        catch (OperationCanceledException exception)
        {
            BurnStatusText = ownsBusyState
                ? "已请求取消；光驱可能仍需一段时间才能停止。"
                : "已取消刻录准备，尚未开始写入。";
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] " + (ownsBusyState
                ? "已取消流式刻录；当前盘片可能无法继续使用。\n"
                : "已取消刻录准备，尚未开始写入。\n"));
            if (exception.InnerException is not null)
                _shell.AppendLog($"取消期间设备还报告了错误：{exception}\n");
        }
        catch (Exception exception)
        {
            BurnStatusText = "刻录未完成，请查看错误信息。";
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] 流式刻录失败：{exception}\n");
            _shell.ShowToast(
                "流式刻录未完成",
                exception.Message + "\n\n如果写入已经开始，当前盘片可能无法继续使用。",
                ToastKind.Error);
        }
        finally
        {
            StopWriteRateMonitor();
            _burnProgressRevision++;
            CurrentWriteRateText = "—";
            preparedImage?.Dispose();
            _burnCancellation?.Dispose();
            _burnCancellation = null;
            _isFinalizingBurn = false;
            IsBurnRunning = false;
            InvalidateConfirmation();
            _isPreparingBurn = false;
            CancelBurnCommand.NotifyCanExecuteChanged();
            if (ownsBusyState)
            {
                _shell.SetBusy(false);
            }

            OnPropertyChanged(nameof(IsSetupEnabled));
            UpdateActionState();
        }
    }

    // Request construction is separate from physical writing so the captured
    // calibration content can also be validated with an ordinary memory stream.
    internal static OpticalBurnRequest CreateCalibrationBurnRequest(
        CalibrationTarget target,
        bool cdInterleave,
        string deviceId,
        OpticalWriteSpeed? writeSpeed)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Parameters.Kind == CalibrationDiscKind.BluRay)
        {
            BluRayIsoPlan plan = CalibrationViewModel.BluRayPlan(target);
            if (plan.DrawingStartSector != target.Parameters.DrawingStartSector
                || plan.DrawingEndSectorExclusive != (target.Parameters.DrawingEndSectorExclusive == 0
                    ? target.Parameters.Sectors : target.Parameters.DrawingEndSectorExclusive))
                throw new InvalidOperationException("标定记录的绘图布局不匹配，请重新创建标定图案。");
            return CreateBluRayBurnRequest(plan, CalibrationViewModel.BluRayPatternMapping(target),
                deviceId, writeSpeed);
        }

        if (target.Parameters.Kind == CalibrationDiscKind.Dvd)
        {
            DvdStreamingOptions options = CalibrationViewModel.DvdOptions(target);
            options.Validate();
            return new OpticalBurnRequest(deviceId, OpticalBurnMediaKind.DvdData, options.ContentLength,
                (output, token) => DvdStreamingGenerator.GeneratePattern(target.Sample, output, options,
                    cancellationToken: token), writeSpeed);
        }

        if (target.Parameters.Kind != CalibrationDiscKind.Cd)
            throw new ArgumentOutOfRangeException(nameof(target), "Unknown calibration disc kind.");

        CdDiscParameters parameters = CalibrationViewModel.CdParameters(target);
        parameters.Validate();
        return new OpticalBurnRequest(deviceId, OpticalBurnMediaKind.CdAudio, parameters.TotalBytes,
            (output, token) => CdTrackGenerator.GeneratePatternToStream(target.Sample, output, parameters,
                cdInterleave, cancellationToken: token, audioByteOrder: CdAudioByteOrder.LittleEndian), writeSpeed);
    }

    internal static OpticalBurnRequest CreateBluRayBurnRequest(
        BluRayIsoPlan plan,
        IBluRayPayloadMapping mapping,
        string deviceId,
        OpticalWriteSpeed? writeSpeed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(mapping);
        return new OpticalBurnRequest(deviceId, OpticalBurnMediaKind.BluRayData, plan.Disc.ContentLength,
            (output, token) => BluRayIsoGenerator.GenerateToStream(output, plan, mapping,
                cancellationToken: token), writeSpeed);
    }

    private void CancelStreamBurn()
    {
        if (_isFinalizingBurn) return;
        BurnStatusText = IsBurnRunning
            ? "正在请求停止刻录，请不要取出盘片…"
            : "正在取消刻录准备…";
        _burnCancellation?.Cancel();
        CancelBurnCommand.NotifyCanExecuteChanged();
    }

    private static string RequirePath(string raw, string message)
    {
        string value = raw.Trim();
        return value.Length == 0 ? throw new ArgumentException(message) : value;
    }

    private CdDiscParameters ReadCdGeneratedParameters()
    {
        CdDiscParameters parameters = new(
            ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, "CD 内半径"),
            ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, "CD 外半径"),
            ParameterParser.ParsePositiveLong(_state.CdSectors, "CD 扇区数"));
        parameters.Validate();
        return parameters;
    }

    private DvdStreamingOptions ReadDvdStreamingOptions()
    {
        int totalSectors = ParameterParser.ParsePositiveInt(_state.DvdTotalSectors, "DVD 总扇区数");
        DvdStreamingOptions options = new(
            checked((uint)totalSectors),
            ParameterParser.ParsePositiveDouble(_state.DvdInnerRadius, "DVD 内半径"),
            ParameterParser.ParsePositiveDouble(_state.DvdOuterRadius, "DVD 外半径"),
            PitchLinear: ParameterParser.ParseDouble(_state.DvdPitchLinear, "DVD 轨距一次项"),
            PitchQuadratic: ParameterParser.ParseDouble(_state.DvdPitchQuadratic, "DVD 轨距二次项"),
            PitchCubic: ParameterParser.ParseDouble(_state.DvdPitchCubic, "DVD 轨距三次项"));
        options.Validate();
        return options;
    }

    private RingImagePreparation.Request CreateCdImageRequest(string sourcePath, int outputSize)
    {
        RingImageLayoutOptions? options = null;
        if (_state.IsCdRingMode)
        {
            double generatedInner = ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, "CD 内半径");
            double generatedOuter = ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, "CD 外半径");
            options = ParameterParser.CreateRingLayoutOptions(
                CdDiscParameters.StandardImageOuterRadiusMm,
                generatedInner,
                generatedOuter,
                _state.CdRingInnerMargin,
                _state.CdRingOuterMargin,
                outputSize);
        }

        return new RingImagePreparation.Request(
            sourcePath,
            options,
            "cd",
            _state.CdImageProcessingEnabled,
            _state.CdGammaCorrectionEnabled);
    }

    private RingImagePreparation.Request CreateDvdImageRequest(string sourcePath, int outputSize)
    {
        RingImageLayoutOptions? options = null;
        if (_state.IsDvdRingMode)
        {
            double generatedInner = ParameterParser.ParsePositiveDouble(_state.DvdInnerRadius, "DVD 内半径");
            double generatedOuter = ParameterParser.ParsePositiveDouble(_state.DvdOuterRadius, "DVD 外半径");
            options = ParameterParser.CreateRingLayoutOptions(
                generatedOuter,
                generatedInner,
                generatedOuter,
                _state.DvdRingInnerMargin,
                _state.DvdRingOuterMargin,
                outputSize);
        }

        return new RingImagePreparation.Request(
            sourcePath,
            options,
            "dvd",
            _state.DvdImageProcessingEnabled,
            _state.DvdGammaCorrectionEnabled);
    }

    public sealed record BurnWriteSpeedOption(
        OpticalWriteSpeed? Speed,
        string DisplayName)
    {
        public static BurnWriteSpeedOption Automatic { get; } =
            new(null, "自动（最低速率，推荐）");
    }
}
