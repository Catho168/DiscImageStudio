using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    public const int DvdTabIndex = 0;
    public const int CdTabIndex = 1;
    public const int BurnTabIndex = 2;
    public const int PreviewTabIndex = 3;
    public const int LogTabIndex = 4;
    public const int AboutTabIndex = 5;
    public const int PageCount = 6;

    private readonly Dispatcher _dispatcher;
    private readonly string? _presetLoadWarning;
    private bool _isBusy;
    private bool _isWindowLoaded;
    private string? _lastOutputPath;

    public ShellViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        State = new DiscParametersState();
        State.PropertyChanged += State_PropertyChanged;
        _presetLoadWarning = State.TryLoadPresets();
        State.SelectDefaultPresets();
        LivePreview = new LivePreviewViewModel(_dispatcher, this, State);
        Burn = new BurnViewModel(this, State);
        Dvd = new DvdViewModel(this, State);
        Cd = new CdViewModel(this, State);
        Log = new LogViewModel(this);
        About = new AboutViewModel();
        OpenOutputCommand = new RelayCommand(OpenOutput, () => OpenOutputEnabled);
        BrowseCdImageCommand = new RelayCommand(BrowseCdImage, () => !IsBusy);
        BrowseDvdImageCommand = new RelayCommand(BrowseDvdImage, () => !IsBusy);
    }

    public DiscParametersState State { get; }

    public LivePreviewViewModel LivePreview { get; }

    public BurnViewModel Burn { get; }

    public DvdViewModel Dvd { get; }

    public CdViewModel Cd { get; }

    public LogViewModel Log { get; }

    public AboutViewModel About { get; }

    public ObservableCollection<ToastItem> Toasts { get; } = [];

    /// <summary>Installed by the window: Win32 file dialogs modal to the main window.</summary>
    internal FileDialogService? Dialogs { get; set; }

    /// <summary>Installed by the window; presents the in-app burn confirmation modal.</summary>
    internal Func<string, string, string, Task<bool>>? ConfirmHandler { get; set; }

    [ObservableProperty]
    private int _currentPageIndex;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private bool _openOutputEnabled;

    public bool IsBusy => _isBusy;

    public bool IsWindowLoaded => _isWindowLoaded;

    public RelayCommand OpenOutputCommand { get; }

    public RelayCommand BrowseCdImageCommand { get; }

    public RelayCommand BrowseDvdImageCommand { get; }

    /// Fired on every busy transition so page commands can refresh CanExecute.
    public event Action? BusyChanged;

    public event Action<string>? LogAppended;

    public event Action? LogCleared;

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
        => ConfirmHandler?.Invoke(title, message, confirmLabel) ?? Task.FromResult(false);

    public void MarkWindowLoaded()
    {
        _isWindowLoaded = true;
        if (_presetLoadWarning is not null)
        {
            ShowToast("预设加载失败", _presetLoadWarning, ToastKind.Warning);
        }

        LivePreview.OnWindowLoaded();
    }

    public void NavigateTo(int pageIndex)
    {
        SelectPage(pageIndex);
        if (pageIndex == PreviewTabIndex)
        {
            LivePreview.Schedule();
        }
        else if (pageIndex == BurnTabIndex)
        {
            Burn.UpdateSourceSummary();
            if (Burn.Devices is null || Burn.Devices.Count == 0)
            {
                _ = Burn.RefreshDevicesAsync();
            }
        }
    }

    /// Plain page switch without navigation side effects (used by job flow and snapshots).
    public void SelectPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        CurrentPageIndex = pageIndex;
    }

    public void AppendLog(string text) => LogAppended?.Invoke(text);

    public void ClearLog() => LogCleared?.Invoke();

    public void ShowToast(string title, string message, ToastKind kind)
        => ShowToast(title, message, kind, kind is ToastKind.Error or ToastKind.Warning
            ? TimeSpan.FromSeconds(6)
            : TimeSpan.FromSeconds(2));

    public void ShowToast(string title, string message, ToastKind kind, TimeSpan timeout)
    {
        ToastItem toast = new(_dispatcher, title, message, kind, timeout);
        toast.Closed += (_, _) => Toasts.Remove(toast);
        Toasts.Add(toast);
        toast.BeginLifetime();
    }

    public void ShowValidationError(Exception exception)
        => ShowToast("请检查输入", exception.Message, ToastKind.Warning);

    private void OpenOutput()
    {
        if (string.IsNullOrWhiteSpace(_lastOutputPath))
        {
            return;
        }

        string? directory = Directory.Exists(_lastOutputPath)
            ? _lastOutputPath
            : Path.GetDirectoryName(_lastOutputPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
    }

    /// <summary>Browse for the CD source image; prefills output/preview when empty.</summary>
    private void BrowseCdImage()
    {
        string? picked = Dialogs?.PickImage();
        if (picked is null)
        {
            return;
        }

        State.CdImagePath = picked;
        string directory = Path.GetDirectoryName(picked) ?? Environment.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(State.CdOutputPath))
        {
            State.CdOutputPath = Path.Combine(directory, "cd-track.raw");
        }

        if (string.IsNullOrWhiteSpace(State.CdPreviewPath))
        {
            State.CdPreviewPath = Path.Combine(directory, "cd-preview.png");
        }
    }

    /// <summary>Browse for the DVD source image; prefills output/preview when empty.</summary>
    private void BrowseDvdImage()
    {
        string? picked = Dialogs?.PickImage();
        if (picked is null)
        {
            return;
        }

        State.DvdImagePath = picked;
        string directory = Path.GetDirectoryName(picked) ?? Environment.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(State.DvdOutputPath))
        {
            State.DvdOutputPath = Path.Combine(directory, "dvd-image.iso");
        }

        if (string.IsNullOrWhiteSpace(State.DvdPreviewPath))
        {
            State.DvdPreviewPath = Path.Combine(directory, "dvd-preview.png");
        }
    }

    /// Streams a disc job through UnifiedCommandRunner on an STA thread, mirroring the
    /// original RunCommandAsync: busy gating, console redirection, log jump, preview jump.
    internal async Task RunDiscJobAsync(string status, string[] arguments, string outputPath)
    {
        if (_isBusy)
        {
            ShowToast("操作未执行", "已有任务正在运行。", ToastKind.Info);
            return;
        }

        SetBusy(true);
        StatusText = status;
        SelectPage(LogTabIndex);
        if (IsPngPath(outputPath))
        {
            LivePreview.PrepareResultPreview(outputPath);
        }

        LogAppended?.Invoke($"\n[{DateTime.Now:HH:mm:ss}] {status}\n");
        DispatcherTextWriter writer = new(_dispatcher, AppendLog);
        TextWriter previousOutput = Console.Out;
        TextWriter previousError = Console.Error;
        int exitCode;
        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            exitCode = await StaWorker.RunAsync(() => UnifiedCommandRunner.Run(arguments));
            writer.Flush();
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
            SetBusy(false);
        }

        if (exitCode == 0)
        {
            _lastOutputPath = Path.GetFullPath(outputPath);
            OpenOutputEnabled = true;
            StatusText = "完成";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 完成：{_lastOutputPath}\n");
            if (IsPngPath(_lastOutputPath))
            {
                LivePreview.ShowResultPreview(_lastOutputPath);
                SelectPage(PreviewTabIndex);
            }
        }
        else
        {
            StatusText = "未完成，请查看日志";
            ShowToast("任务未完成", "请查看运行日志中的错误信息。", ToastKind.Warning);
        }
    }

    internal void SetBusy(bool busy)
    {
        if (_isBusy == busy)
        {
            return;
        }

        _isBusy = busy;
        OnPropertyChanged(nameof(IsBusy));
        BrowseCdImageCommand.NotifyCanExecuteChanged();
        BrowseDvdImageCommand.NotifyCanExecuteChanged();
        BusyChanged?.Invoke();
    }

    internal static bool IsPngPath(string path)
        => string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase);

    private void State_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Any parameter edit refreshes the burn summary and schedules the debounced live
        // preview; both were driven by TextChanged/SelectionChanged before the merge.
        Burn?.UpdateSourceSummary();
        LivePreview?.Schedule();
    }
}
