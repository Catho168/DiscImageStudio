using System.Reflection;

namespace DiscImageStudio.ViewModels;

public class AboutViewModel
{
    public string VersionText { get; } = BuildVersionText();

    private static string BuildVersionText()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return $"版本 {version?.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
    }
}
