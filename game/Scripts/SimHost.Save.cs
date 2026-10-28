using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Godot;
using Sim.Core.Api;
using Sim.Host;
using Sim.Scenarios;
using TrafficCity.Saves;
using TrafficCity.Ui;

namespace TrafficCity;

/// <summary>
/// Сохранение, загрузка и меню (этап 6.1). Сейв - снимок ядра под эксклюзивным доступом (<see cref="HostSaves"/>) в <c>user://saves/*.tcsave</c> с sidecar-json для списка.
/// Загрузка, «Новая игра» и «Главное меню» пересоздают главную сцену (<c>ReloadCurrentScene</c>) с намерением в <see cref="GameFlow"/>: новый <see cref="SimHost"/>
/// строит хост, слои и камеру из загруженного мира тем же путём, что и при старте сценария (ADR-0024 п. 5, слои по <c>NetworkView.Version</c>).
/// Аргументы: <c>--main-menu</c> (показать меню при аргументах), <c>--new-game</c> (сразу новая игра), <c>--load-file путь</c> (загрузить .tcsave), <c>--saves-dir каталог</c>, <c>--autosave-hours N</c> (0 - выкл.),
/// <c>--save-test N</c> (N тиков -> сейв -> загрузка -> сверка тика и хэша, лог <c>[save]</c>), <c>--save-quit</c> (выйти через ~20 кадров после загрузки).
/// </summary>
public partial class SimHost
{
    private const int DefaultAutosaveHours = 24;
    private SaveStore _saves = null!;
    private string _savesDirArg = string.Empty;
    private string _loadFileArg = string.Empty;
    private string _loadPath = string.Empty;
    private bool _mainMenuArg;
    private bool _newGameArg;        // --new-game: сразу «Новая игра» (сценарий GameFlow.NewGameScenario), без меню
    private bool _saveQuitArg;
    private int _autosaveHours = DefaultAutosaveHours;
    private long _nextAutosaveTick = -1;
    private int _saveTestTicks = -1;
    private bool _saveTestArmed;
    private int _quitAfterFrames = -1;
    private JsonObject? _loadedMeta;
    private string _gameScenario = string.Empty;
    private bool _menuPaused;
    private bool _pausedBeforeMenu;
    private bool _flowBusy;
    private bool _menuTest;          // --menu-test: Esc-меню ставит на паузу и возвращает скорость, F5/F9 (скриптом, без клавиш)
    private int _menuTestStep;
    private int _menuTestFrames;

