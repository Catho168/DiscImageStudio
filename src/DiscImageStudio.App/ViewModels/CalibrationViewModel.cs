using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Cd;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class CalibrationViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private CalibrationTarget? _target;
    private CalibrationParameters? _searchCenter;
    private CalibrationFitResult? _fit;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _previewCancellation;
    private bool _cdInterleave = true;
    private string? _sessionPath;
    private bool _installing;
    private bool _previewReady;
    private long _inputRevision;
    private bool _isSolving;
    private CalibrationFitOptions _loadedFitOptions = new();

    public CalibrationViewModel(ShellViewModel shell, DiscParametersState state)
    {
        _shell = shell;
        _state = state;
        CreateTargetCommand = new RelayCommand(CreateTarget, () => !IsBusy);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !IsBusy);
        ExportPngCommand = new AsyncRelayCommand(ExportPngAsync, CanUseTarget);
        GenerateImageCommand = new AsyncRelayCommand(GenerateImageAsync, CanUseTarget);
        StreamBurnCommand = new RelayCommand(PrepareStreamBurn, CanUseTarget);
        SaveSessionCommand = new RelayCommand(SaveSession, CanUseTarget);
        OpenSessionCommand = new AsyncRelayCommand(OpenSessionAsync, () => !IsBusy);
        SolveCommand = new AsyncRelayCommand(SolveAsync, CanSolveTrace);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => _cancellation is not null);
        ApplyParametersCommand = new RelayCommand(ApplyParameters, CanApplyParameters);
        InitializePhotoCommands();
        shell.BusyChanged += () => { OnPropertyChanged(nameof(IsBusy)); RefreshCommands(); };
    }

    public IReadOnlyList<string> DiscTypes { get; } = ["CD", "DVD"];
    [ObservableProperty] private string _selectedDiscType = "CD";
    [ObservableProperty] private BitmapSource? _targetImage;
    [ObservableProperty] private double _canvasOuterRadiusMm = 60;
    [ObservableProperty] private IReadOnlyList<CoarseCandidate> _coarseCandidates = [];
    [ObservableProperty] private CoarseCandidate? _selectedCoarseCandidate;
    public IReadOnlyList<CoarseSearchRange> CoarseSearchRangeChoices { get; } =
        [new("轻微弯曲", 0.005), new("明显弯曲（默认）", 0.02), new("扩大搜索", 0.1), new("大范围（可能遗漏候选）", 0.5)];
    [ObservableProperty] private CoarseSearchRange _selectedCoarseSearchRange = new("明显弯曲（默认）", 0.02);
    [ObservableProperty] private bool _contourConfirmed;
    [ObservableProperty] private bool _fitDvdPitch = true;
    public bool IsDvdTarget => _target?.Parameters.Kind == CalibrationDiscKind.Dvd;
    [ObservableProperty] private string _outputPath = string.Empty;
    [ObservableProperty] private string _statusText = "使用当前 CD / DVD 参数创建标定图案。";
    [ObservableProperty] private string _fitSummary = "先矫正照片，再沿一条清楚的曲线打点。至少 6 点，覆盖靠内、中间和靠外的位置。";
    [ObservableProperty] private string _fitAssessment = "全部有效点都会参与计算，结果需与实拍曲线核对。";
    [ObservableProperty] private string _validationSummary = "拟合后将预测曲线叠加在照片上，检查整条曲线是否吻合。";
    [ObservableProperty] private string _targetSummary = "尚未创建标定图案";
    public bool IsBusy => _shell.IsBusy;
    public CalibrationTarget? Target => _target;
    public bool CdInterleave => _cdInterleave;
    public RelayCommand CreateTargetCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public IAsyncRelayCommand ExportPngCommand { get; }
    public IAsyncRelayCommand GenerateImageCommand { get; }
    public RelayCommand StreamBurnCommand { get; }
    public RelayCommand SaveSessionCommand { get; }
    public IAsyncRelayCommand OpenSessionCommand { get; }
    public IAsyncRelayCommand SolveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ApplyParametersCommand { get; }

    internal void EnsureTarget() { if (_target is null) CreateTarget(); }
    internal void OnWindowClosed() { _cancellation?.Cancel(); _previewCancellation?.Cancel(); }
    private bool CanUseTarget() => !IsBusy && _target is not null;
    private bool CanApplyParameters() => !IsBusy && IsTraceStep && _photoTransform is not null && _fit?.Succeeded == true
        && SelectedCoarseCandidate is { } selected && ContourConfirmed && _previewReady
        && CandidateResidualAccepted(selected)
        && CoarseCandidates.Contains(selected)
        && _fit.Candidates.Any(c => c.Parameters == selected.Parameters && c.RotationRadians == selected.RotationRadians);
    partial void OnContourConfirmedChanged(bool value) => ApplyParametersCommand?.NotifyCanExecuteChanged();
    private bool CandidateResidualAccepted(CoarseCandidate candidate) => double.IsFinite(candidate.RmsErrorMm)
        && candidate.RmsErrorMm <= FitOptions().HuberDeltaMm * 2;

    partial void OnSelectedDiscTypeChanged(string value)
    {
        if (!_installing && _target is not null)
            StatusText = "点击“创建图案”读取所选盘片参数；现有图案和控制点仍保留。";
    }

    private void CreateTarget()
    {
        try
        {
            CalibrationParameters p = SelectedDiscType == "DVD"
                ? new(CalibrationDiscKind.Dvd, Number(_state.DvdInnerRadius), Number(_state.DvdOuterRadius),
                    ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, "DVD 扇区数"), DvdStreamingOptions.StandardChannelBitLengthNm,
                    ParameterParser.ParseDouble(_state.DvdPitchLinear, "DVD 相对一次项"),
                    ParameterParser.ParseDouble(_state.DvdPitchQuadratic, "DVD 相对二次项"),
                    ParameterParser.ParseDouble(_state.DvdPitchCubic, "DVD 相对三次项"))
                : new(CalibrationDiscKind.Cd, Number(_state.CdInnerRadius), Number(_state.CdOuterRadius),
                    ParameterParser.ParsePositiveLong(_state.CdSectors, "CD 扇区数"), CdDiscParameters.StandardLinearVelocityMmPerSecond);
            ValidateOutputParameters(p);
            InstallTarget(CalibrationTarget.Create(p), new(), _state.CdInterleave);
            _sessionPath = null;
            OutputPath = string.Empty;
            StatusText = "已使用 target_slim.png 创建标定图案：原图按生成外半径缩放，再保留内外半径之间的刻录区。可生成文件或流式刻录。";
        }
        catch (Exception ex) { Report(ex); }
    }

    private static double Number(string raw) => ParameterParser.ParsePositiveDouble(raw, "标定参数");

    private void InstallTarget(CalibrationTarget target, CalibrationFitOptions? options, bool interleave)
    {
        _installing = true;
        try
        {
            _previewCancellation?.Cancel();
            ResetPhoto();
            _target = target;
            _cdInterleave = interleave;
            _searchCenter = options?.SearchCenter ?? target.Parameters;
            _loadedFitOptions = options ?? new();
            FitDvdPitch = _loadedFitOptions.FitDvdPitch;
            SelectedCoarseSearchRange = CoarseSearchRangeChoices.FirstOrDefault(r => r.RadiusRangeMm == (options?.RadiusRangeMm ?? 0.02))
                ?? new("会话搜索范围", options?.RadiusRangeMm ?? 0.02);
            SelectedDiscType = target.Parameters.Kind == CalibrationDiscKind.Dvd ? "DVD" : "CD";
            // The 60 mm canvas is a disc-preview background; the target itself maps its
            // reference pixels using the saved generation outer radius.
            CanvasOuterRadiusMm = Math.Max(CalibrationReferencePattern.CanvasRadiusMm, target.Parameters.OuterRadiusMm);
            TargetImage = CalibrationPatternRenderer.Render(target, CanvasOuterRadiusMm, 900);
            InvalidateFit();
            TargetSummary = $"{SelectedDiscType} · 图案 {target.Id[..8]} · target_slim 细线图案\n"
                + $"绘图区 {target.Parameters.InnerRadiusMm:0.###}–{target.Parameters.OuterRadiusMm:0.###} mm\n"
                + (IsDvdTarget ? DvdPitchDisplay.Format(target.Parameters) + "\n" : string.Empty)
                + "原图按生成外半径缩放，再裁切刻录环带。\n"
                + "生成参数是反推基准，不限制照片上可标点的范围。";
            OnPropertyChanged(nameof(Target));
            OnPropertyChanged(nameof(IsDvdTarget));
            RefreshCommands();
            _shell.Burn?.OnCalibrationTargetChanged();
        }
        finally { _installing = false; }
    }

    private void InvalidateFit()
    {
        _inputRevision++;
        if (_isSolving) _cancellation?.Cancel();
        _fit = null;
        ContourConfirmed = false;
        _previewCancellation?.Cancel();
        _previewReady = false;
        CrossPredictionContours = [];
        CoarseCandidates = [];
        SelectedCoarseCandidate = null;
        FitSummary = IsDvdTarget
            ? "沿同一条曲线从内向外打点。半径拟合至少 6 点；三次轨距拟合需要至少 12 个不同半径的点，覆盖刻录环带宽度的 60% 以上。"
            : "沿同一条可见曲线从内向外打点；至少 6 点，并覆盖内、中、外的位置。可拖点修正，在空处点击补点。";
        FitAssessment = "全部有效点都会参与计算，结果需与实拍曲线核对。";
        ValidationSummary = "反推仅使用描出的这一条曲线。应用前请检查预测曲线与照片是否吻合。";
        ApplyParametersCommand.NotifyCanExecuteChanged();
    }

    private CalibrationFitOptions FitOptions() => _loadedFitOptions with
        { SearchCenter = _searchCenter, RadiusRangeMm = SelectedCoarseSearchRange.RadiusRangeMm, FitDvdPitch = FitDvdPitch };

    partial void OnFitDvdPitchChanged(bool value)
    {
        if (!_installing) InvalidateFit();
    }
    private CalibrationSession Session() => new(_target ?? throw new InvalidOperationException("请先创建图案。"),
        [], FitOptions(), _cdInterleave, PhotoSession());

    private async Task SolveAsync()
    {
        if (_target is not { } target) return;
        InvalidateFit();
        long revision = _inputRevision;
        var traces = CrossTraces();
        var options = FitOptions();
        int pointCount = traces.Sum(trace => trace.Points.Count);
        await RunAsync($"正在用全部 {pointCount} 个曲线点计算参数…", async token =>
        {
            _isSolving = true;
            var result = await Task.Run(() => CalibrationCrossFitter.Fit(target, traces, options, token), token);
            var candidates = result.Candidates.Select((c, index) =>
                new CoarseCandidate($"候选 {index + 1} · 偏差 {c.RmsErrorMm:0.###} mm", c.Parameters,
                    c.RotationRadians, c.RmsErrorMm)).ToArray();
            token.ThrowIfCancellationRequested();
            if (revision != _inputRevision || !ReferenceEquals(target, _target)) throw new OperationCanceledException();
            // Publish the result and its candidate set together, only for the current inputs.
            _fit = result;
            ContourConfirmed = false;
            CoarseCandidates = candidates;
            _installing = true;
            try { SelectedCoarseCandidate = CoarseCandidates.FirstOrDefault(); }
            finally { _installing = false; }
            await ShowCandidateAsync(SelectedCoarseCandidate);
            token.ThrowIfCancellationRequested();
            if (!result.Succeeded)
            {
                FitSummary = string.Join("\n", result.Warnings);
                FitAssessment = "计算未通过，全部点已保留。";
                StatusText = $"计算未通过，已保留全部 {pointCount} 点。{result.Warnings.FirstOrDefault()}";
            }
            else if (SelectedCoarseCandidate is { } selected && !CandidateResidualAccepted(selected))
                StatusText = $"候选与实拍点偏差较大，暂不能应用；全部 {pointCount} 点已保留。请检查照片矫正和点位。";
            else if (_previewReady)
                StatusText = $"已用全部 {pointCount} 点生成参数候选。请核对橙色预测是否沿青色实拍点连续贴合，再决定是否应用。";
        });
    }

    private void ApplyParameters()
    {
        if (!CanApplyParameters() || SelectedCoarseCandidate?.Parameters is not { } p) return;
        string ri = p.InnerRadiusMm.ToString("G17", CultureInfo.InvariantCulture);
        string ro = p.OuterRadiusMm.ToString("G17", CultureInfo.InvariantCulture);
        if (p.Kind == CalibrationDiscKind.Cd)
        {
            _state.CdInnerRadius = ri; _state.CdOuterRadius = ro;
            _state.CdActualInnerRadius = ri; _state.CdActualOuterRadius = ro;
            _state.CdSectors = p.Sectors.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            _state.DvdInnerRadius = ri; _state.DvdOuterRadius = ro;
            _state.DvdActualInnerRadius = ri; _state.DvdActualOuterRadius = ro;
            _state.DvdPitchLinear = _state.DvdActualPitchLinear = p.PitchLinear.ToString("G17", CultureInfo.InvariantCulture);
            _state.DvdPitchQuadratic = _state.DvdActualPitchQuadratic = p.PitchQuadratic.ToString("G17", CultureInfo.InvariantCulture);
            _state.DvdPitchCubic = _state.DvdActualPitchCubic = p.PitchCubic.ToString("G17", CultureInfo.InvariantCulture);
            _state.DvdTotalSectors = p.Sectors.ToString(CultureInfo.InvariantCulture);
        }
        StatusText = $"已应用到 {(p.Kind == CalibrationDiscKind.Cd ? "CD" : "DVD")} 参数。原标定盘记录仍保留，可继续补点核对。";
        _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] 标定 {Target?.Id}: {StatusText} 内半径={ri}, 外半径={ro}\n");
    }

    partial void OnSelectedCoarseCandidateChanged(CoarseCandidate? value)
    {
        ContourConfirmed = false;
        _previewReady = false;
        if (value is not null && _fit is not null)
        {
            FitAssessment = _fit.Succeeded && CandidateResidualAccepted(value)
                ? $"候选 · 点位偏差 {value.RmsErrorMm:0.###} mm，待核对"
                : $"未通过 · 点位偏差 {value.RmsErrorMm:0.###} mm，不能应用";
            FitSummary = $"所选候选：内半径 {value.Parameters.InnerRadiusMm:0.############} mm · 外半径 {value.Parameters.OuterRadiusMm:0.############} mm\n"
                + (IsDvdTarget ? DvdPitchDisplay.Format(value.Parameters) + "\n" : string.Empty)
                + $"曲线均方根误差 {value.RmsErrorMm:0.###} mm · 整体转动已单独处理。\n"
                + $"全部 {TracePoints.Count} 个实拍点均用于计算；预设绘图区不裁剪点或预测。\n"
                + string.Join("\n", _fit.Warnings)
                + "\n低偏差不代表唯一或已验证；请核对整条预测曲线是否贴合实拍点、走向与绕行次数是否一致。";
        }
        UpdateValidationSummary(value);
        if (!_installing) _ = ShowCandidateAsync(value);
        ApplyParametersCommand?.NotifyCanExecuteChanged();
    }

    partial void OnSelectedCoarseSearchRangeChanged(CoarseSearchRange value)
    {
        if (!_installing) InvalidateFit();
    }

    private void UpdateValidationSummary(CoarseCandidate? candidate)
    {
        if (_target is null || candidate is null || _fit?.Succeeded != true) return;
        ValidationSummary = $"已用全部 {TracePoints.Count} 个描线点生成候选。橙色参数预测应沿青色实拍点连续吻合；理想图案与圆环不参与点位核对。";
    }

    private async Task ShowCandidateAsync(CoarseCandidate? candidate)
    {
        _previewCancellation?.Cancel();
        _previewReady = false;
        CrossPredictionContours = [];
        ApplyParametersCommand.NotifyCanExecuteChanged();
        if (candidate is null || _target is not { } target) return;
        double[] observedRadii = TracePoints.Select(point => Math.Sqrt(point.XMm * point.XMm + point.YMm * point.YMm)).ToArray();
        if (observedRadii.Length < 2) return;
        double minimumObservedRadius = observedRadii.Min(), maximumObservedRadius = observedRadii.Max();
        if (!(maximumObservedRadius > minimumObservedRadius)) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation?.Token ?? CancellationToken.None);
        _previewCancellation = cancellation;
        long revision = _inputRevision;
        try
        {
            var contours = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var prediction = CalibrationCrossPrediction.GetOutline(target, candidate.Parameters,
                    minimumObservedRadius, maximumObservedRadius, candidate.RotationRadians, cancellationToken: cancellation.Token);
                return new[] { prediction.Select(point => new Point(point.X, point.Y)).ToArray() };
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (revision != _inputRevision || !ReferenceEquals(_target, target)
                || !ReferenceEquals(SelectedCoarseCandidate, candidate)) return;
            CrossPredictionContours = contours;
            _previewReady = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
            {
                CrossPredictionContours = [];
                ContourConfirmed = false;
                Report(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            ApplyParametersCommand.NotifyCanExecuteChanged();
        }
    }

    private void BrowseOutput()
    {
        if (_target is null) return;
        bool dvd = _target.Parameters.Kind == CalibrationDiscKind.Dvd;
        string? path = _shell.Dialogs?.PickSave(dvd ? "DVD 镜像|*.iso" : "CD 音轨|*.wav|CD 原始音轨|*.raw",
            dvd ? ".iso" : ".wav", $"calibration-{_target.Id[..8]}{(dvd ? ".iso" : ".wav")}");
        if (path is not null) OutputPath = path;
    }

    private async Task ExportPngAsync()
    {
        if (_target is not { } target) return;
        string? path = _shell.Dialogs?.PickSave("PNG 图案|*.png", ".png", $"calibration-{target.Id[..8]}.png");
        if (path is null) return;
        await RunAsync("正在导出图案…", async token =>
        {
            BitmapSource image = await Task.Run(() => CalibrationPatternRenderer.RenderEncodingImage(target, token), token);
            CalibrationPatternRenderer.SavePng(image, path);
            CalibrationSessionJson.Save(Path.ChangeExtension(path, ".calibration.json"), Session());
            _shell.RegisterOutput(path);
            StatusText = "PNG 与标定记录已导出。刻录请使用本页的生成或流式刻录按钮，以保持坐标比例。";
        });
    }

    private async Task GenerateImageAsync()
    {
        if (_target is not { } target) return;
        if (string.IsNullOrWhiteSpace(OutputPath)) BrowseOutput();
        if (string.IsNullOrWhiteSpace(OutputPath)) return;
        string path;
        try
        {
            path = Path.GetFullPath(OutputPath.Trim());
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (target.Parameters.Kind == CalibrationDiscKind.Dvd ? extension != ".iso" : extension is not (".wav" or ".raw"))
                throw new ArgumentException("请选择 DVD 的 .iso 或 CD 的 .wav / .raw 输出文件。");
        }
        catch (Exception ex) { Report(ex); return; }
        var session = Session();
        await RunAsync("正在生成标定文件…", async token =>
        {
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            string staging = Path.Combine(directory, $".calibration-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                string stagedPath = Path.Combine(staging, Path.GetFileName(path));
                await Task.Run(() =>
                {
                    if (target.Parameters.Kind == CalibrationDiscKind.Cd)
                        CdTrackGenerator.GeneratePattern(target.Sample, stagedPath, CdParameters(target), session.CdInterleave,
                            cancellationToken: token);
                    else
                    {
                        using FileStream output = new(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        DvdStreamingGenerator.GeneratePattern(target.Sample, output, DvdOptions(target), cancellationToken: token);
                    }
                    token.ThrowIfCancellationRequested();
                    CalibrationSessionJson.Save(Path.ChangeExtension(stagedPath, ".calibration.json"), session);
                }, token);
                token.ThrowIfCancellationRequested();
                CalibrationOutputPublisher.Publish(staging, directory);
                _sessionPath = Path.ChangeExtension(path, ".calibration.json");
                _shell.RegisterOutput(path);
                StatusText = "标定文件与同名标定记录已生成。刻录后打开记录、导入照片，即可矫正和描线。";
            }
            finally { Directory.Delete(staging, recursive: true); }
        });
    }

    private void SaveSession()
    {
        if (_target is null) return;
        string? path = _shell.Dialogs?.PickSave("标定记录|*.calibration.json", ".calibration.json",
            _sessionPath is null ? $"calibration-{_target.Id[..8]}.calibration.json" : Path.GetFileName(_sessionPath));
        if (path is null) return;
        try { SaveSessionTo(path); StatusText = "已保存图案、生成参数、照片、边界和曲线点。"; }
        catch (Exception ex) { Report(ex); }
    }

    internal void SaveSessionTo(string path)
    {
        CalibrationSessionJson.Save(path, Session());
        _sessionPath = path;
        _shell.RegisterOutput(path);
    }

    private async Task OpenSessionAsync()
    {
        string? path = _shell.Dialogs?.PickOpen(title: "打开标定记录", filter: "标定记录|*.calibration.json;*.json");
        if (path is null) return;
        await LoadSessionFromAsync(path);
    }

    private void PrepareStreamBurn()
    {
        if (_target is null) return;
        if (!EnsureSessionSavedForBurn()) return;
        _shell.Burn.IsDvdBurn = _target.Parameters.Kind == CalibrationDiscKind.Dvd;
        _shell.Burn.UseCalibrationPattern = true;
        _shell.NavigateTo(ShellViewModel.BurnTabIndex);
    }

    internal bool EnsureSessionSavedForBurn()
    {
        if (_target is null) return false;
        try
        {
            string? path = _sessionPath ?? _shell.Dialogs?.PickSave("标定记录|*.calibration.json", ".calibration.json",
                $"calibration-{_target.Id[..8]}.calibration.json");
            if (path is null) return false;
            SaveSessionTo(path);
            return true;
        }
        catch (Exception ex) { Report(ex); return false; }
    }

    internal static CdDiscParameters CdParameters(CalibrationTarget target) => new(target.Parameters.InnerRadiusMm,
        target.Parameters.OuterRadiusMm, target.Parameters.Sectors, target.Parameters.LinearDensity, 0,
        target.Parameters.OuterRadiusMm);
    internal static DvdStreamingOptions DvdOptions(CalibrationTarget target) => new(checked((uint)target.Parameters.Sectors),
        target.Parameters.InnerRadiusMm, target.Parameters.OuterRadiusMm, target.Parameters.LinearDensity, 0,
        PitchLinear: target.Parameters.PitchLinear, PitchQuadratic: target.Parameters.PitchQuadratic,
        PitchCubic: target.Parameters.PitchCubic);
    private static void ValidateOutputParameters(CalibrationParameters p)
    {
        p.Validate();
        if (p.Kind == CalibrationDiscKind.Dvd)
            new DvdStreamingOptions(checked((uint)p.Sectors), p.InnerRadiusMm, p.OuterRadiusMm, p.LinearDensity, 0,
                PitchLinear: p.PitchLinear, PitchQuadratic: p.PitchQuadratic, PitchCubic: p.PitchCubic).Validate();
        else new CdDiscParameters(p.InnerRadiusMm, p.OuterRadiusMm, p.Sectors, p.LinearDensity).Validate();
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        _cancellation = new();
        _shell.SetBusy(true);
        StatusText = message;
        _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] 标定：{message}\n");
        try { await action(_cancellation.Token); }
        catch (OperationCanceledException)
        {
            if (_isSolving) InvalidateFit();
            StatusText = "操作已取消。";
        }
        catch (Exception ex) { Report(ex); }
        finally
        {
            _isSolving = false;
            _cancellation.Dispose(); _cancellation = null;
            _shell.SetBusy(false);
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] 标定：{StatusText}\n");
        }
    }

    private void Report(Exception ex)
    {
        StatusText = ex.Message;
        _shell.ShowToast("标定操作未完成", ex.Message, ToastKind.Error);
    }

    private void RefreshCommands()
    {
        CreateTargetCommand.NotifyCanExecuteChanged(); BrowseOutputCommand.NotifyCanExecuteChanged();
        ExportPngCommand.NotifyCanExecuteChanged(); GenerateImageCommand.NotifyCanExecuteChanged();
        StreamBurnCommand.NotifyCanExecuteChanged(); SaveSessionCommand.NotifyCanExecuteChanged();
        OpenSessionCommand.NotifyCanExecuteChanged(); SolveCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged(); ApplyParametersCommand.NotifyCanExecuteChanged();
        RefreshPhotoCommands();
    }

    public sealed record CoarseCandidate(string Label, CalibrationParameters Parameters,
        double RotationRadians, double RmsErrorMm);
    public sealed record CoarseSearchRange(string Label, double RadiusRangeMm);
}
