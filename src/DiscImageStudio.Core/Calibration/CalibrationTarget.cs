using System.Text.Json.Serialization;

namespace DiscImageStudio.Core.Calibration;

public enum CalibrationDiscKind { Cd, Dvd }
public enum CalibrationSector { Coarse, Large, Medium, Small }
public enum CalibrationGroupRole { Coarse, Fine, Validation }

/// <summary>Derived group metadata, reconstructed from the saved generation rules.</summary>
public sealed record CalibrationGroup(string Id, int Index, CalibrationGroupRole Role, double AngleDegrees,
    IReadOnlyList<double> ArcRadiiMm, double ArcThicknessMm, double ArcStartAngleDegrees, double ArcEndAngleDegrees,
    double SpineWidthMm, double SpineInnerRadiusMm, double SpineOuterRadiusMm);

/// <summary>Coordinates are millimetres from the disc centre: x right, y down.</summary>
public sealed record CalibrationPoint(double X, double Y);

/// <summary>Canonical identity of one physical control point, shared by every selecting region.</summary>
public sealed record CalibrationControlPoint(string Id, string CellId, int CornerIndex,
    CalibrationPoint Position, string GroupId, CalibrationGroupRole Role);

public sealed record CalibrationParameters(
    CalibrationDiscKind Kind, double InnerRadiusMm, double OuterRadiusMm,
    long Sectors, double LinearDensity,
    double PitchLinear = 0, double PitchQuadratic = 0, double PitchCubic = 0)
{
    public const int DvdChannelBitsPerSector = 38_688;

    [JsonIgnore]
    public double TrackLengthMm => Kind == CalibrationDiscKind.Cd
        ? Sectors / 75.0 * LinearDensity
        : Sectors * (double)DvdChannelBitsPerSector * LinearDensity * 1e-6;

    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || Sectors <= 0 || !double.IsFinite(InnerRadiusMm)
            || !double.IsFinite(OuterRadiusMm) || InnerRadiusMm <= 0
            || OuterRadiusMm <= InnerRadiusMm || !double.IsFinite(LinearDensity)
            || LinearDensity <= 0 || !double.IsFinite(TrackLengthMm)
            || TrackLengthMm <= OuterRadiusMm - InnerRadiusMm
            || !double.IsFinite(OuterRadiusMm * OuterRadiusMm)
            || !double.IsFinite(2 * TrackLengthMm / (OuterRadiusMm * OuterRadiusMm - InnerRadiusMm * InnerRadiusMm)))
            throw new ArgumentException("标定参数无效：请检查盘片类型、内外半径、扇区数和线速度／位长。");
        DvdTrackGeometry.ValidatePitchCoefficients(PitchLinear, PitchQuadratic, PitchCubic);
        if (Kind == CalibrationDiscKind.Cd && (PitchLinear != 0 || PitchQuadratic != 0 || PitchCubic != 0))
            throw new ArgumentException("三次轨距参数仅适用于 DVD。");
    }
}

/// <summary>Legacy serialized options; group geometry is derived solely from the disc radii.</summary>
public sealed record CalibrationPatternOptions(
    double LargeCellMm = 8, double MediumCellMm = 4,
    double SmallCellMm = 2, double SectorGapMm = 1)
{
    public void Validate()
    {
        if (!double.IsFinite(LargeCellMm) || !double.IsFinite(MediumCellMm)
            || !double.IsFinite(SmallCellMm) || !double.IsFinite(SectorGapMm)
            || SmallCellMm < 0.25 || MediumCellMm <= SmallCellMm
            || LargeCellMm <= MediumCellMm || LargeCellMm > 100
            || SectorGapMm <= 0 || SectorGapMm > 10)
            throw new ArgumentException("标定记录中的兼容图案选项无效，请重新创建标定图案。");
    }
}