    private bool ParseSaveArg(string[] args, ref int i)
    {
        string a = args[i];
        bool hasValue = i + 1 < args.Length;
        if (a == "--main-menu")
        {
            _mainMenuArg = true;
        }
        else if (a == "--new-game")
        {
            _newGameArg = true;
        }
        else if (a == "--menu-test")
        {
            _menuTest = true;
        }
        else if (a == "--save-quit")
        {
            _saveQuitArg = true;
        }
        else if (a == "--load-file" && hasValue)
        {
            _loadFileArg = args[++i];
        }
        else if (a == "--saves-dir" && hasValue)
        {
            _savesDirArg = args[++i];
        }
        else if (a == "--autosave-hours" && hasValue)
        {
            int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out _autosaveHours);
        }
        else if (a == "--save-test" && hasValue)
        {
            int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out _saveTestTicks);
        }
        else
        {
            return false;
        }

        return true;
    }

    private void InitSave(int argCount)
    {
        _saves = new SaveStore(_savesDirArg);
        if (_ui == null)
        {
            return;
        }

        UiMenus m = _ui.Menus;
        m.ListSaves = _saves.List;
        m.NewGameRequested += () => StartFlow(StartKind.New, string.Empty, "Создание города…");
        m.ContinueRequested += () =>
        {
            SaveInfo? latest = _saves.Latest();
            if (latest != null)
            {
                StartFlow(StartKind.Load, latest.Path, "Загрузка…");
            }
        };
        m.LoadRequested += s => StartFlow(StartKind.Load, s.Path, "Загрузка…");
        m.SaveRequested += () => SaveGame(SaveKind.Manual);
        m.MainMenuRequested += () => StartFlow(StartKind.Menu, string.Empty, "Главное меню…");
        m.QuitRequested += () => GetTree().Quit();
        _ui.Router.StateChanged += SyncMenuPause;
        m.Rebuild(); // меню, открытое аргументом --ui-menu до подключения списка
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[save] dir {_saves.Dir}, autosave {(_autosaveHours > 0 ? _autosaveHours + " game h" : "off")}, args {argCount}"));
    }

    /// <summary>Что запускать: намерение после перезагрузки сцены, иначе <c>--load-file</c>, иначе (без аргументов или с <c>--main-menu</c>) главное меню, иначе прежнее поведение.</summary>
    private StartKind DecideStart(int argCount, ref string name)
    {
        StartKind k = GameFlow.Take(out string path);
        if (k == StartKind.Auto)
        {
            if (_loadFileArg.Length > 0)
            {
                k = StartKind.Load;
                path = _loadFileArg;
            }
            else if (_newGameArg)
            {
                k = StartKind.New;
            }
            else if (_mainMenuArg || argCount == 0)
            {
                k = StartKind.Menu;
            }
        }

        if (k == StartKind.New)
        {
            name = GameFlow.NewGameScenario;
        }

        if (k == StartKind.Load)
        {
            _loadPath = path;
        }

        return k;
    }

    private void ShowMainMenu()
    {
        if (_ui == null)
        {
            GD.PushError("[save] главное меню без UiRoot");
            return;
        }

        _ui.EnterMainMenu();
        GD.Print("[save] main menu");
    }

    /// <summary>После успешного старта сцены: камера из сейва уже применена, остаются автосохранение, пауза меню и самопроверка.</summary>
    private void AfterStart()
    {
        AttachEventSource();
        InitTools();
        _nextAutosaveTick = -1;
        _menuPaused = false;
        SyncMenuPause();
        if (_loadPath.Length > 0 && (_saveQuitArg || GameFlow.Verify))
        {
            _quitAfterFrames = 20;
        }

        GameFlow.ClearVerify();
    }

    // ---- пересоздание сцены ----

    private void StartFlow(StartKind kind, string path, string busyText)
    {
        if (_flowBusy)
        {
            return;
        }

        _flowBusy = true;
        GameFlow.Request(kind, path);
        _ui?.Menus.ShowBusy(busyText);
        GD.Print($"[save] flow {kind} {path}");
        // кадр на отрисовку «Загрузка…» до тяжёлой генерации/загрузки в _Ready новой сцены
        GetTree().CreateTimer(0.15).Timeout += () => GetTree().ReloadCurrentScene();
    }

    private void QuickLoad()
    {
        string p = Path.Combine(_saves.Dir, "quicksave" + SaveStore.Extension);
        if (File.Exists(p))
        {
            StartFlow(StartKind.Load, p, "Загрузка…");
        }
        else
        {
            _ui?.Menus.Flash("Быстрого сохранения нет", error: true);
        }
    }

    // ---- пауза на время меню ----

    /// <summary>Меню игры открыто - игра на паузе; закрыто - прежнее состояние паузы, скорость не трогаем.</summary>
    private void SyncMenuPause()
    {
        bool open = _ui != null && _ui.Router.MenuOpen && !_ui.InMainMenu;
        if (open == _menuPaused)
        {
            return;
        }

        _menuPaused = open;
        if (open)
        {
            _pausedBeforeMenu = _paused;
            SetPaused(true);
        }
        else
        {
            SetPaused(_pausedBeforeMenu);
        }

        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[save] menu {(open ? "open" : "closed")}: paused {_paused} (before {_pausedBeforeMenu}), speed x{_timeScale:0.##}"));
    }

    /// <summary>
    /// <c>--menu-test</c> (скрипт вместо клавиш): открыть Esc-меню -> игра на паузе, скорость та же -> закрыть -> пауза снята, скорость возвращена -> F5 (быстрое сохранение) ->
    /// несколько тиков -> F9 (загрузка быстрого сохранения с проверкой тика и хэша).
    /// </summary>
    private void UpdateMenuTest()
    {
        if (!_menuTest || _ui == null || _host == null || _loadPath.Length > 0 || _flowBusy || ++_menuTestFrames < 30)
        {
            return;
        }

        switch (_menuTestStep)
        {
            case 0:
                _ui.Router.Cancel(); // Esc: ни инструмента, ни инспектора, ни инфо -> меню
                GD.Print($"[save] menu-test: after Esc paused={_paused} (expect True) menuOpen={_ui.Router.MenuOpen}");
                _menuTestFrames = 0;
                _menuTestStep = 1;
                break;
            case 1:
                _ui.Router.Cancel(); // второй Esc закрывает меню
                GD.Print($"[save] menu-test: after second Esc paused={_paused} (expect False) menuOpen={_ui.Router.MenuOpen} speed x{_timeScale:0.##}");
                _menuTestFrames = 0;
                _menuTestStep = 2;
                break;
            case 2:
                if (SaveGame(SaveKind.Quick, null, out string path, out long tick, out ulong hash))
                {
                    GameFlow.RequestVerifiedLoad(path, tick, hash);
                    QuickLoad();
                }

                _menuTestStep = 3;
                break;
            default:
                break;
        }
    }

    // ---- сохранение ----

    private string BuildMeta(Simulation sim, SaveKind kind)
    {
        var o = new JsonObject
        {
            ["format"] = 1,
            ["kind"] = kind switch { SaveKind.Auto => "auto", SaveKind.Quick => "quick", _ => "manual" },
            ["scenario"] = _gameScenario,
            ["prof_res"] = _profRes,
            ["saved_at"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
            ["world_version"] = Simulation.CurrentWorldVersion,
            ["tick"] = sim.Tick,
            ["hash"] = sim.ComputeStateHash().ToString("x16", CultureInfo.InvariantCulture),
        };
        int dayTicks = sim.CityDayTicks;
        if (dayTicks > 0)
        {
            o["day"] = (int)(sim.Tick / dayTicks) + 1;
            o["hour"] = 24f * (sim.Tick % dayTicks) / dayTicks;
            o["population"] = sim.CityPopulation;
            if (sim.GetCityBudget(out CityBudgetInfo b))
            {
                o["balance"] = b.Balance;
            }
        }

        if (_camera != null)
        {
            _camera.GetPose(out Vector3 c, out float dist, out float pitch, out float yaw);
            o["cam"] = new JsonObject { ["x"] = c.X, ["z"] = c.Z, ["dist"] = dist, ["pitch"] = pitch, ["yaw"] = yaw };
        }

        return o.ToJsonString();
    }

    /// <summary>Сохранить игру (ядро стоит на время снимка, десятки мс). Автосохранение и быстрое перезаписывают свой файл.</summary>
    private bool SaveGame(SaveKind kind) => SaveGame(kind, null, out _, out _, out _);

    private bool SaveGame(SaveKind kind, string? stem, out string path, out long tick, out ulong hash)
    {
        path = string.Empty;
        tick = 0;
        hash = 0;
        if (_host == null || _frame == null)
        {
            return false;
        }

        var sw = Stopwatch.StartNew();
        SaveStatus st = HostSaves.Save(_host, sim => BuildMeta(sim, kind), out byte[] bytes, out string meta, out tick, out hash);
        long snapMs = sw.ElapsedMilliseconds;
        if (st != SaveStatus.Ok)
        {
            GD.PushError("[save] Save failed: " + st);
            _ui?.Menus.Flash("Не удалось сохранить: " + st, error: true);
            return false;
        }

        try
        {
            path = _saves.Write(stem ?? SaveStore.StemFor(kind, DateTime.Now), bytes, meta);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushError("[save] write failed: " + e.Message);
            _ui?.Menus.Flash("Не удалось записать файл: " + e.Message, error: true);
            return false;
        }

        GD.Print(string.Create(
            CultureInfo.InvariantCulture,
            $"[save] saved {kind} {path} tick {tick} hash {hash:x16}, container {bytes.Length} B, file {new FileInfo(path).Length} B, snapshot {snapMs} ms, total {sw.ElapsedMilliseconds} ms"));
        _ui?.Menus.Flash(kind switch { SaveKind.Auto => "Автосохранение", SaveKind.Quick => "Быстрое сохранение сделано", _ => "Игра сохранена" });
        return true;
    }

    /// <summary>Раз в кадр: автосохранение по игровому времени, самопроверка <c>--save-test</c>, отложенный выход.</summary>
    private void UpdateSaveFlow(SimFrame f)
    {
        if (_quitAfterFrames > 0 && --_quitAfterFrames == 0)
        {
            GetTree().Quit();
            return;
        }

        UpdateSaveTest();
        UpdateMenuTest();
        if (_autosaveHours <= 0 || _dayTicks <= 0 || _flowBusy || (_ui?.Router.MenuOpen ?? false))
        {
            return;
        }

        long interval = Math.Max(1L, (long)_dayTicks * _autosaveHours / 24);
        if (_nextAutosaveTick < 0)
        {
            _nextAutosaveTick = ((f.SnapshotTick / interval) + 1) * interval;
        }
        else if (f.SnapshotTick >= _nextAutosaveTick)
        {
            SaveGame(SaveKind.Auto);
            _nextAutosaveTick = ((f.SnapshotTick / interval) + 1) * interval;
        }
    }

    /// <summary>
    /// <c>--save-test N</c>: пауза, N тиков вперёд, сейв, затем настоящая загрузка (перезагрузка сцены) и сверка; сверка и выход - в <see cref="LoadWorld"/>.
    /// </summary>
    private void UpdateSaveTest()
    {
        if (_saveTestTicks < 0 || _host == null || _loadPath.Length > 0 || _flowBusy)
        {
            return;
        }

        if (!_saveTestArmed)
        {
            _saveTestArmed = true;
            _paused = true;
            _targetTick = _host.Tick + _saveTestTicks;
            GD.Print($"[save] test: stepping {_saveTestTicks} ticks from {_host.Tick}");
            return;
        }

        if (_host.Tick < _targetTick)
        {
            return;
        }

        _saveTestTicks = -1;
        if (SaveGame(SaveKind.Manual, "savetest", out string path, out long tick, out ulong hash))
        {
            GameFlow.RequestVerifiedLoad(path, tick, hash);
            StartFlow(StartKind.Load, path, "Загрузка…");
        }
        else
        {
            GD.Print("[save] RESULT FAIL: save failed");
            GetTree().Quit(1);
        }
    }

    // ---- загрузка ----

    /// <summary>Читает файл, строит мир из него (хэш проверяется строго) и подбирает сценарий для клиента. null - ошибка в <paramref name="error"/>.</summary>
    private Simulation? LoadWorld(in SimRuntimeOptions rt, out string scenarioName, out string error)
    {
        scenarioName = string.Empty;
        error = string.Empty;
        var sw = Stopwatch.StartNew();
        byte[] container;
        try
        {
            container = SaveEnvelope.ReadContainer(_loadPath);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error = Path.GetFileName(_loadPath) + ": " + e.Message;
            return null;
        }

        LoadStatus ls = HostSaves.Load(container, in rt, out Simulation? sim, out string metaJson, out LoadReport rep);
        if (ls != LoadStatus.Ok || sim == null)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(_loadPath)}: {ls} (блок {rep.FailedBlock:x}, строка {rep.FailedRow})");
            return null;
        }

        try
        {
            _loadedMeta = metaJson.Length > 0 ? JsonNode.Parse(metaJson) as JsonObject : null;
        }
        catch (System.Text.Json.JsonException)
        {
            _loadedMeta = null; // чужой или порванный meta: игра идёт, камера и сценарий подбираются по имени файла
        }

        if (_loadedMeta?["prof_res"] is JsonNode pr && (bool?)pr == true)
        {
            _profRes = true;
        }

        scenarioName = PickScenario((string?)_loadedMeta?["scenario"], _loadPath);
        ulong hash = sim.ComputeStateHash();
        GD.Print(string.Create(
            CultureInfo.InvariantCulture,
            $"[save] loaded {_loadPath} world_version {rep.FileWorldVersion} (migrations {rep.MigrationsApplied}), tick {sim.Tick} hash {hash:x16}, header hash checked {rep.HashChecked} matched {rep.HashMatched}, scenario '{scenarioName}', {sw.ElapsedMilliseconds} ms"));
        if (GameFlow.Verify)
        {
            bool tickOk = sim.Tick == GameFlow.ExpectTick;
            bool hashOk = hash == GameFlow.ExpectHash;
            GD.Print(string.Create(
                CultureInfo.InvariantCulture,
                $"[save] verify: tick {sim.Tick} vs saved {GameFlow.ExpectTick} {(tickOk ? "equal" : "DIFFER")}, hash {hash:x16} vs saved {GameFlow.ExpectHash:x16} {(hashOk ? "equal" : "DIFFER")}"));
            GD.Print(tickOk && hashOk ? "[save] RESULT PASS" : "[save] RESULT FAIL");
        }

        return sim;
    }

    /// <summary>Сценарий для клиента (типы ТС, сетка привязки): из meta сейва, иначе из имени файла корпуса (<c>w10-s1k-res-d3-t216000</c> -> <c>s1k-res</c>), иначе s1k-res.</summary>
    private static string PickScenario(string? fromMeta, string loadPath)
    {
        string stem = Path.GetFileNameWithoutExtension(loadPath);
        string fromName = Regex.Replace(Regex.Replace(stem, @"^w\d+-", string.Empty), @"-t\d+$", string.Empty);
        string scenariosDir = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "scenarios"));
        foreach (string c in new[] { fromMeta ?? string.Empty, fromName, Regex.Replace(fromName, @"-d\d+$", string.Empty), "s1k-res" })
        {
            if (c.Length == 0)
            {
                continue;
            }

            try
            {
                ScenarioLocator.Resolve(c, scenariosDir);
                return c;
            }
            catch (ScenarioException)
            {
                // следующий кандидат
            }
        }

        return "s1k-res";
    }

    private void RestoreLoadedCamera()
    {
        if (_camera == null || _loadedMeta?["cam"] is not JsonObject c)
        {
            return;
        }

        try
        {
            _camera.SetPose(new Vector3((float)c["x"]!, 0f, (float)c["z"]!), (float)c["dist"]!, (float)c["pitch"]!, (float)c["yaw"]!);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or NullReferenceException)
        {
            // камера из чужого meta не прочиталась: остаётся поза по умолчанию
        }
    }
}
