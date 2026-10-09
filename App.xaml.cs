using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
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
    readonly DispatcherTimer _sshTimer = new();
    readonly Dictionary<string, (ServerStats? stats, string error)> _srv = new();
    bool _sshBusy;
    readonly Dictionary<string, (double total, double idle, DateTime at)> _cpuPrev = new();
    readonly Dictionary<string, Queue<double>> _cpuHist = new();
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
    List<ServiceRow> _svcRows = new();
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
        _win.MutesChanged = () => { ReapplyExtras(); RefreshServicesView(); };

        _tg.Configure(_s);
        _tg.Handler = HandleTg;
        if (_s.MonitorSince == null) { _s.MonitorSince = DateTime.Now; _s.Save(); }
        _tgTimer.Tick += async (_, _) =>
        {
            // время «не беспокоить» вышло — режим выключается сам
            if (_s.TgDnd && !_s.TgDndActive) { _s.TgDnd = false; _s.TgDndUntil = null; _s.Save(); }
            UpdateDndChip();
            await _tg.TickAsync();
        };
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

        _sshTimer.Tick += async (_, _) => { await RefreshSsh(); await RefreshChecker(); };
        _sshTimer.Interval = TimeSpan.FromSeconds(60);
        _sshTimer.Start();

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
        _ = RefreshSsh();
        _ = RefreshChecker();
    }

    void ShowWin()
    {
        if (_win == null) return;
        _win.Show(); _win.WindowState = WindowState.Normal; _win.Activate();
    }

    void OpenSettings()
    {
        var nodes = _prev?.Nodes.Select(n => (n.Name, n.Address)).ToList() ?? new List<(string, string)>();
        var users = _prev?.Users.Select(u => (u.Username, u.SubscriptionUrl)).ToList() ?? new List<(string, string)>();
        var conns = _chkRaw?.Results.Select(r => r.Name).ToList() ?? new List<string>();
        var w = new SettingsWindow(_s, nodes, users, conns);
        if (w.ShowDialog() != true) return;
        _chk = Filtered(_chkRaw);
        UpdateVpnChip();
        _s.Save();
        _tg.Configure(_s);
        UpdateDndChip();
        _timer.Interval = TimeSpan.FromSeconds(_s.IntervalSec);
        _ = Refresh(true);
        _ = RefreshServices();
        _ = CheckAppUpdateAsync();
        _ = RefreshSsh();
        _ = RefreshChecker();
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
            _svcRows = rows;
            _svcOk = rows.Count(r => r.Level != "bad");
            _svcTotal = rows.Count;
            RefreshServicesView();
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
            var vpn = VpnForNode(n);
            ServerStats? st = null;
            string sshErr = "";
            if (_srv.TryGetValue(n.Name, out var sr)) { st = sr.stats; sshErr = sr.error ?? ""; }
            return n with
            {
                VersionText = string.Join(" · ", parts), VersionLevel = old ? "warn" : "", RuText = ru.Item1, RuLevel = ru.Item2,
                SrvCpu = st?.Cpu ?? -1, SrvRam = st?.Ram ?? -1, SrvDisk = st?.Disk ?? -1,
                SrvRamText = st?.RamText ?? "", SrvDiskText = st?.DiskText ?? "",
                SrvNote = sshErr != "" ? "ssh: " + sshErr : "",
                SrvCpuNote = st == null ? "" : CpuNote(st),
                VpnText = vpn.text, VpnLevel = vpn.level, VpnTip = vpn.tip,
                MutedText = MutedLabel(n.Name),
            };
        }).ToList();
        return s with { Nodes = nodes };
    }

    // Загрузка CPU, память и диск серверов нод по SSH (раз в минуту, все серверы параллельно)
    async Task RefreshSsh()
    {
        if (_sshBusy) return;
        var list = _s.SshServers.Where(x => x.Enabled && x.Host != "" && x.Password != "").ToList();
        foreach (var k in _srv.Keys.Where(k => list.All(x => x.Node != k)).ToList()) _srv.Remove(k);
        if (list.Count == 0) { ReapplyExtras(); return; }

        _sshBusy = true;
        try
        {
            var results = await Task.WhenAll(list.Select(async srv =>
            {
                try
                {
                    var (st, key) = await SshStats.CollectAsync(srv.Clone());
                    return new SshResult(srv, st, key, "");
                }
                catch (Exception ex) { return new SshResult(srv, null, "", ex.Message); }
            }));

            bool save = false;
            foreach (var r in results)
            {
                _srv[r.Srv.Node] = (r.Stats == null ? null : AverageCpu(r.Srv.Node, r.Stats), r.Error);
                if (r.Stats != null && r.Srv.HostKey == "" && r.Key != "") { r.Srv.HostKey = r.Key; save = true; }   // запоминаем ключ сервера
            }
            if (save) _s.Save();
            ReapplyExtras();
        }
        finally { _sshBusy = false; }
    }

    record SshResult(SshServer Srv, ServerStats? Stats, string Key, string Error);

    // ---- xray-чекер (сервер в РФ): результат забирается по SSH раз в минуту ----
    CheckerReport? _chk, _chkRaw;

    // Подключения, снятые с проверки в настройках, скрываются и не дают тревог
    CheckerReport? Filtered(CheckerReport? rep) =>
        rep == null ? null : rep with { Results = rep.Results.Where(r => !_s.CheckerExcluded.Contains(r.Name)).ToList() };
    string _chkError = "";
    bool _chkBusy, _chkSeeded;
    readonly HashSet<string> _vpnKeys = new();
    readonly Dictionary<string, (IPAddress[] ips, DateTime at)> _dns = new();

    bool CheckerOn => _s.CheckerEnabled && _s.Checker.Host != "" && _s.Checker.Password != "";

    async Task RefreshChecker()
    {
        if (_chkBusy) return;
        if (!CheckerOn)
        {
            _chk = null; _chkError = "";
            ReapplyExtras(); UpdateVpnChip();
            return;
        }
        _chkBusy = true;
        try
        {
            var srv = _s.Checker;
            var (rep, key) = await Checker.FetchAsync(srv.Clone());
            if (srv.HostKey == "" && key != "") { srv.HostKey = key; _s.Save(); }
            _chkError = "";
            Track("checker|ssh", "xray-чекер", false, false, "", "Связь с xray-чекером восстановлена");
            bool fresh = rep != null && rep.CheckedAt != _chk?.CheckedAt;
            _chkRaw = rep;
            rep = Filtered(rep);
            _chk = rep;
            if (rep != null)
            {
                await ResolveHosts(rep.Results.Select(r => r.Address).Concat(_prev?.Nodes.Select(n => n.Address) ?? Enumerable.Empty<string>()));
                if (fresh) TrackVpn(rep, seed: !_chkSeeded);
                _chkSeeded = true;
                var age = DateTime.UtcNow - rep.CheckedAt;
                Track("checker|stale", "xray-чекер", age > TimeSpan.FromMinutes(Math.Max(15, _s.CheckerIntervalMin * 3)), false,
                    $"Xray-чекер давно не обновлял результат (последний {Ago(rep.CheckedAt)})", "Xray-чекер снова присылает результаты");
            }
        }
        catch (Exception ex)
        {
            _chkError = ex.Message;
            Track("checker|ssh", "xray-чекер", true, false, $"Нет связи с xray-чекером: {ex.Message}", "Связь с xray-чекером восстановлена");
        }
        finally { _chkBusy = false; }
        ReapplyExtras();
        UpdateVpnChip();
        SyncJournalUi();
    }

    // Тревога по каждому подключению — только на новом результате (иначе один и тот же отчёт считался бы несколько раз)
    void TrackVpn(CheckerReport rep, bool seed)
    {
        Track("checker|error", "xray-чекер", rep.Error != "", seed, $"Xray-чекер: {rep.Error}", "Xray-чекер снова работает");
        var now = new HashSet<string>();
        foreach (var r in rep.Results.Where(r => r.Ok != null))
        {
            var key = $"vpn:{r.Name}|vpn";
            now.Add(key);
            Track(key, r.Name, r.Ok == false, seed, $"«{r.Name}» не работает из РФ: {r.Error}", $"«{r.Name}» снова работает из РФ");
        }
        // сайты: одна тревога на сайт, если он не открывается хотя бы через одно рабочее подключение.
        // Подключения нод, у которых «сайты через VPN» отключены, в тревоге не участвуют.
        foreach (var g in rep.Results.Where(r => r.Ok == true && !SitesMuted(r)).SelectMany(r => r.Sites.Select(s => (conn: r.Name, s)))
                     .GroupBy(x => x.s.Name))
        {
            var key = $"site:{g.Key}|vpnsite";
            now.Add(key);
            var bad = g.Where(x => !x.s.Ok).ToList();
            var all = bad.Count == g.Count();
            Track(key, g.Key, bad.Count > 0, seed,
                $"{g.Key} не открывается через VPN из РФ ({(all ? "ни через одно подключение" : string.Join(", ", bad.Select(x => x.conn)))}): {bad.FirstOrDefault().s?.Error}",
                $"{g.Key} снова открывается через VPN из РФ");
        }
        foreach (var gone in _vpnKeys.Where(k => !now.Contains(k)).ToList()) Track(gone, gone, false, false, "", "");
        _vpnKeys.Clear();
        foreach (var k in now) _vpnKeys.Add(k);
    }

    async Task ResolveHosts(IEnumerable<string> hosts)
    {
        foreach (var h in hosts.Where(h => h != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (_dns.TryGetValue(h.ToLowerInvariant(), out var c) && DateTime.Now - c.at < TimeSpan.FromMinutes(30)) continue;
            IPAddress[] ips;
            try { ips = IPAddress.TryParse(h, out var ip) ? new[] { ip } : await Dns.GetHostAddressesAsync(h); }
            catch { ips = Array.Empty<IPAddress>(); }
            _dns[h.ToLowerInvariant()] = (ips, DateTime.Now);
        }
    }

    // Подключение относится к ноде, если адрес совпадает (в том числе по IP) или в названии подключения есть имя ноды
    bool SameNode(VpnCheck r, NodeRow n)
    {
        if (r.Address.Equals(n.Address, StringComparison.OrdinalIgnoreCase) && r.Address != "") return true;
        if (n.Name.Length > 2 && r.Name.Contains(n.Name, StringComparison.OrdinalIgnoreCase)) return true;
        if (_dns.TryGetValue(r.Address.ToLowerInvariant(), out var a) && _dns.TryGetValue(n.Address.ToLowerInvariant(), out var b))
            return a.ips.Any(x => b.ips.Contains(x));
        return false;
    }

    static string SitesShort(VpnCheck r) =>
        r.Sites.Count == 0 ? "" : r.Sites.All(s => s.Ok) ? $" · сайты {r.Sites.Count}/{r.Sites.Count}"
        : " · не открываются: " + string.Join(", ", r.Sites.Where(s => !s.Ok).Select(s => s.Name));

    static string VpnLine(VpnCheck r) =>
        r.Ok == true ? $"✓ {r.Name} {r.Ms} мс{SitesShort(r)}" : r.Ok == false ? $"✗ {r.Name}" : $"— {r.Name}";

    static string VpnDetail(VpnCheck r) =>
        (r.Ok == true ? $"✓ {r.Name} — отклик {r.Ms} мс" + (r.ConnectMs is int c && c != r.Ms ? $", первое подключение {c} мс" : "") + $" ({r.Network}/{r.Security})"
         : r.Ok == false ? $"✗ {r.Name} — {r.Error}"
         : $"— {r.Name} — {r.Error}")
        + string.Concat(r.Sites.Select(s => s.Ok ? $"\n     ✓ {s.Name} {s.Ms} мс" : $"\n     ✗ {s.Name} — {s.Error}"));

    // ok — всё работает; warn — часть подключений или сайтов не работает; bad — не работает ни одно подключение
    static string Level(IEnumerable<VpnCheck> list)
    {
        var l = list.Where(r => r.Ok != null).ToList();
        if (l.Count == 0) return "none";
        if (l.All(r => r.Ok == false)) return "bad";
        return l.All(r => r.Ok == true && r.Sites.All(s => s.Ok)) ? "ok" : "warn";
    }

    (string text, string level, string tip) VpnForNode(NodeRow n)
    {
        if (!CheckerOn || _chk == null) return ("", "", "");
        var list = _chk.Results.Where(r => SameNode(r, n)).ToList();
        if (list.Count == 0) return ("", "", "");
        var tip = string.Join("\n", list.Select(VpnDetail)) + $"\nпроверено {Ago(_chk.CheckedAt)} с сервера {_chk.Server}";
        return ("VPN из РФ:  " + string.Join("   ", list.Select(VpnLine)), Level(list), tip);
    }

    static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        return d.TotalMinutes < 1 ? "только что" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} мин назад" : $"{(int)d.TotalHours} ч {d.Minutes} мин назад";
    }

    void UpdateVpnChip()
    {
        if (!CheckerOn) { _win.SetVpn(null, "", ""); return; }
        if (_chkError != "") { _win.SetVpn("VPN из РФ: нет связи", "bad", "xray-чекер: " + _chkError); return; }
        if (_chk == null) { _win.SetVpn("VPN из РФ: ждём результат", "none", "чекер на сервере ещё не выполнил первую проверку"); return; }
        if (_chk.Error != "") { _win.SetVpn("VPN из РФ: ошибка", "bad", _chk.Error); return; }
        var l = _chk.Results.Where(r => r.Ok != null).ToList();
        var tip = string.Join("\n", _chk.Results.Select(VpnDetail)) + $"\n\nпроверено {Ago(_chk.CheckedAt)} с сервера {_chk.Server}";
        _win.SetVpn($"VPN из РФ {l.Count(r => r.Ok == true)}/{l.Count}", Level(l), tip);
    }

    string BuildVpn()
    {
        if (!CheckerOn) return "🛡 Xray-чекер не настроен (настройки → xray-чекер).";
        if (_chkError != "") return "🛡 Нет связи с xray-чекером: " + _chkError;
        if (_chk == null) return "🛡 Чекер ещё не выполнил первую проверку.";
        var sb = new StringBuilder();
        sb.AppendLine($"🛡 VPN из РФ — сервер {_chk.Server}, проверено {Ago(_chk.CheckedAt)}");
        if (_chk.Error != "") sb.AppendLine("🔴 " + _chk.Error);
        foreach (var r in _chk.Results)
        {
            sb.AppendLine(r.Ok == true ? $"✅ {r.Name} — {r.Ms} мс" : r.Ok == false ? $"🔴 {r.Name} — {r.Error}" : $"⚪ {r.Name} — {r.Error}");
            if (r.Sites.Count > 0)
                sb.AppendLine("     " + string.Join(" · ", r.Sites.Select(s => s.Ok ? $"✓ {s.Name}" : $"✗ {s.Name} ({s.Error})")));
        }
        return sb.ToString().TrimEnd();
    }

    // Загрузка CPU — среднее за время между опросами (около минуты) по накопительным счётчикам /proc/stat.
    // Короткий замер скрипта на многоядерных серверах слишком грубый: при слабой нагрузке он прыгает и часто даёт 0.
    ServerStats AverageCpu(string node, ServerStats st)
    {
        var now = DateTime.Now;
        if (!st.Legacy)
        {
            var res = st;
            if (_cpuPrev.TryGetValue(node, out var p) && st.StatTotal > p.total && now - p.at < TimeSpan.FromMinutes(10))
                res = st with
                {
                    Cpu = Math.Clamp(1 - (st.StatIdle - p.idle) / (st.StatTotal - p.total), 0, 1),
                    CpuWindowSec = (int)Math.Round((now - p.at).TotalSeconds),
                };
            _cpuPrev[node] = (st.StatTotal, st.StatIdle, now);   // при перезагрузке сервера счётчики сбросятся — возьмём короткий замер
            return res;
        }

        // старый скрипт на сервере (без счётчиков): сглаживаем по последним 5 замерам
        if (!_cpuHist.TryGetValue(node, out var q)) _cpuHist[node] = q = new Queue<double>();
        if (st.Cpu >= 0) { q.Enqueue(st.Cpu); while (q.Count > 5) q.Dequeue(); }
        return q.Count > 0 ? st with { Cpu = q.Average(), CpuWindowSec = q.Count > 1 ? -q.Count : 0 } : st;
    }

    static string CpuNote(ServerStats st) =>
        st.Legacy
            ? "на сервере старый скрипт — выполните команду установки ещё раз (настройки → серверы)" +
              (st.CpuWindowSec < 0 ? $" · среднее {-st.CpuWindowSec} замеров" : "")
            : st.CpuWindowSec > 0 ? $"среднее за {st.CpuWindowSec} с" : "замер за 2 с";

    void ReapplyExtras()
    {
        if (_prev == null) return;
        _prev = ApplyExtras(_prev);
        _win.Update(_prev);
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
        bool muted = IsMutedKey(key);   // отключено пользователем: в журнал пишем, но без всплывающих, Telegram и красной иконки
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
                    _log.Open(key, subject, alarm + (muted ? " (уведомления отключены)" : ""), severity, unread: !muted);
                    if (!muted) Balloon(alarm, Forms.ToolTipIcon.Warning, incident: true, tgKind: TgKind(key), tgText: "🔴 " + alarm);
                }
            }
            _strikes[key] = next;
        }
        else
        {
            if (cur >= 2)
            {
                var downtime = _log.Resolve(key);
                if (recovered != "" && !muted)
                    Balloon(recovered, Forms.ToolTipIcon.Info, tgKind: TgKind(key),
                        tgText: "🟢 " + recovered + (downtime is TimeSpan d ? $" (простой {IncidentLog.Dur(d)})" : ""));
            }
            _strikes.Remove(key);
        }
    }

    // ---- Telegram: ответы на команды (только чтение) ----
    const string NoData = "Данных пока нет: программа ещё не получила ответ панели.";

    static string Flag(string cc) =>
        cc.Length == 2 && cc.All(char.IsLetter)
            ? string.Concat(cc.ToUpperInvariant().Select(c => char.ConvertFromUtf32(0x1F1E6 + (c - 'A')))) + " "
            : "";

    string? HandleTg(string cmd, string arg) => cmd switch
    {
        "/status" => BuildStatus(),
        "/problems" => BuildProblems(),
        "/nodes" => BuildNodes(),
        "/users" => BuildUsers(),
        "/services" => BuildServices(),
        "/version" => BuildVersion(),
        "/vpn" => BuildVpn(),
        _ => null,
    };

    string BuildNodes()
    {
        var s = _prev;
        if (s == null) return NoData;
        var sb = new StringBuilder();
        sb.AppendLine($"🖥 Ноды ({s.Nodes.Count})");
        foreach (var n in s.Nodes)
        {
            sb.AppendLine();
            sb.AppendLine($"{(n.Connected && n.XrayOk ? "✅" : "🔴")} {Flag(n.Country)}{n.Name} · онлайн {n.UsersOnline}");
            var l2 = (n.SrvCpu >= 0 || n.SrvRam >= 0 || n.SrvDisk >= 0
                    ? new[] { $"CPU {n.CpuPctText}", $"RAM {n.RamPctText}", $"диск {n.DiskPctText}", n.Load }
                    : new[] { n.MemPctText != "" ? $"RAM {n.MemPctText} ({n.MemText})" : "", n.Cpu != "—" ? n.Cpu : "", n.Load })
                .Where(x => x != "");
            var l3 = new[] { n.Down, n.Up, n.Xray }.Where(x => x != "");
            var l5 = new[] { n.PingText, n.RuText }.Where(x => x != "");
            foreach (var line in new[] { string.Join(" · ", l2), string.Join(" · ", l3),
                         n.TrafficText != "—" ? "трафик " + n.TrafficText : "", string.Join(" · ", l5), n.BillingText })
                if (line != "") sb.AppendLine("   " + line);
        }
        return sb.ToString().TrimEnd();
    }

    string BuildUsers()
    {
        var s = _prev;
        if (s == null) return NoData;
        var online = s.Users.Where(u => u.IsOnline).ToList();
        if (online.Count == 0) return $"👥 Сейчас никого онлайн (всего пользователей: {s.Users.Count})";
        var sb = new StringBuilder();
        sb.AppendLine($"👥 Онлайн: {online.Count} из {s.Users.Count}");
        foreach (var u in online) sb.AppendLine($"• {u.Username} — {Flag(u.LastNodeCountry)}{u.LastNode}");
        return sb.ToString().TrimEnd();
    }

    string BuildTop()
    {
        var s = _prev;
        if (s == null) return NoData;
        var sb = new StringBuilder();
        sb.AppendLine("📈 Топ-10 по трафику");
        int i = 1;
        foreach (var u in s.Users.OrderByDescending(x => x.TrafficBytes).Take(10))
            sb.AppendLine($"{i++}. {u.Username} — {J.Bytes(u.TrafficBytes)} ({Flag(u.LastNodeCountry)}{u.LastNode})");
        return sb.ToString().TrimEnd();
    }

    string BuildExpiring()
    {
        var s = _prev;
        if (s == null) return NoData;
        var list = s.Users.Where(u => u.ExpireLevel != "" || u.TrafficLevel != "ok")
            .OrderByDescending(u => u.ExpireLevel == "crit" || u.TrafficLevel == "crit" ? 2 : 1).ThenBy(u => u.Username).ToList();
        if (list.Count == 0) return "✅ Никто не упирается в лимит, подписки не истекают в ближайшие 7 дней";
        var sb = new StringBuilder();
        sb.AppendLine("⏳ Истекает или упирается в лимит");
        foreach (var u in list)
        {
            var parts = new List<string>();
            if (u.ExpireLevel != "") parts.Add($"подписка {u.ExpireNote}");
            if (u.TrafficLevel != "ok") parts.Add($"трафик {u.TrafficPct:P0} лимита");
            sb.AppendLine($"{(u.ExpireLevel == "crit" || u.TrafficLevel == "crit" ? "🔴" : "🟡")} {u.Username}: {string.Join(", ", parts)}");
        }
        return sb.ToString().TrimEnd();
    }

    string BuildServices()
    {
        if (_svcRows.Count == 0) return "Список сайтов пуст или проверка ещё не выполнялась.";
        var sb = new StringBuilder();
        sb.AppendLine($"🌐 Сайты: {_svcRows.Count(r => r.Level != "bad")}/{_svcRows.Count} отвечают");
        foreach (var r in _svcRows)
            sb.AppendLine($"{(r.Level == "ok" ? "✅" : r.Level == "warn" ? "🟡" : "🔴")} {r.Name} · {r.StatusText}"
                          + (r.Time != "—" ? $" · {r.Time}" : "") + (r.Cert != "" ? $" · {r.Cert}" : ""));
        return sb.ToString().TrimEnd();
    }

    string BuildUser(string q)
    {
        var s = _prev;
        if (s == null) return NoData;
        q = q.Trim();
        var exact = s.Users.FirstOrDefault(u => u.Username.Equals(q, StringComparison.OrdinalIgnoreCase));
        var found = exact != null ? new List<UserRow> { exact }
            : s.Users.Where(u => u.Username.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(9).ToList();
        if (found.Count == 0) return $"Пользователь «{q}» не найден";
        if (found.Count > 1)
        {
            var sb2 = new StringBuilder();
            sb2.AppendLine("Найдено несколько, уточните имя:");
            foreach (var f in found) sb2.AppendLine("• " + f.Username);
            return sb2.ToString().TrimEnd();
        }
        var u = found[0];
        var sb = new StringBuilder();
        sb.AppendLine($"👤 {u.Username}");
        sb.AppendLine($"Статус: {u.Status} · " + (u.IsOnline ? "онлайн сейчас" : u.Online == "—" ? "активности не было" : $"был онлайн {u.Online}"));
        sb.AppendLine("Трафик: " + u.Traffic + (u.HasLimit ? $" ({u.TrafficPct:P0})" : ""));
        if (u.ExpireNote != "") sb.AppendLine("Подписка: " + u.ExpireNote);
        sb.Append($"Последняя нода: {Flag(u.LastNodeCountry)}{u.LastNode}");
        return sb.ToString();
    }

    string BuildBilling()
    {
        var s = _prev;
        if (s == null) return NoData;
        var rows = s.Nodes.Where(n => n.BillingText != "")
            .OrderBy(n => n.BillingLevel == "crit" ? 0 : n.BillingLevel == "warn" ? 1 : 2).ToList();
        if (rows.Count == 0) return "💳 Дат оплаты нет: биллинг в панели не заполнен.";
        var sb = new StringBuilder();
        sb.AppendLine("💳 Оплата серверов");
        foreach (var n in rows)
            sb.AppendLine($"{(n.BillingLevel == "crit" ? "🔴" : n.BillingLevel == "warn" ? "🟡" : "🟢")} {n.Name}: {n.BillingText}");
        return sb.ToString().TrimEnd();
    }

    string BuildVersion()
    {
        var sb = new StringBuilder();
        sb.AppendLine("ℹ️ Версии");
        sb.AppendLine($"Панель: {_panelVersion ?? "неизвестна"}"
            + (ReleaseCheck.Newer(_latestBackend, _panelVersion) ? $" → доступна {_latestBackend}" : _latestBackend != null ? " (актуальна)" : ""));
        if (_prev != null)
            foreach (var n in _prev.Nodes.Where(x => x.VersionText != "")) sb.AppendLine($"• {n.Name}: {n.VersionText}");
        sb.Append($"Программа: {Updater.CurrentText}" + (_appUpdate != null ? $" → доступна {_appUpdate.Version}" : ""));
        return sb.ToString();
    }

    // Аптайм считается по журналу событий: падения короче ~1 минуты не фиксируются, время, когда программа была закрыта, не учитывается
    string BuildUptime()
    {
        var now = DateTime.Now;
        var since = _s.MonitorSince ?? now;

        string Line(string name, string[] suffixes)
        {
            var parts = new List<string>();
            foreach (var days in new[] { 7, 30 })
            {
                var from = now.AddDays(-days);
                if (from < since) from = since;
                var span = now - from;
                if (span < TimeSpan.FromHours(1)) { parts.Add($"{days} дн: мало данных"); continue; }
                var (down, _) = _log.Downtime(name, suffixes, from);
                double pct = Math.Max(0, 100 - down.TotalSeconds / span.TotalSeconds * 100);
                parts.Add($"{days} дн: {pct:0.##}%");
            }
            var from30 = now.AddDays(-30) < since ? since : now.AddDays(-30);
            var (_, falls) = _log.Downtime(name, suffixes, from30);
            return $"{name} — {string.Join(" · ", parts)}" + (falls > 0 ? $" · падений: {falls}" : "");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"🕒 Аптайм (мониторинг с {since:dd.MM.yyyy})");
        if (_prev != null)
        {
            sb.AppendLine("Ноды");
            foreach (var n in _prev.Nodes) sb.AppendLine("• " + Line(n.Name, new[] { "|conn", "|xray" }));
        }
        if (_svcRows.Count > 0)
        {
            sb.AppendLine("Сайты");
            foreach (var r in _svcRows) sb.AppendLine("• " + Line(r.Name, new[] { "|down" }));
        }
        return sb.ToString().TrimEnd();
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
        if (_s.TgEnabled && allowed && !_s.TgSilentNow) _tg.Enqueue(text);   // в режиме «не беспокоить» тревоги не уходят, ответы на команды — уходят
    }

    // Индикатор в шапке: «☾ тихо до 08:00» — пока уведомления в Telegram не отправляются
    void UpdateDndChip()
    {
        var until = _s.TgEnabled ? _s.SilentUntil(DateTime.Now) : null;
        _win?.SetDnd(until is DateTime u ? "☾ тихо до " + Settings.WhenText(u) : null);
    }

    string BuildStatus()
    {
        var s = _prev;
        if (s == null) return "Данных пока нет: программа ещё не получила ответ панели.";
        int online = s.Users.Count(u => u.IsOnline);
        int ok = s.Nodes.Count(n => n.Connected && n.XrayOk);
        var sb = new StringBuilder();
        sb.AppendLine($"📊 Онлайн: {online} · ноды {ok}/{s.Nodes.Count}" + (_svcTotal > 0 ? $" · сайты {_svcOk}/{_svcTotal}" : ""));
        if (_s.SilentUntil(DateTime.Now) is DateTime su)
            sb.AppendLine(su == DateTime.MaxValue ? "🔕 Не беспокоить: тихие часы на весь день" : $"🔕 Не беспокоить до {Settings.WhenText(su)}");
        foreach (var n in s.Nodes)
            sb.AppendLine($"{(n.Connected && n.XrayOk ? "✅" : "🔴")} {n.Name} · онлайн {n.UsersOnline}"
                          + (n.SrvCpu >= 0 ? $" · CPU {n.CpuPctText}" : "") + (n.RamPctText != "—" ? $" · RAM {n.RamPctText}" : "") + (n.PingText != "" ? $" · {n.PingText}" : ""));
        if (CheckerOn && _chk != null && _chk.Error == "")
        {
            var l = _chk.Results.Where(r => r.Ok != null).ToList();
            sb.AppendLine($"🛡 VPN из РФ: {l.Count(r => r.Ok == true)}/{l.Count} работают");
        }
        sb.Append($"Активных проблем: {_log.Active} · обновлено {s.At:HH:mm:ss}");
        return sb.ToString();
    }

    string BuildProblems()
    {
        var rows = _log.Rows().Where(r => r.Active).ToList();
        if (rows.Count == 0) return "✅ Активных проблем нет";
        return string.Join("\n", rows.Select(r => $"{(r.Level == "warn" ? "🟡" : "🔴")} {r.Subject}: {r.Text} ({r.Status})"));
    }

    void RefreshServicesView() =>
        _win.UpdateServices(_svcRows.Select(r => r with { Muted = _s.IsMuted("site:" + r.Url, "site") ? "🔕 уведомления выкл" : "" }).ToList());

    // ---- отключённые уведомления ----
    bool IsMutedKey(string key)
    {
        int bar = key.LastIndexOf('|');
        if (bar < 0) return false;
        var head = key[..bar];
        var kind = key[(bar + 1)..];
        switch (kind)
        {
            case "conn": case "xray": return _s.IsMuted("node:" + head, "node");
            case "reach": return _s.IsMuted("node:" + head, "reach");
            case "ru": return _s.IsMuted("node:" + head, "ru");
            case "bill": return _s.IsMuted("node:" + head, "bill");
            case "down": case "cert": return _s.IsMuted("site:" + head, "site");
            case "vpn":
                // подключение из xray-чекера отключено, если отключён VPN у ноды, к которой оно относится
                var name = head.StartsWith("vpn:") ? head[4..] : head;
                var r = _chk?.Results.FirstOrDefault(x => x.Name == name);
                return r != null && _prev != null && _prev.Nodes.Any(n => SameNode(r, n) && _s.IsMuted("node:" + n.Name, "vpn"));
            default: return false;
        }
    }

    bool SitesMuted(VpnCheck r) =>
        _prev != null && _prev.Nodes.Any(n => SameNode(r, n) && _s.IsMuted("node:" + n.Name, "sites"));

    string MutedLabel(string nodeName)
    {
        var off = Settings.NodeMuteCats.Where(c => _s.IsMuted("node:" + nodeName, c.cat)).ToList();
        if (off.Count == 0) return "";
        return off.Count == Settings.NodeMuteCats.Length ? "🔕 уведомления выкл" : "🔕 " + string.Join(", ", off.Select(c => c.shortLabel));
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