/// <summary>Three radial tiers with asymmetric angular steps on both ends.</summary>
public sealed record CalibrationArcShape(double CenterRadiusMm, double ThicknessMm)
{
    // These scalar values, rather than an array of vertices, preserve value equality
    // when a saved target is reconstructed and compared with its generation rules.
    internal (double Radius, double Degrees)[] PolarCorners =>
    [
        (CenterRadiusMm - ThicknessMm / 2, -157),
        (CenterRadiusMm - ThicknessMm / 2, -128),
        (CenterRadiusMm - ThicknessMm * 0.19, -128),
        (CenterRadiusMm - ThicknessMm * 0.19, -115),
        (CenterRadiusMm + ThicknessMm * 0.16, -115),
        (CenterRadiusMm + ThicknessMm * 0.16, -99),
        (CenterRadiusMm + ThicknessMm / 2, -99),
        (CenterRadiusMm + ThicknessMm / 2, -169),
        (CenterRadiusMm + ThicknessMm * 0.16, -169),
        (CenterRadiusMm + ThicknessMm * 0.16, -162),
        (CenterRadiusMm - ThicknessMm * 0.19, -162),
        (CenterRadiusMm - ThicknessMm * 0.19, -157),
    ];

    internal bool Contains(double radius, double degrees)
    {
        if (radius < CenterRadiusMm - ThicknessMm / 2 || radius > CenterRadiusMm + ThicknessMm / 2)
            return false;
        (double start, double end) = radius < CenterRadiusMm - ThicknessMm * 0.19
            ? (-157, -128) : radius < CenterRadiusMm + ThicknessMm * 0.16 ? (-162, -115) : (-169, -99);
        return degrees >= start && degrees <= end;
    }

    internal static CalibrationPoint ToPoint(double radius, double degrees)
    {
        double angle = degrees * Math.PI / 180;
        return new(radius * Math.Cos(angle), radius * Math.Sin(angle));
    }
}

/// <summary>A selectable region between two arcs and a radial spine; one side has no ink.</summary>
public sealed record CalibrationPolarQuad(double InnerRadiusMm, double OuterRadiusMm,
    double StartAngleDegrees, double EndAngleDegrees, double SpineAngleDegrees)
{
    internal (double Radius, double Degrees)[] PolarCorners =>
    [
        (InnerRadiusMm, StartAngleDegrees), (InnerRadiusMm, EndAngleDegrees),
        (OuterRadiusMm, EndAngleDegrees), (OuterRadiusMm, StartAngleDegrees),
    ];

    internal (double Radius, double Degrees)[] OpenBoundary => SpineAngleDegrees == EndAngleDegrees
        ? PolarCorners : [PolarCorners[1], PolarCorners[0], PolarCorners[3], PolarCorners[2]];
}

/// <summary>Derived editable centreline; it adds controls without changing the engraved pattern.</summary>
public sealed record CalibrationRadialLine(double InnerRadiusMm, double OuterRadiusMm, double AngleDegrees)
{
    internal (double Radius, double Degrees)[] PolarCorners =>
        [(InnerRadiusMm, AngleDegrees), (OuterRadiusMm, AngleDegrees)];
}

public sealed record CalibrationCell(
    string Id, CalibrationSector Sector, double X, double Y, double Size, double? HeightMm = null,
    CalibrationArcShape? ArcShape = null, CalibrationPolarQuad? PolarQuad = null,
    [property: JsonIgnore] CalibrationRadialLine? RadialLine = null)
{
    [JsonIgnore]
    public double Height => HeightMm ?? Size;

    [JsonIgnore]
    public string? GroupId => PolarQuad is null && RadialLine is null ? null : string.Join('-', Id.Split('-').Take(2));

    [JsonIgnore]
    public bool IsOutlineClosed => PolarQuad is null && RadialLine is null;

    // Rectangles: clockwise from top left. Arcs: ordered around their 12-vertex boundary.
    [JsonIgnore]
    public IReadOnlyList<CalibrationPoint> Corners => RadialLine is not null
        ? RadialLine.PolarCorners.Select(point => CalibrationArcShape.ToPoint(point.Radius, point.Degrees)).ToArray()
        : PolarQuad is not null
        ? PolarQuad.PolarCorners.Select(point => CalibrationArcShape.ToPoint(point.Radius, point.Degrees)).ToArray()
        : ArcShape is null
        ? [new(X, Y), new(X + Size, Y), new(X + Size, Y + Height), new(X, Y + Height)]
        : ArcShape.PolarCorners.Select(point => CalibrationArcShape.ToPoint(point.Radius, point.Degrees)).ToArray();
}

