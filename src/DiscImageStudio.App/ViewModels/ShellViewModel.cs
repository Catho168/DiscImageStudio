using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    public const int HomeTabIndex = 0;
    public const int DvdTabIndex = 1;
    public const int CdTabIndex = 2;
    public const int BurnTabIndex = 3;
    public const int PreviewTabIndex = 4;
    public const int LogTabIndex = 5;
    public const int AboutTabIndex = 6;
    public const int PageCount = 7;

    private readonly Dispatcher _dispatcher;
    private readonly StringBuilder _log = new();
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
        Home = new HomeViewModel(this, State, new RecentJobStore());
        Burn = new BurnViewModel(this, State);
        Dvd = new DvdViewModel(this, State);
        Cd = new CdViewModel(this, State);
        Log = new LogViewModel(this);
        About = new AboutViewModel();
        OpenOutputCommand = new RelayCommand(OpenOutput, () => OpenOutputEnabled);
        OpenPresetsJsonCommand = new RelayCommand(OpenPresetsJson);
        BrowseCdImageCommand = new RelayCommand(() => BrowseCdImage(), () => !IsBusy);
        BrowseDvdImageCommand = new RelayCommand(() => BrowseDvdImage(), () => !IsBusy);
        BrowseCdTrackCommand = new RelayCommand(() => BrowseCdTrack(), () => !IsBusy);
        BrowseDvdIsoCommand = new RelayCommand(() => BrowseDvdIso(), () => !IsBusy);
    }

    public DiscParametersState State { get; }

    public LivePreviewViewModel LivePreview { get; }

    public HomeViewModel Home { get; }

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
    private bool _openOutputEnabled;

    // The log page's button relies on the command's own CanExecute rather than a second
    // IsEnabled binding, so the enablement has to reach the command.
    partial void OnOpenOutputEnabledChanged(bool value) => OpenOutputCommand.NotifyCanExecuteChanged();

    public bool IsBusy => _isBusy;

    public bool IsWindowLoaded => _isWindowLoaded;

    /// <summary>Reveals the last job's output folder; shared by the log page's header.</summary>
    public RelayCommand OpenOutputCommand { get; }

    /// <summary>Opens the editable disc-preset JSON; shared by the start page and the DVD page.</summary>
    public RelayCommand OpenPresetsJsonCommand { get; }

    public RelayCommand BrowseCdImageCommand { get; }

    public RelayCommand BrowseDvdImageCommand { get; }

    /// <summary>Live preview page: picks the generated CD track to simulate.</summary>
    public RelayCommand BrowseCdTrackCommand { get; }

    /// <summary>Live preview page: picks the generated DVD ISO to simulate.</summary>
    public RelayCommand BrowseDvdIsoCommand { get; }

    /// Fired on every busy transition so page commands can refresh CanExecute.
    public event Action? BusyChanged;

    public event Action<string>? LogAppended;

    public event Action? LogCleared;

    /// <summary>The entire log so far. It lives here rather than only in the log TextBox because
    /// the log page is one of several: output produced while that page is hidden has to survive,
    /// so the view re-renders this text every time the page becomes visible again.</summary>
    public string LogText => _log.ToString();

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

    public void AppendLog(string text)
    {
        _log.Append(text);
        LogAppended?.Invoke(text);
    }

    public void ClearLog()
    {
        _log.Clear();
        LogCleared?.Invoke();
    }

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

    /// <summary>Points the log page's "open output location" action at a file written outside a
    /// job, such as the live preview's export.</summary>
    internal void SetLastOutputPath(string path)
    {
        _lastOutputPath = Path.GetFullPath(path);
        OpenOutputEnabled = true;
    }

    /// <summary>Browse for the CD source image; prefills the output when empty.
    /// Returns false when the picker is cancelled.</summary>
    internal bool BrowseCdImage()
    {
        string? picked = Dialogs?.PickImage();
        if (picked is null)
        {
            return false;
        }

        State.CdImagePath = picked;
        string directory = Path.GetDirectoryName(picked) ?? Environment.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(State.CdOutputPath))
        {
            State.CdOutputPath = Path.Combine(directory, "cd-track.wav");
        }

        return true;
    }

    /// <summary>Browse for the generated CD track used by the live read-back preview.</summary>
    internal bool BrowseCdTrack()
    {
        string? picked = Dialogs?.PickOpen("选择生成的 CD 音轨", "WAV 音轨|*.wav|RAW 音轨|*.raw|所有文件|*.*");
        if (picked is null)
        {
            return false;
        }

        State.CdTrackPath = picked;
        return true;
    }

    /// <summary>Browse for the generated DVD ISO used by the live read-back preview.</summary>
    internal bool BrowseDvdIso()
    {
        string? picked = Dialogs?.PickOpen("选择生成的 DVD ISO", "ISO 镜像|*.iso|所有文件|*.*");
        if (picked is null)
        {
            return false;
        }

        State.DvdIsoPath = picked;
        return true;
    }

    /// <summary>Browse for the DVD source image; prefills the ISO output when empty.
    /// Returns false when the picker is cancelled.</summary>
    internal bool BrowseDvdImage()
    {
        string? picked = Dialogs?.PickImage();
        if (picked is null)
        {
            return false;
        }

        State.DvdImagePath = picked;
        string directory = Path.GetDirectoryName(picked) ?? Environment.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(State.DvdOutputPath))
        {
            State.DvdOutputPath = Path.Combine(directory, "dvd-image.iso");
        }

        return true;
    }

    /// <summary>Opens the editable preset catalog in the shell's default JSON handler.</summary>
    private void OpenPresetsJson()
    {
        try
        {
            State.EnsurePresetsJsonExists();
            Process.Start(new ProcessStartInfo(State.DiscPresetJsonPath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            ShowToast(
                "打开失败",
                $"无法打开预设 JSON。\n\n{exception.Message}\n\n文件位置：\n{State.DiscPresetJsonPath}",
                ToastKind.Error);
        }
    }

    /// Streams a disc job through UnifiedCommandRunner on an STA thread, mirroring the
    /// original RunCommandAsync: busy gating, console redirection, log jump, preview jump.
    /// A generation job also passes its family and source picture, which is what the start
    /// page's recent list reopens; previews and CLI runs leave both null.
    internal async Task RunDiscJobAsync(
        string status,
        string[] arguments,
        string outputPath,
        string? discFamily = null,
        string? sourceImagePath = null)
    {
        if (_isBusy)
        {
            ShowToast("操作未执行", "已有任务正在运行。", ToastKind.Info);
            return;
        }

        SetBusy(true);
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
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 完成：{_lastOutputPath}\n");
            if (discFamily is not null && !string.IsNullOrWhiteSpace(sourceImagePath))
            {
                Home.RecordJob(discFamily, Path.GetFullPath(sourceImagePath), _lastOutputPath);
            }

            if (IsPngPath(_lastOutputPath))
            {
                LivePreview.ShowResultPreview(_lastOutputPath);
                SelectPage(PreviewTabIndex);
            }
        }
        else
        {
            AppendLog(
                $"[{DateTime.Now:HH:mm:ss}] 任务失败：退出码 {exitCode}，未生成 {Path.GetFullPath(outputPath)}\n");
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
        BrowseCdTrackCommand.NotifyCanExecuteChanged();
        BrowseDvdIsoCommand.NotifyCanExecuteChanged();
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
