namespace TrafficCity.Saves;

/// <summary>Что запускает следующая загрузка главной сцены.</summary>
public enum StartKind
{
    /// <summary>Решает SimHost по аргументам запуска (без аргументов - главное меню, иначе прежнее поведение).</summary>
    Auto,
    Menu,
    New,
    Load,
}

/// <summary>
/// Передача намерения между перезагрузками сцены. «Новая игра», «Загрузить» и «Главное меню» пересоздают главную сцену (<c>ReloadCurrentScene</c>):
/// так <c>SimHost</c> заново строит хост, слои и камеру из загруженного мира без ручного сноса, а статика остаётся тут. Только главный поток.
/// </summary>
public static class GameFlow
{
    /// <summary>Сценарий «Новой игры»: готовый сгенерированный город с жителями и казной (<c>scenarios/game-city.json</c>), мир готовит ReadyCity в фоне (SimHost.ReadyCity).</summary>
    public const string NewGameScenario = "game-city";

    public static StartKind Pending { get; private set; }

    public static string PendingPath { get; private set; } = string.Empty;

    /// <summary>Сообщение для главного меню (ошибка загрузки); читается один раз.</summary>
    public static string Notice { get; set; } = string.Empty;

    /// <summary>Самопроверка <c>--save-test</c>: после загрузки сверить тик и хэш с сохранённым.</summary>
    public static bool Verify { get; private set; }

    public static long ExpectTick { get; private set; }

    public static ulong ExpectHash { get; private set; }

    public static void Request(StartKind kind, string path = "")
    {
        Pending = kind;
        PendingPath = path;
    }

    public static void RequestVerifiedLoad(string path, long tick, ulong hash)
    {
        Request(StartKind.Load, path);
        Verify = true;
        ExpectTick = tick;
        ExpectHash = hash;
    }

    public static void ClearVerify() => Verify = false;

    /// <summary>Забрать намерение (оно одноразово).</summary>
    public static StartKind Take(out string path)
    {
        StartKind k = Pending;
        path = PendingPath;
        Pending = StartKind.Auto;
        PendingPath = string.Empty;
        return k;
    }
}
