using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Godot;
using Sim.Host;

namespace TrafficCity.Saves;

/// <summary>Откуда сейв: ручной, быстрый (F5) или автосохранение.</summary>
public enum SaveKind
{
    Manual,
    Quick,
    Auto,
}

/// <summary>Строка списка сейвов: данные из sidecar-файла <c>имя.json</c> (клиентский <c>meta</c>) либо из заголовка и времени файла, если sidecar'а нет.</summary>
public sealed class SaveInfo
{
    public string Path { get; init; } = string.Empty;

    public string Stem { get; init; } = string.Empty;

    public SaveKind Kind { get; init; }

    public DateTime SavedAt { get; init; }

    public long Tick { get; init; }

    /// <summary>Игровой день с 1; 0 - у сценария нет календаря.</summary>
    public int Day { get; init; }

    public int Population { get; init; }

    public string Scenario { get; init; } = string.Empty;

    public string Title => Kind switch
    {
        SaveKind.Auto => "Автосохранение",
        SaveKind.Quick => "Быстрое сохранение",
        _ => "Сохранение " + SavedAt.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture),
    };

    /// <summary>Вторая строка: игровой день и жители, дата. Без календаря - тик.</summary>
    public string Subtitle
    {
        get
        {
            string when = SavedAt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            return Day > 0
                ? string.Create(CultureInfo.InvariantCulture, $"День {Day}  ·  жителей {Population}  ·  {when}")
                : string.Create(CultureInfo.InvariantCulture, $"Тик {Tick}  ·  {when}");
        }
    }
}

/// <summary>
/// Файлы сейвов: каталог <c>user://saves</c> (или <c>--saves-dir</c>), <c>имя.tcsave</c> (конверт save-format.md §7) и рядом <c>имя.json</c> с краткими данными для списка.
/// Запись через временный файл и переименование: оборванный сейв не заменяет хороший. Только клиент; ядро файлов не знает.
/// </summary>
public sealed class SaveStore
{
    public const string Extension = ".tcsave";

    public SaveStore(string? overrideDir)
    {
        Dir = string.IsNullOrEmpty(overrideDir) ? ProjectSettings.GlobalizePath("user://saves") : System.IO.Path.GetFullPath(overrideDir);
    }

    public string Dir { get; }

    public static string ManualStem(DateTime now) => "save-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    public static string StemFor(SaveKind kind, DateTime now) => kind switch
    {
        SaveKind.Auto => "autosave",
        SaveKind.Quick => "quicksave",
        _ => ManualStem(now),
    };

    /// <summary>Все сейвы каталога, свежие первыми. Порченые файлы не скрываются (ошибка проявится при загрузке).</summary>
    public List<SaveInfo> List()
    {
        var list = new List<SaveInfo>();
        if (!Directory.Exists(Dir))
        {
            return list;
        }

        foreach (string file in Directory.EnumerateFiles(Dir, "*" + Extension))
        {
            list.Add(Describe(file));
        }

        list.Sort((a, b) => b.SavedAt.CompareTo(a.SavedAt));
        return list;
    }

    public SaveInfo? Latest()
    {
        List<SaveInfo> all = List();
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>Пишет сейв: <paramref name="container"/> - сырой контейнер ядра, в файл уходит конверт; sidecar - <paramref name="metaJson"/>. Возвращает путь.</summary>
    public string Write(string stem, byte[] container, string metaJson)
    {
        Directory.CreateDirectory(Dir);
        string path = System.IO.Path.Combine(Dir, stem + Extension);
        AtomicWrite(path, SaveEnvelope.Pack(container));
        if (metaJson.Length > 0)
        {
            AtomicWrite(System.IO.Path.ChangeExtension(path, ".json"), System.Text.Encoding.UTF8.GetBytes(metaJson));
        }

        return path;
    }

    private static void AtomicWrite(string path, byte[] data)
    {
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    private static SaveInfo Describe(string file)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(file);
        DateTime written = File.GetLastWriteTime(file);
        long tick = 0;
        try
        {
            using FileStream fs = File.OpenRead(file);
            var head = new byte[SaveEnvelope.EnvelopeHeaderSize];
            int n = fs.Read(head, 0, head.Length);
            if (HostSaves.Peek(head.AsSpan(0, n), out Sim.Core.Api.SaveHeaderInfo h) == Sim.Core.Api.LoadStatus.Ok)
            {
                tick = h.Tick;
            }
        }
        catch (IOException)
        {
        }

        string side = System.IO.Path.ChangeExtension(file, ".json");
        SaveKind kind = stem == "autosave" ? SaveKind.Auto : stem == "quicksave" ? SaveKind.Quick : SaveKind.Manual;
        DateTime savedAt = written;
        int day = 0;
        int pop = 0;
        string scenario = string.Empty;
        try
        {
            if (File.Exists(side) && JsonNode.Parse(File.ReadAllText(side)) is JsonObject o)
            {
                tick = (long?)o["tick"] ?? tick;
                day = (int?)o["day"] ?? 0;
                pop = (int?)o["population"] ?? 0;
                scenario = (string?)o["scenario"] ?? string.Empty;
                if (DateTime.TryParse((string?)o["saved_at"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t))
                {
                    savedAt = t;
                }

                kind = ((string?)o["kind"]) switch
                {
                    "auto" => SaveKind.Auto,
                    "quick" => SaveKind.Quick,
                    _ => SaveKind.Manual,
                };
            }
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidCastException or FormatException)
        {
            // sidecar порван: показываем по заголовку и времени файла
        }

        return new SaveInfo { Path = file, Stem = stem, Kind = kind, SavedAt = savedAt, Tick = tick, Day = day, Population = pop, Scenario = scenario };
    }
}
