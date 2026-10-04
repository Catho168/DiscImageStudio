using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly IOpticalDiscBurner _opticalDiscBurner = new WindowsImapiBurner();
    private CancellationTokenSource? _burnCancellation;
    private int _burnWriteSpeedRefreshRevision;
    private int _burnPreparationRevision;
    private string? _sourceIdentity;
    private bool _isPreparingBurn;

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
    private bool _isDvdBurn;

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
    private bool _isBurnRunning;

    public BurnViewModel(ShellViewModel shell, DiscParametersState state)
    {
        _shell = shell;
        _state = state;
        RefreshDevicesCommand = new RelayCommand(() => _ = RefreshDevicesAsync(), () => !_shell.IsBusy);
        RefreshSpeedsCommand = new RelayCommand(() => _ = RefreshWriteSpeedsAsync(), () => !_shell.IsBusy);
        StartBurnCommand = new AsyncRelayCommand(() => StartStreamBurnAsync(), CanStartBurn);
        CancelBurnCommand = new RelayCommand(CancelStreamBurn, () => IsBurnRunning);
        OpenCdParametersCommand = new RelayCommand(() => shell.NavigateTo(ShellViewModel.CdTabIndex));
        OpenDvdParametersCommand = new RelayCommand(() => shell.NavigateTo(ShellViewModel.DvdTabIndex));
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
            && (target.Parameters.Kind == CalibrationDiscKind.Dvd) == IsDvdBurn;

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

    partial void OnIsDvdBurnChanged(bool value)
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
                SourceDetails = "请先在辅助标定页面创建图案，再选择对应的 CD / DVD 刻录类型。";
                identity = "calibration:none";
            }
            else
            {
                CalibrationParameters p = target.Parameters;
                string kind = p.Kind == CalibrationDiscKind.Dvd ? "DVD" : "CD";
                SourceTitle = $"标定图案 · {kind} · {target.Id[..8]}";
                string encoding = p.Kind == CalibrationDiscKind.Dvd
                    ? $"位长 {p.LinearDensity:0.######} nm"
                    : $"线速度 {p.LinearDensity:0.######} mm/s · "
                        + (_shell.Calibration.CdInterleave ? "延迟交织" : "未交织");
                SourceDetails = $"绘图区 {p.InnerRadiusMm:0.######}–{p.OuterRadiusMm:0.######} mm"
                    + $" · {p.Sectors} 扇区 · {encoding}\n"
                    + (CalibrationMediaMatches()
                        ? "使用创建图案时保存的参数；开始前保存标定记录。"
                        : $"盘片类型不一致，请将上方刻录类型切换为 {kind}。");
                identity = $"calibration:{target.Id}:{p}:{_shell.Calibration.CdInterleave}:{IsDvdBurn}";
            }
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
            identity = string.Join("\u001f", "dvd", source, _state.DvdTotalSectors,
                _state.DvdInnerRadius, _state.DvdOuterRadius,
                _state.DvdPitchLinear, _state.DvdPitchQuadratic, _state.DvdPitchCubic,
                _state.DvdImageProcessingModeIndex, _state.DvdRingInnerMargin, _state.DvdRingOuterMargin,
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
            identity = string.Join("\u001f", "cd", source, _state.CdSectors,
                _state.CdInnerRadius, _state.CdOuterRadius, _state.CdImageProcessingModeIndex,
                _state.CdRingInnerMargin, _state.CdRingOuterMargin, _state.CdInterleave);
        }

        if (!string.Equals(identity, _sourceIdentity, StringComparison.Ordinal))
        {
            _sourceIdentity = identity;
            InvalidateConfirmation();
        }
    }

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
                ? "未检测到支持 IMAPI2 的 CD/DVD 刻录机。"
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

        OpticalBurnMediaKind mediaKind = IsDvdBurn
            ? OpticalBurnMediaKind.DvdData
            : OpticalBurnMediaKind.CdAudio;
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

    private async Task StartStreamBurnAsync()
    {
        if (_shell.IsBusy)
        {
            _shell.ShowToast("操作未执行", "已有任务正在运行。", ToastKind.Info);
            return;
        }

        RingImagePreparation.PreparedImage? preparedImage = null;
        bool ownsBusyState = false;
        _isPreparingBurn = true;
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

            bool dvd = IsDvdBurn;
            bool calibration = UseCalibrationPattern;
            int preparationRevision = _burnPreparationRevision;
            OpticalBurnMediaKind mediaKind = dvd
                ? OpticalBurnMediaKind.DvdData
                : OpticalBurnMediaKind.CdAudio;
            BurnWriteSpeedOption selectedWriteSpeed = SelectedSpeed ?? BurnWriteSpeedOption.Automatic;
            OpticalBurnRequest request;
            string contentDescription;
            if (calibration)
            {
                CalibrationTarget target = _shell.Calibration.Target
                    ?? throw new ArgumentException("请先在辅助标定页面创建图案。");
                bool interleave = _shell.Calibration.CdInterleave;
                if ((target.Parameters.Kind == CalibrationDiscKind.Dvd) != dvd)
                {
                    throw new ArgumentException("刻录类型与标定图案不一致，请切换到图案对应的 CD / DVD。");
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
                contentDescription = $"{(dvd ? "DVD" : "CD")} 标定图案 · {target.Id[..8]}"
                    + $" · {target.Parameters.Sectors} 扇区 · {request.ContentLength / (1024.0 * 1024.0):F1} MiB";
            }
            else if (dvd)
            {
                string source = RequirePath(_state.DvdImagePath, "请先在 DVD 页面选择源图片。");
                preparedImage = PrepareDvdImage(source, RingImageQuality.GenerationSize);
                string preparedPath = preparedImage.Path;
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
                preparedImage = PrepareCdImage(source, RingImageQuality.GenerationSize);
                string preparedPath = preparedImage.Path;
                CdDiscParameters parameters = ReadCdGeneratedParameters();
                bool interleave = _state.CdInterleave;
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

            // In-app danger modal; default focus sits on the refusing button.
            bool confirmed = await _shell.ConfirmAsync(
                "确认开始流式刻录",
                $"即将写入：{device.DisplayName}\n{contentDescription}\n"
                    + $"请求速度：{selectedWriteSpeed.DisplayName}\n\n"
                    + "仅允许空白盘。开始后取消或断电可能使盘片报废。是否继续？",
                "确认刻录");
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
            _burnCancellation = new CancellationTokenSource();
            BurnProgressValue = 0;
            BurnStatusText = "正在准备流式刻录…";
            _shell.AppendLog(
                $"\n[{DateTime.Now:HH:mm:ss}] 开始流式刻录：{contentDescription}；"
                + $"设备={device.DisplayName}；请求速度={selectedWriteSpeed.DisplayName}。\n");
            Progress<OpticalBurnProgress> progress = new(value =>
            {
                BurnStatusText = value.Message;
                BurnProgressValue = Math.Clamp(value.Fraction * 100.0, 0.0, 100.0);
            });
            OpticalBurnResult result = await _opticalDiscBurner.BurnAsync(
                request,
                progress,
                _burnCancellation.Token);
            BurnProgressValue = 100;
            BurnStatusText = "刻录完成，可以取出光盘。";
            _shell.AppendLog(
                $"[{DateTime.Now:HH:mm:ss}] 流式刻录完成：{result.BytesWritten} 字节，"
                + $"耗时 {result.Elapsed.TotalSeconds:F1} 秒"
                + (result.ActualWriteSpeed is null
                    ? "。\n"
                    : $"，实际速度={FormatBurnWriteSpeed(result.ActualWriteSpeed, mediaKind)}。\n"));
            ConfirmChecked = false;
            _shell.ShowToast("流式刻录完成", "流式刻录已经完成，可以取出光盘。", ToastKind.Success);
        }
        catch (OperationCanceledException)
        {
            BurnStatusText = "已请求取消；光驱可能仍需一段时间才能停止。";
            _shell.AppendLog(
                $"[{DateTime.Now:HH:mm:ss}] 已取消流式刻录；当前盘片可能无法继续使用。\n");
        }
        catch (Exception exception)
        {
            BurnStatusText = "刻录未完成，请查看错误信息。";
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] 流式刻录失败：{exception.Message}\n");
            _shell.ShowToast(
                "流式刻录未完成",
                exception.Message + "\n\n如果写入已经开始，当前盘片可能无法继续使用。",
                ToastKind.Error);
        }
        finally
        {
            preparedImage?.Dispose();
            _burnCancellation?.Dispose();
            _burnCancellation = null;
            IsBurnRunning = false;
            InvalidateConfirmation();
            _isPreparingBurn = false;
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
        if (target.Parameters.Kind == CalibrationDiscKind.Dvd)
        {
            DvdStreamingOptions options = CalibrationViewModel.DvdOptions(target);
            options.Validate();
            return new OpticalBurnRequest(deviceId, OpticalBurnMediaKind.DvdData, options.ContentLength,
                (output, token) => DvdStreamingGenerator.GeneratePattern(target.Sample, output, options,
                    cancellationToken: token), writeSpeed);
        }

        CdDiscParameters parameters = CalibrationViewModel.CdParameters(target);
        parameters.Validate();
        return new OpticalBurnRequest(deviceId, OpticalBurnMediaKind.CdAudio, parameters.TotalBytes,
            (output, token) => CdTrackGenerator.GeneratePatternToStream(target.Sample, output, parameters,
                cdInterleave, cancellationToken: token, audioByteOrder: CdAudioByteOrder.LittleEndian), writeSpeed);
    }

    private void CancelStreamBurn()
    {
        CancelBurnCommand.NotifyCanExecuteChanged();
        BurnStatusText = "正在请求停止刻录，请不要取出盘片…";
        _burnCancellation?.Cancel();
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

    private RingImagePreparation.PreparedImage PrepareCdImage(string sourcePath, int outputSize)
    {
        if (_state.CdImageProcessingModeIndex != 1)
        {
            return RingImagePreparation.PreparedImage.Original(sourcePath);
        }

        double generatedInner = ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, "CD 内半径");
        double generatedOuter = ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, "CD 外半径");
        RingImageLayoutOptions options = ParameterParser.CreateRingLayoutOptions(
            CdDiscParameters.StandardImageOuterRadiusMm,
            generatedInner,
            generatedOuter,
            _state.CdRingInnerMargin,
            _state.CdRingOuterMargin,
            outputSize);
        return RingImagePreparation.PrepareRingImage(
            sourcePath,
            options,
            "cd",
            appendLog: _shell.AppendLog);
    }

    private RingImagePreparation.PreparedImage PrepareDvdImage(string sourcePath, int outputSize)
    {
        if (_state.DvdImageProcessingModeIndex != 1)
        {
            return RingImagePreparation.PreparedImage.Original(sourcePath);
        }

        double generatedInner = ParameterParser.ParsePositiveDouble(_state.DvdInnerRadius, "DVD 内半径");
        double generatedOuter = ParameterParser.ParsePositiveDouble(_state.DvdOuterRadius, "DVD 外半径");
        RingImageLayoutOptions options = ParameterParser.CreateRingLayoutOptions(
            generatedOuter,
            generatedInner,
            generatedOuter,
            _state.DvdRingInnerMargin,
            _state.DvdRingOuterMargin,
            outputSize);
        return RingImagePreparation.PrepareRingImage(
            sourcePath,
            options,
            "dvd",
            appendLog: _shell.AppendLog);
    }

    public sealed record BurnWriteSpeedOption(
        OpticalWriteSpeed? Speed,
        string DisplayName)
    {
        public static BurnWriteSpeedOption Automatic { get; } =
            new(null, "自动（最低速率，推荐）");
    }
}
