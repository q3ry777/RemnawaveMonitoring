using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace RemnawaveMonitor;

public partial class App : Application
{
    readonly Settings _s = Settings.Load();
    readonly Api _api = new();
    readonly DispatcherTimer _timer = new();
    readonly DispatcherTimer _svcTimer = new();
    readonly Icon _ok = MakeIcon(Color.FromArgb(74, 222, 128));
    readonly Icon _bad = MakeIcon(Color.FromArgb(251, 73, 52));
    readonly HashSet<string> _alerted = new();
    readonly IncidentLog _log = IncidentLog.Load();
    readonly TelegramService _tg = new();
    readonly DispatcherTimer _tgTimer = new();
    IncidentsWindow? _journal;
    bool _balloonIncident;
    DateTime _balloonShownAt = DateTime.MinValue;
    readonly DispatcherTimer _ruTimer = new();
    readonly DispatcherTimer _relTimer = new();
    readonly Dictionary<string, (string text, string level)> _ru = new();
    string? _latestNode, _latestBackend, _panelVersion;
    bool _ruBusy, _verSeeded;
    readonly Dictionary<string, int> _strikes = new();
    Forms.NotifyIcon _tray = null!;
    MainWindow _win = null!;
    Snapshot? _prev;
    bool _busy, _svcBusy, _svcSeeded;
    int _svcOk, _svcTotal;
    Mutex? _mutex;
    EventWaitHandle? _showEvt;

    public static void ApplyTheme(string theme)
    {
        var name = theme == "light" ? "Light" : "Dark";
        Current.Resources.MergedDictionaries[0] =
            new ResourceDictionary { Source = new Uri($"Themes/{name}.xaml", UriKind.Relative) };
        foreach (Window w in Current.Windows) WinChrome.Apply(w);
    }

