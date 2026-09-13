using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Imaging;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class DvdViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;

    /// <summary>Exposed for the view's two-way bindings to the shared parameters.</summary>
    public DiscParametersState State => _state;

    public DvdViewModel(ShellViewModel shell, DiscParametersState state)
    {
        _shell = shell;
        _state = state;
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !_shell.IsBusy);
        BrowseDataCommand = new RelayCommand(BrowseData, () => !_shell.IsBusy);
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !_shell.IsBusy);
        ReloadPresetsCommand = new RelayCommand(ReloadPresets);
        _shell.BusyChanged += NotifyCommands;
    }

    public RelayCommand BrowseOutputCommand { get; }

    /// <summary>Shared with the live preview page: picks the image and prefills paths.</summary>
    public RelayCommand BrowseImageCommand => _shell.BrowseDvdImageCommand;

    public RelayCommand BrowseDataCommand { get; }

    /// <summary>Shared with the start page: opens the editable preset catalog.</summary>
    public RelayCommand OpenPresetsJsonCommand => _shell.OpenPresetsJsonCommand;

    public IAsyncRelayCommand GenerateCommand { get; }

    public RelayCommand ReloadPresetsCommand { get; }

    private void NotifyCommands()
    {
        BrowseOutputCommand.NotifyCanExecuteChanged();
        BrowseDataCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
    }

    private void BrowseOutput()
    {
        string? picked = _shell.Dialogs?.PickSave("ISO 镜像|*.iso|所有文件|*.*", ".iso", "dvd-image.iso");
        if (picked is not null)
        {
            _state.DvdOutputPath = picked;
        }
    }

    private void BrowseData()
    {
        string? picked = _shell.Dialogs?.PickFolder("选择要放入 DVD 内圈的文件夹");
        if (picked is not null)
        {
            _state.DvdDataDirectory = picked;
        }
    }

    private async Task GenerateAsync()
    {
        try
        {
            string image = RequirePath(_state.DvdImagePath, "请选择 DVD 源图片。");
            string output = RequirePath(_state.DvdOutputPath, "请选择 DVD ISO 输出位置。");
            using RingImagePreparation.PreparedImage preparedImage = PrepareDvdImage(
                image,
                RingImageQuality.GenerationSize);
            List<string> arguments =
            [
                "solve",
                "--image", preparedImage.Path,
                "--iso-output", output,
                "--total-sectors", _state.DvdTotalSectors.Trim(),
                "--inner-radius-mm", _state.DvdInnerRadius.Trim(),
                "--outer-radius-mm", _state.DvdOuterRadius.Trim(),
                "--spiral-direction", "cw",
                "--image-threshold", "128",
                "--alpha-threshold", "1",
                "--algorithm", "dispersion",
                "--iterations-per-block", "1",
                "--fast-output", "true",
                "--volume-label", string.IsNullOrWhiteSpace(_state.DvdVolumeLabel) ? "DISC_IMAGE" : _state.DvdVolumeLabel.Trim(),
            ];
            if (string.IsNullOrWhiteSpace(_state.DvdDataDirectory))
            {
                arguments.Add("--fill-sectors");
                arguments.Add(_state.DvdTotalSectors.Trim());
            }
            else
            {
                arguments.Add("--data-dir");
                arguments.Add(_state.DvdDataDirectory.Trim());
            }

            await _shell.RunDiscJobAsync(
                "正在生成 DVD ISO…",
                arguments.ToArray(),
                output,
                RecentJobEntry.DvdFamily,
                _state.DvdImagePath);
            // The generated ISO is the live preview page's calibration input.
            _state.DvdIsoPath = output;
        }
        catch (Exception exception)
        {
            _shell.ShowValidationError(exception);
        }
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

    private void ReloadPresets()
    {
        try
        {
            (int cdCount, int dvdCount) = _state.ReloadPresets();
            _shell.ShowToast(
                "预设已更新",
                $"预设已重新加载：{cdCount} 个 CD、{dvdCount} 个 DVD。\n\n文件位置：\n{_state.DiscPresetJsonPath}",
                ToastKind.Success);
        }
        catch (Exception exception)
        {
            _shell.ShowToast(
                "重新加载失败",
                $"JSON 中有无法使用的内容，当前有效预设未改变。\n\n{exception.Message}\n\n文件位置：\n{_state.DiscPresetJsonPath}",
                ToastKind.Error);
        }
    }

    private static string RequirePath(string raw, string message)
    {
        string value = raw.Trim();
        return value.Length == 0 ? throw new ArgumentException(message) : value;
    }
}
