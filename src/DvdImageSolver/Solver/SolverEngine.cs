using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

internal static class SolverEngine
{
    internal static SolverResult Solve(
        ImageConstraint constraint,
        ReadOnlySpan<byte> initialPayloads,
        uint initialLba,
        uint psnOffset,
        ModulationBoundary initialBoundary,
        SolverOptions options,
        Action<int, int, int>? progress = null)
        => options.Algorithm switch
        {
            SolverAlgorithm.Full => new LocalSearchSolver().Solve(
                constraint,
                initialPayloads,
                initialLba,
                psnOffset,
                initialBoundary,
                options,
                progress),
            SolverAlgorithm.StateControl => new StateControlSolver().Solve(
                constraint,
                initialPayloads,
                initialLba,
                psnOffset,
                initialBoundary,
                options,
                progress),
            SolverAlgorithm.Dispersion => new DispersionPoolSolver().Solve(
                constraint,
                initialPayloads,
                initialLba,
                psnOffset,
                initialBoundary,
                options,
                progress),
            _ => throw new ArgumentOutOfRangeException(nameof(options), "Unknown solver algorithm."),
        };
}
