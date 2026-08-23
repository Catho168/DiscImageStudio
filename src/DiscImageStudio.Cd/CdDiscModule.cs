using System.Globalization;
using System.IO;
using DiscImageStudio.Core;

namespace DiscImageStudio.Cd;

public sealed class CdDiscModule : IOpticalDiscModule
{
    public const string ModuleId = "cd-da";

    private static readonly IReadOnlyList<DiscOptionDefinition> GeometryOptions =
    [
        new("r0", "Inner radius", DiscOptionValueType.Decimal, DefaultValue: "24.5", Unit: "mm"),
        new("r1", "Outer radius", DiscOptionValueType.Decimal, DefaultValue: "56.8", Unit: "mm"),
        new("sectors", "Program sectors", DiscOptionValueType.Integer, DefaultValue: "359849"),
        new("velocity", "Linear velocity", DiscOptionValueType.Decimal, DefaultValue: "1100", Unit: "mm/s"),
        new("theta0", "Start angle", DiscOptionValueType.Decimal, DefaultValue: "0", Unit: "degrees"),
        new("outer", "Image outer radius", DiscOptionValueType.Decimal, DefaultValue: "57.5", Unit: "mm"),
    ];

    private static readonly DiscModuleDescriptor ModuleDescriptor = new(
        ModuleId,
        "CD-DA",
        OpticalDiscFamily.CompactDisc,
        "Raw 2352-byte/sector CD-DA track generation and CLV geometry previews.",
        SupportsCancellation: true,
        Commands:
        [
            new DiscCommandDescriptor(
                "cd-generate",
                "Generate CD raw track",
                "Maps an image to a raw CD-DA track and optionally applies delay interleave.",
                DiscModuleCapabilities.RawTrackGeneration,
                [
                    new("input", "Source image", DiscOptionValueType.InputFile, Required: true),
                    new("output", "Raw track", DiscOptionValueType.OutputFile, Required: true),
                    .. GeometryOptions,
                    new("interleave", "CD-DA delay interleave", DiscOptionValueType.Boolean, DefaultValue: "true"),
                ]),
            new DiscCommandDescriptor(
                "cd-preview-warp",
                "Preview CD geometry",
                "Projects generated geometry onto measured disc geometry.",
                DiscModuleCapabilities.GeometryPreview,
                [
                    new("input", "Source image", DiscOptionValueType.InputFile, Required: true),
                    new("output", "Preview PNG", DiscOptionValueType.OutputFile, Required: true),
                    new("size", "Preview size", DiscOptionValueType.Integer, DefaultValue: "1600", Unit: "px"),
                    new("samples-per-sector", "Samples per sector", DiscOptionValueType.Integer, DefaultValue: "16"),
                ]),
            new DiscCommandDescriptor(
                "cd-preview-track",
                "Preview CD raw track",
                "Renders an existing raw CD-DA track with measured geometry.",
                DiscModuleCapabilities.EncodedOutputPreview,
                [
                    new("track", "Raw track", DiscOptionValueType.InputFile, Required: true),
                    new("output", "Preview PNG", DiscOptionValueType.OutputFile, Required: true),
                    new("size", "Preview size", DiscOptionValueType.Integer, DefaultValue: "1600", Unit: "px"),
                    new("byte-step", "Byte sampling interval", DiscOptionValueType.Integer, DefaultValue: "48"),
                ]),
        ]);

    public DiscModuleDescriptor Descriptor => ModuleDescriptor;

    public Task<DiscJobResult> ExecuteAsync(
        DiscJobRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        CdCommandOptions options = CdCommandOptions.Parse(request.Arguments);
        DiscJobResult result = request.Command switch
        {
            "cd-generate" => Generate(request, options, progress, cancellationToken),
            "cd-preview-warp" => PreviewWarp(request, options, progress, cancellationToken),
            "cd-preview-track" => PreviewTrack(request, options, progress, cancellationToken),
            _ => throw new ArgumentException(
                $"CD module does not support command '{request.Command}'.",
                nameof(request)),
        };
        return Task.FromResult(result);
    }