    static Icon MakeIcon(Color c)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            const int d = 14;
            path.AddArc(1, 1, d, d, 180, 90);
            path.AddArc(31 - d, 1, d, d, 270, 90);
            path.AddArc(31 - d, 31 - d, d, d, 0, 90);
            path.AddArc(1, 31 - d, d, d, 90, 90);
            path.CloseFigure();
            using var bg = new SolidBrush(Color.FromArgb(18, 21, 26));
            g.FillPath(bg, path);
            using var pen = new Pen(Color.FromArgb(48, 54, 66), 1);
            g.DrawPath(pen, path);
            using var b = new SolidBrush(c);
            g.FillRectangle(b, 7, 14, 18, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // защита от второго запуска: вторая копия лишь просит первую показать окно
        _mutex = new Mutex(false, "RemnawaveMonitor.SingleInstance");
        bool first;
        try { first = _mutex.WaitOne(e.Args.Contains("--updated") ? 15000 : 0); }   // после обновления ждём, пока уйдёт старая копия
        catch (AbandonedMutexException) { first = true; }
        _showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "RemnawaveMonitor.ShowWindow");
        if (!first)
        {
            if (!e.Args.Contains("--tray")) _showEvt.Set();
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            while (_showEvt.WaitOne()) Dispatcher.BeginInvoke(new Action(ShowWin));
        }) { IsBackground = true }.Start();

        try { File.Delete((Environment.ProcessPath ?? "") + ".old"); } catch { }

        ApplyTheme(_s.Theme);
        _win = new MainWindow(_s, OpenSettings, () => { _ = Refresh(true); _ = RefreshServices(); });
        _win.InstallUpdateRequested = () => _ = InstallUpdate();
        _win.OpenJournalRequested = OpenJournal;

        _tg.Configure(_s);
        _tg.StatusProvider = BuildStatus;
        _tg.ProblemsProvider = BuildProblems;
        _tgTimer.Tick += async (_, _) => await _tg.TickAsync();
        _tgTimer.Interval = TimeSpan.FromSeconds(5);
        _tgTimer.Start();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => ShowWin());
        menu.Items.Add("Журнал событий", null, (_, _) => OpenJournal());
        menu.Items.Add("Отметить прочитанным", null, (_, _) => AckAll());
        menu.Items.Add("Настройки", null, (_, _) => OpenSettings());
        menu.Items.Add("Выход", null, (_, _) => { _tray.Visible = false; Shutdown(); });
        _tray = new Forms.NotifyIcon { Icon = _ok, Visible = true, Text = "Remnawave", ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowWin();
        // клик по уведомлению о проблеме открывает журнал и сбрасывает красную иконку
        _tray.BalloonTipClicked += (_, _) => { if (_balloonIncident) { OpenJournal(); AckAll(); } else ShowWin(); };
        // Если уведомление о проблеме закрыли крестиком (быстро, но не мгновенно) — считаем его прочитанным и красим иконку в зелёный.
        // Мгновенное закрытие — это замена другим уведомлением, закрытие по таймеру (~5 с) прочтением не считается.
        _tray.BalloonTipClosed += (_, _) =>
        {
            double sec = (DateTime.Now - _balloonShownAt).TotalSeconds;
            if (_balloonIncident && sec >= 1.0 && sec < 4.0) AckAll();
        };
        SyncJournalUi();

        _timer.Tick += async (_, _) => await Refresh();
        _timer.Interval = TimeSpan.FromSeconds(_s.IntervalSec);
        _timer.Start();

        _svcTimer.Tick += async (_, _) => await RefreshServices();
        _svcTimer.Interval = TimeSpan.FromSeconds(60);
        _svcTimer.Start();

        _ruTimer.Tick += async (_, _) => await RefreshRu();
        _ruTimer.Interval = TimeSpan.FromMinutes(10);
        _ruTimer.Start();

        _relTimer.Tick += async (_, _) => { await RefreshReleases(); await CheckAppUpdateAsync(); };
        _relTimer.Interval = TimeSpan.FromHours(6);
        _relTimer.Start();

        try
        {
            var c = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Settings.CachePath));
            if (c is { Length: >= 2 })
            {
                var snap = SnapshotBuilder.Build(c[0], c[1], _s, c.Length > 2 ? c[2] : "", c.Length > 3 ? c[3] : "")
                           with { At = File.GetLastWriteTime(Settings.CachePath) };
                snap = ApplyExtras(snap);
                _win.Update(snap, $"данные из кэша от {snap.At:dd.MM HH:mm}");
            }
        }
        catch { }

        if (!e.Args.Contains("--tray")) ShowWin();
        _ = Refresh();
        _ = RefreshServices();
        _ = RefreshReleases();
        _ = CheckAppUpdateAsync();
    }

    void ShowWin()
    {
        if (_win == null) return;
        _win.Show(); _win.WindowState = WindowState.Normal; _win.Activate();
    }

    void OpenSettings()
    {
        var w = new SettingsWindow(_s);
        if (w.ShowDialog() != true) return;
        _s.Save();
        _tg.Configure(_s);
        _timer.Interval = TimeSpan.FromSeconds(_s.IntervalSec);
        _ = Refresh(true);
        _ = RefreshServices();
        _ = CheckAppUpdateAsync();
    }

    async Task Refresh(bool manual = false)
    {
        if (_busy) { if (manual) _win.SetStatus("обновление уже идёт…"); return; }
        _busy = true;
        if (manual) _win.SetBusy(true);
        try
        {
            var (u, n, m, b) = await _api.FetchAsync(_s);
            var snap = SnapshotBuilder.Build(u, n, _s, m, b);
            snap = await WithProbes(snap);
            snap = ApplyExtras(snap);
            bool firstSnap = _prev == null;
            Directory.CreateDirectory(Path.GetDirectoryName(Settings.CachePath)!);
            File.WriteAllText(Settings.CachePath, JsonSerializer.Serialize(new[] { u, n, m, b }));
            Track("panel|offline", "Панель", false, false, "", "Связь с панелью восстановлена");
            Notify(snap);
            UpdateTray(snap);
            NotifyUsers(snap, seed: _prev == null);
            _prev = snap;
            _win.Update(snap);
            RefreshChip();
            if (firstSnap) _ = RefreshRu();
            _win.SetOffline(null);
        }
        catch (Exception ex)
        {
            _win.SetStatus("нет связи");
            _tray.Text = "Remnawave: нет связи с панелью";
            Track("panel|offline", "Панель", true, false, $"Нет связи с панелью: {ex.Message}", "Связь с панелью восстановлена");
            SyncJournalUi();
            _win.SetOffline(ex.Message);
        }
        finally { _busy = false; _win.SetBusy(false); }
    }

    // ping и TCP-порт каждой ноды с этого компьютера
    async Task<Snapshot> WithProbes(Snapshot s)
    {
        if (!_s.ProbeEnabled) return s;
        int port = _s.ProbePort;
        var tasks = s.Nodes.Select(async n =>
        {
            if (n.Address == "") return n;
            var (icmp, tcp) = await NodeProbe.RunAsync(n.Address, port);
            var parts = new List<string> { icmp is int ms ? $"ping {ms} мс" : "ping ✗" };
            if (port > 0) parts.Add(tcp != null ? $":{port} ✓" : $":{port} ✗");
            bool unreachable = port > 0 ? tcp == null : icmp == null;
            string level = unreachable ? "bad" : icmp is > 200 ? "warn" : "ok";
            return n with { PingText = string.Join(" · ", parts), PingLevel = level };
        }).ToList();
        return s with { Nodes = (await Task.WhenAll(tasks)).ToList() };
    }

    async Task RefreshServices()
    {
        if (_svcBusy) return;
        _svcBusy = true;
        try
        {
            var urls = ParseUrls(_s.ServicesText);
            var rows = (await Task.WhenAll(urls.Select(ServiceMonitor.CheckAsync))).ToList();
            NotifyServices(rows, seed: !_svcSeeded);
            SyncJournalUi();
            _svcSeeded = true;
            _svcOk = rows.Count(r => r.Level != "bad");
            _svcTotal = rows.Count;
            _win.UpdateServices(rows);
            if (_prev != null) UpdateTray(_prev);
        }
        catch { }
        finally { _svcBusy = false; }
    }

    static List<string> ParseUrls(string text) =>
        text.Split('\n').Select(l => l.Trim())
            .Where(l => l != "" && !l.StartsWith('#'))
            .Select(l => l.Contains("://") ? l : "https://" + l)
            .Distinct().ToList();

    void NotifyServices(List<ServiceRow> rows, bool seed)
    {
        foreach (var r in rows)
        {
            Track(r.Url + "|down", r.Name, r.Level == "bad", seed, $"Сайт {r.Name} недоступен: {r.StatusText}", $"Сайт {r.Name} снова доступен");
            Track(r.Url + "|cert", r.Name, r.CertLevel is "warn" or "crit", false, $"{r.Name}: сертификат — {r.Cert}", $"{r.Name}: сертификат обновлён");
        }
    }

    // ---- обновление самой программы (релизы приватного репозитория) ----
    UpdateInfo? _appUpdate;
    string? _appNotified;

    async Task CheckAppUpdateAsync()
    {
        try
        {
            _appUpdate = await Updater.CheckAsync(_s);
            _win.SetAppUpdate(_appUpdate == null ? null : $"доступна версия {_appUpdate.Version} · обновить");
            if (_appUpdate != null && _appNotified != _appUpdate.Version)
            {
                _appNotified = _appUpdate.Version;
                Balloon($"Доступна новая версия программы: {_appUpdate.Version}. Нажмите «обновить» в шапке окна.", Forms.ToolTipIcon.Info,
                    tgKind: "updates", tgText: $"⬆️ Доступна новая версия Remnawave Monitor: {_appUpdate.Version}");
            }
        }
        catch { /* нет сети или не настроен доступ — молча пробуем позже */ }
    }

    async Task InstallUpdate()
    {
        var u = _appUpdate;
        if (u == null) return;
        var notes = string.IsNullOrWhiteSpace(u.Notes) ? "" : "\n\n" + (u.Notes.Length > 600 ? u.Notes[..600] + "…" : u.Notes);
        var ask = MessageBox.Show($"Установить версию {u.Version} и перезапустить программу?{notes}", "Обновление",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;
        try
        {
            _win.SetStatus("загрузка обновления…");
            var path = await Updater.DownloadAsync(_s, u);
            _s.Save();
            Updater.ApplyAndRestart(path);
            _tray.Visible = false;
            Shutdown();
        }
        catch (Exception ex) { _win.SetStatus("не удалось обновить: " + ex.Message); }
    }

    // Результаты проверки из РФ и версии накладываются на каждый снимок
    Snapshot ApplyExtras(Snapshot s)
    {
        string? maxNode = s.Nodes.Select(n => n.NodeVersion).Where(v => v != "")
            .OrderByDescending(v => Version.TryParse(v.TrimStart('v'), out var x) ? x : new Version(0, 0)).FirstOrDefault();
        string? latest = ReleaseCheck.Newer(_latestNode, maxNode) ? _latestNode : maxNode;

        var nodes = s.Nodes.Select(n =>
        {
            bool old = ReleaseCheck.Newer(latest, n.NodeVersion);
            var parts = new List<string>();
            if (n.NodeVersion != "") parts.Add(old ? $"node {n.NodeVersion} → {latest}" : $"node {n.NodeVersion}");
            if (n.XrayVersion != "") parts.Add($"xray {n.XrayVersion}");
            var ru = _ru.TryGetValue(n.Name, out var r) ? r : ("", "");
            return n with { VersionText = string.Join(" · ", parts), VersionLevel = old ? "warn" : "", RuText = ru.Item1, RuLevel = ru.Item2 };
        }).ToList();
        return s with { Nodes = nodes };
    }

    // Проверка нод из России: раз в 10 минут, по одной, чтобы не упереться в лимиты check-host.net
    async Task RefreshRu()
    {
        if (_ruBusy || !_s.RuCheckEnabled || _prev == null) return;
        _ruBusy = true;
        try
        {
            int port = _s.ProbePort > 0 ? _s.ProbePort : 443;
            foreach (var n in _prev.Nodes.Where(x => x.Address != "" && x.Connected).ToList())
            {
                try
                {
                    if (await RuCheck.CheckAsync(n.Address, port) is { } v)
                    {
                        string level = v.ok == v.total ? "ok" : v.ok == 0 ? "bad" : "warn";
                        _ru[n.Name] = ($"из РФ {v.ok}/{v.total}" + (v.ok > 0 ? $" · {v.avgMs} мс" : ""), level);
                        Track(n.Name + "|ru", n.Name, level == "bad", false,
                            $"Нода {n.Name} недоступна из России (0 из {v.total} точек) — возможна блокировка",
                            $"Нода {n.Name} снова доступна из России");
                    }
                }
                catch { }
                await Task.Delay(2000);
            }
            SyncJournalUi();
            if (_prev != null)
            {
                _prev = ApplyExtras(_prev);
                _win.Update(_prev);
            }
        }
        finally { _ruBusy = false; }
    }

    // Новые версии панели и нод (GitHub-релизы Remnawave)
    async Task RefreshReleases()
    {
        try
        {
            var node = await ReleaseCheck.LatestAsync("remnawave/node");
            var back = await ReleaseCheck.LatestAsync("remnawave/backend");
            string? pv = null;
            var meta = await _api.TryGetAsync(_s, "/api/system/metadata");
            if (meta != "")
            {
                try { using var d = JsonDocument.Parse(meta); pv = J.S(d.RootElement, "response", "version"); }
                catch { }
            }
            if (!string.IsNullOrEmpty(pv)) _panelVersion = pv;

            if (_verSeeded)
            {
                if (back != null && back != _latestBackend && ReleaseCheck.Newer(back, _panelVersion))
                    Balloon($"Вышла новая версия Remnawave: {back} (у вас {_panelVersion})", Forms.ToolTipIcon.Info, tgKind: "updates");
                if (node != null && node != _latestNode && (_prev?.Nodes.Any(x => ReleaseCheck.Newer(node, x.NodeVersion)) ?? false))
                    Balloon($"Вышла новая версия ноды Remnawave: {node}", Forms.ToolTipIcon.Info, tgKind: "updates");
            }
            _verSeeded = true;
            if (node != null) _latestNode = node;
            if (back != null) _latestBackend = back;

            if (_prev != null)
            {
                _prev = ApplyExtras(_prev);
                _win.Update(_prev);
            }
            RefreshChip();
        }
        catch { }
    }

    void RefreshChip()
    {
        var parts = new List<string>();
        string url = "https://github.com/remnawave/node/releases/latest";
        if (ReleaseCheck.Newer(_latestBackend, _panelVersion))
        {
            parts.Add($"панель {_panelVersion} → {_latestBackend}");
            url = "https://github.com/remnawave/backend/releases/latest";
        }
        int old = _prev?.Nodes.Count(n => n.VersionLevel == "warn") ?? 0;
        if (old > 0) parts.Add($"нод устарело: {old}");
        _win.SetUpdates(parts.Count > 0 ? "обновление: " + string.Join(" · ", parts) : null, url);
    }

    // Тревога только если проблема держится два обновления подряд (защита от разовых сбоев сети);
    // при запуске уже лежащие объекты не считаются новыми; о восстановлении сообщаем, если тревога была.
    void Notify(Snapshot s)
    {
        bool seed = _prev == null;
        // если с этого ПК недоступны ВСЕ ноды — скорее всего проблема у вас, а не у нод
        bool anyReachable = s.Nodes.Any(x => x.PingLevel is "ok" or "warn");
        foreach (var n in s.Nodes)
        {
            Track(n.Name + "|conn", n.Name, !n.Connected, seed, $"Нода {n.Name} не в сети", $"Нода {n.Name} снова в сети");
            if (n.Connected)
                Track(n.Name + "|xray", n.Name, !n.XrayOk, seed, $"Xray остановлен на {n.Name}", $"Xray на {n.Name} снова работает");
            Track(n.Name + "|reach", n.Name, n.Connected && n.PingLevel == "bad" && anyReachable, seed,
                $"Нода {n.Name} недоступна с вашего ПК (панель считает её рабочей)", $"Нода {n.Name} снова доступна с вашего ПК");
            Track(n.Name + "|bill", n.Name, n.BillingLevel == "crit", false, $"{n.Name}: {n.BillingText}", "");
        }
        SyncJournalUi();
    }

    void Track(string key, string subject, bool failing, bool seed, string alarm, string recovered)
    {
        int cur = _strikes.GetValueOrDefault(key);
        string severity = key.EndsWith("|cert") || key.EndsWith("|bill") ? "warn" : "bad";
        if (failing)
        {
            int next;
            if (seed)
            {
                next = 99;   // проблема уже была при запуске: в журнале есть, но красной иконкой не тревожит
                if (cur == 0) _log.Open(key, subject, alarm, severity, unread: false);
            }
            else
            {
                next = cur + 1;
                if (next == 2)
                {
                    _log.Open(key, subject, alarm, severity, unread: true);
                    Balloon(alarm, Forms.ToolTipIcon.Warning, incident: true, tgKind: TgKind(key), tgText: "🔴 " + alarm);
                }
            }
            _strikes[key] = next;
        }
        else
        {
            if (cur >= 2)
            {
                var downtime = _log.Resolve(key);
                if (recovered != "")
                    Balloon(recovered, Forms.ToolTipIcon.Info, tgKind: TgKind(key),
                        tgText: "🟢 " + recovered + (downtime is TimeSpan d ? $" (простой {IncidentLog.Dur(d)})" : ""));
            }
            _strikes.Remove(key);
        }
    }

    // ---- Telegram ----
    static string TgKind(string key) =>
        key.EndsWith("|down") || key.EndsWith("|cert") ? "services" : key.EndsWith("|bill") ? "billing" : "nodes";

    void Tg(string kind, string text)
    {
        bool allowed = kind switch
        {
            "nodes" => _s.TgNodes, "services" => _s.TgServices, "limits" => _s.TgLimits,
            "billing" => _s.TgBilling, "updates" => _s.TgUpdates, _ => true,
        };
        if (_s.TgEnabled && allowed) _tg.Enqueue(text);
    }

    string BuildStatus()
    {
        var s = _prev;
        if (s == null) return "Данных пока нет: программа ещё не получила ответ панели.";
        int online = s.Users.Count(u => u.IsOnline);
        int ok = s.Nodes.Count(n => n.Connected && n.XrayOk);
        var sb = new StringBuilder();
        sb.AppendLine($"📊 Онлайн: {online} · ноды {ok}/{s.Nodes.Count}" + (_svcTotal > 0 ? $" · сайты {_svcOk}/{_svcTotal}" : ""));
        foreach (var n in s.Nodes)
            sb.AppendLine($"{(n.Connected && n.XrayOk ? "✅" : "🔴")} {n.Name} · онлайн {n.UsersOnline}"
                          + (n.MemPctText != "" ? $" · RAM {n.MemPctText}" : "") + (n.PingText != "" ? $" · {n.PingText}" : ""));
        sb.Append($"Активных проблем: {_log.Active} · обновлено {s.At:HH:mm:ss}");
        return sb.ToString();
    }

    string BuildProblems()
    {
        var rows = _log.Rows().Where(r => r.Active).ToList();
        if (rows.Count == 0) return "✅ Активных проблем нет";
        return string.Join("\n", rows.Select(r => $"{(r.Level == "warn" ? "🟡" : "🔴")} {r.Subject}: {r.Text} ({r.Status})"));
    }

    // ---- журнал событий и цвет иконки ----
    // красная иконка — есть непрочитанные проблемы; зелёная — всё прочитано (или проблем нет)
    void UpdateIcon() => _tray.Icon = _log.Unread > 0 ? _bad : _ok;

    void SyncJournalUi()
    {
        UpdateIcon();
        _win?.SetJournalBadge(_log.Unread, _log.Active);
        _journal?.Refresh();
    }

    void AckAll()
    {
        _log.MarkAllRead();
        SyncJournalUi();
    }

    void OpenJournal()
    {
        if (_journal == null)
        {
            _journal = new IncidentsWindow(_log, AckAll);
            _journal.Closed += (_, _) => { _journal = null; AckAll(); };   // закрыл журнал — значит, посмотрел
        }
        _journal.Show();
        _journal.WindowState = WindowState.Normal;
        _journal.Activate();
    }

    void UpdateTray(Snapshot s)
    {
        int online = s.Users.Count(u => u.IsOnline);
        int ok = s.Nodes.Count(n => n.Connected && n.XrayOk);
        double ram = s.Nodes.Count > 0 ? s.Nodes.Max(n => n.MemPct) * 100 : 0;
        var text = $"онлайн {online} · ноды {ok}/{s.Nodes.Count}{(ok < s.Nodes.Count ? " ⚠" : "")} · RAM до {ram:0}%"
                   + (_svcTotal > 0 ? $" · сайты {_svcOk}/{_svcTotal}" : "");
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    // Лимит трафика ≥95% и подписка, истекающая в ближайшие 3 дня (или уже истёкшая): одно уведомление на событие
    void NotifyUsers(Snapshot s, bool seed)
    {
        var now = new Dictionary<string, string>();
        foreach (var u in s.Users)
        {
            if (u.TrafficLevel == "crit") now[$"{u.Username}|limit"] = $"{u.Username}: израсходовано {u.TrafficPct:P0} лимита трафика";
            if (u.ExpireLevel == "crit") now[$"{u.Username}|exp"] = $"{u.Username}: подписка {u.ExpireNote}";
        }
        if (!seed)
        {
            var fresh = now.Where(kv => !_alerted.Contains(kv.Key)).Select(kv => kv.Value).ToList();
            if (fresh.Count > 0)
                Balloon(string.Join("\n", fresh.Take(3)) + (fresh.Count > 3 ? $"\nи ещё {fresh.Count - 3}…" : ""),
                    Forms.ToolTipIcon.Warning, tgKind: "limits", tgText: "⚠️ " + string.Join("\n", fresh.Take(15)));
        }
        _alerted.Clear();
        foreach (var k in now.Keys) _alerted.Add(k);
    }

    void Balloon(string text, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Warning, bool incident = false,
        string? tgKind = null, string? tgText = null)
    {
        if (tgKind != null) Tg(tgKind, tgText ?? text);
        _balloonIncident = incident;
        _balloonShownAt = DateTime.Now;
        _tray.ShowBalloonTip(5000, "Remnawave", text, icon);
    }
}
