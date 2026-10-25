using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Core;
using DiscImageStudio.Imaging;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// <summary>
/// Blu-ray image generation with selectable 3T/6T run-count bias and byte experiments.
/// </summary>
public partial class BluRayViewModel : ObservableObject
{
    private const long MaxPreviewFileBytes = 64L * 1024 * 1024;
    private readonly ShellViewModel _shell;
    private readonly IBluRayLuminanceMapping? _luminanceMapping;
    private readonly BluRayPresetStore _presetStore;
    private IReadOnlyList<BluRayDiscPreset> _presets = [.. BluRayDiscPreset.BuiltIn, BluRayDiscPreset.Manual];
    private bool _isApplyingDiscPreset;
    private int _layoutRevision;
    private int _sourcePreviewRevision;
    private CancellationTokenSource? _sourcePreviewCancellation;

    public BluRayViewModel(ShellViewModel shell, IBluRayLuminanceMapping? luminanceMapping = null, string? presetJsonPath = null)
    {
        _shell = shell;
        _luminanceMapping = luminanceMapping;
        _presetStore = new(presetJsonPath ?? BluRayPresetStore.DefaultPath);
        _invertMapping = (luminanceMapping as BluRayRunLengthMapping)?.Invert ?? false;
        BrowseImageCommand = new RelayCommand(() => BrowseImage(), () => !_shell.IsBusy);
        BrowseImageOutputCommand = new RelayCommand(BrowseImageOutput, () => !_shell.IsBusy);
        BrowseProbeOutputCommand = new RelayCommand(BrowseProbeOutput, () => !_shell.IsBusy);
        BrowseDataDirectoryCommand = new RelayCommand(BrowseDataDirectory, () => !_shell.IsBusy);
        ClearDataDirectoryCommand = new RelayCommand(() => DataDirectory = string.Empty, () => !_shell.IsBusy);
        CheckLayoutCommand = new AsyncRelayCommand(CheckLayoutAsync, () => !_shell.IsBusy);
        OpenBurnCommand = new RelayCommand(OpenBurn, () => !_shell.IsBusy);
        OpenCalibrationCommand = new RelayCommand(OpenCalibration, () => !_shell.IsBusy);
        OpenLivePreviewCommand = new RelayCommand(OpenLivePreview, () => !_shell.IsBusy);
        ExportParametersCommand = new AsyncRelayCommand(ExportParametersAsync, () => !_shell.IsBusy);
        SavePresetCommand = new RelayCommand(SavePreset, () => !_shell.IsBusy);
        ImportPresetCommand = new RelayCommand(ImportPreset, () => !_shell.IsBusy);
        ReloadPresetsCommand = new RelayCommand(ReloadPresets, () => !_shell.IsBusy);
        GenerateProbeCommand = new AsyncRelayCommand(GenerateProbeAsync, () => !_shell.IsBusy);
        GenerateImageCommand = new AsyncRelayCommand(GenerateImageAsync, () => !_shell.IsBusy);
        CancelImageCommand = new RelayCommand(GenerateImageCommand.Cancel, () => GenerateImageCommand.CanBeCanceled);
        GenerateImageCommand.PropertyChanged += (_, _) => CancelImageCommand.NotifyCanExecuteChanged();
        _shell.BusyChanged += NotifyCommands;
        SelectedPreset = Presets[0];
        if ((luminanceMapping as BluRayRunLengthMapping)?.Invert == true) InvertMapping = true;
        UpdateGeometry();
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(TotalSectors) or nameof(InnerRadius) or nameof(OuterRadius)
                or nameof(ImagePath) or nameof(InvertMapping) or nameof(DataDirectory) or nameof(VolumeLabel)
                or nameof(ImageProcessingEnabled) or nameof(GammaCorrectionEnabled))
                _shell.Burn?.UpdateSourceSummary();
        };
    }

    public IReadOnlyList<BluRayDiscPreset> Presets => _presets;
    public bool CanEditPresets => !_shell.IsBusy;
    public string PresetJsonPath => _presetStore.Path;

    [ObservableProperty] private string _presetName = "我的蓝光预设";
    [ObservableProperty] private string _presetStatus = string.Empty;

    [ObservableProperty]
    private BluRayDiscPreset? _selectedPreset;

    [ObservableProperty]
    private string _totalSectors = BluRayDiscParameters.SectorsPerLayer.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _dataDirectory = string.Empty;

    [ObservableProperty]
    private string _volumeLabel = "BD_IMAGE";

    [ObservableProperty]
    private string _layoutStatus = "未包含文件夹；整个用户数据区用于绘图，不写入文件系统。";

    [ObservableProperty]
    private string _innerRadius = "24";

    [ObservableProperty]
    private string _outerRadius = "58";

    [ObservableProperty]
    private string _imagePath = string.Empty;

    [ObservableProperty] private bool _imageProcessingEnabled;
    [ObservableProperty] private bool _gammaCorrectionEnabled;

    [ObservableProperty]
    private string _imageOutputPath = string.Empty;

    [ObservableProperty]
    private string _probeOutputPath = string.Empty;

    [ObservableProperty]
    private string _startSector = "0";

    [ObservableProperty]
    private string _sectorCount = "32";

    [ObservableProperty]
    private string _targetByte = "0";

    [ObservableProperty]
    private string _presetSummary = string.Empty;

    [ObservableProperty]
    private string _geometryStatus = string.Empty;

    [ObservableProperty]
    private string _previewStatus = "选择源图片后显示环带布局。";

    [ObservableProperty]
    private BitmapSource? _sourcePreview;

    [ObservableProperty]
    private Geometry _previewClip = Geometry.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MappingStatus))]
    private bool _invertMapping;

    public bool UsesRunLengthMapping => _luminanceMapping is null or BluRayRunLengthMapping;

    public bool CanChangeMapping => UsesRunLengthMapping && !_shell.IsBusy;

    public string MappingHeading => UsesRunLengthMapping ? "按 3T / 6T 数量偏向绘图" : "已接入自定义亮度映射";

    public string MappingStatus => !UsesRunLengthMapping
        ? "已提供自定义亮度映射；本页的 3T / 6T 反转选项不作用于此映射。"
        : $"以亮度 128 为界：暗部（0–127）偏 {(InvertMapping ? "6T" : "3T")}，亮部（128–255）偏 {(InvertMapping ? "3T" : "6T")}。偏向指调制后的对应游程数量较多，并非全为该游程；盘面明暗仍需实盘验证。";

    public RelayCommand BrowseImageCommand { get; }
    public RelayCommand BrowseImageOutputCommand { get; }
    public RelayCommand BrowseProbeOutputCommand { get; }
    public RelayCommand BrowseDataDirectoryCommand { get; }
    public RelayCommand ClearDataDirectoryCommand { get; }
    public IAsyncRelayCommand CheckLayoutCommand { get; }
    public RelayCommand OpenBurnCommand { get; }
    public RelayCommand OpenCalibrationCommand { get; }
    public RelayCommand OpenLivePreviewCommand { get; }
    public IAsyncRelayCommand ExportParametersCommand { get; }
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand ImportPresetCommand { get; }
    public RelayCommand ReloadPresetsCommand { get; }
    public IAsyncRelayCommand GenerateProbeCommand { get; }
    public IAsyncRelayCommand GenerateImageCommand { get; }
    public RelayCommand CancelImageCommand { get; }

    partial void OnSelectedPresetChanged(BluRayDiscPreset? value)
    {
        if (!_isApplyingDiscPreset && value is not null) ApplyDiscPreset(value);
    }

    /// <summary>New jobs and preset cards reapply defaults even when the same preset
    /// is still selected after manual geometry edits.</summary>
    internal void ApplyDiscPreset(BluRayDiscPreset preset)
    {
        _isApplyingDiscPreset = true;
        try
        {
            SelectedPreset = preset;
            if (!preset.IsCustom)
            {
                BluRayDiscParameters parameters = preset.CreateParameters();
                TotalSectors = parameters.TotalSectors.ToString(CultureInfo.InvariantCulture);
                InnerRadius = ParameterNumberText.ForInput(parameters.InnerRadiusMm);
                OuterRadius = ParameterNumberText.ForInput(parameters.OuterRadiusMm);
                InvertMapping = preset.InvertMapping;
                PresetName = preset.IsUserPreset ? preset.DisplayName : "我的蓝光预设";
            }
        }
        finally { _isApplyingDiscPreset = false; }
        UpdateGeometry();
        OnPropertyChanged(nameof(SelectedPreset));
    }

    private void MarkPresetEdited()
    {
        if (!_isApplyingDiscPreset && SelectedPreset?.IsCustom != true)
        {
            SelectedPreset = BluRayDiscPreset.Manual;
            PresetStatus = "当前参数已修改，可命名保存为独立预设。";
        }
    }

    // Startup is explicit so constructing a view model in tests never reads the user's catalog.
    internal int LoadPresets()
    {
        IReadOnlyList<BluRayDiscPreset> saved = _presetStore.Load();
        string? selectedId = SelectedPreset?.Id;
        _presets = [.. BluRayDiscPreset.BuiltIn, .. saved, BluRayDiscPreset.Manual];
        OnPropertyChanged(nameof(Presets));
        ApplyDiscPreset(Presets.FirstOrDefault(preset => preset.Id == selectedId) ?? BluRayDiscPreset.Manual);
        return saved.Count;
    }

    internal string? TryLoadPresets()
    {
        try { LoadPresets(); return null; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
            PresetStatus = $"蓝光预设加载失败：{exception.Message}";
            return PresetStatus;
        }
    }

    internal BluRayDiscPreset SaveCurrentPreset(string name)
    {
        if (_shell.IsBusy) throw new InvalidOperationException("任务进行中，暂不能保存预设。");
        BluRayDiscPreset saved = _presetStore.Save(name, ReadParameters(), InvertMapping);
        LoadPresets();
        ApplyDiscPreset(Presets.Single(preset => preset.Id == saved.Id));
        PresetStatus = $"已保存“{saved.DisplayName}”；下次启动仍可选择。";
        return saved;
    }

    internal IReadOnlyList<BluRayDiscPreset> ImportPresetsFrom(string path)
    {
        if (_shell.IsBusy) throw new InvalidOperationException("任务进行中，暂不能导入预设。");
        IReadOnlyList<BluRayDiscPreset> imported = _presetStore.Import(path);
        LoadPresets();
        ApplyDiscPreset(Presets.Single(preset => preset.Id == imported[0].Id));
        PresetStatus = $"已导入 {imported.Count} 个蓝光预设并保存到本机。";
        return imported;
    }

    private void SavePreset()
    {
        try { SaveCurrentPreset(PresetName); }
        catch (Exception exception) { _shell.ShowValidationError(exception); }
    }

    private void ImportPreset()
    {
        try
        {
            string? path = _shell.Dialogs?.PickOpen("导入蓝光参数或预设", "参数 JSON|*.json");
            if (path is not null) ImportPresetsFrom(path);
        }
        catch (Exception exception) { _shell.ShowValidationError(exception); }
    }

    private void ReloadPresets()
    {
        try { PresetStatus = $"已重新加载 {LoadPresets()} 个蓝光自定义预设。"; }
        catch (Exception exception) { _shell.ShowValidationError(exception); }
    }

    partial void OnInnerRadiusChanged(string value) { MarkPresetEdited(); UpdateGeometry(); }
    partial void OnOuterRadiusChanged(string value) { MarkPresetEdited(); UpdateGeometry(); }
    partial void OnInvertMappingChanged(bool value) => MarkPresetEdited();
    partial void OnImagePathChanged(string value) => LoadSourcePreview();
    partial void OnImageProcessingEnabledChanged(bool value) => LoadSourcePreview();
    partial void OnGammaCorrectionEnabledChanged(bool value) => LoadSourcePreview();
    partial void OnTotalSectorsChanged(string value) { MarkPresetEdited(); UpdateGeometry(); }
    partial void OnDataDirectoryChanged(string value) => InvalidateLayout();
    partial void OnVolumeLabelChanged(string value) => InvalidateLayout();

    private void NotifyCommands()
    {
        BrowseImageCommand.NotifyCanExecuteChanged();
        BrowseImageOutputCommand.NotifyCanExecuteChanged();
        BrowseProbeOutputCommand.NotifyCanExecuteChanged();
        ExportParametersCommand.NotifyCanExecuteChanged();
        SavePresetCommand.NotifyCanExecuteChanged();
        ImportPresetCommand.NotifyCanExecuteChanged();
        ReloadPresetsCommand.NotifyCanExecuteChanged();
        GenerateProbeCommand.NotifyCanExecuteChanged();
        GenerateImageCommand.NotifyCanExecuteChanged();
        BrowseDataDirectoryCommand.NotifyCanExecuteChanged();
        ClearDataDirectoryCommand.NotifyCanExecuteChanged();
        CheckLayoutCommand.NotifyCanExecuteChanged();
        OpenBurnCommand.NotifyCanExecuteChanged();
        OpenCalibrationCommand.NotifyCanExecuteChanged();
        OpenLivePreviewCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanChangeMapping));
        OnPropertyChanged(nameof(CanEditPresets));
    }

    internal bool BrowseImage()
    {
        string? path = _shell.Dialogs?.PickImage();
        if (path is null) return false;
        ImagePath = path;
        string directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(ImageOutputPath))
            ImageOutputPath = Path.Combine(directory, "bd-image.iso");
        if (string.IsNullOrWhiteSpace(ProbeOutputPath))
            ProbeOutputPath = Path.Combine(directory, "bd-probe.bin");
        return true;
    }

    private void BrowseImageOutput()
    {
        string? path = _shell.Dialogs?.PickSave("蓝光 ISO 镜像|*.iso", ".iso", "bd-image.iso");
        if (path is not null) ImageOutputPath = path;
    }

    private void BrowseProbeOutput()
    {
        string? path = _shell.Dialogs?.PickSave("二进制数据|*.bin|所有文件|*.*", ".bin", "bd-probe.bin");
        if (path is not null) ProbeOutputPath = path;
    }

    public BluRayDiscParameters ReadParameters()
    {
        BluRayDiscPreset preset = SelectedPreset ?? throw new ArgumentException("请选择蓝光盘片预设。");
        BluRayDiscParameters parameters = new(
            TotalSectors: checked((uint)ParameterParser.ParsePositiveLong(TotalSectors, "蓝光总扇区数")),
            LayerCount: preset.LayerCount,
            InnerRadiusMm: ParameterParser.ParsePositiveDouble(InnerRadius, "数据区内半径"),
            OuterRadiusMm: ParameterParser.ParsePositiveDouble(OuterRadius, "数据区外半径"));
        parameters.Validate();
        return parameters;
    }

    private List<string> CreateArguments(string command, BluRayDiscParameters parameters)
    {
        string presetId = BluRayDiscPreset.BuiltIn.FirstOrDefault(preset => preset.Id == SelectedPreset?.Id)?.Id
            ?? BluRayDiscPreset.BuiltIn[0].Id;
        return
        [
            command,
            "--preset", presetId,
            "--total-sectors", parameters.TotalSectors.ToString(CultureInfo.InvariantCulture),
            "--r0", parameters.InnerRadiusMm.ToString("R", CultureInfo.InvariantCulture),
            "--r1", parameters.OuterRadiusMm.ToString("R", CultureInfo.InvariantCulture),
            "--start-angle", "0",
        ];
    }

    private async Task ExportParametersAsync()
    {
        try
        {
            BluRayDiscParameters parameters = ReadParameters();
            string? output = _shell.Dialogs?.PickSave("参数 JSON|*.json", ".json", "bd-parameters.json");
            if (output is null) return;
            EnsureNotSourceImage(output);
            List<string> arguments = CreateArguments("bd-info", parameters);
            arguments.AddRange(["--output", output, "--invert", InvertMapping.ToString(CultureInfo.InvariantCulture)]);
            await _shell.RunDiscJobAsync("正在导出蓝光参数…", arguments.ToArray(), output);
        }
        catch (Exception exception)
        {
            _shell.ShowValidationError(exception);
        }
    }

    private async Task GenerateProbeAsync()
    {
        try
        {
            BluRayDiscParameters parameters = ReadParameters();
            string output = ProbeOutputPath.Trim();
            if (output.Length == 0) throw new ArgumentException("请选择字节测试数据的输出位置。");
            EnsureNotSourceImage(output);
            if (!uint.TryParse(StartSector.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint start)
                || start % 32 != 0)
                throw new ArgumentException("起始逻辑扇区必须是非负整数且按 32 个扇区对齐。");
            if (!uint.TryParse(SectorCount.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint count)
                || count == 0 || count % 32 != 0)
                throw new ArgumentException("扇区数量必须是 32 的正整数倍。");
            if ((ulong)start + count > parameters.TotalSectors)
                throw new ArgumentException("测试范围超出所选蓝光盘片的用户数据容量。");
            if (!byte.TryParse(TargetByte.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out byte target))
                throw new ArgumentException("扰码后字节必须是 0–255 之间的整数。");
            List<string> arguments = CreateArguments("bd-probe", parameters);
            arguments.AddRange(
            [
                "--output", output,
                "--start-sector", start.ToString(CultureInfo.InvariantCulture),
                "--sectors", count.ToString(CultureInfo.InvariantCulture),
                "--byte", target.ToString(CultureInfo.InvariantCulture),
            ]);
            await _shell.RunDiscJobAsync("正在生成蓝光字节测试数据…", arguments.ToArray(), output);
        }
        catch (Exception exception)
        {
            _shell.ShowValidationError(exception);
        }
    }

    private async Task GenerateImageAsync(CancellationToken cancellationToken)
    {
        bool busyStarted = false;
        try
        {
            if (_shell.IsBusy) return;
            BluRayDiscParameters parameters = ReadParameters();
            string source = ImagePath.Trim();
            if (source.Length == 0 || !File.Exists(source)) throw new ArgumentException("请选择有效的蓝光源图片。");
            string output = ImageOutputPath.Trim();
            if (output.Length == 0) throw new ArgumentException("请选择蓝光 ISO 镜像的输出位置。");
            if (!Path.GetExtension(output).Equals(".iso", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("蓝光图片请输出为 .iso 镜像。");
            EnsureNotSourceImage(output);
            string dataDirectory = DataDirectory.Trim();
            string volumeLabel = string.IsNullOrWhiteSpace(VolumeLabel) ? "BD_IMAGE" : VolumeLabel.Trim();
            // Freeze the selected polarity before dispatching work to the background thread.
            IBluRayLuminanceMapping luminanceMapping = CreateLuminanceMapping();
            string mappingDescription = MappingStatus;
            bool imageProcessingEnabled = ImageProcessingEnabled;
            bool gammaCorrectionEnabled = GammaCorrectionEnabled;
            IProgress<string> processingLog = new Progress<string>(_shell.AppendLog);
            _shell.SetBusy(true);
            busyStarted = true;
            _shell.SelectPage(ShellViewModel.LogTabIndex);
            _shell.AppendLog($"\n[{DateTime.Now:HH:mm:ss}] 正在生成蓝光 ISO 镜像…\n{mappingDescription}\n需要停止时，可返回蓝光页面点击“取消生成”。\n");
            IProgress<BluRayStreamingProgress> progress = new PercentageProgress(new Progress<BluRayStreamingProgress>(value =>
                _shell.AppendLog($"蓝光数据：{value.Fraction:P0} · {value.CompletedBytes:N0} / {value.TotalBytes:N0} 字节\n")));
            var generated = await Task.Run(() =>
            {
                BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(parameters, dataDirectory.Length == 0 ? null : dataDirectory, volumeLabel);
                cancellationToken.ThrowIfCancellationRequested();
                if (imageProcessingEnabled) BluRayImageLoader.ValidateSource(source, cancellationToken);
                using RingImagePreparation.PreparedImage preparedImage = RingImagePreparation.PrepareImage(
                    source, null, "bd", imageProcessingEnabled, gammaCorrectionEnabled,
                    cancellationToken, appendLog: processingLog.Report);
                BluRayImagePayloadMapping mapping = BluRayImageLoader.CreateMapping(preparedImage.Path, parameters, luminanceMapping, cancellationToken);
                BluRayStreamingSummary summary = BluRayIsoGenerator.GenerateFile(output, plan, mapping, progress, cancellationToken);
                return (Summary: summary, Plan: plan);
            }, cancellationToken);
            _shell.RegisterOutput(output);
            _shell.Home.RecordJob(RecentJobEntry.BluRayFamily, Path.GetFullPath(source), Path.GetFullPath(output));
            _shell.LivePreview.ImportBluRayIso(Path.GetFullPath(output));
            LayoutStatus = DescribeLayout(generated.Plan);
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] 完成：{Path.GetFullPath(output)}（{generated.Summary.BytesWritten:N0} 字节，{(generated.Plan.HasFileSystem ? "UDF 2.01 镜像" : "全区绘图镜像，无文件系统")}）\n{LayoutStatus}\n");
        }
        catch (OperationCanceledException)
        {
            _shell.AppendLog("蓝光图像数据生成已取消，未发布不完整文件。\n");
        }
        catch (Exception exception)
        {
            if (busyStarted) _shell.AppendLog($"蓝光图像数据生成失败：{exception.Message}\n");
            _shell.ShowValidationError(exception);
        }
        finally
        {
            if (busyStarted) _shell.SetBusy(false);
        }
    }

    public IBluRayLuminanceMapping CreateLuminanceMapping() => UsesRunLengthMapping
        ? new BluRayRunLengthMapping(InvertMapping)
        : _luminanceMapping!;

    private void BrowseDataDirectory()
    {
        string? path = _shell.Dialogs?.PickFolder("选择要放入蓝光内圈的文件夹");
        if (path is not null) DataDirectory = path;
    }

    private void OpenBurn()
    {
        _shell.Burn.IsBluRayBurn = true;
        _shell.Burn.UseCalibrationPattern = false;
        _shell.Burn.UpdateSourceSummary();
        _shell.NavigateTo(ShellViewModel.BurnTabIndex);
    }

    private void OpenCalibration()
    {
        _shell.Calibration.SelectedDiscType = "BD";
        _shell.NavigateTo(ShellViewModel.CalibrationTabIndex);
    }

    private void OpenLivePreview()
    {
        _shell.LivePreview.SetDiscType(2, clearResult: true);
        _shell.LivePreview.SetPreviewMode(calibration: true, clearResult: true);
        _shell.NavigateTo(ShellViewModel.PreviewTabIndex);
    }

    private void InvalidateLayout()
    {
        _layoutRevision++;
        LayoutStatus = string.IsNullOrWhiteSpace(DataDirectory)
            ? "未包含文件夹；整个用户数据区用于绘图，不写入文件系统。"
            : "文件放在内圈，图案从其后的完整蓝光簇开始；点击“计算文件布局”查看占用。";
    }

    private async Task CheckLayoutAsync()
    {
        int revision = _layoutRevision;
        try
        {
            BluRayDiscParameters parameters = ReadParameters();
            string directory = DataDirectory.Trim();
            string label = string.IsNullOrWhiteSpace(VolumeLabel) ? "BD_IMAGE" : VolumeLabel.Trim();
            LayoutStatus = "正在计算内圈文件布局…";
            BluRayIsoPlan plan = await Task.Run(() => BluRayIsoGenerator.PrepareDrawing(parameters, directory.Length == 0 ? null : directory, label));
            if (revision == _layoutRevision) LayoutStatus = DescribeLayout(plan);
        }
        catch (Exception exception)
        {
            if (revision == _layoutRevision) LayoutStatus = $"无法生成此布局：{exception.Message}";
        }
    }

    private static string DescribeLayout(BluRayIsoPlan plan)
        => (plan.HasFileSystem ? $"内圈 {plan.FileCount:N0} 个文件，共 {plan.FileBytes:N0} 字节；" : "未包含文件夹；全区绘图，无文件系统；")
            + $"绘图扇区 {plan.DrawingStartSector:N0}–{plan.DrawingEndSectorExclusive - 1:N0}，"
            + (plan.HasFileSystem ? $"UDF ISO 共 {plan.TotalSectors:N0} 个扇区，尾部保留备份元数据。" : $"共 {plan.TotalSectors:N0} 个用户扇区，首尾均用于绘图。");

    private sealed class PercentageProgress(IProgress<BluRayStreamingProgress> target) : IProgress<BluRayStreamingProgress>
    {
        private int _lastPercent = -1;

        public void Report(BluRayStreamingProgress value)
        {
            int percent = (int)(value.Fraction * 100);
            if (percent == _lastPercent) return;
            _lastPercent = percent;
            target.Report(value);
        }
    }

    private void EnsureNotSourceImage(string output)
    {
        if (!string.IsNullOrWhiteSpace(ImagePath)
            && string.Equals(Path.GetFullPath(output), Path.GetFullPath(ImagePath.Trim()), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("输出路径不能覆盖源图片。");
    }

    private void UpdateGeometry()
    {
        InvalidateLayout();
        try
        {
            BluRayDiscParameters parameters = ReadParameters();
            PresetSummary = $"{parameters.LayerCount} 层 · 当前 {parameters.TotalSectors:N0} 个用户扇区 · {parameters.ContentLength:N0} 字节。预设提供初始值，总扇区数可手动修改。";
            double inner = 90 * parameters.InnerRadiusMm / parameters.OuterRadiusMm;
            CombinedGeometry ring = new(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(90, 90), 90, 90),
                new EllipseGeometry(new Point(90, 90), inner, inner));
            ring.Freeze();
            PreviewClip = ring;
            GeometryStatus = $"数据区：{ParameterNumberText.ForInput(parameters.InnerRadiusMm)}–{ParameterNumberText.ForInput(parameters.OuterRadiusMm)} mm · 起始角 0° · 有效轨距 {new BluRayTrackGeometry(parameters).EffectiveTrackPitchMicrometers:0.######} µm。";
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            PreviewClip = Geometry.Empty;
            PresetSummary = "请填写有效的蓝光总扇区数与半径。";
            GeometryStatus = exception.Message;
        }
    }

    private async void LoadSourcePreview()
    {
        int revision = ++_sourcePreviewRevision;
        _sourcePreviewCancellation?.Cancel();
        _sourcePreviewCancellation = null;
        SourcePreview = null;
        string path = ImagePath.Trim();
        bool imageProcessingEnabled = ImageProcessingEnabled;
        bool gammaCorrectionEnabled = GammaCorrectionEnabled;
        if (path.Length == 0)
        {
            PreviewStatus = "选择源图片后显示环带布局。";
            return;
        }

        try
        {
            FileInfo file = new(path);
            if (!file.Exists) throw new FileNotFoundException("找不到源图片。");
            if (file.Length > MaxPreviewFileBytes)
                throw new ArgumentException("源图片超过 64 MiB，请缩小后再预览。");
            BitmapImage bitmap = new();
            using (FileStream stream = File.OpenRead(file.FullName))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 512;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
            }

            BitmapSource result = bitmap;
            if (imageProcessingEnabled)
            {
                PreviewStatus = "正在处理布局缩略图…";
                using CancellationTokenSource cancellation = new();
                _sourcePreviewCancellation = cancellation;
                CancellationToken token = cancellation.Token;
                try
                {
                    // Keep the existing 512 px decode cap for this layout thumbnail.
                    // Generation and the live projection process the full source image.
                    result = await Task.Run(() => GrayscaleDither.Process(bitmap,
                        BinaryGrayscale.Levels, token, linearizeSrgb: gammaCorrectionEnabled), token);
                    token.ThrowIfCancellationRequested();
                }
                finally
                {
                    if (ReferenceEquals(_sourcePreviewCancellation, cancellation))
                        _sourcePreviewCancellation = null;
                }
            }
            if (revision != _sourcePreviewRevision) return;
            SourcePreview = result;
            PreviewStatus = imageProcessingEnabled
                ? $"已处理布局缩略图 · 黑白抖动 · sRGB 伽马：{(gammaCorrectionEnabled ? "开" : "关")}。实时预览与生成使用原图尺寸处理。"
                : "已载入源图，保持图片长宽比。中心与绘图区外的部分不参与布局。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or FormatException or NotSupportedException or UnauthorizedAccessException or OutOfMemoryException)
        {
            if (revision == _sourcePreviewRevision) PreviewStatus = $"无法预览：{exception.Message}";
        }
    }
}