    private static DiscJobResult Generate(
        DiscJobRequest request,
        CdCommandOptions options,
        IProgress<DiscJobProgress>? progress,
        CancellationToken cancellationToken)
    {
        string output = options.Require("output");
        CdGenerationSummary summary = CdTrackGenerator.Generate(
            options.Require("input"),
            output,
            ReadParameters(options, string.Empty),
            options.GetBoolean("interleave", true),
            value => progress?.Report(new DiscJobProgress(
                ModuleId,
                "generate",
                $"CD progress {value.Fraction:P1}",
                value.Fraction,
                value.CompletedBytes,
                value.TotalBytes,
                value.Elapsed)),
            cancellationToken);
        string message =
            $"CD track complete: {summary.Sectors} sectors, {summary.BytesWritten} bytes, "
            + $"interleave={summary.Interleaved}, elapsed={summary.Elapsed.TotalSeconds:F1}s, "
            + $"output={summary.OutputTrack}";
        return new DiscJobResult(ModuleId, request.Command, 0, message, summary.OutputTrack);
    }

    private static DiscJobResult PreviewWarp(
        DiscJobRequest request,
        CdCommandOptions options,
        IProgress<DiscJobProgress>? progress,
        CancellationToken cancellationToken)
    {
        string output = Path.GetFullPath(options.Require("output"));
        progress?.Report(new DiscJobProgress(ModuleId, "preview", "Rendering CD geometry preview…"));
        CdTrackGenerator.PreviewWarp(
            options.Require("input"),
            output,
            ReadParameters(options, "gen-"),
            ReadParameters(options, "actual-"),
            options.GetInt("size", 1600),
            options.GetInt("samples-per-sector", 16),
            cancellationToken);
        return new DiscJobResult(
            ModuleId,
            request.Command,
            0,
            $"CD geometry preview complete: output={output}",
            output);
    }

    private static DiscJobResult PreviewTrack(
        DiscJobRequest request,
        CdCommandOptions options,
        IProgress<DiscJobProgress>? progress,
        CancellationToken cancellationToken)
    {
        string output = Path.GetFullPath(options.Require("output"));
        progress?.Report(new DiscJobProgress(ModuleId, "preview", "Rendering CD track preview…"));
        CdTrackGenerator.PreviewTrack(
            options.Require("track"),
            output,
            ReadParameters(options, string.Empty),
            options.GetInt("size", 1600),
            options.GetInt("byte-step", 48),
            cancellationToken);
        return new DiscJobResult(
            ModuleId,
            request.Command,
            0,
            $"CD track preview complete: output={output}",
            output);
    }

    private static CdDiscParameters ReadParameters(CdCommandOptions options, string prefix)
    {
        double startAngleDegrees = options.GetDouble(prefix + "theta0", 0.0);
        return new CdDiscParameters(
            options.GetDouble(prefix + "r0", 24.5),
            options.GetDouble(prefix + "r1", 56.8),
            options.GetLong(prefix + "sectors", 359849),
            options.GetDouble(prefix + "velocity", 1100.0),
            startAngleDegrees * Math.PI / 180.0,
            options.GetDouble(prefix + "outer", 57.5));
    }

    private sealed class CdCommandOptions
    {
        private readonly Dictionary<string, string> _values;

        private CdCommandOptions(Dictionary<string, string> values) => _values = values;

        internal static CdCommandOptions Parse(IReadOnlyList<string> arguments)
        {
            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < arguments.Count; index++)
            {
                string argument = arguments[index];
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Unexpected argument '{argument}'.");
                }

                int equals = argument.IndexOf('=');
                string key;
                string value;
                if (equals >= 0)
                {
                    key = argument[2..equals];
                    value = argument[(equals + 1)..];
                }
                else
                {
                    key = argument[2..];
                    if (++index >= arguments.Count)
                    {
                        throw new ArgumentException($"Missing value for --{key}.");
                    }

                    value = arguments[index];
                }

                if (!values.TryAdd(key, value))
                {
                    throw new ArgumentException($"Option --{key} was specified more than once.");
                }
            }

            return new CdCommandOptions(values);
        }

        internal string Require(string key)
            => _values.TryGetValue(key, out string? value)
                ? value
                : throw new ArgumentException($"Missing required option --{key}.");

        internal int GetInt(string key, int fallback)
            => _values.TryGetValue(key, out string? value)
                ? int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
                : fallback;

        internal long GetLong(string key, long fallback)
            => _values.TryGetValue(key, out string? value)
                ? long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
                : fallback;

        internal double GetDouble(string key, double fallback)
            => _values.TryGetValue(key, out string? value)
                ? double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)
                : fallback;

        internal bool GetBoolean(string key, bool fallback)
        {
            if (!_values.TryGetValue(key, out string? value))
            {
                return fallback;
            }

            return value switch
            {
                "1" => true,
                "0" => false,
                _ => bool.Parse(value),
            };
        }
    }
}