public sealed class CalibrationTarget
{
    public const int CurrentSchemaVersion = 6;
    private readonly Dictionary<string, CalibrationCell> _byId;
    private readonly Dictionary<(string CellId, int CornerIndex), CalibrationControlPoint> _controlPointByReference;
    private readonly CalibrationGeometry _generatedGeometry;

    [JsonConstructor]
    public CalibrationTarget(int schemaVersion, string id, CalibrationParameters parameters,
        CalibrationPatternOptions options, IReadOnlyList<CalibrationCell> cells)
    {
        SchemaVersion = schemaVersion;
        Id = id;
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Cells = Array.AsReadOnly(cells?.ToArray() ?? throw new ArgumentNullException(nameof(cells)));
        Validate();
        _generatedGeometry = new(Parameters);
        Groups = Array.AsReadOnly(BuildGroups(Parameters));
        double pixelSizeMm = CalibrationReferencePattern.MillimetresPerPixel(Parameters.OuterRadiusMm, Parameters.Kind);
        RingRadiiMm = Array.AsReadOnly(CalibrationReferencePattern.RingRadiiPixels.Select(radius => radius * pixelSizeMm).ToArray());
        ControlCells = Array.AsReadOnly(Cells.Concat(BuildSpineCells(Groups)).ToArray());
        _byId = ControlCells.ToDictionary(cell => cell.Id, StringComparer.Ordinal);
        _controlPointByReference = new();
        var uniquePoints = new Dictionary<CalibrationPoint, CalibrationControlPoint>();
        var canonicalPoints = new List<CalibrationControlPoint>();
        foreach (CalibrationCell cell in ControlCells)
        {
            IReadOnlyList<CalibrationPoint> corners = cell.Corners;
            CalibrationGroup group = Groups.First(item => item.Id == cell.GroupId);
            for (int index = 0; index < corners.Count; index++)
            {
                CalibrationPoint position = corners[index];
                if (!uniquePoints.TryGetValue(position, out CalibrationControlPoint? canonical))
                {
                    canonical = new($"{cell.Id}:{index}", cell.Id, index, position, group.Id, group.Role);
                    uniquePoints.Add(position, canonical);
                    canonicalPoints.Add(canonical);
                }
                _controlPointByReference.Add((cell.Id, index), canonical);
            }
        }
        ControlPoints = canonicalPoints.AsReadOnly();
    }

    public int SchemaVersion { get; }
    public string Id { get; }
    public CalibrationParameters Parameters { get; }
    public CalibrationPatternOptions Options { get; }
    public IReadOnlyList<CalibrationCell> Cells { get; }

    [JsonIgnore]
    public IReadOnlyList<CalibrationGroup> Groups { get; }

    /// <summary>Visual guides only. Rings supply no angular observations to curve fitting.</summary>
    [JsonIgnore]
    public IReadOnlyList<double> RingRadiiMm { get; }

    [JsonIgnore]
    public double RingThicknessMm => CalibrationReferencePattern.NominalLineWidthPixels
        * CalibrationReferencePattern.MillimetresPerPixel(Parameters.OuterRadiusMm, Parameters.Kind);

    [JsonIgnore]
    public IReadOnlyList<CalibrationCell> ControlCells { get; }

    [JsonIgnore]
    public IReadOnlyList<CalibrationControlPoint> ControlPoints { get; }

    public CalibrationControlPoint GetControlPoint(string cellId, int cornerIndex)
    {
        if (string.IsNullOrWhiteSpace(cellId)
            || !_controlPointByReference.TryGetValue((cellId, cornerIndex), out CalibrationControlPoint? point))
            throw new ArgumentException("控制块或控制点编号无效。");
        return point;
    }

