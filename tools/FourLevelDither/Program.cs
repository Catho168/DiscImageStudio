using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DitherProcessor = DiscImageStudio.Imaging.GrayscaleDither;

namespace DiscImageStudio.DitherTool;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || (args.Length == 1 && !args[0].StartsWith("-", StringComparison.Ordinal)))
            {
                Application application = new() { ShutdownMode = ShutdownMode.OnMainWindowClose };
                return application.Run(new DitherWindow(args.Length == 1 ? args[0] : null));
            }

            AttachToParentConsole();
            if (args is ["--help"] or ["-h"])
            {
                Console.WriteLine("FourLevelDither [IMAGE] — open the independent image tool.\n"
                    + "FourLevelDither --input IMAGE --output RESULT.png [--mode dvd|cd] [--gamma none|srgb] — convert without a window.\n"
                    + "Both modes output black/white 0/255. Default dvd: A5/92; cd: 0D/AA.\n"
                    + "Gamma defaults to none (existing output); srgb decodes RGB to linear light before dithering.\n"
                    + "Output: serpentine Floyd–Steinberg, original size, alpha over white.");
                return 0;
            }

            Dictionary<string, string> options = ParseOptions(args);
            DitherMode mode = options.TryGetValue("--mode", out string? modeName) ? DitherModes.Parse(modeName) : DitherMode.Dvd;
            bool linearizeSrgb = options.TryGetValue("--gamma", out string? gammaName) && ParseGamma(gammaName);
            string input = Path.GetFullPath(Required(options, "--input"));
            if (options.TryGetValue("--snapshot", out string? snapshot))
            {
                if (options.ContainsKey("--output")) throw new ArgumentException("Choose --snapshot or --output.");
                return Snapshot(input, Path.GetFullPath(snapshot), mode, linearizeSrgb);
            }

            string output = Path.GetFullPath(Required(options, "--output"));
            ValidateOutputPath(input, output);
            if (File.Exists(output)) throw new IOException("输出文件已存在，请选择另一个文件名。");
            BitmapSource source = DitherProcessor.LoadImage(input);
            BitmapSource result = DitherProcessor.Process(source, DitherModes.Levels(mode), linearizeSrgb: linearizeSrgb);
            DitherProcessor.SavePng(result, output, DitherModes.Levels(mode));
            Console.WriteLine($"Saved {result.PixelWidth} × {result.PixelHeight}, {DitherModes.Label(mode)} ({DitherModes.PaletteLabel(mode)}), gamma={(linearizeSrgb ? "srgb" : "none")}: {output}");
            return 0;
        }
        catch (Exception exception)
        {
            AttachToParentConsole();
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    internal static void ValidateOutputPath(string input, string output)
    {
        if (Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请另存为新文件，不能覆盖原图。");
        if (!Path.GetExtension(output).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请保存为 PNG，以保留准确的灰阶。");
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || args[index] is not ("--input" or "--output" or "--snapshot" or "--mode" or "--gamma")
                || !options.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("用法：FourLevelDither --input 原图 --output 结果.png [--mode dvd|cd] [--gamma none|srgb]");
        }
        return options;
    }

    private static string Required(Dictionary<string, string> options, string name)
        => options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Missing {name}.");

    private static bool ParseGamma(string value) => value.ToLowerInvariant() switch
    {
        "none" => false,
        "srgb" => true,
        _ => throw new ArgumentException("--gamma 请选择 none（关闭）或 srgb（sRGB 伽马校正）。"),
    };

    // Off-screen rendering exercises the same window and asynchronous processing used interactively.
    private static int Snapshot(string input, string output, DitherMode mode, bool linearizeSrgb)
    {
        ValidateOutputPath(input, output);
        Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        DitherWindow window = new(initialMode: mode, initialLinearizeSrgb: linearizeSrgb)
        {
            Left = -10000, Top = -10000, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        Task processing = window.LoadImageAsync(input);
        DispatcherFrame frame = new();
        DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) => { if (processing.IsCompleted) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        processing.GetAwaiter().GetResult();
        if (!window.HasResult) throw new InvalidOperationException("Snapshot input could not be processed.");
        window.UpdateLayout();
        FrameworkElement content = (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
        int height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (FileStream stream = File.Create(output)) encoder.Save(stream);
        window.Close();
        application.Shutdown();
        Console.WriteLine($"Snapshot: {output}");
        return 0;
    }

    private static void AttachToParentConsole()
    {
        if (!AttachConsole(0xFFFFFFFF)) return;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
