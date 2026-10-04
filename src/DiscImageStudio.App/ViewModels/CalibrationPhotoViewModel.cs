using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Controls;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class CalibrationViewModel
{
    private CalibrationPhotoTransform? _photoTransform;
    private string? _photoPng;
    private bool _restoringPhoto;
    private long _photoRevision;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsTraceStep))]
    private bool _isPhotoStep = true;
    public bool IsTraceStep => !IsPhotoStep;
    [ObservableProperty] private BitmapSource? _photoImage;
    [ObservableProperty] private BitmapSource? _rectifiedPhoto;
    [ObservableProperty] private bool _isRectificationReady;
    [ObservableProperty] private bool _previewRectification;
    [ObservableProperty] private double _photoCanvasRadiusMm = 63;
    [ObservableProperty] private IReadOnlyList<CalibrationPhotoBoundaryPoint> _photoBoundaryPoints = [];
    [ObservableProperty] private IReadOnlyList<Point[]> _photoBoundaryContours = [];
    [ObservableProperty] private IReadOnlyList<CalibrationTracePoint> _tracePoints = [];
    [ObservableProperty] private IReadOnlyList<Point[]> _crossPredictionContours = [];
    [ObservableProperty] private int _selectedTraceArm;
    [ObservableProperty] private string _photoStatus = "导入能看清实体外缘、中心孔和一条径向曲线的照片；盘片朝向不限。";
    [ObservableProperty] private string _rectificationSummary = "拖动外缘与中心孔的控制点，使它们贴合实体边缘，再计算矫正。";
    public string TraceSummary => $"已保留 {TracePoints.Count} 点 · 全部参与反推";
    public IReadOnlyList<PhotoDiscSize> PhysicalDiscSizeChoices { get; } =
        [new("标准光盘 · 12 cm", 60), new("小光盘 · 8 cm", 40)];
    [ObservableProperty] private PhotoDiscSize _selectedPhysicalDiscSize = new("标准光盘 · 12 cm", 60);

    public IAsyncRelayCommand ImportPhotoCommand { get; private set; } = null!;
    public IAsyncRelayCommand RectifyPhotoCommand { get; private set; } = null!;
    public RelayCommand ConfirmRectificationCommand { get; private set; } = null!;
    public RelayCommand BackToPhotoCommand { get; private set; } = null!;
    public RelayCommand ClearSelectedTraceCommand { get; private set; } = null!;
    public RelayCommand ClearTracesCommand { get; private set; } = null!;
    public RelayCommand<CalibrationBoundaryEdit> BoundaryEditedCommand { get; private set; } = null!;
    public RelayCommand<CalibrationTraceEdit> TraceEditedCommand { get; private set; } = null!;

    private void InitializePhotoCommands()
    {
        ImportPhotoCommand = new AsyncRelayCommand(ImportPhotoAsync, CanUseTarget);
        RectifyPhotoCommand = new AsyncRelayCommand(RectifyPhotoAsync,
            () => CanUseTarget() && PhotoImage is not null && IsPhotoStep && PhotoBoundaryPoints.Count >= 10);
        ConfirmRectificationCommand = new RelayCommand(ConfirmRectification,
            () => !IsBusy && IsPhotoStep && IsRectificationReady && _photoTransform is not null);
        BackToPhotoCommand = new RelayCommand(() =>
        {
            IsPhotoStep = true;
            PreviewRectification = false;
            ContourConfirmed = false;
            StatusText = "已返回照片矫正。修改边界或盘片规格后，原描线与拟合结果会清空。";
        }, () => !IsBusy && IsTraceStep);
        ClearTracesCommand = new RelayCommand(ClearTraces, () => !IsBusy && TracePoints.Count > 0);
        ClearSelectedTraceCommand = new RelayCommand(ClearTraces, () => !IsBusy && TracePoints.Count > 0);
        BoundaryEditedCommand = new RelayCommand<CalibrationBoundaryEdit>(EditBoundary,
            _ => !IsBusy && IsPhotoStep && !PreviewRectification && PhotoImage is not null);
        TraceEditedCommand = new RelayCommand<CalibrationTraceEdit>(EditTrace,
            _ => !IsBusy && IsTraceStep && _photoTransform is not null);
    }

    private bool CanSolveTrace() => CanUseTarget() && IsTraceStep && _photoTransform is not null && TracePoints.Count >= 6;

    partial void OnIsPhotoStepChanged(bool value) => RefreshCommands();
    partial void OnPreviewRectificationChanged(bool value) => RefreshPhotoCommands();
    partial void OnTracePointsChanged(IReadOnlyList<CalibrationTracePoint> value)
    {
        OnPropertyChanged(nameof(TraceSummary));
        RefreshPhotoCommands();
        SolveCommand?.NotifyCanExecuteChanged();
    }

    partial void OnSelectedPhysicalDiscSizeChanged(PhotoDiscSize value)
    {
        PhotoCanvasRadiusMm = value.OuterRadiusMm + 3;
        if (_restoringPhoto) return;
        InvalidatePhotoRectification();
        PhotoStatus = "盘片规格已更改，请重新计算照片矫正。";
    }

    private void RefreshPhotoCommands()
    {
        ImportPhotoCommand?.NotifyCanExecuteChanged(); RectifyPhotoCommand?.NotifyCanExecuteChanged();
        ConfirmRectificationCommand?.NotifyCanExecuteChanged(); BackToPhotoCommand?.NotifyCanExecuteChanged();
        ClearSelectedTraceCommand?.NotifyCanExecuteChanged(); ClearTracesCommand?.NotifyCanExecuteChanged();
        BoundaryEditedCommand?.NotifyCanExecuteChanged(); TraceEditedCommand?.NotifyCanExecuteChanged();
    }

    private void ResetPhoto()
    {
        _photoRevision++;
        _photoTransform = null; _photoPng = null;
        PhotoImage = null; RectifiedPhoto = null;
        PhotoBoundaryPoints = []; PhotoBoundaryContours = [];
        TracePoints = []; CrossPredictionContours = [];
        IsRectificationReady = false; PreviewRectification = false; IsPhotoStep = true;
        PhotoStatus = "导入照片后，先沿实体外缘和中心孔调整轮廓。照片不需要摆正，也不要求描黑。";
        RectificationSummary = "矫正只依据光盘实体边缘，不会拉直刻录曲线。";
        RefreshPhotoCommands();
    }

    private void InvalidatePhotoRectification()
    {
        _photoRevision++;
        _photoTransform = null; RectifiedPhoto = null;
        IsRectificationReady = false; PreviewRectification = false; IsPhotoStep = true;
        PhotoBoundaryContours = []; TracePoints = [];
        InvalidateFit();
        RectificationSummary = "轮廓已更改，请重新计算并检查矫正预览。";
        RefreshCommands();
    }

    private async Task ImportPhotoAsync()
    {
        string? path = _shell.Dialogs?.PickOpen(title: "导入标定盘照片", filter: "照片或扫描图|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff");
        if (path is not null) await ImportPhotoFromAsync(path);
    }

    internal async Task ImportPhotoFromAsync(string path)
    {
        await RunAsync("正在读取照片…", async token =>
        {
            var result = await Task.Run(() =>
            {
                var image = CalibrationPhotoService.Load(path, 1600, token);
                var contours = CalibrationPhotoService.SuggestContours(image, 8, token);
                return (Image: image, Contours: contours, Png: EncodePhoto(image));
            }, token);
            token.ThrowIfCancellationRequested();
            InvalidatePhotoRectification();
            PhotoImage = result.Image; _photoPng = result.Png;
            SetBoundaryPoints(result.Contours.Outer, result.Contours.Hole);
            PhotoStatus = result.Contours.Message;
            StatusText = "照片已导入。请把两组控制点拖到光盘实体外缘和中心孔边缘，然后计算矫正。";
            RefreshCommands();
        });
    }

    private void SetBoundaryPoints(IReadOnlyList<CalibrationPoint> outer, IReadOnlyList<CalibrationPoint> hole)
    {
        PhotoBoundaryPoints = outer.Select((p, i) => new CalibrationPhotoBoundaryPoint(CalibrationPhotoBoundaryKind.OuterEdge, i, p.X, p.Y))
            .Concat(hole.Select((p, i) => new CalibrationPhotoBoundaryPoint(CalibrationPhotoBoundaryKind.CenterHole, i, p.X, p.Y))).ToArray();
    }

    private void EditBoundary(CalibrationBoundaryEdit? edit)
    {
        if (edit is null || PhotoImage is not { } image || !IsPhotoStep || IsBusy
            || !double.IsFinite(edit.X) || !double.IsFinite(edit.Y)) return;
        if (!PhotoBoundaryPoints.Any(p => p.Boundary == edit.Boundary && p.Index == edit.Index)) return;
        var updated = PhotoBoundaryPoints.Select(p => p.Boundary == edit.Boundary && p.Index == edit.Index
            ? p with { X = Math.Clamp(edit.X, 0, image.PixelWidth - 1), Y = Math.Clamp(edit.Y, 0, image.PixelHeight - 1) } : p).ToArray();
        InvalidatePhotoRectification();
        PhotoBoundaryPoints = updated;
    }

    private CalibrationPoint[] Boundary(CalibrationPhotoBoundaryKind kind) => PhotoBoundaryPoints
        .Where(p => p.Boundary == kind).OrderBy(p => p.Index).Select(p => new CalibrationPoint(p.X, p.Y)).ToArray();

    private async Task RectifyPhotoAsync()
    {
        if (PhotoImage is not { } image) return;
        var outer = Boundary(CalibrationPhotoBoundaryKind.OuterEdge);
        var hole = Boundary(CalibrationPhotoBoundaryKind.CenterHole);
        double radius = SelectedPhysicalDiscSize.OuterRadiusMm;
        double canvasRadius = PhotoCanvasRadiusMm;
        long revision = _photoRevision;
        await RunAsync("正在根据实体边缘矫正照片…", async token =>
        {
            var fit = await Task.Run(() => CalibrationPhotoRectifier.Fit(outer, hole, radius, 7.5, token), token);
            if (!fit.Succeeded || fit.Transform is null) throw new ArgumentException(fit.Message);
            var corrected = await Task.Run(() => CalibrationPhotoService.Rectify(image, fit.Transform, canvasRadius, 1400, token), token);
            token.ThrowIfCancellationRequested();
            if (revision != _photoRevision || !ReferenceEquals(image, PhotoImage)) throw new OperationCanceledException();
            _photoTransform = fit.Transform;
            RectifiedPhoto = corrected;
            PhotoBoundaryContours = BoundaryContours(fit.Transform, radius);
            RectificationSummary = $"外缘偏差 {fit.OuterRmsPixels:0.0} 像素 · 中心孔偏差 {fit.HoleRmsPixels:0.0} 像素。请检查圆形边缘是否贴合；十字线保留实际弯曲。";
            IsRectificationReady = true;
            PreviewRectification = true;
            StatusText = "矫正预览已生成。检查盘面恢复正视效果后，确认并开始描线。";
            RefreshPhotoCommands();
        });
    }

    private static Point[][] BoundaryContours(CalibrationPhotoTransform transform, double radius) =>
        new[] { radius, 7.5 }.Select(r => Enumerable.Range(0, 181).Select(i =>
        {
            double angle = i * 2 * Math.PI / 180;
            var point = transform.DiscToSource(new(r * Math.Cos(angle), r * Math.Sin(angle)));
            return new Point(point.X, point.Y);
        }).ToArray()).ToArray();

    private void ConfirmRectification()
    {
        if (!IsRectificationReady || _photoTransform is null || RectifiedPhoto is null) return;
        PreviewRectification = false;
        IsPhotoStep = false;
        StatusText = "沿任意一条清楚的曲线从内向外打点，不必对应圆环。实体盘面内的点全部保留，生成时的内外半径不裁剪照片点；满 6 点可反推。";
        RefreshCommands();
    }

    private void EditTrace(CalibrationTraceEdit? edit)
    {
        if (edit is null || !IsTraceStep || IsBusy || _photoTransform is null) return;
        if (!double.IsFinite(edit.XMm) || !double.IsFinite(edit.YMm)) return;
        double radius = Math.Sqrt(edit.XMm * edit.XMm + edit.YMm * edit.YMm);
        if (!edit.Remove && (radius < 7.5 || radius > SelectedPhysicalDiscSize.OuterRadiusMm))
        {
            StatusText = "该点位于中心孔或实体盘片外，未加入曲线。请沿盘面上同一条可见曲线打点；原有点全部保留。";
            return;
        }
        var points = TracePoints.ToList();
        int at = points.FindIndex(p => p.Index == edit.Index);
        if (edit.Remove)
        {
            if (at < 0) return;
            points.RemoveAt(at);
        }
        else if (at >= 0) points[at] = new(0, edit.Index, edit.XMm, edit.YMm);
        else
        {
            if (edit.Index != -1 || points.Count >= 4000) return;
            int id = points.Count == 0 ? 0 : points.Max(p => p.Index) + 1;
            points.Add(new(0, id, edit.XMm, edit.YMm));
        }
        // Point entry is not a timed pen stroke: radial order allows inserting points into any gap.
        TracePoints = points.OrderBy(p => p.XMm * p.XMm + p.YMm * p.YMm).ToArray();
        InvalidateFit();
        RefreshCommands();
    }

    private void ClearTraces()
    {
        TracePoints = [];
        InvalidateFit();
        RefreshCommands();
    }

    private IReadOnlyList<CalibrationCrossTrace> CrossTraces() => TracePoints.Count == 0 ? [] :
        [new(0, TracePoints.Select(p => new CalibrationPoint(p.XMm, p.YMm)).ToArray())];

    private CalibrationPhotoSession? PhotoSession() => _photoPng is null ? null : new(_photoPng,
        Boundary(CalibrationPhotoBoundaryKind.OuterEdge), Boundary(CalibrationPhotoBoundaryKind.CenterHole),
        SelectedPhysicalDiscSize.OuterRadiusMm, _photoTransform is not null && TracePoints.Count > 0 || IsTraceStep,
        CrossTraces());

    private static string EncodePhoto(BitmapSource source)
    {
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using MemoryStream stream = new();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    private static BitmapSource DecodePhoto(string png)
    {
        using MemoryStream stream = new(Convert.FromBase64String(png));
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if (frame.PixelWidth is < 32 or > 2000 || frame.PixelHeight is < 32 or > 2000)
            throw new ArgumentException("标定记录中的照片尺寸无效，请重新导入。");
        frame.Freeze();
        return frame;
    }

    internal async Task LoadSessionFromAsync(string path)
    {
        await RunAsync("正在恢复标定照片与记录…", async token =>
        {
            var prepared = await Task.Run(() =>
            {
                var session = CalibrationSessionJson.Load(path);
                ValidateOutputParameters(session.Target.Parameters);
                BitmapSource? image = null, rectified = null;
                CalibrationPhotoFitResult? fit = null;
                if (session.Photo is { } photo)
                {
                    image = DecodePhoto(photo.ImagePngBase64);
                    if (photo.OuterBoundary.Concat(photo.HoleBoundary).Any(p => p.X >= image.PixelWidth || p.Y >= image.PixelHeight))
                        throw new ArgumentException("标定轮廓超出照片范围。");
                    if (photo.RectificationConfirmed)
                    {
                        fit = CalibrationPhotoRectifier.Fit(photo.OuterBoundary, photo.HoleBoundary, photo.PhysicalOuterRadiusMm, 7.5, token);
                        if (!fit.Succeeded || fit.Transform is null) throw new ArgumentException(fit.Message);
                        rectified = CalibrationPhotoService.Rectify(image, fit.Transform, photo.PhysicalOuterRadiusMm + 3, 1400, token);
                    }
                }
                return (Session: session, Image: image, Rectified: rectified, Fit: fit);
            }, token);
            token.ThrowIfCancellationRequested();
            var session = prepared.Session;
            InstallTarget(session.Target, session.FitOptions, session.CdInterleave);
            if (session.Photo is { } photo)
            {
                _restoringPhoto = true;
                try { SelectedPhysicalDiscSize = PhysicalDiscSizeChoices.Single(s => s.OuterRadiusMm == photo.PhysicalOuterRadiusMm); }
                finally { _restoringPhoto = false; }
                PhotoImage = prepared.Image; RectifiedPhoto = prepared.Rectified; _photoPng = photo.ImagePngBase64;
                _photoTransform = prepared.Fit?.Transform;
                SetBoundaryPoints(photo.OuterBoundary, photo.HoleBoundary);
                IsRectificationReady = _photoTransform is not null;
                if (_photoTransform is not null) PhotoBoundaryContours = BoundaryContours(_photoTransform, photo.PhysicalOuterRadiusMm);
                TracePoints = photo.Traces.SelectMany(t => t.Points).Select((p, i) => new CalibrationTracePoint(0, i, p.X, p.Y))
                    .OrderBy(p => p.XMm * p.XMm + p.YMm * p.YMm).ToArray();
                IsPhotoStep = !photo.RectificationConfirmed;
                PhotoStatus = "已恢复保存的照片和边界控制点。";
                RectificationSummary = IsRectificationReady ? "已根据保存的实体边缘恢复矫正。返回修改边界会清空原描线。" : "请检查边界后计算矫正。";
            }
            _sessionPath = path; OutputPath = string.Empty;
            StatusText = "已恢复标定图案、照片和曲线点，可继续编辑或重新反推。";
            RefreshCommands();
        });
    }

    public sealed record PhotoDiscSize(string Label, double OuterRadiusMm);
}
