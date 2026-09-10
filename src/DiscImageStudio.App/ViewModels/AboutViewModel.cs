using System.Reflection;

namespace DiscImageStudio.ViewModels;

public class AboutViewModel
{
    public string VersionText { get; } = BuildVersionText();

    private static string BuildVersionText()
    {
        Version version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);
        return $"版本 {version.Major}.{version.Minor}.{version.Build}";
    }
}
