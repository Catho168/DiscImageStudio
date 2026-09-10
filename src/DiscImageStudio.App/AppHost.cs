using System.Windows;

namespace DiscImageStudio;

internal static class AppHost
{
    internal static int Run()
    {
        Application application = CreateApplication(ShutdownMode.OnMainWindowClose);
        MainWindow window = new();
        return application.Run(window);
    }

    /// <summary>Installs the design-token theme at the application scope so every window,
    /// control template, and embedded UserControl can resolve StaticResource lookups.</summary>
    internal static Application CreateApplication(ShutdownMode shutdownMode)
    {
        Application application = new()
        {
            ShutdownMode = shutdownMode,
        };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Themes/Tokens.xaml", UriKind.Relative),
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Themes/Controls.xaml", UriKind.Relative),
        });
        return application;
    }
}
