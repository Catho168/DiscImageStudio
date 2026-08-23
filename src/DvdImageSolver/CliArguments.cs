using System.Globalization;

namespace DvdImageSolver;

internal sealed class CliArguments
{
    private readonly Dictionary<string, string> _values;

    private CliArguments(Dictionary<string, string> values) => _values = values;

    internal static CliArguments Parse(ReadOnlySpan<string> arguments)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{argument}'. Options must start with --.");
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
                if (++index >= arguments.Length)
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

        return new CliArguments(values);
    }

    internal string Require(string key)
        => _values.TryGetValue(key, out string? value)
            ? value
            : throw new ArgumentException($"Missing required option --{key}.");

    internal string Get(string key, string fallback)
        => _values.TryGetValue(key, out string? value) ? value : fallback;

    internal int GetInt(string key, int fallback)
        => _values.TryGetValue(key, out string? value)
            ? int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : fallback;

    internal uint GetUInt(string key, uint fallback)
        => _values.TryGetValue(key, out string? value) ? ParseUInt(value) : fallback;

    internal double GetDouble(string key, double fallback)
        => _values.TryGetValue(key, out string? value)
            ? double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)
            : fallback;

    internal bool Has(string key) => _values.ContainsKey(key);

    private static uint ParseUInt(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
