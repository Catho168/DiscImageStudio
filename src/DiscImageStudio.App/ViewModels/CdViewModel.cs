using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Cd;
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
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !_shell.IsBusy);
        _shell.BusyChanged += NotifyCommands;
    }

    public RelayCommand BrowseOutputCommand { get; }

    /// <summary>Shared with the live preview page: picks the image and prefills paths.</summary>
    public RelayCommand BrowseImageCommand => _shell.BrowseCdImageCommand;

    /// <summary>Shared with the start page, the DVD page and the live preview page.</summary>
    public RelayCommand OpenPresetsJsonCommand => _shell.OpenPresetsJsonCommand;

    public RelayCommand ReloadPresetsCommand => _shell.ReloadPresetsCommand;

    public IAsyncRelayCommand GenerateCommand { get; }

    private void NotifyCommands()
    {
        BrowseOutputCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
    }

    private void BrowseOutput()
    {
        string? picked = _shell.Dialogs?.PickSave("WAV 音轨（推荐）|*.wav|RAW 音轨（旧格式）|*.raw|所有文件|*.*", ".wav", "cd-track.wav");
        if (picked is not null)
        {
            _state.CdOutputPath = picked;
        }
    }

    private async Task GenerateAsync()
    {
        try
        {
            string input = RequirePath(_state.CdImagePath, "请选择 CD 源图片。");
            string output = RequirePath(_state.CdOutputPath, "请选择 CD 音轨输出位置。");
            RingImagePreparation.Request imagePreparation = CreateCdImageRequest(
                input,
                RingImageQuality.GenerationSize);
            List<string> arguments = ["cd-generate", "--input", input, "--output", output];
            AddCdGeometry(arguments);
            arguments.Add("--interleave");
            arguments.Add(_state.CdInterleave.ToString().ToLowerInvariant());
            arguments.Add("--cue");
            arguments.Add(_state.CdWriteCue.ToString().ToLowerInvariant());
            await _shell.RunDiscJobAsync(
                "正在生成 CD 音轨…",
                arguments.ToArray(),
                output,
                RecentJobEntry.CdFamily,
                input,
                imagePreparation,
                imageOption: "--input");
            // The generated track is the live preview page's calibration input.
            _state.CdTrackPath = output;
        }
        catch (Exception exception)
        {
            _shell.ShowValidationError(exception);
        }
    }

    // The CD engine defaults (1200 mm/s, 0°, 57.5 mm) cover velocity, start angle and the
    // image outer radius; only the user-visible geometry is passed through.
    private void AddCdGeometry(List<string> arguments)
    {
        arguments.AddRange(
        [
            "--r0", _state.CdInnerRadius.Trim(),
            "--r1", _state.CdOuterRadius.Trim(),
            "--sectors", _state.CdSectors.Trim(),
        ]);
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

    private static string RequirePath(string raw, string message)
    {
        string value = raw.Trim();
        return value.Length == 0 ? throw new ArgumentException(message) : value;
    }
}