    public CalibrationGroupRole GetGroupRole(string cellId)
    {
        if (string.IsNullOrWhiteSpace(cellId) || !_byId.TryGetValue(cellId, out CalibrationCell? cell))
            throw new ArgumentException("控制块编号无效。");
        return Groups.First(group => group.Id == cell.GroupId).Role;
    }

    public static CalibrationTarget Create(CalibrationParameters parameters, CalibrationPatternOptions? options = null)
    {
        parameters.Validate();
        double scale = Math.Clamp((parameters.OuterRadiusMm - parameters.InnerRadiusMm) / 32.3, 0.5, 2);
        options ??= new(8 * scale, 4 * scale, 2 * scale);
        options.Validate();
        return new(CurrentSchemaVersion, Guid.NewGuid().ToString("N"), parameters, options,
            BuildCells(parameters));
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new ArgumentException($"不支持的标定会话版本 {SchemaVersion}。");
        if (!Guid.TryParseExact(Id, "N", out _)) throw new ArgumentException("标定图案 ID 无效。");
        Parameters.Validate();
        Options.Validate();
        if (Cells.Count > 20_000 || Cells.Any(cell => cell is null))
            throw new ArgumentException("标定图案控制块数量无效。");
        if (Cells.Select(cell => cell.Id).Distinct(StringComparer.Ordinal).Count() != Cells.Count)
            throw new ArgumentException("标定图案含重复控制块 ID。");
        // Persist the actual control geometry; reject altered or incompatible definitions.
        CalibrationCell[] expected = BuildCells(Parameters);
        if (!Cells.SequenceEqual(expected))
            throw new ArgumentException("标定图案与生成参数或图案规则不一致。");
    }

    public CalibrationPoint GetCorner(string cellId, int cornerIndex)
    {
        if (string.IsNullOrWhiteSpace(cellId) || !_byId.TryGetValue(cellId, out CalibrationCell? cell)
            || cornerIndex < 0 || cornerIndex >= cell.Corners.Count)
            throw new ArgumentException("控制块或角点编号无效。");
        return cell.Corners[cornerIndex];
    }

    /// <summary>True source ink path. Each cross arm is an open radial line.</summary>
    public IReadOnlyList<CalibrationPoint> GetOutline(string cellId, int samplesPerEdge = 32)
    {
        if (samplesPerEdge is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(samplesPerEdge));
        if (string.IsNullOrWhiteSpace(cellId) || !_byId.TryGetValue(cellId, out CalibrationCell? cell))
            throw new ArgumentException("控制块编号无效。");
        return SampleBoundary(CellBoundary(cell), samplesPerEdge);
    }

    public IReadOnlyList<IReadOnlyList<CalibrationPoint>> GetGroupContours(string groupId, int samplesPerEdge = 32)
    {
        if (samplesPerEdge is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(samplesPerEdge));
        CalibrationGroup group = Groups.FirstOrDefault(item => item.Id == groupId)
            ?? throw new ArgumentException("标定组编号无效。");
        return GroupBoundaries(group).Select(boundary => SampleBoundary(boundary, samplesPerEdge)).ToArray();
    }

