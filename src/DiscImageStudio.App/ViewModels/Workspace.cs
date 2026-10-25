namespace DiscImageStudio.ViewModels;

/// <summary>The shell's workspaces. Each one is a different lens on the same open project,
/// arranged along the workflow: the design canvas, the read-back preview monitor, then the
/// two delivery surfaces — 导出 writes the composite to a file, 刻录 streams it (or the
/// calibration target) to a blank disc — and the calibration workflow that feeds measured
/// geometry back into the project. The start screen (<see cref="Home"/>) is the only view
/// shown without a project, which is what makes the sidebar appear.</summary>
public enum Workspace
{
    Home,
    Design,
    Preview,
    Export,
    Burn,
    Calibration,
}
