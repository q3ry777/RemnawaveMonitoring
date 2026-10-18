using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

public static class Program
{
    public static async Task<int> Main()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("ru-RU");
        var cfg = BotConfig.Load();
        Log.Write($"Remnawave bot {Bot.Version} · панель {cfg.BaseUrl} · часовой пояс {TimeZoneInfo.Local.Id}");
        var problems = cfg.Problems();
        if (problems.Count > 0)
        {
            Log.Write("ошибка настройки: " + string.Join("; ", problems) + ". Проверьте .env и выполните docker compose up -d");
            await Task.Delay(TimeSpan.FromMinutes(1));   // чтобы docker не перезапускал контейнер слишком часто
            return 1;
        }
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
        await new Bot(cfg).RunAsync(cts.Token);
        return 0;
    }
}

public class Bot
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    readonly BotConfig _cfg;
    readonly Telegram _tg;
    readonly Api _api = new();
    readonly BotState _state;
    readonly IncidentLog _log;
    readonly Dictionary<string, int> _strikes = new();
    readonly object _lock = new();
    Snapshot? _snap;
    string _panelError = "";
    DateTime _panelErrorSince;
    string? _panelVersion;

    public Bot(BotConfig cfg)
    {
        _cfg = cfg;
        _tg = new Telegram(cfg) { Handler = HandleAsync };
        _state = BotState.Load(cfg.DataDir);
        // открытые проблемы сохраняются между перезапусками: по ним уже была тревога, повторно не шлём
        _log = IncidentLog.Load(cfg.DataDir, closeActive: false);
        foreach (var k in _log.ActiveKeys) _strikes[k] = 2;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_state.Signature != _cfg.Signature)
        {
            _tg.Send($"✅ Remnawave bot {Version} запущен на сервере с панелью. Меню с кнопками — внизу.\n\n{Telegram.Help}");
            _state.Signature = _cfg.Signature;
            Save();
        }
        await RefreshAsync();   // команды начинаем принимать, когда уже есть данные панели
        var tg = Task.Run(() => _tg.RunAsync(ct), ct);
        var rel = Task.Run(() => ReleasesLoopAsync(ct), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(_cfg.IntervalSec), ct); }
            catch (OperationCanceledException) { break; }
            await RefreshAsync();
        }
        Save();
        Log.Write("остановлен");
    }

    void Save() { lock (_lock) _state.Save(_cfg.DataDir); }

    // ---- опрос панели ----
    async Task RefreshAsync()
    {
        try
        {
            var (u, n, m, b) = await _api.FetchAsync(_cfg);
            var snap = SnapshotBuilder.Build(u, n, _cfg, m, b);
            if (_panelError != "") Log.Write("связь с панелью восстановлена");
            _panelError = "";
            lock (_lock)
            {
                bool seed = !_state.Seeded;
                foreach (var node in snap.Nodes)
                {
                    Track(node.Name + "|conn", node.Name, !node.Connected, seed,
                        $"Нода {node.Name} не в сети", $"Нода {node.Name} снова в сети", _cfg.NotifyNodes);
                    Track(node.Name + "|bill", node.Name, node.BillingLevel == "crit", seed,
                        $"{node.Name}: {node.BillingText}", "", _cfg.NotifyBilling);
                }
                // ноды, которых больше нет в панели, — закрываем их проблемы без сообщений
                foreach (var key in _strikes.Keys.Where(k => snap.Nodes.All(x => x.Name + "|conn" != k && x.Name + "|bill" != k)).ToList())
                {
                    _log.Resolve(key);
                    _strikes.Remove(key);
                }
                NotifyUsers(snap, seed);
                _snap = snap;
                if (seed) { _state.Seeded = true; _state.Save(_cfg.DataDir); }
            }
        }
        catch (Exception ex)
        {
            // Панель на этом же сервере: о её недоступности сообщает программа на ПК, здесь — только журнал контейнера
            if (_panelError == "") { _panelErrorSince = DateTime.Now; Log.Write("нет связи с панелью: " + ex.Message); }
            _panelError = ex.Message;
        }
    }

    // Тревога, только если проблема держится два опроса подряд; о восстановлении — если тревога была
    void Track(string key, string subject, bool failing, bool seed, string alarm, string recovered, bool notify)
    {
        int cur = _strikes.GetValueOrDefault(key);
        string severity = key.EndsWith("|bill") ? "warn" : "bad";
        if (failing)
        {
            int next;
            if (seed)
            {
                next = 99;   // самый первый запуск: то, что уже лежит, — в журнал без тревоги
                if (cur == 0) _log.Open(key, subject, alarm, severity, unread: false);
            }
            else
            {
                next = Math.Min(cur + 1, 99);
                if (next == 2)
                {
                    _log.Open(key, subject, alarm, severity, unread: true);
                    Log.Write("тревога: " + alarm);
                    if (notify) Alert((severity == "warn" ? "🟡 " : "🔴 ") + alarm);
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
                {
                    Log.Write("восстановлено: " + recovered);
                    if (notify) Alert("🟢 " + recovered + (downtime is TimeSpan d ? $" (простой {IncidentLog.Dur(d)})" : ""));
                }
            }
            _strikes.Remove(key);
        }
    }

    // Лимит трафика ≥95% и подписка, истекающая в ближайшие 3 дня (или уже истёкшая): одно сообщение на событие
    void NotifyUsers(Snapshot s, bool seed)
    {
        var now = new Dictionary<string, string>();
        foreach (var u in s.Users)
        {
            if (u.TrafficLevel == "crit") now[$"{u.Username}|limit"] = $"{u.Username}: израсходовано {u.TrafficPct:P0} лимита трафика";
            if (u.ExpireLevel == "crit") now[$"{u.Username}|exp"] = $"{u.Username}: подписка {u.ExpireNote}";
        }
        var fresh = now.Where(kv => !_state.Alerted.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        if (!seed && fresh.Count > 0 && _cfg.NotifyLimits)
            Alert("⚠️ " + string.Join("\n", fresh.Take(15)) + (fresh.Count > 15 ? $"\nи ещё {fresh.Count - 15}…" : ""));
        var keys = now.Keys.OrderBy(k => k).ToList();
        if (!keys.SequenceEqual(_state.Alerted.OrderBy(k => k)))
        {
            _state.Alerted = keys;
            _state.Save(_cfg.DataDir);
        }
    }

    // ---- тишина: «не беспокоить» до времени и тихие часы ----
    // null — сейчас можно; DateTime.MaxValue — тихие часы на все сутки
    DateTime? SilentUntil(DateTime now)
    {
        var t = now;
        bool silent = false;
        for (int i = 0; i < 60; i++)
        {
            if (_state.DndUntil is DateTime u && t < u) { t = u; silent = true; continue; }
            if (_cfg.QuietHours.Contains(t.Hour))
            {
                if (_cfg.QuietHours.Count >= 24) return DateTime.MaxValue;
                t = t.Date.AddHours(t.Hour + 1);
                silent = true;
                continue;
            }
            break;
        }
        return silent ? t : null;
    }

    static string When(DateTime t)
    {
        if (t == DateTime.MaxValue) return "всегда";
        var d = (t.Date - DateTime.Today).Days;
        return d == 0 ? $"{t:HH:mm}" : d == 1 ? $"{t:HH:mm} завтра" : $"{t:HH:mm} {t:dd.MM}";
    }

    // Тревоги в тишину не уходят (остаются в /problems); ответы на команды приходят всегда
    void Alert(string text)
    {
        if (SilentUntil(DateTime.Now) is DateTime until) { Log.Write($"тихо до {When(until)} — не отправлено: {text}"); return; }
        _tg.Send(text);
    }

    // ---- новые версии Remnawave (раз в 6 часов) ----
    async Task ReleasesLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await CheckReleasesAsync(); }
            catch (Exception ex) { Log.Write("проверка релизов: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromHours(6), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task CheckReleasesAsync()
    {
        var node = await ReleaseCheck.LatestAsync("remnawave/node");
        var back = await ReleaseCheck.LatestAsync("remnawave/backend");
        await RefreshPanelVersion();
        lock (_lock)
        {
            bool first = _state.LatestNode == null && _state.LatestBackend == null;
            if (!first && _cfg.NotifyUpdates)
            {
                if (back != null && back != _state.LatestBackend && ReleaseCheck.Newer(back, _panelVersion))
                    Alert($"⬆️ Вышла новая версия Remnawave: {back} (у вас {_panelVersion ?? "?"})");
                if (node != null && node != _state.LatestNode && (_snap?.Nodes.Any(x => ReleaseCheck.Newer(node, x.NodeVersion)) ?? false))
                    Alert($"⬆️ Вышла новая версия ноды Remnawave: {node}");
            }
            if (node != null) _state.LatestNode = node;
            if (back != null) _state.LatestBackend = back;
            _state.Save(_cfg.DataDir);
        }
    }

    async Task RefreshPanelVersion()
    {
        var meta = await _api.TryGetAsync(_cfg, "/api/system/metadata");
        if (meta == "") return;
        try
        {
            using var d = JsonDocument.Parse(meta);
            var v = J.S(d.RootElement, "response", "version");
            if (v != "") _panelVersion = v;
        }
        catch { }
    }

    // ---- команды ----
    const string NoData = "Данных пока нет: бот ещё не получил ответ панели.";

    async Task<string?> HandleAsync(string cmd, string arg)
    {
        switch (cmd)
        {
            case "/status": return BuildStatus();
            case "/nodes": return BuildNodes();
            case "/users": return await BuildUsersAsync();
            case "/problems": return BuildProblems();
            case "/version": await RefreshPanelVersion(); return BuildVersion();
            case "/dnd": return Dnd(arg);
            default: return null;
        }
    }

    string PanelLine() =>
        _panelError == "" ? "" : $"⚠️ Панель не отвечает с {_panelErrorSince:HH:mm}: {_panelError}\n";

    string Dnd(string arg)
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            bool active = _state.DndUntil is DateTime u && u > now;
            string a = arg.Trim().ToLowerInvariant();
            if (a is "off" or "выкл" or "0" || (a == "" && active))
            {
                _state.DndUntil = null;
                _state.Save(_cfg.DataDir);
                var q = SilentUntil(now);
                return "🔔 «Не беспокоить» выключено." + (q is DateTime qq ? $" Сейчас тихие часы — уведомления пойдут с {When(qq)}." : "");
            }
            var t = _cfg.DndTime;
            if (a != "" && !TimeSpan.TryParseExact(a.Replace('.', ':'), new[] { @"h\:mm", @"hh\:mm", "%h", "hh" }, CultureInfo.InvariantCulture, out t))
                return "Время в формате ЧЧ:ММ, например /dnd 08:00. Выключить — /dnd off.";
            var at = now.Date + t;
            if (at <= now) at = at.AddDays(1);
            _state.DndUntil = at;
            _state.Save(_cfg.DataDir);
            return $"🔕 Не беспокоить до {When(at)}. Ответы на команды приходят как обычно, проблемы видны в /problems. Выключить — кнопкой ещё раз или /dnd off.";
        }
    }

    string BuildStatus()
    {
        lock (_lock)
        {
            var s = _snap;
            if (s == null) return PanelLine() + NoData;
            int online = s.Users.Count(u => u.IsOnline);
            int ok = s.Nodes.Count(n => n.Connected);
            var sb = new StringBuilder(PanelLine());
            sb.AppendLine($"📊 Онлайн: {online} · ноды {ok}/{s.Nodes.Count}");
            if (SilentUntil(DateTime.Now) is DateTime su)
                sb.AppendLine(su == DateTime.MaxValue ? "🔕 Тихие часы на весь день" : $"🔕 Не беспокоить до {When(su)}");
            foreach (var n in s.Nodes)
                sb.AppendLine($"{(n.Connected ? "✅" : "🔴")} {Fmt.Flag(n.Country)}{n.Name} · онлайн {n.UsersOnline}"
                              + (n.RamPctText != "—" ? $" · RAM {n.RamPctText}" : "")
                              + (n.BillingLevel is "warn" or "crit" ? $" · {n.BillingShort}" : ""));
            sb.Append($"Активных проблем: {_log.Active} · обновлено {s.At:HH:mm:ss}");
            return sb.ToString();
        }
    }

    string BuildNodes()
    {
        lock (_lock)
        {
            var s = _snap;
            if (s == null) return PanelLine() + NoData;
            var sb = new StringBuilder(PanelLine());
            sb.AppendLine($"🖥 Ноды ({s.Nodes.Count})");
            foreach (var n in s.Nodes)
            {
                sb.AppendLine();
                sb.AppendLine($"{(n.Connected ? "✅" : "🔴")} {Fmt.Flag(n.Country)}{n.Name} · {n.Status} · онлайн {n.UsersOnline}");
                var l2 = new[] { n.MemPctText != "" ? $"RAM {n.MemPctText} ({n.MemText})" : "", n.LoadText }.Where(x => x != "");
                var l3 = new[] { n.Down, n.Up, n.Xray }.Where(x => x != "");
                foreach (var line in new[] { string.Join(" · ", l2), string.Join(" · ", l3),
                             n.TrafficText != "—" ? "трафик " + n.TrafficText : "", n.BillingText })
                    if (line != "") sb.AppendLine("   " + line);
            }
            return sb.ToString().TrimEnd();
        }
    }

    // Онлайн с IP: адреса запрашиваются у панели в момент команды (только ноды, где есть люди)
    async Task<string> BuildUsersAsync()
    {
        Snapshot? s;
        lock (_lock) s = _snap;
        if (s == null) return PanelLine() + NoData;
        var online = s.Users.Where(u => u.IsOnline).ToList();
        if (online.Count == 0) return $"👥 Сейчас никого онлайн (всего пользователей: {s.Users.Count})";

        var ips = new Dictionary<string, List<(string ip, DateTimeOffset seen, string node)>>();
        string ipError = "";
        var nodes = s.Nodes.Where(n => n.Connected && n.UsersOnline > 0 && n.NodeUuid != "").ToList();
        var tasks = nodes.Select(async n =>
        {
            try { return (n.Name, list: await _api.FetchNodeIpsAsync(_cfg, n.NodeUuid), err: ""); }
            catch (Exception ex) { return (n.Name, list: new List<(string user, string ip, DateTimeOffset seen)>(), err: ex.Message); }
        }).ToList();
        var all = Task.WhenAll(tasks);
        if (await Task.WhenAny(all, Task.Delay(25000)) == all)
        {
            foreach (var (node, list, err) in all.Result)
            {
                if (err != "" && ipError == "") ipError = err.Contains("403") ? "у API-токена нет прав на подключения" : err;
                foreach (var (user, ip, seen) in list)
                {
                    if (!ips.TryGetValue(user, out var l)) ips[user] = l = new();
                    l.Add((ip, seen, node));
                }
            }
        }
        else ipError = "панель долго не отвечает";

        var sb = new StringBuilder();
        sb.AppendLine($"👥 Онлайн: {online.Count} из {s.Users.Count}");
        foreach (var u in online)
        {
            sb.AppendLine($"• {u.Username} — {Fmt.Flag(u.LastNodeCountry)}{u.LastNode}");
            var key = u.UserId != "" ? u.UserId : u.UserUuid;
            if (key != "" && ips.TryGetValue(key, out var list))
                foreach (var x in list.OrderByDescending(x => x.seen).GroupBy(x => x.ip).Select(g => g.First()))
                    sb.AppendLine($"     🌐 {x.ip} — {x.node}, {x.seen.ToLocalTime():dd.MM HH:mm:ss}");
        }
        if (ipError != "") sb.AppendLine($"(IP не получены: {ipError})");
        return sb.ToString().TrimEnd();
    }

    string BuildProblems()
    {
        lock (_lock)
        {
            var rows = _log.Rows().Where(r => r.Active).ToList();
            var head = PanelLine();
            if (rows.Count == 0) return head + "✅ Активных проблем нет";
            return head + string.Join("\n", rows.Select(r => $"{(r.Level == "warn" ? "🟡" : "🔴")} {r.Text} ({r.Status})"));
        }
    }

    string BuildVersion()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ℹ️ Версии");
            sb.AppendLine($"Панель: {_panelVersion ?? "неизвестна"}"
                + (ReleaseCheck.Newer(_state.LatestBackend, _panelVersion) ? $" → доступна {_state.LatestBackend}" : _state.LatestBackend != null ? " (актуальна)" : ""));
            if (_snap != null)
                foreach (var n in _snap.Nodes.Where(x => x.NodeVersion != "" || x.XrayVersion != ""))
                {
                    bool old = ReleaseCheck.Newer(_state.LatestNode, n.NodeVersion);
                    var parts = new List<string>();
                    if (n.NodeVersion != "") parts.Add(old ? $"node {n.NodeVersion} → {_state.LatestNode}" : $"node {n.NodeVersion}");
                    if (n.XrayVersion != "") parts.Add($"xray {n.XrayVersion}");
                    sb.AppendLine($"• {n.Name}: {string.Join(" · ", parts)}");
                }
            sb.Append($"Бот: {Version}");
            return sb.ToString();
        }
    }
}