    public IReadOnlyList<IReadOnlyList<CalibrationPoint>> GetMappedGroupContours(string groupId,
        CalibrationParameters actual, double rotationRadians = 0, int maxPoints = 50000)
    {
        if (maxPoints is < 16 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        CalibrationGroup group = Groups.FirstOrDefault(item => item.Id == groupId)
            ?? throw new ArgumentException("标定组编号无效。");
        var paths = new List<IReadOnlyList<CalibrationPoint>>();
        int remaining = maxPoints;
        foreach (SourceBoundary boundary in GroupBoundaries(group))
        {
            if (remaining < 2) throw OutlineLimit();
            IReadOnlyList<CalibrationPoint> path = MapBoundary(boundary, actual, rotationRadians, remaining);
            paths.Add(path);
            remaining -= path.Count;
        }
        return paths;
    }

    private static IReadOnlyList<CalibrationPoint> SampleBoundary(SourceBoundary boundary, int samplesPerEdge)
    {
        IReadOnlyList<CalibrationPoint> corners = boundary.Corners;
        (double Radius, double Degrees)[]? polar = boundary.Polar;
        int edgeCount = boundary.Closed ? corners.Count : corners.Count - 1;
        var outline = new List<CalibrationPoint>(edgeCount * samplesPerEdge + 1);
        for (int edge = 0; edge < edgeCount; edge++)
        {
            int next = (edge + 1) % corners.Count;
            for (int sample = 0; sample < samplesPerEdge; sample++)
            {
                double t = sample / (double)samplesPerEdge;
                if (polar is not null)
                    outline.Add(CalibrationArcShape.ToPoint(
                        polar[edge].Radius + (polar[next].Radius - polar[edge].Radius) * t,
                        polar[edge].Degrees + (polar[next].Degrees - polar[edge].Degrees) * t));
                else
                    outline.Add(new(corners[edge].X + (corners[next].X - corners[edge].X) * t,
                        corners[edge].Y + (corners[next].Y - corners[edge].Y) * t));
            }
        }
        outline.Add(boundary.Closed ? outline[0] : corners[^1]);
        return outline;
    }

    /// <summary>The original PNG mapped to the generation outer radius, then clipped to its annulus.</summary>
    public byte Sample(double xMm, double yMm)
    {
        if (!double.IsFinite(xMm) || !double.IsFinite(yMm)) return 255;
        double radiusSquared = xMm * xMm + yMm * yMm;
        if (radiusSquared < Parameters.InnerRadiusMm * Parameters.InnerRadiusMm - 1e-10
            || radiusSquared > Parameters.OuterRadiusMm * Parameters.OuterRadiusMm + 1e-10)
            return 255;
        return CalibrationReferencePattern.Sample(xMm, yMm, Parameters.OuterRadiusMm, Parameters.Kind);
    }

    public CalibrationPoint MapPoint(CalibrationPoint sourcePoint, CalibrationParameters actualParameters,
        double rotationRadians = 0)
    {
        return CreateMapping(actualParameters, rotationRadians)(sourcePoint);
    }

    public Func<CalibrationPoint, CalibrationPoint> CreateMapping(CalibrationParameters actualParameters,
        double rotationRadians = 0)
    {
        ValidateCompatible(actualParameters);
        if (!double.IsFinite(rotationRadians)) throw new ArgumentException("旋转必须是有限数值。");
        var actual = new CalibrationGeometry(actualParameters);
        return sourcePoint =>
        {
            ArgumentNullException.ThrowIfNull(sourcePoint);
            if (!double.IsFinite(sourcePoint.X) || !double.IsFinite(sourcePoint.Y))
                throw new ArgumentException("角点坐标必须是有限数值。");
            double radius = Math.Sqrt(sourcePoint.X * sourcePoint.X + sourcePoint.Y * sourcePoint.Y);
            if (radius < Parameters.InnerRadiusMm - 1e-8 || radius > Parameters.OuterRadiusMm + 1e-8)
                throw new ArgumentException("源控制点不在生成时的绘图环带内。");
            double progress = _generatedGeometry.ProgressAtRadius(radius);
            (double actualRadius, double actualAngle) = actual.AtProgress(progress);
            double angle = Math.Atan2(sourcePoint.Y, sourcePoint.X)
                + actualAngle - _generatedGeometry.AngleAtRadius(radius) + rotationRadians;
            return new(actualRadius * Math.Cos(angle), actualRadius * Math.Sin(angle));
        };
    }

    /// <summary>
    /// Trace the complete mapped outline using a bound on unwrapped angular variation.
    /// Coincident endpoints cannot hide intervening turns. Throws rather than silently
    /// simplifying an outline whose required point count exceeds the caller's budget.
    /// </summary>
    public IReadOnlyList<CalibrationPoint> GetMappedOutline(string cellId, CalibrationParameters actual,
        double rotationRadians = 0, int maxPoints = 20000)
    {
        if (maxPoints is < 16 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        if (string.IsNullOrWhiteSpace(cellId) || !_byId.TryGetValue(cellId, out CalibrationCell? cell))
            throw new ArgumentException("控制块编号无效。");
        return MapBoundary(CellBoundary(cell), actual, rotationRadians, maxPoints);
    }

    private IReadOnlyList<CalibrationPoint> MapBoundary(SourceBoundary boundary, CalibrationParameters actual,
        double rotationRadians, int maxPoints)
    {
        ValidateCompatible(actual);
        Func<CalibrationPoint, CalibrationPoint> map = CreateMapping(actual, rotationRadians);
        var actualGeometry = new CalibrationGeometry(actual);
        var reverseModel = new CalibrationCrossModel(actualGeometry, _generatedGeometry,
            Parameters.Kind, actual == Parameters);
        var points = new List<CalibrationPoint>();
        IReadOnlyList<CalibrationPoint> corners = boundary.Corners;
        (double Radius, double Degrees)[]? polar = boundary.Polar;
        const double maxAngleStep = Math.PI / 36;
        int edgeCount = boundary.Closed ? corners.Count : corners.Count - 1;
        for (int edge = 0; edge < edgeCount; edge++)
        {
            int next = (edge + 1) % corners.Count;
            CalibrationPoint SourceAt(double t) => polar is not null
                ? CalibrationArcShape.ToPoint(polar[edge].Radius + (polar[next].Radius - polar[edge].Radius) * t,
                    polar[edge].Degrees + (polar[next].Degrees - polar[edge].Degrees) * t)
                : new(corners[edge].X + (corners[next].X - corners[edge].X) * t,
                    corners[edge].Y + (corners[next].Y - corners[edge].Y) * t);

            void AddSegment(double firstT, double lastT, int depth)
            {
                CalibrationPoint first = SourceAt(firstT), last = SourceAt(lastT);
                double firstRadius = Math.Sqrt(first.X * first.X + first.Y * first.Y);
                double lastRadius = Math.Sqrt(last.X * last.X + last.Y * last.Y);
                double lowRadius = Math.Min(firstRadius, lastRadius), highRadius = Math.Max(firstRadius, lastRadius);
                if (!reverseModel.TryAtRadius(lowRadius, out CalibrationCrossSample lowSample)
                    || !reverseModel.TryAtRadius(highRadius, out CalibrationCrossSample highSample))
                    throw new ArgumentException("轮廓在当前轨距参数下没有有效映射。");
                double derivativeBound = reverseModel.DerivativeBound(lowSample, highSample);
                double sourceAngle = Math.Atan2(last.Y, last.X) - Math.Atan2(first.Y, first.X);
                sourceAngle = Math.Abs(Math.Atan2(Math.Sin(sourceAngle), Math.Cos(sourceAngle)));
                double variationBound = sourceAngle + derivativeBound * (highRadius - lowRadius);
                if (variationBound > maxAngleStep)
                {
                    if (depth >= 40) throw OutlineLimit();
                    double middle = (firstT + lastT) / 2;
                    AddSegment(firstT, middle, depth + 1);
                    AddSegment(middle, lastT, depth + 1);
                }
                else
                {
                    if (points.Count >= maxPoints - 1) throw OutlineLimit();
                    points.Add(map(first));
                }
            }
            AddSegment(0, 1, 0);
        }
        points.Add(boundary.Closed ? points[0] : map(corners[^1]));
        return points;
    }

    private static InvalidOperationException OutlineLimit() => new(
        "候选绕行过多，完整轮廓采样超出上限；请缩小搜索范围或提高采样上限后再确认形状。");

    /// <summary>Map a point in the predicted actual disc back into the saved generated target.</summary>
    public Func<CalibrationPoint, CalibrationPoint?> CreateInverseMapping(CalibrationParameters actualParameters,
        double rotationRadians = 0)
    {
        ValidateCompatible(actualParameters);
        if (!double.IsFinite(rotationRadians)) throw new ArgumentException("旋转必须是有限数值。");
        var actual = new CalibrationGeometry(actualParameters);
        return point =>
        {
            if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y)) return null;
            double radius = Math.Sqrt(point.X * point.X + point.Y * point.Y);
            if (radius < actualParameters.InnerRadiusMm || radius > actualParameters.OuterRadiusMm) return null;
            double progress = actual.ProgressAtRadius(radius);
            (double sourceRadius, double sourceAngle) = _generatedGeometry.AtProgress(progress);
            double angle = Math.Atan2(point.Y, point.X) - rotationRadians
                + sourceAngle - actual.AngleAtRadius(radius);
            return new(sourceRadius * Math.Cos(angle), sourceRadius * Math.Sin(angle));
        };
    }

