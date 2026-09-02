using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;

namespace DiscImageStudio;

internal static class UnifiedCommandRunner
{
    private static readonly DiscModuleCatalog Modules = new(
    [
        new CdDiscModule(),
        new DvdDiscModule(),
    ]);

    internal static int Run(string[] arguments)
    {
        try
        {
            if (arguments.Length == 0)
            {
                throw new ArgumentException("Missing command.");
            }

            string command = arguments[0].ToLowerInvariant();
            if (command is "help" or "--help" or "-h")
            {
                return PrintHelp();
            }

            if (command == "ui-snapshot")
            {
                ReadOnlySpan<string> snapshotArguments = arguments.AsSpan(1);
                string? tabValue = FindOption(snapshotArguments, "tab");
                int tabIndex = tabValue is null ? 0 : int.Parse(tabValue);
                return UiPreviewRenderer.Render(
                    RequireOption(snapshotArguments, "output"),
                    tabIndex,
                    FindOption(snapshotArguments, "preview"),
                    FindOption(snapshotArguments, "live-input"),
                    FindOption(snapshotArguments, "live-angle"),
                    FindOption(snapshotArguments, "live-disc"),
                    FindOption(snapshotArguments, "live-ring"));
            }

            if (command == "burn-devices")
            {
                return PrintBurnDevices();
            }

            if (command == "burn-build-info")
            {
                return PrintBurnBuildInfo();
            }

            DiscJobRequest request = new(command, arguments.Skip(1));
            ImmediateProgress progress = new(value =>
            {
                if (!string.IsNullOrWhiteSpace(value.Message))
                {
                    Console.WriteLine(value.Message);
                }
            });
            DiscJobResult result = Modules
                .ExecuteAsync(request, progress)
                .GetAwaiter()
                .GetResult();
            if (!string.IsNullOrWhiteSpace(result.Summary))
            {
                Console.WriteLine(result.Summary);
            }

            return result.ExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static int PrintHelp()
    {
        Console.WriteLine(
            """
            Disc Image Studio - modular DVD/CD optical-disc image generator

            Start without arguments to open the graphical interface.
            Disc commands are provided by modules through DiscImageStudio.Core.
            """);
        foreach (IOpticalDiscModule module in Modules.Modules)
        {
            Console.WriteLine($"\n{module.Descriptor.DisplayName} [{module.Descriptor.Id}]");
            foreach (DiscCommandDescriptor command in module.Descriptor.Commands)
            {
                Console.WriteLine($"  {command.Name,-20} {command.Description}");
            }
        }

        Console.WriteLine(
            """

            Developer command:
              ui-snapshot --output SCREENSHOT.png
              burn-devices
              burn-build-info

            Blu-ray support is added as another IOpticalDiscModule; CD and DVD engines
            do not need to be modified. See docs/ADDING_BLURAY.md.
            """);
        return 0;
    }

    private static int PrintBurnDevices()
    {
        IReadOnlyList<OpticalBurnDevice> devices = new WindowsImapiBurner()
            .GetDevicesAsync()
            .GetAwaiter()
            .GetResult();
        if (devices.Count == 0)
        {
            Console.WriteLine("No IMAPI2 optical recorder was found.");
            return 0;
        }

        foreach (OpticalBurnDevice device in devices)
        {
            Console.WriteLine($"{device.DisplayName}\t{device.Id}");
        }

        return 0;
    }

    private static int PrintBurnBuildInfo()
    {
        Type burningAssemblyMarker = typeof(WindowsImapiBurner);
        Type? sessionType = burningAssemblyMarker.Assembly.GetType(
            "DiscImageStudio.Burning.ICdTrackAtOnceSession",
            throwOnError: false);
        bool containsLegacyFinalizeSetter = sessionType?
            .GetProperty("DoNotFinalizeMedia") is not null;
        Type? rawSessionType = burningAssemblyMarker.Assembly.GetType(
            "DiscImageStudio.Burning.ICdRawSession",
            throwOnError: false);
        bool containsRawDaoWriter = rawSessionType?
            .GetMethod("WriteMedia") is not null;
        Console.WriteLine(
            $"Burning assembly MVID: {burningAssemblyMarker.Module.ModuleVersionId}");
        Console.WriteLine(
            $"Legacy DoNotFinalizeMedia setter present: {containsLegacyFinalizeSetter}");
        Console.WriteLine($"Raw CD DAO writer present: {containsRawDaoWriter}");
        return containsLegacyFinalizeSetter || !containsRawDaoWriter ? 1 : 0;
    }

    private static string RequireOption(ReadOnlySpan<string> arguments, string key)
        => FindOption(arguments, key)
            ?? throw new ArgumentException($"Missing required option --{key}.");

    private static string? FindOption(ReadOnlySpan<string> arguments, string key)
    {
        string name = "--" + key;
        string prefix = name + "=";
        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return argument[prefix.Length..];
            }

            if (argument.Equals(name, StringComparison.OrdinalIgnoreCase)
                && index + 1 < arguments.Length)
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private sealed class ImmediateProgress(Action<DiscJobProgress> report)
        : IProgress<DiscJobProgress>
    {
        public void Report(DiscJobProgress value) => report(value);
    }
}
