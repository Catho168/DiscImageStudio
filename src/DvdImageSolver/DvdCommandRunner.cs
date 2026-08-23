namespace DvdImageSolver;

/// <summary>
/// Stable facade used by the separate desktop front end. The original command-line
/// entry point and all solver behavior remain unchanged.
/// </summary>
public static class DvdCommandRunner
{
    public static int Run(params string[] arguments) => Program.RunCommand(arguments);
}
