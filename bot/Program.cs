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
    readonly Webhook _wh;
    readonly Api _api = new();
    readonly BotState _state;
    readonly IncidentLog _log;
    readonly Dictionary<string, int> _strikes = new();
    readonly Dictionary<string, DateTime> _suppress = new();          // после вебхука «нода снова в сети» опрос какое-то время не поднимает тревогу
    readonly Dictionary<string, Queue<DateTime>> _rate = new();        // защита от спама сообщениями от пользователей
    readonly object _lock = new();
    Snapshot? _snap;
    Dictionary<string, List<UserRow>> _byTg = new();                   // Telegram ID → пользователи панели
    string _panelError = "";
    DateTime _panelErrorSince;
    string? _panelVersion;
    Dictionary<string, List<IpSeen>> _ips = new();   // IP активных подключений: по ним уточняется «онлайн»
    string _ipError = "";

    public Bot(BotConfig cfg)
    {
        _cfg = cfg;
        _tg = new Telegram(cfg) { OnMessage = OnMessageAsync, OnCallback = OnCallbackAsync };
        _wh = new Webhook(cfg) { OnEvent = OnWebhookAsync };
        _state = BotState.Load(cfg.DataDir);
        // старый общий «не беспокоить» → по чатам админов
        if (_state.DndUntil is DateTime old)
        {
            foreach (var c in cfg.Chats) _state.Dnd[c] = old;
            _state.DndUntil = null;
        }
        // открытые проблемы сохраняются между перезапусками: по ним уже была тревога, повторно не шлём
        _log = IncidentLog.Load(cfg.DataDir, closeActive: false);
        foreach (var k in _log.ActiveKeys) _strikes[k] = 2;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_state.Signature != _cfg.Signature)
        {
            foreach (var c in _cfg.AllAdminChats)
                _tg.Send(c, $"✅ Remnawave bot {Version} запущен на сервере с панелью. Меню с кнопками — внизу.\n\n{AdminHelp}", AdminKeyboard(c));
            _state.Signature = _cfg.Signature;
            Save();
        }
        await RefreshAsync();   // команды начинаем принимать, когда уже есть данные панели
        var tg = Task.Run(() => _tg.RunAsync(ct), ct);
        var rel = Task.Run(() => ReleasesLoopAsync(ct), ct);
        var ipl = Task.Run(() => IpsLoopAsync(ct), ct);
        var wh = Task.Run(() => _wh.RunAsync(ct), ct);
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

    bool IsAdmin(string chat) => _cfg.Chats.Contains(chat) || _cfg.Observers.ContainsKey(chat);

    // ---- опрос панели ----
    async Task RefreshAsync()
    {
        try
        {
            var (u, n, m, b) = await _api.FetchAsync(_cfg);
            var snap = SnapshotBuilder.Build(u, n, _cfg, m, b);
            snap = snap with { Users = Online.Apply(snap.Users, _ips, _cfg.OnlineWindowSec) };
            if (_panelError != "") Log.Write("связь с панелью восстановлена");
            _panelError = "";
            lock (_lock)
            {
                bool seed = !_state.Seeded;
                foreach (var node in snap.Nodes)
                {
                    Track(node.Name + "|conn", node.Name, !node.Connected, seed,
                        $"Нода {node.Name} не в сети", $"Нода {node.Name} снова в сети", "nodes");
                    Track(node.Name + "|bill", node.Name, node.BillingLevel == "crit", seed,
                        $"{node.Name}: {node.BillingText}", "", "billing");
                }
                // ноды, которых больше нет в панели, — закрываем их проблемы без сообщений
                foreach (var key in _strikes.Keys.Where(k => snap.Nodes.All(x => x.Name + "|conn" != k && x.Name + "|bill" != k)).ToList())
                {
                    _log.Resolve(key);
                    _strikes.Remove(key);
                }
                NotifyUsers(snap, seed);
                _snap = snap;
                _byTg = ByTelegram(snap.Users);
                if (_cfg.UserReminders) Remind();
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

    static string TgOf(UserRow u)
    {
        if (u.Raw == "") return "";
        try { using var d = JsonDocument.Parse(u.Raw); return J.S(d.RootElement, "telegramId"); }
        catch { return ""; }
    }

    static Dictionary<string, List<UserRow>> ByTelegram(IEnumerable<UserRow> users)
    {
        var map = new Dictionary<string, List<UserRow>>();
        foreach (var u in users)
        {
            var tg = TgOf(u);
            if (tg == "" || tg == "0") continue;
            if (!map.TryGetValue(tg, out var l)) map[tg] = l = new();
            l.Add(u);
        }
        return map;
    }

    List<UserRow> SubsOf(string chat)
    {
        lock (_lock) return _byTg.TryGetValue(chat, out var l) ? l.OrderBy(x => x.Username).ToList() : new();
    }

    // Тревога, только если проблема держится два опроса подряд; о восстановлении — если тревога была
    void Track(string key, string subject, bool failing, bool seed, string alarm, string recovered, string kind)
    {
        if (failing && _suppress.TryGetValue(key, out var until) && DateTime.Now < until) return;
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
                    Alert(kind, (severity == "warn" ? "🟡 " : "🔴 ") + alarm);
                }
            }
            _strikes[key] = next;
        }
        else
        {
            if (cur >= 2)
            {
                var downtime = _log.Resolve(key);
                if (recovered != "") Alert(kind, "🟢 " + recovered + (downtime is TimeSpan d ? $" (простой {IncidentLog.Dur(d)})" : ""));
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
        if (!seed && fresh.Count > 0)
            Alert("limits", "⚠️ " + string.Join("\n", fresh.Take(15)) + (fresh.Count > 15 ? $"\nи ещё {fresh.Count - 15}…" : ""));
        var keys = now.Keys.OrderBy(k => k).ToList();
        if (!keys.SequenceEqual(_state.Alerted.OrderBy(k => k)))
        {
            _state.Alerted = keys;
            _state.Save(_cfg.DataDir);
        }
    }

    // ---- тишина: «не беспокоить» (у каждого чата своё) и тихие часы ----
    // null — сейчас можно; DateTime.MaxValue — тихие часы на все сутки
    DateTime? SilentUntil(string chat, DateTime now)
    {
        var t = now;
        bool silent = false;
        for (int i = 0; i < 60; i++)
        {
            if (_state.Dnd.TryGetValue(chat, out var u) && t < u) { t = u; silent = true; continue; }
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

    // Тревога админам: основным — если вид включён, дополнительным — если он в их списке. В тишину не уходит (видна в /problems)
    void Alert(string kind, string text)
    {
        Log.Write($"[{kind}] {text}");
        var now = DateTime.Now;
        var to = _cfg.Chats.Where(_ => _cfg.KindEnabled(kind))
            .Concat(_cfg.Observers.Where(o => o.Value.Contains(kind)).Select(o => o.Key)).Distinct();
        foreach (var chat in to)
        {
            if (SilentUntil(chat, now) is DateTime until) { Log.Write($"  {chat}: тихо до {When(until)} — не отправлено"); continue; }
            _tg.Send(chat, text, AdminKeyboard(chat));
        }
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
            if (!first)
            {
                if (back != null && back != _state.LatestBackend && ReleaseCheck.Newer(back, _panelVersion))
                    Alert("updates", $"⬆️ Вышла новая версия Remnawave: {back} (у вас {_panelVersion ?? "?"})");
                if (node != null && node != _state.LatestNode && (_snap?.Nodes.Any(x => ReleaseCheck.Newer(node, x.NodeVersion)) ?? false))
                    Alert("updates", $"⬆️ Вышла новая версия ноды Remnawave: {node}");
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

    // ---- вебхуки панели: события приходят сразу ----
    async Task OnWebhookAsync(string ev, JsonElement data)
    {
        Log.Write("вебхук: " + ev);
        string Name(JsonElement e) => J.S(e, "username") is { Length: > 0 } un ? un : J.S(e, "name");
        var user = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("user", out var uu) ? uu : data;
        string un = data.ValueKind == JsonValueKind.Object ? Name(user) : "";

        lock (_lock)
        {
            switch (ev)
            {
                case "node.connection_lost": NodeDown(Name(data)); break;
                case "node.connection_restored": NodeUp(Name(data)); break;
                case "node.created": Alert("nodes", $"➕ Добавлена нода {Name(data)}"); break;
                case "node.deleted": Alert("nodes", $"➖ Удалена нода {Name(data)}"); break;
                case "node.disabled": Alert("nodes", $"⏸ Нода {Name(data)} отключена в панели"); break;
                case "node.enabled": Alert("nodes", $"▶️ Нода {Name(data)} включена в панели"); break;
                case "node.traffic_notify":
                    {
                        double used = J.N(data, "trafficUsedBytes"), limit = J.N(data, "trafficLimitBytes");
                        Alert("nodes", $"📶 Нода {Name(data)}: трафик {J.Bytes(used)}" + (limit > 0 ? $" из {J.Bytes(limit)} ({used / limit:P0})" : ""));
                        break;
                    }
                case "user.created": Alert("users", $"👤 Создан пользователь {un}" + UserBrief(user)); break;
                case "user.deleted": Alert("users", $"🗑 Удалён пользователь {un}"); break;
                case "user.revoked": Alert("users", $"🔄 Перевыпущена подписка {un}"); break;
                case "user.disabled": Alert("users", $"⏸ Пользователь {un} отключён"); break;
                case "user.enabled": Alert("users", $"▶️ Пользователь {un} включён"); break;
                case "user.limited": Alert("users", $"⛔ {un}: трафик закончился — доступ приостановлен"); break;
                case "user.expired": Alert("users", $"⛔ {un}: подписка истекла"); break;
                case "user.first_connected": Alert("users", $"🎉 {un} впервые подключился"); break;
                case "user_hwid_devices.added":
                    {
                        var dev = data.TryGetProperty("hwidUserDevice", out var hd) ? hd : default;
                        var what = dev.ValueKind == JsonValueKind.Object
                            ? string.Join(" · ", new[] { J.S(dev, "deviceModel"), J.S(dev, "platform"), J.S(dev, "osVersion") }.Where(x => x != ""))
                            : "";
                        var lim = J.S(user, "hwidDeviceLimit");
                        Alert("users", $"📱 Новое устройство у {un}" + (what != "" ? $": {what}" : "") + (lim != "" && lim != "0" ? $" (лимит {lim})" : ""));
                        break;
                    }
                case "torrent_blocker.report":
                    {
                        var node = data.TryGetProperty("node", out var nd) ? Name(nd) : "";
                        Alert("users", $"🧲 Торрент: {un}" + (node != "" ? $" на ноде {node}" : "") + " — заблокирован");
                        break;
                    }
                case "service.login_attempt_failed":
                case "service.login_attempt_success":
                    {
                        var la = data.TryGetProperty("loginAttempt", out var l) ? l : default;
                        bool ok = ev.EndsWith("success");
                        // пароль из события намеренно не пересылаем
                        Alert("panel", (ok ? "🔑 Вход в панель" : "🚫 Неудачный вход в панель")
                                       + $": {J.S(la, "username")}, IP {J.S(la, "ip")}"
                                       + (J.S(la, "description") is { Length: > 0 } ds ? $" ({ds})" : ""));
                        break;
                    }
                case "service.panel_started":
                    Alert("panel", "🔄 Панель запущена" + (J.S(data, "panelVersion") is { Length: > 0 } pv ? $" (версия {pv})" : ""));
                    break;
                case "service.api_token_created":
                case "service.api_token_deleted":
                    {
                        var t = data.TryGetProperty("apiToken", out var at) ? J.S(at, "name") : "";
                        Alert("panel", (ev.EndsWith("created") ? "🔑 Создан API-токен" : "🗑 Удалён API-токен") + (t != "" ? $" «{t}»" : ""));
                        break;
                    }
            }
        }
        // изменения пользователя — сразу освежить данные (кабинет, напоминания)
        if (ev.StartsWith("user.")) await RefreshAsync();
    }

    static string UserBrief(JsonElement u)
    {
        var parts = new List<string>();
        if (DateTimeOffset.TryParse(J.S(u, "expireAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e))
            parts.Add($"до {e.ToLocalTime():dd.MM.yyyy}");
        double lim = J.N(u, "trafficLimitBytes");
        parts.Add(lim > 0 ? $"лимит {J.Bytes(lim)}" : "без лимита");
        return " (" + string.Join(", ", parts) + ")";
    }

    void NodeDown(string name)
    {
        if (name == "") return;
        var key = name + "|conn";
        if (_strikes.GetValueOrDefault(key) >= 2) return;
        _log.Open(key, name, $"Нода {name} не в сети", "bad", unread: true);
        _strikes[key] = 2;
        _suppress.Remove(key);
        Alert("nodes", $"🔴 Нода {name} не в сети");
    }

    void NodeUp(string name)
    {
        if (name == "") return;
        var key = name + "|conn";
        _suppress[key] = DateTime.Now.AddSeconds(_cfg.IntervalSec * 3);
        if (_strikes.GetValueOrDefault(key) < 2) { _strikes.Remove(key); return; }
        var downtime = _log.Resolve(key);
        _strikes.Remove(key);
        Alert("nodes", $"🟢 Нода {name} снова в сети" + (downtime is TimeSpan d ? $" (простой {IncidentLog.Dur(d)})" : ""));
    }

    // ---- напоминания пользователям (по Telegram ID из панели) ----
    void Remind()
    {
        var now = Api.Now;
        var sent = _state.Reminded.ToHashSet();
        bool changed = false;
        foreach (var (chat, users) in _byTg)
        {
            if (!_state.Started.Contains(chat) || _state.NoRemind.Contains(chat)) continue;
            var lines = new List<string>();
            foreach (var u in users)
            {
                if (u.Status == "DISABLED") continue;
                foreach (var (key, text) in ReminderEvents(u, now))
                {
                    if (sent.Contains(key)) continue;
                    sent.Add(key);
                    _state.Reminded.Add(key);
                    changed = true;
                    lines.Add(text);
                }
            }
            if (lines.Count == 0) continue;
            var msg = lines.Count == 1 ? lines[0] : "Напоминание по подпискам:\n\n" + string.Join("\n\n", lines);
            msg += "\n\nПодробности — «👤 Мои подписки». Продлить — напишите администратору.";
            Log.Write($"напоминание для {chat}: {lines.Count}");
            _tg.Send(chat, msg, ChatKeyboard(chat));
        }
        if (_state.Reminded.Count > 3000) { _state.Reminded.RemoveRange(0, _state.Reminded.Count - 3000); changed = true; }
        if (changed) _state.Save(_cfg.DataDir);
    }

    IEnumerable<(string key, string text)> ReminderEvents(UserRow u, DateTimeOffset now)
    {
        JsonElement r;
        try { r = JsonDocument.Parse(u.Raw).RootElement.Clone(); } catch { yield break; }
        var id = u.UserUuid != "" ? u.UserUuid : u.Username;

        if (DateTimeOffset.TryParse(J.S(r, "expireAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exp))
        {
            var left = exp - now;
            var stamp = exp.UtcDateTime.ToString("yyyyMMddHH");
            var date = exp.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
            if (left <= TimeSpan.Zero)
            {
                if (left > TimeSpan.FromDays(-3)) yield return ($"exp0:{id}:{stamp}", $"⛔ Подписка «{u.Username}» истекла {date}.");
            }
            else if (left.TotalDays <= 1)
                yield return ($"exp1:{id}:{stamp}", $"⚠️ Подписка «{u.Username}» истекает {(left.TotalHours < 24 && exp.ToLocalTime().Date == DateTimeOffset.Now.Date ? "сегодня" : "завтра")} — {date}.");
            else if (left.TotalDays <= _cfg.RemindDays)
                yield return ($"expN:{id}:{stamp}", $"⚠️ Подписка «{u.Username}» истекает через {(int)Math.Ceiling(left.TotalDays)} д — {date}.");
        }

        double limit = J.N(r, "trafficLimitBytes");
        if (limit > 0)
        {
            double used = J.N(r, "userTraffic", "usedTrafficBytes");
            if (used == 0) used = J.N(r, "usedTrafficBytes");
            var period = J.S(r, "lastTrafficResetAt") is { Length: > 0 } rs ? rs : "none";
            double pct = used / limit;
            if (pct >= 1)
                yield return ($"tr100:{id}:{period}:{limit}", $"⛔ Трафик подписки «{u.Username}» закончился: {J.Bytes(used)} из {J.Bytes(limit)}.");
            else if (pct * 100 >= _cfg.RemindTraffic)
                yield return ($"trN:{id}:{period}:{limit}", $"⚠️ По подписке «{u.Username}» израсходовано {pct:P0} трафика: {J.Bytes(used)} из {J.Bytes(limit)}.");
        }
    }

    // ---- клавиатуры ----
    const string BtnMy = "👤 Мои подписки", BtnRemindOff = "🔕 Не напоминать", BtnRemindOn = "🔔 Напоминать",
                 BtnDnd = "🔕 Не беспокоить";

    static readonly (string text, string cmd)[] AdminButtons =
    {
        ("📊 Сводка", "/status"), ("🖥 Ноды", "/nodes"), ("👥 Онлайн", "/users"),
        ("⚠️ Проблемы", "/problems"), ("ℹ️ Версии", "/version"), (BtnDnd, "/dnd"), (BtnMy, "/my"),
    };

    string AdminKeyboard(string chat)
    {
        var rows = new List<string[]> { new[] { "📊 Сводка", "🖥 Ноды", "👥 Онлайн" }, new[] { "⚠️ Проблемы", "ℹ️ Версии", BtnDnd } };
        if (_cfg.UserCabinet && SubsOf(chat).Count > 0) rows.Add(new[] { BtnMy });
        return Telegram.Keyboard(rows.ToArray());
    }

    string UserKeyboard(string chat)
    {
        var rows = new List<string[]> { new[] { BtnMy } };
        if (_cfg.UserReminders) rows.Add(new[] { _state.NoRemind.Contains(chat) ? BtnRemindOn : BtnRemindOff });
        return Telegram.Keyboard(rows.ToArray());
    }

    string ChatKeyboard(string chat) => IsAdmin(chat) ? AdminKeyboard(chat) : UserKeyboard(chat);

    static string Norm(string s) => s.Replace("️", "").Trim();

    // ---- входящие сообщения ----
    const string AdminHelp =
        "Команды (или кнопки внизу):\n" +
        "/status — сводка\n/nodes — подробности по нодам\n/users — кто онлайн и с каких IP\n/problems — активные проблемы\n" +
        "/version — версии\n/dnd — не беспокоить до утра (повторно — выключить), /dnd 10:30 — до указанного времени\n/my — мои подписки";

    async Task OnMessageAsync(string chat, string raw)
    {
        if (!IsAdmin(chat) && TooFast(chat)) return;
        string cmd, arg = "";
        if (raw.StartsWith('/'))
        {
            var parts = raw.Split(new[] { ' ', '\n' }, 2, StringSplitOptions.RemoveEmptyEntries);
            cmd = parts[0].Split('@')[0].ToLowerInvariant();
            if (parts.Length > 1) arg = parts[1].Trim();
        }
        else
        {
            var t = Norm(raw);
            cmd = AdminButtons.FirstOrDefault(b => Norm(b.text) == t).cmd
                  ?? (t == Norm(BtnRemindOff) || t == Norm(BtnRemindOn) ? "/remind" : "");
        }

        if (IsAdmin(chat))
        {
            string? reply = cmd switch
            {
                "/start" or "/help" => AdminHelp,
                "/status" => BuildStatus(),
                "/nodes" => BuildNodes(),
                "/users" => await BuildUsersAsync(),
                "/problems" => BuildProblems(),
                "/version" => await BuildVersionAsync(),
                "/dnd" => Dnd(chat, arg),
                "/my" => null,
                "" => "Не понял. Команды — кнопками внизу или /help.",
                _ => "Не знаю такой команды.\n\n" + AdminHelp,
            };
            if (cmd == "/my") { await ShowMyAsync(chat); return; }
            _tg.Send(chat, reply!, AdminKeyboard(chat));
            return;
        }

        // пользователи панели
        if (!_cfg.UserCabinet && !_cfg.UserReminders) return;   // бот только для админов — посторонним не отвечаем
        var subs = SubsOf(chat);
        if (subs.Count == 0)
        {
            _tg.Send(chat, "У вас нет подписок, привязанных к этому Telegram.\n\n" +
                           $"Ваш Telegram ID: {chat} — передайте его администратору, чтобы он указал его в вашей подписке.", null);
            return;
        }
        MarkStarted(chat, subs);
        switch (cmd)
        {
            case "/remind":
                lock (_lock)
                {
                    bool off = !_state.NoRemind.Remove(chat);
                    if (off) _state.NoRemind.Add(chat);
                    _state.Save(_cfg.DataDir);
                    _tg.Send(chat, off ? "🔕 Напоминания выключены. Включить — кнопкой внизу." : "🔔 Напоминания включены: напишу, когда подписка скоро истечёт или заканчивается трафик.", UserKeyboard(chat));
                }
                break;
            case "/start":
            case "/help":
                _tg.Send(chat, "Здравствуйте! Здесь можно посмотреть свои подписки: срок, трафик, устройства и ссылку." +
                               (_cfg.UserReminders && !_state.NoRemind.Contains(chat) ? "\nЯ напомню, когда подписка будет истекать или закончится трафик." : ""),
                         UserKeyboard(chat));
                await ShowMyAsync(chat);
                break;
            default:
                await ShowMyAsync(chat);
                break;
        }
    }

    bool TooFast(string chat)
    {
        lock (_lock)
        {
            if (!_rate.TryGetValue(chat, out var q)) _rate[chat] = q = new();
            var now = DateTime.Now;
            while (q.Count > 0 && now - q.Peek() > TimeSpan.FromMinutes(1)) q.Dequeue();
            q.Enqueue(now);
            return q.Count > 20;
        }
    }

    void MarkStarted(string chat, List<UserRow> subs)
    {
        lock (_lock)
        {
            if (_state.Started.Contains(chat)) return;
            _state.Started.Add(chat);
            _state.Save(_cfg.DataDir);
        }
        Alert("users", $"👤 {string.Join(", ", subs.Select(x => x.Username))} подключил(а) бота — теперь получает напоминания");
    }

    // ---- личный кабинет ----
    async Task ShowMyAsync(string chat)
    {
        if (!_cfg.UserCabinet && !IsAdmin(chat)) { _tg.Send(chat, "Просмотр подписок выключен администратором.", ChatKeyboard(chat)); return; }
        var subs = SubsOf(chat);
        if (subs.Count == 0) { _tg.Send(chat, $"К вашему Telegram (ID {chat}) не привязано ни одной подписки.", ChatKeyboard(chat)); return; }
        if (subs.Count == 1) { await ShowSubAsync(chat, subs[0]); return; }
        var now = Api.Now;
        var sb = new StringBuilder($"👤 Ваши подписки ({subs.Count}):\n\n");
        foreach (var u in subs) sb.AppendLine(SubLine(u, now));
        sb.Append("\nНажмите на подписку, чтобы открыть подробности и ссылку.");
        _tg.Send(chat, sb.ToString(), Telegram.Inline(subs.Select(u => (u.Username, "sub:" + (u.UserUuid != "" ? u.UserUuid : u.Username)))));
    }

    static string StatusRu(string s) => s switch
    {
        "ACTIVE" => "активна", "DISABLED" => "отключена", "LIMITED" => "трафик закончился", "EXPIRED" => "истекла", _ => s.ToLowerInvariant(),
    };

    static string SubLine(UserRow u, DateTimeOffset now)
    {
        JsonElement r = default;
        try { r = JsonDocument.Parse(u.Raw).RootElement.Clone(); } catch { }
        string icon = u.Status == "ACTIVE" ? "✅" : "⛔";
        var parts = new List<string>();
        if (r.ValueKind == JsonValueKind.Object &&
            DateTimeOffset.TryParse(J.S(r, "expireAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exp))
        {
            var left = exp - now;
            parts.Add(left <= TimeSpan.Zero ? $"истекла {exp.ToLocalTime():dd.MM}" : $"до {exp.ToLocalTime():dd.MM} (ещё {Math.Max(1, (int)Math.Ceiling(left.TotalDays))} д)");
        }
        parts.Add(u.Traffic + (u.HasLimit ? "" : " без лимита"));
        return $"{icon} {u.Username} — {string.Join(" · ", parts)}";
    }

    async Task OnCallbackAsync(string chat, string id, string data)
    {
        if (!data.StartsWith("sub:")) { await _tg.AnswerCallbackAsync(id); return; }
        var key = data[4..];
        var u = SubsOf(chat).FirstOrDefault(x => (x.UserUuid != "" ? x.UserUuid : x.Username) == key);
        if (u == null) { await _tg.AnswerCallbackAsync(id, "Подписка не найдена"); return; }   // чужие подписки не показываем
        await _tg.AnswerCallbackAsync(id);
        await ShowSubAsync(chat, u);
    }

    async Task ShowSubAsync(string chat, UserRow u)
    {
        JsonElement r = default;
        try { r = JsonDocument.Parse(u.Raw).RootElement.Clone(); } catch { }
        var now = Api.Now;
        var sb = new StringBuilder();
        sb.AppendLine($"📄 {u.Username} — {StatusRu(u.Status)}");
        if (r.ValueKind == JsonValueKind.Object &&
            DateTimeOffset.TryParse(J.S(r, "expireAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exp))
        {
            var left = exp - now;
            sb.AppendLine($"Срок: до {exp.ToLocalTime():dd.MM.yyyy HH:mm}" + (left > TimeSpan.Zero ? $" (ещё {(left.TotalDays >= 1 ? $"{(int)left.TotalDays} д" : $"{Math.Max(1, (int)left.TotalHours)} ч")})" : " — истекла"));
        }
        double limit = r.ValueKind == JsonValueKind.Object ? J.N(r, "trafficLimitBytes") : 0;
        double used = r.ValueKind == JsonValueKind.Object ? J.N(r, "userTraffic", "usedTrafficBytes") : 0;
        if (used == 0) used = u.TrafficBytes;
        var strategy = r.ValueKind == JsonValueKind.Object ? J.S(r, "trafficLimitStrategy") : "";
        sb.AppendLine("Трафик: " + (limit > 0 ? $"{J.Bytes(used)} из {J.Bytes(limit)} ({used / limit:P0}), осталось {J.Bytes(Math.Max(0, limit - used))}" : $"{J.Bytes(used)}, без лимита")
                      + (strategy is "DAY" ? " · сброс каждый день" : strategy is "WEEK" ? " · сброс каждую неделю" : strategy is "MONTH" or "MONTH_ROLLING" or "CALENDAR_MONTH" ? " · сброс каждый месяц" : ""));
        try
        {
            var devs = await _api.GetUserDevicesAsync(_cfg, u.UserId, u.UserUuid);
            var lim = r.ValueKind == JsonValueKind.Object ? J.S(r, "hwidDeviceLimit") : "";
            if (devs != null)
            {
                sb.AppendLine($"Устройства: {devs.Count}" + (lim != "" && lim != "0" ? $" из {lim}" : ""));
                foreach (var d in devs.Take(10))
                {
                    var name = string.Join(" · ", new[] { d.Model, d.Platform, d.Os }.Where(x => x != ""));
                    sb.AppendLine("   • " + (name == "" ? "устройство" : name) + (d.Updated is DateTimeOffset up ? $" — {up.ToLocalTime():dd.MM HH:mm}" : ""));
                }
            }
        }
        catch { }
        sb.AppendLine(u.IsOnline ? "Сейчас подключено" : u.Online != "—" ? $"Последнее подключение: {u.Online}" : "Ещё не подключались");
        if (u.SubscriptionUrl != "") sb.Append($"\nСсылка на подписку:\n{u.SubscriptionUrl}");
        _tg.Send(chat, sb.ToString().TrimEnd(), ChatKeyboard(chat));
    }

    // ---- команды админа ----
    const string NoData = "Данных пока нет: бот ещё не получил ответ панели.";

    string PanelLine() =>
        _panelError == "" ? "" : $"⚠️ Панель не отвечает с {_panelErrorSince:HH:mm}: {_panelError}\n";

    string Dnd(string chat, string arg)
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            bool active = _state.Dnd.TryGetValue(chat, out var u) && u > now;
            string a = arg.Trim().ToLowerInvariant();
            if (a is "off" or "выкл" or "0" || (a == "" && active))
            {
                _state.Dnd.Remove(chat);
                _state.Save(_cfg.DataDir);
                var q = SilentUntil(chat, now);
                return "🔔 «Не беспокоить» выключено." + (q is DateTime qq ? $" Сейчас тихие часы — уведомления пойдут с {When(qq)}." : "");
            }
            var t = _cfg.DndTime;
            if (a != "" && !TimeSpan.TryParseExact(a.Replace('.', ':'), new[] { @"h\:mm", @"hh\:mm", "%h", "hh" }, CultureInfo.InvariantCulture, out t))
                return "Время в формате ЧЧ:ММ, например /dnd 08:00. Выключить — /dnd off.";
            var at = now.Date + t;
            if (at <= now) at = at.AddDays(1);
            _state.Dnd[chat] = at;
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
            foreach (var n in s.Nodes)
                sb.AppendLine($"{(n.Connected ? "✅" : "🔴")} {Fmt.Flag(n.Country)}{n.Name} · онлайн {n.UsersOnline}"
                              + (n.RamPctText != "—" ? $" · RAM {n.RamPctText}" : "")
                              + (n.BillingLevel is "warn" or "crit" ? $" · {n.BillingShort}" : ""));
            sb.AppendLine($"Активных проблем: {_log.Active} · обновлено {s.At:HH:mm:ss}");
            // пользователи с Telegram: сколько подключили бота
            if (_cfg.UserCabinet || _cfg.UserReminders)
            {
                int withTg = _byTg.Count, started = _byTg.Keys.Count(k => _state.Started.Contains(k));
                sb.AppendLine($"👤 Telegram у пользователей: {withTg}, подключили бота: {started}");
            }
            sb.Append(_cfg.WebhookSecret.Length >= 32
                ? "🔗 Вебхуки панели: " + (_wh.LastEvent is DateTime le ? $"последнее событие {le:dd.MM HH:mm}" : "событий ещё не было")
                : "🔗 Вебхуки панели: выключены");
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

    // IP активных подключений — раз в минуту (только ноды, где есть люди)
    async Task IpsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshIpsAsync();
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task RefreshIpsAsync()
    {
        Snapshot? s;
        lock (_lock) s = _snap;
        if (s == null) return;
        try
        {
            var (map, err) = await Online.FetchIpsAsync(_api, _cfg, s.Nodes);
            if (err != "" && err != _ipError) Log.Write("IP пользователей: " + err);
            lock (_lock)
            {
                _ips = map;
                _ipError = err;
                if (_snap != null) _snap = _snap with { Users = Online.Apply(_snap.Users, map, _cfg.OnlineWindowSec) };
            }
        }
        catch (Exception ex) { Log.Write("IP пользователей: " + ex.Message); }
    }

    async Task<string> BuildUsersAsync()
    {
        await RefreshIpsAsync();   // свежие IP на момент команды
        lock (_lock)
        {
            var s = _snap;
            if (s == null) return PanelLine() + NoData;
            var online = s.Users.Where(u => u.IsOnline).ToList();
            if (online.Count == 0) return $"👥 Сейчас никого онлайн (всего пользователей: {s.Users.Count})";
            var sb = new StringBuilder();
            sb.AppendLine($"👥 Онлайн: {online.Count} из {s.Users.Count}");
            foreach (var u in online)
            {
                sb.AppendLine($"• {u.Username} — {Fmt.Flag(u.LastNodeCountry)}{u.LastNode}");
                if (u.IpTip != "")
                    foreach (var line in u.IpTip.Split('\n')) sb.AppendLine("     🌐 " + line);
            }
            if (_ipError != "") sb.AppendLine($"(IP не получены: {_ipError})");
            return sb.ToString().TrimEnd();
        }
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

    async Task<string> BuildVersionAsync()
    {
        await RefreshPanelVersion();
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