    internal void ValidateCompatible(CalibrationParameters actual)
    {
        actual.Validate();
        if (actual.Kind != Parameters.Kind || actual.Sectors != Parameters.Sectors
            || actual.LinearDensity != Parameters.LinearDensity)
            throw new ArgumentException("标定固定盘片类型、扇区数和线速度／位长；DVD 可拟合内外半径、三次轨距与整体旋转。");
    }

    private static CalibrationCell[] BuildCells(CalibrationParameters parameters)
        => [];

    private static SourceBoundary CellBoundary(CalibrationCell cell)
    {
        (double Radius, double Degrees)[]? polar = cell.RadialLine?.PolarCorners
            ?? cell.PolarQuad?.OpenBoundary ?? cell.ArcShape?.PolarCorners;
        return polar is null ? new(cell.Corners, null, true) : PolarBoundary(polar, cell.IsOutlineClosed);
    }

    private static SourceBoundary PolarBoundary((double Radius, double Degrees)[] polar, bool closed = false)
        => new(polar.Select(point => CalibrationArcShape.ToPoint(point.Radius, point.Degrees)).ToArray(), polar, closed);

    private static IEnumerable<SourceBoundary> GroupBoundaries(CalibrationGroup group)
    {
        yield return PolarBoundary([(group.SpineInnerRadiusMm, group.AngleDegrees), (group.SpineOuterRadiusMm, group.AngleDegrees)]);
    }

