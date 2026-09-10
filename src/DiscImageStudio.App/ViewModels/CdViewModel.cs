using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Imaging;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class CdViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;

    /// <summary>Exposed for the view's two-way bindings to the shared parameters.</summary>
    public DiscParametersState State => _state;

    public CdViewModel(ShellViewModel shell, DiscParametersState state)
    {
        _shell = shell;
        _state = state;
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !_shell.IsBusy);
        BrowsePreviewCommand = new RelayCommand(BrowsePreview, () => !_shell.IsBusy);
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !_shell.IsBusy);
        PreviewWarpCommand = new AsyncRelayCommand(PreviewWarpAsync, () => !_shell.IsBusy);
        PreviewTrackCommand = new AsyncRelayCommand(PreviewTrackAsync, () => !_shell.IsBusy);
        _shell.BusyChanged += NotifyCommands;
    }

    public RelayCommand BrowseOutputCommand { get; }

    /// <summary>Shared with the live preview page: picks the image and prefills paths.</summary>
    public RelayCommand BrowseImageCommand => _shell.BrowseCdImageCommand;

    public RelayCommand BrowsePreviewCommand { get; }

    public IAsyncRelayCommand GenerateCommand { get; }

    public IAsyncRelayCommand PreviewWarpCommand { get; }

    public IAsyncRelayCommand PreviewTrackCommand { get; }

    private void NotifyCommands()
    {
        BrowseOutputCommand.NotifyCanExecuteChanged();
        BrowsePreviewCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
        PreviewWarpCommand.NotifyCanExecuteChanged();
        PreviewTrackCommand.NotifyCanExecuteChanged();
    }

    private void BrowseOutput()
    {
        string? picked = _shell.Dialogs?.PickSave("CD 原始音轨|*.raw|所有文件|*.*", ".raw", "cd-track.raw");
        if (picked is not null)
        {
            _state.CdOutputPath = picked;
        }
    }

    private void BrowsePreview()
    {
        string? picked = _shell.Dialogs?.PickSave("PNG 图片|*.png", ".png", "cd-preview.png");
        if (picked is not null)
        {
            _state.CdPreviewPath = picked;
        }
    }

    private async Task GenerateAsync()
    {
        try
        {
            string input = RequirePath(_state.CdImagePath, "请选择 CD 源图片。");
            string output = RequirePath(_state.CdOutputPath, "请选择 CD 原始音轨输出位置。");
            using RingImagePreparation.PreparedImage preparedImage = PrepareCdImage(
                input,
                RingImageQuality.GenerationSize);
            List<string> arguments = ["cd-generate", "--input", preparedImage.Path, "--output", output];
            AddCdGeometry(arguments, string.Empty, actual: false);
            arguments.Add("--interleave");
            arguments.Add(_state.CdInterleave.ToString().ToLowerInvariant());
            await _shell.RunDiscJobAsync(
                "正在生成 CD 原始音轨…",
                arguments.ToArray(),
                output,
                RecentJobEntry.CdFamily,
                _state.CdImagePath);
        }
        catch (Exception exception)
        {
            _shell.ShowValidationError(exception);
        }
    }

    private async Task PreviewWarpAsync()
    {
        try
        {
            string input = RequirePath(_state.CdImagePath, "请选择 CD 源图片。");
            string output = RequirePath(_state.CdPreviewPath, "请选择 CD 预览输出位置。");
            int processingSize = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.CdPreviewSize, "预览尺寸"),
                512,
                4096);
            using RingImagePreparation.PreparedImage preparedImage = PrepareCdImage(
                input,
                Math.Max(RingImageQuality.SavedPreviewSize, processingSize));
            List<string> arguments = ["cd-preview-warp", "--input", preparedImage.Path, "--output", output];
            AddCdGeometry(arguments, "gen-", actual: false);
            AddCdGeometry(arguments, "actual-", actual: true);
            arguments.AddRange(["--size", _state.CdPreviewSize.Trim(), "--samples-per-sector", _state.CdSamplesPerSector.Trim()]);
            await _shell.RunDiscJobAsync("正在生成 CD 几何预览…", arguments.ToArray(), output);
        }
        catch (Exception exception)
        {
            _shell.ShowValidationError(exception);
        }
    }

    private async Task PreviewTrackAsync()
    {
        try
        {
            string track = RequirePath(_state.CdOutputPath, "请选择或生成 CD 原始音轨。");
            string output = RequirePath(_state.CdPreviewPath, "请选择 CD 预览输出位置。");
            List<string> arguments = ["cd-preview-track", "--track", track, "--output", output];
            AddCdGeometry(arguments, string.Empty, actual: true);
            arguments.AddRange(["--size", _state.CdPreviewSize.Trim(), "--byte-step", "48"]);
            await _shell.RunDiscJobAsync("正在预览 CD 音轨…", arguments.ToArray(), output);
        }
        catch (Exception exception)
        {
            _shell.ShowValidationError(exception);
        }
    }

    private void AddCdGeometry(List<string> arguments, string prefix, bool actual)
    {
        arguments.AddRange(
        [
            "--" + prefix + "r0", actual ? _state.CdActualInnerRadius.Trim() : _state.CdInnerRadius.Trim(),
            "--" + prefix + "r1", actual ? _state.CdActualOuterRadius.Trim() : _state.CdOuterRadius.Trim(),
            "--" + prefix + "sectors", _state.CdSectors.Trim(),
            "--" + prefix + "velocity", actual ? _state.CdActualVelocity.Trim() : _state.CdVelocity.Trim(),
            "--" + prefix + "theta0", actual ? _state.CdActualStartAngle.Trim() : _state.CdStartAngle.Trim(),
            "--" + prefix + "outer", _state.CdImageOuterRadius.Trim(),
        ]);
    }

    private RingImagePreparation.PreparedImage PrepareCdImage(string sourcePath, int outputSize)
    {
        if (_state.CdImageProcessingModeIndex != 1)
        {
            return RingImagePreparation.PreparedImage.Original(sourcePath);
        }

        double generatedInner = ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, "CD 内半径");
        double generatedOuter = ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, "CD 外半径");
        double canvasOuter = ParameterParser.ParsePositiveDouble(_state.CdImageOuterRadius, "图片外半径");
        RingImageLayoutOptions options = ParameterParser.CreateRingLayoutOptions(
            canvasOuter,
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

    private static string RequirePath(string raw, string message)
    {
        string value = raw.Trim();
        return value.Length == 0 ? throw new ArgumentException(message) : value;
    }
}
