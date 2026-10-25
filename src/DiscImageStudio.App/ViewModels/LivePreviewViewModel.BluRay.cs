using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class LivePreviewViewModel
{
    public BluRayViewModel BluRay => _shell.BluRay;

    [ObservableProperty] private string _bluRayIsoPath = string.Empty;
    [ObservableProperty] private string _bluRayActualInnerRadius = "24";
    [ObservableProperty] private string _bluRayActualOuterRadius = "58";
    [ObservableProperty] private string _bluRayActualStartAngle = "0";
    [ObservableProperty] private string _bluRayPreviewSize = "2048";

    public RelayCommand BrowseBluRayIsoCommand { get; private set; } = null!;

    internal void InitializeBluRay()
    {
        BrowseBluRayIsoCommand = new RelayCommand(() =>
        {
            string? path = _shell.Dialogs?.PickOpen("选择生成的 BD ISO", "ISO 镜像|*.iso|所有文件|*.*");
            if (path is not null) ImportBluRayIso(path);
        }, () => !_shell.IsBusy);
        _shell.BusyChanged += () => BrowseBluRayIsoCommand.NotifyCanExecuteChanged();
        BluRay.PropertyChanged += BluRay_PropertyChanged;
        SynchronizeBluRayPreset();
    }

    private void SynchronizeBluRayPreset()
    {
        if (BluRay.SelectedPreset is not { } preset || preset.IsCustom) return;
        BluRayDiscParameters parameters = preset.CreateParameters();
        BluRayActualInnerRadius = FormatNumber(parameters.InnerRadiusMm);
        BluRayActualOuterRadius = FormatNumber(parameters.OuterRadiusMm);
        BluRayActualStartAngle = FormatNumber(parameters.StartAngleDegrees);
    }

    private void BluRay_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BluRayViewModel.SelectedPreset)) SynchronizeBluRayPreset();
        if (e.PropertyName is nameof(BluRayViewModel.SelectedPreset)
            or nameof(BluRayViewModel.TotalSectors) or nameof(BluRayViewModel.InnerRadius)
            or nameof(BluRayViewModel.OuterRadius) or nameof(BluRayViewModel.ImagePath)
            or nameof(BluRayViewModel.InvertMapping) or nameof(BluRayViewModel.DataDirectory)
            or nameof(BluRayViewModel.VolumeLabel) or nameof(BluRayViewModel.ImageProcessingEnabled)
            or nameof(BluRayViewModel.GammaCorrectionEnabled))
        {
            ScheduleBluRay();
        }
    }

    private void ScheduleBluRay()
    {
        if (!IsBluRaySelected) return;
        _downloadablePreview = false;
        DownloadPreviewCommand.NotifyCanExecuteChanged();
        Schedule();
    }

    /// <summary>Explicit imports re-read geometry even if this path is already selected:
    /// the file may have been regenerated, or its capacity may have been edited in the UI.</summary>
    internal void ImportBluRayIso(string path)
    {
        if (string.Equals(BluRayIsoPath, path, StringComparison.Ordinal))
            OnBluRayIsoPathChanged(path);
        else BluRayIsoPath = path;
    }

    partial void OnBluRayIsoPathChanged(string value)
    {
        // A generated ISO contains one 2048-byte user-data block per logical sector.
        // Adopt its capacity, while leaving measured radii available for calibration.
        try
        {
            FileInfo file = new(value.Trim());
            long sectors = file.Exists ? file.Length / BluRayDiscParameters.BytesPerSector : 0;
            if (sectors > 0 && sectors <= BluRayDiscParameters.SectorsPerLayer
                && sectors % BluRayDiscParameters.SectorsPerCluster == 0
                && file.Length % BluRayDiscParameters.BytesPerSector == 0)
                BluRay.TotalSectors = sectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Partial path edits are validated by the debounced renderer.
        }
        ScheduleBluRay();
    }

    partial void OnBluRayActualInnerRadiusChanged(string value) => ScheduleBluRay();
    partial void OnBluRayActualOuterRadiusChanged(string value) => ScheduleBluRay();
    partial void OnBluRayActualStartAngleChanged(string value) => ScheduleBluRay();
    partial void OnBluRayPreviewSizeChanged(string value) => ScheduleBluRay();

    private async Task RefreshBluRayAsync(int revision)
    {
        _livePreviewCancellation?.Cancel();
        _livePreviewCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _livePreviewCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        string? outputPath = null;
        bool keepOutput = false;
        try
        {
            bool calibration = IsCalibrationPreview;
            string inputPath = (calibration ? BluRay.ImagePath : BluRayIsoPath).Trim();
            string inputName = calibration ? "BD 源图片" : "生成的 BD ISO";
            if (inputPath.Length == 0) throw new ArgumentException($"请先选择{inputName}。");
            if (!File.Exists(inputPath)) throw new FileNotFoundException($"{inputName}文件不存在。");
            if (calibration && !BluRay.UsesRunLengthMapping)
                throw new ArgumentException("当前自定义亮度映射不支持 3T/6T 实时预览。");

            // Readback uses measured geometry; hidden source-projection radius edits
            // must not prevent a valid ISO from rendering.
            BluRayDiscParameters generated = calibration ? BluRay.ReadParameters() : new(
                TotalSectors: checked((uint)ParameterParser.ParsePositiveLong(BluRay.TotalSectors, "蓝光总扇区数")),
                LayerCount: BluRay.SelectedPreset?.LayerCount ?? 1);
            BluRayDiscParameters actual = generated with
            {
                InnerRadiusMm = ParameterParser.ParsePositiveDouble(BluRayActualInnerRadius, "实际内半径"),
                OuterRadiusMm = ParameterParser.ParsePositiveDouble(BluRayActualOuterRadius, "实际外半径"),
                StartAngleDegrees = ParameterParser.ParseDouble(BluRayActualStartAngle, "实际起始角"),
            };
            actual.Validate();
            int outputSize = ParameterParser.ParsePreviewSize(BluRayPreviewSize);
            bool invert = BluRay.InvertMapping;
            bool imageProcessingEnabled = BluRay.ImageProcessingEnabled;
            bool gammaCorrectionEnabled = BluRay.GammaCorrectionEnabled;
            string directory = BluRay.DataDirectory.Trim();
            string label = string.IsNullOrWhiteSpace(BluRay.VolumeLabel) ? "BD_IMAGE" : BluRay.VolumeLabel.Trim();
            Directory.CreateDirectory(RingImagePreparation.LivePreviewDirectory);
            outputPath = Path.Combine(RingImagePreparation.LivePreviewDirectory, $"bd-live-{Guid.NewGuid():N}.png");
            StatusText = $"正在刷新 {outputSize} px BD 实时预览…";
            string destination = outputPath;
            await StaWorker.RunAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                if (calibration)
                {
                    BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(generated, directory.Length == 0 ? null : directory, label);
                    token.ThrowIfCancellationRequested();
                    if (imageProcessingEnabled) BluRayImageLoader.ValidateSource(inputPath, token);
                    using RingImagePreparation.PreparedImage preparedImage = RingImagePreparation.PrepareImage(
                        inputPath, null, "bd-live", imageProcessingEnabled, gammaCorrectionEnabled,
                        token, writeLog: false);
                    BluRayPreviewRenderer.RenderCalibration(preparedImage.Path, destination, generated, actual,
                        plan.DrawingStartSector, plan.DrawingEndSectorExclusive, outputSize, invert, token);
                }
                else
                {
                    BluRayPreviewRenderer.RenderReadback(inputPath, destination, actual, outputSize, token);
                }
                return 0;
            });
            token.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision) return;
            PublishPreview(outputPath, inputPath, "实时预览 - BD");
            keepOutput = true;
            StatusText = $"已实时更新 · {(calibration ? (imageProcessingEnabled ? "处理后投影" : "原图投影") : "ISO 读回")} · 3T/6T 图案模拟 · {outputSize} px";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (revision != _livePreviewRevision || token.IsCancellationRequested) return;
            UpdateMode(clearResult: true);
            StatusText = exception.Message;
            ResultPreviewPlaceholderText = exception.Message;
            if (exception is not (ArgumentException or FormatException or OverflowException or FileNotFoundException))
                _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] BD 实时预览失败：{exception}\n");
        }
        finally
        {
            if (!keepOutput) RingImagePreparation.TryDeleteTemporaryFile(outputPath);
        }
    }
}