    private static CalibrationGroup[] BuildGroups(CalibrationParameters parameters)
    {
        return Enumerable.Range(0, 4).Select(index =>
        {
            double angle = -90 + index * 90;
            return new CalibrationGroup($"C-{index}", index, CalibrationGroupRole.Coarse, angle, [],
                0, angle, angle, CalibrationReferencePattern.NominalLineWidthPixels
                    * CalibrationReferencePattern.MillimetresPerPixel(parameters.OuterRadiusMm, parameters.Kind),
                parameters.InnerRadiusMm, parameters.OuterRadiusMm);
        }).ToArray();
    }

    private static IEnumerable<CalibrationCell> BuildSpineCells(IReadOnlyList<CalibrationGroup> groups)
    {
        foreach (CalibrationGroup group in groups)
        {
            var line = new CalibrationRadialLine(group.SpineInnerRadiusMm, group.SpineOuterRadiusMm, group.AngleDegrees);
            CalibrationPoint first = CalibrationArcShape.ToPoint(line.InnerRadiusMm, line.AngleDegrees);
            CalibrationPoint last = CalibrationArcShape.ToPoint(line.OuterRadiusMm, line.AngleDegrees);
            yield return new($"{group.Id}-spine", CalibrationSector.Coarse,
                Math.Min(first.X, last.X), Math.Min(first.Y, last.Y), Math.Abs(last.X - first.X),
                Math.Abs(last.Y - first.Y), RadialLine: line);
        }
    }

    private sealed record SourceBoundary(IReadOnlyList<CalibrationPoint> Corners,
        (double Radius, double Degrees)[]? Polar, bool Closed);
}
