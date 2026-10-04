using System.Globalization;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Services;

internal static class DvdPitchDisplay
{
    internal static string Format(CalibrationParameters parameters)
    {
        parameters.Validate();
        var geometry = DvdTrackGeometry.Create(parameters.InnerRadiusMm, parameters.OuterRadiusMm,
            parameters.TrackLengthMm, parameters.PitchLinear, parameters.PitchQuadratic, parameters.PitchCubic);
        double scale = geometry.TrackPitchMm * 1000;
        string Number(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
        return $"轨距 p(x) = {Number(scale)} + ({Number(scale * parameters.PitchLinear)})x"
            + $" + ({Number(scale * parameters.PitchQuadratic)})x² + ({Number(scale * parameters.PitchCubic)})x³ μm"
            + "\n常数项 a₀ 由总轨道长度自动计算；x = (r − 内半径) / (外半径 − 内半径)。";
    }
}
