using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sim.Headless.Bench;

/// <summary>Минимальный разбор <c>--name value</c> для подкоманд bench/compare/update-baseline.</summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string Error { get; private set; } = "";

    /// <summary>Разбирает args[1..]; допустимые опции — <paramref name="allowed"/> (с префиксом "--"), у каждой есть значение.</summary>
    public static bool TryParse(string[] args, string[] allowed, out CliArgs result)
    {
        result = new CliArgs();
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (Array.IndexOf(allowed, a) < 0)
            {
                result.Error = $"unknown option '{a}'";
                return false;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result.Error = $"option '{a}' requires a value";
                return false;
            }

            i++;
            result._values[a] = args[i];
        }

        return true;
    }

    public string? Get(string name) => _values.TryGetValue(name, out string? v) ? v : null;

    public bool TryGetInt(string name, int min, out int value, int defaultValue)
    {
        value = defaultValue;
        string? s = Get(name);
        if (s == null)
        {
            return true;
        }

        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < min)
        {
            Error = $"option '{name}' must be an integer >= {min}, got '{s}'";
            return false;
        }

        return true;
    }

    public bool TryGetDouble(string name, double min, double max, out double value, double defaultValue)
    {
        value = defaultValue;
        string? s = Get(name);
        if (s == null)
        {
            return true;
        }

        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < min || value > max)
        {
            Error = $"option '{name}' must be a number in [{min}, {max}], got '{s}'";
            return false;
        }

        return true;
    }
}
