using System.Windows;

namespace DiscImageStudio;

internal static class AppHost
{
    internal static int Run()
    {
        Application application = new()
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose,
        };
        MainWindow window = new();
        return application.Run(window);
    }
}
