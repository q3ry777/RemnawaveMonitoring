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
    readonly BotApi _apiServer;
    readonly Api _api = new();
    readonly CheckHost _check = new();
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
        _apiServer = new BotApi(cfg) { Payments = PaymentsReport };
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
        var api = Task.Run(() => _apiServer.RunAsync(ct), ct);
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
            var toPay = new List<UserRow>();
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
                    if (key.StartsWith("exp") && Payable(u) && !toPay.Contains(u)) toPay.Add(u);
                }
            }
            if (lines.Count == 0) continue;
            var msg = lines.Count == 1 ? lines[0] : "Напоминание по подпискам:\n\n" + string.Join("\n\n", lines);
            msg += toPay.Count > 0 ? "\n\nПродлить можно прямо здесь — кнопкой ниже." : "\n\nПодробности — «👤 Мои подписки». Продлить — напишите администратору.";
            Log.Write($"напоминание для {chat}: {lines.Count}");
            _tg.Send(chat, msg, toPay.Count == 1
                ? Telegram.Inline(new[] { ($"💳 Продлить «{toPay[0].Username}»", "pay:" + Key(toPay[0])) })
                : toPay.Count > 1
                    ? Telegram.Inline(new[] { ($"💳 Продлить ({toPay.Count})", "paysel:") })
                    : ChatKeyboard(chat));
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

    static string Key(UserRow u) => u.UserUuid != "" ? u.UserUuid : u.Username;

    static DateTimeOffset? ExpireOf(UserRow u)
    {
        if (u.Raw == "") return null;
        try
        {
            using var d = JsonDocument.Parse(u.Raw);
            return DateTimeOffset.TryParse(J.S(d.RootElement, "expireAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e) ? e : null;
        }
        catch { return null; }
    }

    // «Бессрочные» профили (срок до 2099 и т.п.) — без продления и напоминаний о сроке
    static bool Unlimited(UserRow u) => ExpireOf(u) is not DateTimeOffset e || e.Year >= 2090 || e - Api.Now > TimeSpan.FromDays(365 * 20);

    // Можно оплатить продление: оплата включена, у профиля настоящий срок, он не отключён админом
    bool Payable(UserRow u) => _cfg.PayEnabled && _cfg.PayPlans.Count > 0 && !Unlimited(u) && u.Status != "DISABLED";

    // ---- клавиатуры ----
    const string BtnMy = "👤 Мои подписки", BtnRemindOff = "🔕 Не напоминать", BtnRemindOn = "🔔 Напоминать",
                 BtnDnd = "🔕 Не беспокоить", BtnPay = "💳 Продлить подписку", BtnPayments = "💳 Оплаты",
                 BtnBotUsers = "🤝 Бот у пользователей", BtnCheck = "🔎 Проверка из РФ", BtnSupport = "✉️ Написать администратору";

    static readonly (string text, string cmd)[] AdminButtons =
    {
        ("📊 Сводка", "/status"), ("🖥 Ноды", "/nodes"), ("👥 Онлайн", "/users"),
        ("⚠️ Проблемы", "/problems"), ("ℹ️ Версии", "/version"), (BtnDnd, "/dnd"), (BtnMy, "/my"),
        (BtnPay, "/pay"), (BtnPayments, "/payments"), (BtnBotUsers, "/botusers"), (BtnCheck, "/check"),
    };

    string AdminKeyboard(string chat)
    {
        var rows = new List<string[]> { new[] { "📊 Сводка", "🖥 Ноды", "👥 Онлайн" }, new[] { "⚠️ Проблемы", "ℹ️ Версии", BtnCheck } };
        var last = new List<string> { BtnDnd };
        if (_cfg.UserCabinet && SubsOf(chat).Count > 0) last.Add(BtnMy);
        if (_cfg.PayEnabled && _cfg.Chats.Contains(chat)) last.Add(BtnPayments);
        if ((_cfg.UserCabinet || _cfg.UserReminders) && _cfg.Chats.Contains(chat)) last.Add(BtnBotUsers);
        if (last.Count > 3) { rows.Add(last.Take(2).ToArray()); rows.Add(last.Skip(2).ToArray()); }
        else rows.Add(last.ToArray());
        return Telegram.Keyboard(rows.ToArray());
    }

    string UserKeyboard(string chat)
    {
        var rows = new List<string[]> { new[] { BtnMy } };
        if (SubsOf(chat).Any(Payable)) rows.Add(new[] { BtnPay });
        rows.Add(new[] { BtnSupport });
        if (_cfg.UserReminders) rows.Add(new[] { _state.NoRemind.Contains(chat) ? BtnRemindOn : BtnRemindOff });
        return Telegram.Keyboard(rows.ToArray());
    }

    string ChatKeyboard(string chat) => IsAdmin(chat) ? AdminKeyboard(chat) : UserKeyboard(chat);

    static string Norm(string s) => s.Replace("️", "").Trim();

    // ---- входящие сообщения ----
    const string AdminHelp =
        "Команды (или кнопки внизу):\n" +
        "/status — сводка\n/nodes — подробности по нодам\n/users — кто онлайн и с каких IP\n/problems — активные проблемы\n" +
        "/version — версии\n/dnd — не беспокоить до утра (повторно — выключить), /dnd 10:30 — до указанного времени\n/my — мои подписки\n/payments — оплаты\n/botusers — кто из пользователей подключил бота\n/check — доступность нод из России (check-host.net), /check IP:порт — любой адрес\n/broadcast текст — рассылка всем, кто подключил бота\n\nНа сообщения пользователей «📨» отвечайте ответом (reply) — бот перешлёт ответ.";

    async Task OnMessageAsync(string chat, string raw, long messageId, bool media, string who, string replyTo)
    {
        if (!IsAdmin(chat) && TooFast(chat)) return;
        // посторонний (не админ и нет подписок с его Telegram ID): бот молчит, админам — короткое сообщение
        if (!IsAdmin(chat) && SubsOf(chat).Count == 0) { Stranger(chat, who, media ? "[файл или картинка]" : raw); return; }
        // ответ админа на сообщение пользователя (reply на «📨 Сообщение от …») — пересылается пользователю
        if (_cfg.Chats.Contains(chat) && replyTo.StartsWith("📨") &&
            System.Text.RegularExpressions.Regex.Match(replyTo, @"\(ID (\d+)\)") is { Success: true } rm)
        {
            AdminReply(chat, rm.Groups[1].Value, raw, messageId, media);
            return;
        }
        // скриншот перевода к заявке на оплату
        if (media && _cfg.PayEnabled)
        {
            string? pid;
            lock (_lock) pid = _state.AwaitProof.GetValueOrDefault(chat);
            if (pid != null) { ForwardProof(chat, pid, messageId); return; }
        }
        // файлы и картинки от пользователя — администратору
        if (media && !IsAdmin(chat)) { ForwardSupport(chat, who, raw, messageId, true); return; }
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
                  ?? (t == Norm(BtnRemindOff) || t == Norm(BtnRemindOn) ? "/remind" : t == Norm(BtnSupport) ? "/support" : "");
        }

        // админ, у которого есть свои подписки (его Telegram ID указан в панели), тоже считается подключившим бота
        if (IsAdmin(chat)) MarkStarted(chat, SubsOf(chat));

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
                "/pay" => null,
                "/payments" => BuildPayments(),
                "/botusers" => BuildBotUsers(),
                "/check" => null,
                "/broadcast" => null,
                "" => "Не понял. Команды — кнопками внизу или /help.",
                _ => "Не знаю такой команды.\n\n" + AdminHelp,
            };
            if (cmd == "/my") { await ShowMyAsync(chat); return; }
            if (cmd == "/pay") { StartPay(chat); return; }
            if (cmd == "/broadcast") { PrepareBroadcast(chat, arg); return; }
            if (cmd == "/check") { await StartCheckAsync(chat, arg); return; }
            if (cmd == "/payments" && PendingPayments().Count is var np and > 0)
            {   // заявки на проверке — кнопкой под сводкой (нижняя клавиатура остаётся прежней)
                _tg.Send(chat, reply!, Telegram.Inline(new[] { ($"⏳ Показать заявки на проверке ({np})", "pend:") }));
                return;
            }
            _tg.Send(chat, reply!, AdminKeyboard(chat));
            return;
        }

        // пользователи панели
        if (!_cfg.UserCabinet && !_cfg.UserReminders) return;   // бот только для админов — посторонним не отвечаем
        var subs = SubsOf(chat);
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
            case "/pay":
                StartPay(chat);
                break;
            case "/support":
                _tg.Send(chat, "Напишите вопрос одним сообщением — можно приложить скриншот или файл. Я передам администратору, ответ придёт сюда.", UserKeyboard(chat));
                break;
            case "":
                // свободный текст — вопрос администратору
                ForwardSupport(chat, who, raw, messageId, false);
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

    // ---- связь с администратором ----
    void ForwardSupport(string chat, string who, string text, long messageId, bool media)
    {
        var names = string.Join(", ", SubsOf(chat).Select(u => u.Username));
        var head = $"📨 Сообщение от {names}" + (who != "" ? $" — {who}" : "") + $" (ID {chat})";
        const string hint = "\n\n↩️ Ответьте на это сообщение (reply) — бот перешлёт ответ.";
        foreach (var a in _cfg.Chats)
        {
            if (media) _tg.Copy(a, chat, messageId, head + (text != "" ? ":\n" + text : "") + hint, null);
            else _tg.Send(a, head + ":\n" + text + hint, null);
        }
        Log.Write($"сообщение администратору от {names}");
        _tg.Send(chat, "✉️ Передал администратору. Ответ придёт сюда.", UserKeyboard(chat));
    }

    void AdminReply(string admin, string target, string text, long messageId, bool media)
    {
        var names = string.Join(", ", SubsOf(target).Select(u => u.Username));
        if (names == "") { _tg.Send(admin, $"Не нашёл пользователя с Telegram ID {target} — ответ не отправлен.", AdminKeyboard(admin)); return; }
        if (media) _tg.Copy(target, admin, messageId, "✉️ Ответ администратора" + (text != "" ? ":\n" + text : ""), null);
        else _tg.Send(target, "✉️ Ответ администратора:\n" + text, ChatKeyboard(target));
        _tg.Send(admin, $"✅ Ответ отправлен: {names}", AdminKeyboard(admin));
        foreach (var a in _cfg.Chats.Where(c => c != admin)) _tg.Send(a, $"↩️ Другой админ ответил {names}:\n{text}", AdminKeyboard(a));
    }

    // ---- рассылка всем, кто подключил бота (с подтверждением) ----
    readonly Dictionary<string, string> _broadcasts = new();

    void PrepareBroadcast(string chat, string text)
    {
        if (!_cfg.Chats.Contains(chat)) { _tg.Send(chat, "Рассылка доступна только основному админу.", AdminKeyboard(chat)); return; }
        if (text == "") { _tg.Send(chat, "Напишите: /broadcast и текст сообщения (можно в несколько строк). Получат все, кто подключил бота.", AdminKeyboard(chat)); return; }
        int n;
        string id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        lock (_lock)
        {
            n = _state.Started.Count(c => !IsAdmin(c) && _byTg.ContainsKey(c));
            _broadcasts[id] = text;
        }
        _tg.Send(chat, $"📢 Рассылку получат {n} чел.:\n\n{text}",
            Telegram.Inline(new[] { ("✅ Отправить", "bc:" + id), ("Отмена", "bcx:" + id) }));
    }

    string? Broadcast(string chat, string id, bool send)
    {
        if (!_cfg.Chats.Contains(chat)) return "Только для основного админа";
        string? text;
        List<string> to;
        lock (_lock)
        {
            if (!_broadcasts.Remove(id, out text)) return "Рассылка уже отправлена или отменена";
            to = _state.Started.Where(c => !IsAdmin(c) && _byTg.ContainsKey(c)).ToList();
        }
        if (!send) { _tg.Send(chat, "Рассылка отменена.", AdminKeyboard(chat)); return null; }
        foreach (var c in to) _tg.Send(c, "📢 " + text, ChatKeyboard(c));
        Log.Write($"рассылка: {to.Count} получателей");
        _tg.Send(chat, $"📢 Рассылка отправлена: {to.Count} чел.", AdminKeyboard(chat));
        return "Отправлено";
    }

    // Не чаще раза в сутки на человека, чтобы не засыпать админов
    void Stranger(string chat, string who, string text)
    {
        lock (_lock)
        {
            if (_state.Strangers.TryGetValue(chat, out var last) && DateTime.Now - last < TimeSpan.FromDays(1)) return;
            _state.Strangers[chat] = DateTime.Now;
            if (_state.Strangers.Count > 500)
                foreach (var old in _state.Strangers.OrderBy(x => x.Value).Take(_state.Strangers.Count - 500).Select(x => x.Key).ToList()) _state.Strangers.Remove(old);
            _state.Save(_cfg.DataDir);
        }
        if (text.Length > 100) text = text[..100] + "…";
        Alert("strangers", $"👀 Боту написал посторонний: {(who != "" ? who : "без имени")}, Telegram ID {chat}\n«{text}»\n" +
                           "Бот ему не ответил. Чтобы дать доступ — впишите этот ID в поле Telegram ID его подписки в панели.");
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
        if (subs.Count == 0) return;
        lock (_lock)
        {
            if (_state.Started.Contains(chat)) return;
            _state.Started.Add(chat);
            _state.StartedAt[chat] = DateTime.Now;
            _state.Save(_cfg.DataDir);
        }
        if (IsAdmin(chat)) return;   // админ с собственными подписками — без сообщения самому себе
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
        var buttons = subs.Select(u => (u.Username, "sub:" + Key(u))).ToList();
        var payable = subs.Where(Payable).ToList();
        var pay = payable.Count > 1
            ? new List<(string, string)> { ($"💳 Продлить несколько ({payable.Count})", "paysel:") }
            : payable.Select(u => ($"💳 Продлить «{u.Username}»", "pay:" + Key(u))).ToList();
        // кнопки подписок по две в ряд, трафик и продление — отдельными строками
        var rows = buttons.Select((b, i) => (b, i)).GroupBy(x => x.i / 2).Select(g => g.Select(x => x.b).ToArray())
            .Append(new[] { ("📈 Трафик по месяцам", "traf:*") })
            .Concat(pay.Select(p => new[] { p })).ToArray();
        _tg.Send(chat, sb.ToString(), Telegram.InlineRows(rows));
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
        if (Unlimited(u)) parts.Add("бессрочно");
        else if (r.ValueKind == JsonValueKind.Object &&
            DateTimeOffset.TryParse(J.S(r, "expireAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exp))
        {
            var left = exp - now;
            parts.Add(left <= TimeSpan.Zero ? $"истекла {exp.ToLocalTime():dd.MM}" : $"до {exp.ToLocalTime():dd.MM} (ещё {Math.Max(1, (int)Math.Ceiling(left.TotalDays))} д)");
        }
        parts.Add(u.Traffic + (u.HasLimit ? "" : " без лимита"));
        return $"{icon} {u.Username} — {string.Join(" · ", parts)}";
    }

    async Task OnCallbackAsync(string chat, string id, string data, long messageId)
    {
        var colon = data.IndexOf(':');
        var kind = colon > 0 ? data[..colon] : data;
        if (kind is "bc" or "bcx")
        {
            await _tg.AnswerCallbackAsync(id, Broadcast(chat, colon > 0 ? data[(colon + 1)..] : "", kind == "bc") ?? "");
            return;
        }
        if (kind is "pay" or "plan" or "paid" or "cancel" or "ok" or "no" or "sel" or "selx" or "selgo" or "paysel")
        {
            var reply = await PayCallbackAsync(chat, kind, colon > 0 ? data[(colon + 1)..] : "", messageId);
            await _tg.AnswerCallbackAsync(id, reply ?? "");
            return;
        }
        if (kind == "chk")
        {
            if (!IsAdmin(chat)) { await _tg.AnswerCallbackAsync(id); return; }
            await _tg.AnswerCallbackAsync(id, _check.Busy ? "Идёт другая проверка — эта начнётся следом" : "");
            _ = RunCheckAsync(chat, colon > 0 ? data[(colon + 1)..] : "");
            return;
        }
        if (kind == "pend")
        {
            if (!_cfg.Chats.Contains(chat)) { await _tg.AnswerCallbackAsync(id); return; }
            var pend = PendingPayments();
            await _tg.AnswerCallbackAsync(id, pend.Count == 0 ? "Заявок на проверке нет" : "");
            foreach (var p in pend) _tg.Send(chat, $"⏳ Заявка от {p.Created:dd.MM HH:mm}: " + PayInfo(p), PayButtons(p.Id));
            return;
        }
        if (kind == "traf")
        {
            await _tg.AnswerCallbackAsync(id);
            await ShowTrafficAsync(chat, colon > 0 ? data[(colon + 1)..] : "*");
            return;
        }
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
        if (Unlimited(u)) sb.AppendLine("Срок: бессрочно");
        else if (r.ValueKind == JsonValueKind.Object &&
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
        try
        {
            var months = await MonthlyUsageAsync(u, 2);
            if (months != null && months.Count > 0)
                sb.AppendLine("Трафик за месяц: " + string.Join(", ", months.Select(m => $"{MonthName(m.Month)} — {J.Bytes(m.Bytes)}" + (m.Partial ? $" (по {now.ToLocalTime():dd.MM})" : ""))));
        }
        catch { }
        sb.AppendLine(u.IsOnline ? "Сейчас подключено" : u.Online != "—" ? $"Последнее подключение: {u.Online}" : "Ещё не подключались");
        if (u.SubscriptionUrl != "") sb.Append($"\nСсылка на подписку:\n{u.SubscriptionUrl}");
        var btns = new List<(string, string)> { ("📈 Трафик по месяцам", "traf:" + Key(u)) };
        if (Payable(u)) btns.Add(("💳 Продлить", "pay:" + Key(u)));
        _tg.Send(chat, sb.ToString().TrimEnd(), Telegram.Inline(btns));
    }

    // ---- трафик по календарным месяцам (из статистики панели по дням) ----
    public record MonthUsage(DateTime Month, double Bytes, bool Partial);
    readonly Dictionary<string, (DateTime at, List<MonthUsage> data)> _usageCache = new();

    static readonly string[] Months = { "январь", "февраль", "март", "апрель", "май", "июнь", "июль", "август", "сентябрь", "октябрь", "ноябрь", "декабрь" };
    static string MonthName(DateTime m) => Months[m.Month - 1];

    // Последние count месяцев (текущий — неполный), от новых к старым. Месяцы до создания пользователя не показываются. null — панель не отдаёт статистику
    async Task<List<MonthUsage>?> MonthlyUsageAsync(UserRow u, int count)
    {
        var key = Key(u) + "|" + count;
        lock (_lock)
            if (_usageCache.TryGetValue(key, out var c) && DateTime.Now - c.at < TimeSpan.FromMinutes(10)) return c.data;
        var today = Api.Now.UtcDateTime.Date;
        var first = new DateTime(today.Year, today.Month, 1).AddMonths(-(count - 1));
        var usage = await _api.GetUserUsageAsync(_cfg, u.UserId, u.UserUuid, (int)(today - first).TotalDays + 1);
        if (usage == null) return null;
        DateTime? created = null;
        try
        {
            using var d = JsonDocument.Parse(u.Raw);
            if (DateTimeOffset.TryParse(J.S(d.RootElement, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var cr)) created = cr.UtcDateTime.Date;
        }
        catch { }
        var list = new List<MonthUsage>();
        for (var m = new DateTime(today.Year, today.Month, 1); m >= first; m = m.AddMonths(-1))
        {
            if (created is DateTime cd && cd >= m.AddMonths(1)) break;   // пользователя ещё не было
            var bytes = usage.Value.days.Where(x => x.Day >= m && x.Day < m.AddMonths(1)).Sum(x => x.Bytes);
            list.Add(new MonthUsage(m, bytes, m.Year == today.Year && m.Month == today.Month));
        }
        // старые месяцы без трафика в конце списка — скорее всего, панель их уже не хранит (или ещё не пользовались)
        while (list.Count > 1 && list[^1].Bytes == 0) list.RemoveAt(list.Count - 1);
        lock (_lock) _usageCache[key] = (DateTime.Now, list);
        return list;
    }

    async Task ShowTrafficAsync(string chat, string key)
    {
        var subs = SubsOf(chat).Where(u => key == "*" || Key(u) == key).ToList();
        if (subs.Count == 0) { _tg.Send(chat, "Подписка не найдена.", ChatKeyboard(chat)); return; }
        var today = Api.Now.UtcDateTime.Date;
        var sb = new StringBuilder();
        var totals = new Dictionary<DateTime, double>();
        bool any = false;
        foreach (var u in subs)
        {
            List<MonthUsage>? months = null;
            try { months = await MonthlyUsageAsync(u, 6); } catch (Exception ex) { Log.Write("трафик по месяцам: " + ex.Message); }
            sb.AppendLine($"📈 Трафик «{u.Username}»");
            if (months == null) { sb.AppendLine("нет данных — панель не отдаёт статистику"); sb.AppendLine(); continue; }
            any = true;
            foreach (var m in months)
            {
                sb.AppendLine($"{MonthName(m.Month)}{(m.Partial ? $" (по {today:dd.MM})" : "")} — {J.Bytes(m.Bytes)}");
                totals[m.Month] = totals.GetValueOrDefault(m.Month) + m.Bytes;
            }
            var full = months.Where(m => !m.Partial && m.Bytes > 0).ToList();
            if (full.Count >= 2) sb.AppendLine($"В среднем: {J.Bytes(full.Average(m => m.Bytes))} в месяц");
            sb.AppendLine();
        }
        if (subs.Count > 1 && any && totals.Count > 0)
        {
            sb.AppendLine("Σ Всего по подпискам");
            foreach (var t in totals.OrderByDescending(k => k.Key))
                sb.AppendLine($"{MonthName(t.Key)}{(t.Key.Year == today.Year && t.Key.Month == today.Month ? $" (по {today:dd.MM})" : "")} — {J.Bytes(t.Value)}");
        }
        if (subs.Count > 1 && any) sb.AppendLine();
        sb.Append("Считается по календарным месяцам.");
        _tg.Send(chat, sb.ToString().TrimEnd(), ChatKeyboard(chat));
    }

    // ---- оплата переводом: тариф → «я оплатил» (+ скриншот) → админ подтверждает → подписка продлевается ----
    string Money(int amount) => $"{amount:N0} {_cfg.Currency}";

    static string MonthsText(int m) => m % 10 == 1 && m % 100 != 11 ? $"{m} месяц"
        : m % 10 is >= 2 and <= 4 && (m % 100 < 10 || m % 100 >= 20) ? $"{m} месяца" : $"{m} месяцев";

    // ---- выбор подписок (массовая оплата) ----
    readonly Dictionary<string, HashSet<string>> _paySel = new();     // чат → отмеченные подписки
    readonly Dictionary<string, List<string>> _paySets = new();       // набор для выбора срока: id → подписки

    // Истекает скоро (или уже истекла) — такие отмечаются сразу
    bool Expiring(UserRow u) => ExpireOf(u) is DateTimeOffset e && e - Api.Now <= TimeSpan.FromDays(Math.Max(7, _cfg.RemindDays));

    // Кнопка «Продлить подписку»: одна оплачиваемая подписка — сразу сроки, несколько — выбор галочками
    void StartPay(string chat, long editMessage = 0)
    {
        var pay = SubsOf(chat).Where(Payable).ToList();
        if (!_cfg.PayEnabled || pay.Count == 0) { _tg.Send(chat, "Продлевать здесь нечего: у ваших подписок нет срока окончания или оплата выключена.", ChatKeyboard(chat)); return; }
        if (pay.Count == 1) { ShowPlans(chat, pay); return; }
        lock (_lock)
        {
            var sel = pay.Where(Expiring).Select(Key).ToHashSet();
            _paySel[chat] = sel;
        }
        _ = ShowSelection(chat, editMessage);
    }

    async Task ShowSelection(string chat, long editMessage)
    {
        var pay = SubsOf(chat).Where(Payable).ToList();
        HashSet<string> sel;
        lock (_lock) sel = _paySel.TryGetValue(chat, out var s0) ? s0 : (_paySel[chat] = new());
        sel.RemoveWhere(k => pay.All(u => Key(u) != k));
        var now = Api.Now;
        var rows = pay.Select(u =>
        {
            var e = ExpireOf(u);
            var when = e is DateTimeOffset x ? (x <= now ? $"истекла {x.ToLocalTime():dd.MM}" : $"до {x.ToLocalTime():dd.MM} (ещё {Math.Max(1, (int)Math.Ceiling((x - now).TotalDays))} д)") : "";
            return new[] { ((sel.Contains(Key(u)) ? "☑ " : "☐ ") + u.Username + (when != "" ? " — " + when : ""), "sel:" + Key(u)) };
        }).ToList();
        rows.Add(new[] { ("Выбрать истекающие", "selx:"), ($"Далее → ({sel.Count})", "selgo:") });
        var text = "Выберите подписки для продления — нажатие ставит или снимает галочку. Затем «Далее».";
        var markup = Telegram.InlineRows(rows);
        if (editMessage > 0) await _tg.EditAsync(chat, editMessage, text, markup);
        else _tg.Send(chat, text, markup);
    }

    void ShowPlans(string chat, List<UserRow> users)
    {
        var now = Api.Now;
        var setId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        lock (_lock) _paySets[setId] = users.Select(Key).ToList();
        string Line(UserRow u) => ExpireOf(u) is DateTimeOffset e
            ? (e > now ? $"«{u.Username}» — до {e.ToLocalTime():dd.MM.yyyy}" : $"«{u.Username}» — истекла")
            : $"«{u.Username}»";
        var head = users.Count == 1
            ? (ExpireOf(users[0]) is DateTimeOffset e1 && e1 > now
                ? $"Подписка «{users[0].Username}» действует до {e1.ToLocalTime():dd.MM.yyyy}. Продление добавится к этой дате."
                : $"Подписка «{users[0].Username}» истекла. Продление начнётся с момента подтверждения оплаты.")
            : $"Продление {users.Count} подписок:\n" + string.Join("\n", users.Select(u => "• " + Line(u))) +
              "\n\nДействующим срок добавится к дате окончания, истёкшим — с момента подтверждения.";
        int n = users.Count;
        _tg.Send(chat, head + "\n\nВыберите срок" + (n > 1 ? " (одинаковый для всех)" : "") + ":",
            Telegram.Inline(_cfg.PayPlans.Select(p => ($"{MonthsText(p.months)} — {Money(p.price * n)}" + (n > 1 ? $" ({n} × {Money(p.price)})" : ""), $"plan:{setId}:{p.months}")), 1));
    }

    string PayButtons(string pid) => Telegram.Inline(new[] { ("✅ Подтвердить", "ok:" + pid), ("❌ Отклонить", "no:" + pid) });

    string PayInfo(Payment p)
    {
        var now = Api.Now;
        var keys = p.Keys();
        var names = p.AllNames();
        var lines = keys.Select((k, i) =>
        {
            var u = UserByKey(k);
            var e = u != null ? ExpireOf(u) : null;
            var done = p.DoneItems.TryGetValue(k, out var d) ? $" ✅ продлено до {d}" : "";
            return $"• «{(i < names.Count ? names[i] : k)}»" + (e is DateTimeOffset x ? $" (сейчас {(x > now ? "до " + x.ToLocalTime().ToString("dd.MM.yyyy") : "истекла")})" : "") + done;
        });
        return (keys.Count > 1 ? $"{keys.Count} подписки на {MonthsText(p.Months)} — {Money(p.Amount)} ({keys.Count} × {Money(p.Price > 0 ? p.Price : p.Amount / keys.Count)})"
                               : $"«{names[0]}» на {MonthsText(p.Months)} — {Money(p.Amount)}")
               + (keys.Count > 1 ? "\n" + string.Join("\n", lines) : (p.DoneItems.Count > 0 ? "" : lines.First().Replace($"• «{names[0]}»", "").Trim() is { Length: > 0 } one ? " " + one : ""))
               + $"\nTelegram ID: {p.Chat} · заявка {p.Id}";
    }

    UserRow? UserByKey(string key)
    {
        lock (_lock) return _snap?.Users.FirstOrDefault(x => Key(x) == key);
    }

    async Task<string?> PayCallbackAsync(string chat, string kind, string arg, long messageId)
    {
        if (!_cfg.PayEnabled) return "Оплата выключена";
        switch (kind)
        {
            case "pay":
                {
                    var u = SubsOf(chat).FirstOrDefault(x => Key(x) == arg);
                    if (u == null || !Payable(u)) return "Эту подписку нельзя продлить здесь";
                    ShowPlans(chat, new List<UserRow> { u });
                    return null;
                }
            case "paysel":
                StartPay(chat);
                return null;
            case "sel":
            case "selx":
                {
                    var pay = SubsOf(chat).Where(Payable).ToList();
                    lock (_lock)
                    {
                        if (!_paySel.TryGetValue(chat, out var sel)) _paySel[chat] = sel = new();
                        if (kind == "selx") { sel.Clear(); foreach (var u in pay.Where(Expiring)) sel.Add(Key(u)); }
                        else if (pay.Any(u => Key(u) == arg)) { if (!sel.Remove(arg)) sel.Add(arg); }
                    }
                    await ShowSelection(chat, messageId);
                    return kind == "selx" && !pay.Any(Expiring) ? "Истекающих в ближайшие дни нет" : null;
                }
            case "selgo":
                {
                    HashSet<string> sel;
                    lock (_lock) sel = _paySel.TryGetValue(chat, out var s0) ? new HashSet<string>(s0) : new();
                    var users = SubsOf(chat).Where(u => Payable(u) && sel.Contains(Key(u))).ToList();
                    if (users.Count == 0) return "Отметьте хотя бы одну подписку";
                    ShowPlans(chat, users);
                    return null;
                }
            case "plan":
                {
                    var i = arg.LastIndexOf(':');
                    if (i < 0 || !int.TryParse(arg[(i + 1)..], out var months)) return null;
                    var setOrKey = arg[..i];
                    List<string> keys;
                    lock (_lock) keys = _paySets.TryGetValue(setOrKey, out var ks) ? ks : new List<string> { setOrKey };   // старые кнопки — одна подписка
                    var mine = SubsOf(chat);
                    var users = keys.Select(k => mine.FirstOrDefault(x => Key(x) == k)).Where(u => u != null && Payable(u)).Select(u => u!).ToList();
                    var plan = _cfg.PayPlans.FirstOrDefault(p => p.months == months);
                    if (users.Count == 0 || plan.months == 0) return "Тариф или подписки не найдены — начните заново";
                    var pay = new Payment
                    {
                        Id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),
                        Chat = chat, UserUuid = Key(users[0]), Username = string.Join(", ", users.Select(u => u.Username)),
                        Items = users.Select(Key).ToList(), Names = users.Select(u => u.Username).ToList(),
                        Months = months, Price = plan.price, Amount = plan.price * users.Count,
                    };
                    lock (_lock)
                    {
                        _state.Payments.Add(pay);
                        if (_state.Payments.Count > 500) _state.Payments.RemoveRange(0, _state.Payments.Count - 500);
                        _paySel.Remove(chat);
                        _state.Save(_cfg.DataDir);
                    }
                    var details = _cfg.PayDetails != "" ? _cfg.PayDetails : "реквизиты уточните у администратора";
                    var what = users.Count == 1
                        ? $"продление «{users[0].Username}» на {MonthsText(months)}"
                        : $"продление {users.Count} подписок на {MonthsText(months)} ({users.Count} × {Money(plan.price)}): {string.Join(", ", users.Select(u => u.Username))}";
                    _tg.Send(chat, $"К оплате: {Money(pay.Amount)} — {what}.\n\n" +
                                   $"Реквизиты для перевода:\n{details}\n\n" +
                                   "Переведите всю сумму одним платежом и нажмите «Я оплатил». Администратор проверит поступление, и подписки продлятся автоматически.",
                        Telegram.Inline(new[] { ("✅ Я оплатил", "paid:" + pay.Id), ("Отмена", "cancel:" + pay.Id) }));
                    return null;
                }
            case "paid":
            case "cancel":
                {
                    Payment? p;
                    lock (_lock) p = _state.Payments.FirstOrDefault(x => x.Id == arg && x.Chat == chat);
                    if (p == null) return "Заявка не найдена";
                    if (p.Status != "new") return p.Status == "sent" ? "Уже отправлено на проверку" : "Заявка уже закрыта";
                    lock (_lock)
                    {
                        p.Status = kind == "paid" ? "sent" : "cancel";
                        if (kind == "paid") _state.AwaitProof[chat] = p.Id;
                        _state.Save(_cfg.DataDir);
                    }
                    if (kind == "cancel") { _tg.Send(chat, "Заявка отменена.", ChatKeyboard(chat)); return null; }
                    _tg.Send(chat, "Спасибо! Отправил администратору на проверку — после подтверждения " + (p.Keys().Count > 1 ? "подписки продлятся" : "подписка продлится") + ", я напишу.\n\n" +
                                   "Если есть скриншот или чек перевода — пришлите его сюда, это ускорит проверку.", ChatKeyboard(chat));
                    var info = PayInfo(p);
                    foreach (var a in _cfg.Chats) _tg.Send(a, "💳 Оплата на проверку: " + info, PayButtons(p.Id));
                    Log.Write($"оплата на проверку: {p.Username} {p.Months} мес {p.Amount}");
                    return null;
                }
            case "ok":
            case "no":
                {
                    if (!_cfg.Chats.Contains(chat)) return "Подтверждать оплату может только админ";
                    Payment? p;
                    lock (_lock) p = _state.Payments.FirstOrDefault(x => x.Id == arg);
                    if (p == null) return "Заявка не найдена";
                    if (p.Status != "sent") return p.Status == "ok" ? "Уже подтверждено" : p.Status == "no" ? "Уже отклонено" : "Заявка закрыта";
                    if (kind == "no")
                    {
                        if (p.DoneItems.Count > 0) return "Часть подписок уже продлена — отклонить нельзя";
                        lock (_lock) { p.Status = "no"; p.Done = DateTime.Now; p.By = chat; _state.AwaitProof.Remove(p.Chat); _state.Save(_cfg.DataDir); }
                        _tg.Send(p.Chat, $"❌ Оплата продления «{p.Username}» не подтверждена. Если вы переводили деньги — напишите администратору.", ChatKeyboard(p.Chat));
                        foreach (var a in _cfg.Chats) _tg.Send(a, $"❌ Оплата отклонена: «{p.Username}», {Money(p.Amount)} (заявка {p.Id})", AdminKeyboard(a));
                        return "Отклонено";
                    }
                    lock (_lock)
                    {
                        if (p.Status != "sent") return "Уже обрабатывается";
                        p.Status = "busy";   // защита от двойного нажатия
                    }
                    // продлеваем каждую подписку; уже продлённые (при повторном нажатии после ошибки) пропускаем
                    var keys = p.Keys();
                    var names = p.AllNames();
                    var errors = new List<string>();
                    for (int k = 0; k < keys.Count; k++)
                    {
                        if (p.DoneItems.ContainsKey(keys[k])) continue;
                        var name = k < names.Count ? names[k] : keys[k];
                        var u = UserByKey(keys[k]);
                        if (u == null) { errors.Add($"«{name}»: пользователь не найден в панели"); continue; }
                        try
                        {
                            var newExp = await _api.ExtendUserAsync(_cfg.Writer, u, p.Months);
                            lock (_lock) { p.DoneItems[keys[k]] = newExp.ToLocalTime().ToString("dd.MM.yyyy"); _state.Save(_cfg.DataDir); }
                        }
                        catch (Exception ex) { errors.Add($"«{name}»: {ex.Message}"); Log.Write($"продление {name} не удалось: {ex.Message}"); }
                    }
                    string Summary() => string.Join(", ", keys.Select((key, k) =>
                        p.DoneItems.TryGetValue(key, out var d) ? $"«{(k < names.Count ? names[k] : key)}» до {d}" : null).Where(x => x != null));
                    if (errors.Count == 0)
                    {
                        lock (_lock)
                        {
                            p.Status = "ok"; p.Done = DateTime.Now; p.By = chat; p.NewExpire = Summary();
                            if (keys.Count == 1) p.NewExpire = p.DoneItems.Values.FirstOrDefault() ?? "";
                            _state.AwaitProof.Remove(p.Chat);
                            _state.Save(_cfg.DataDir);
                        }
                        var userText = keys.Count == 1
                            ? $"✅ Оплата получена, спасибо! Подписка «{names[0]}» продлена до {p.NewExpire}."
                            : "✅ Оплата получена, спасибо! Продлено:\n" + string.Join("\n", keys.Select((key, k) => $"• «{(k < names.Count ? names[k] : key)}» — до {p.DoneItems[key]}"));
                        _tg.Send(p.Chat, userText, ChatKeyboard(p.Chat));
                        foreach (var a in _cfg.Chats) _tg.Send(a, $"✅ Оплата подтверждена: +{MonthsText(p.Months)}, {Money(p.Amount)} → {Summary()}", AdminKeyboard(a));
                        Log.Write($"оплата подтверждена: {Summary()}");
                        await RefreshAsync();
                        return keys.Count == 1 ? "Продлено до " + p.NewExpire : $"Продлено: {keys.Count}";
                    }
                    lock (_lock) { p.Status = "sent"; _state.Save(_cfg.DataDir); }
                    var partial = p.DoneItems.Count > 0 ? $"\nУже продлено: {Summary()} — повторно не продлится." : "";
                    _tg.Send(chat, "⚠️ Не удалось продлить:\n" + string.Join("\n", errors) + partial +
                                   "\nИсправьте (или продлите вручную в панели) и нажмите «Подтвердить» ещё раз — продлятся только оставшиеся." + (errors.Any(e => e.Contains("401") || e.Contains("403")) ? " Нужен токен с правом изменять пользователей (REMNAWAVE_WRITE_TOKEN)." : ""),
                        PayButtons(p.Id));
                    if (p.DoneItems.Count > 0) await RefreshAsync();
                    return "Ошибка продления";
                }
        }
        return null;
    }

    // Скриншот перевода — копией всем админам с теми же кнопками
    void ForwardProof(string chat, string pid, long messageId)
    {
        Payment? p;
        lock (_lock) p = _state.Payments.FirstOrDefault(x => x.Id == pid && x.Chat == chat);
        if (p == null || p.Status != "sent") { lock (_lock) _state.AwaitProof.Remove(chat); _tg.Send(chat, "Эта заявка уже обработана.", ChatKeyboard(chat)); return; }
        var info = PayInfo(p);
        foreach (var a in _cfg.Chats) _tg.Copy(a, chat, messageId, "🧾 Скриншот оплаты: " + info, PayButtons(p.Id));
        _tg.Send(chat, "Скриншот передан администратору. Как только оплату подтвердят — напишу.", ChatKeyboard(chat));
    }

    PaymentsReport PaymentsReport()
    {
        lock (_lock)
            return new PaymentsReport(DateTime.Now, _cfg.Currency, _state.Payments
                .Where(p => p.Status is "sent" or "ok" or "no" or "busy")
                .Select(p => new PaymentInfo(p.Id, p.Created, p.Done, p.Status == "busy" ? "sent" : p.Status, p.Username, p.Months, p.Amount, p.NewExpire, p.Chat))
                .ToList());
    }

    // ---- проверка доступности из России (check-host.net) ----
    async Task StartCheckAsync(string chat, string arg)
    {
        arg = arg.Trim();
        if (arg != "") { _ = RunCheckAsync(chat, "addr:" + arg); return; }
        List<NodeRow> nodes;
        lock (_lock) nodes = _snap?.Nodes.Where(n => n.Address != "").ToList() ?? new();
        var rows = nodes.Select(n => new[] { ($"{Fmt.Flag(n.Country)}{n.Name} · {n.Address}", "chk:" + (n.NodeUuid != "" ? n.NodeUuid : n.Name)) }).ToList();
        if (nodes.Count > 1) rows.Add(new[] { ("Все ноды", "chk:*") });
        _tg.Send(chat, "🔎 Проверка TCP-доступности с российских точек check-host.net — видно, не заблокирован ли IP из России.\n" +
                       (nodes.Count > 0 ? "Выберите ноду (порты — из её инбаундов) или напишите /check IP:порт." : "Нод с адресом нет — напишите /check IP:порт."),
            rows.Count > 0 ? Telegram.InlineRows(rows) : AdminKeyboard(chat));
        await Task.CompletedTask;
    }

    // Порты инбаундов ноды из панели (configProfile.activeInbounds[].port); пусто — CHECK_PORT
    async Task<Dictionary<string, List<int>>> NodePortsAsync()
    {
        var res = new Dictionary<string, List<int>>();
        try
        {
            var raw = await _api.TryGetAsync(_cfg, "/api/nodes");
            if (raw == "") return res;
            using var doc = JsonDocument.Parse(raw);
            foreach (var n in J.Items(doc.RootElement, "nodes"))
            {
                var ports = new List<int>();
                if (J.P(n, "configProfile", "activeInbounds") is JsonElement ib && ib.ValueKind == JsonValueKind.Array)
                    foreach (var i in ib.EnumerateArray())
                        if (J.N(i, "port") is var pt and > 0 and < 65536 && !ports.Contains((int)pt)) ports.Add((int)pt);
                res[J.S(n, "uuid")] = ports;
            }
        }
        catch (Exception ex) { Log.Write("порты нод: " + ex.Message); }
        return res;
    }

    async Task RunCheckAsync(string chat, string what)
    {
        try
        {
            // что проверяем: (подпись, адрес:порт, нода в сети по данным панели или null — неизвестно)
            var targets = new List<(string title, string target, bool? connected)>();
            if (what.StartsWith("addr:"))
            {
                var a = what[5..].Trim().Replace("https://", "").Replace("http://", "").TrimEnd('/');
                if (a == "" || a.Contains(' ')) { _tg.Send(chat, "Формат: /check 185.200.196.14:443 (порт можно не указывать — " + _cfg.CheckPort + ")", AdminKeyboard(chat)); return; }
                var hasPort = a.StartsWith("[") ? a.Contains("]:") : a.Count(c => c == ':') == 1;
                targets.Add((a, hasPort ? a : (a.Contains(':') && !a.StartsWith("[") ? $"[{a}]" : a) + ":" + _cfg.CheckPort, null));
            }
            else
            {
                var key = what.StartsWith("chk:") ? what[4..] : what;
                List<NodeRow> nodes;
                lock (_lock) nodes = _snap?.Nodes.Where(n => n.Address != "" && (key == "*" || n.NodeUuid == key || n.Name == key)).ToList() ?? new();
                if (nodes.Count == 0) { _tg.Send(chat, "Нода не найдена — откройте /check заново.", AdminKeyboard(chat)); return; }
                var ports = await NodePortsAsync();
                foreach (var n in nodes)
                {
                    var ps = ports.TryGetValue(n.NodeUuid, out var l) && l.Count > 0 ? l.Take(3).ToList() : new List<int> { _cfg.CheckPort };
                    var host = n.Address.Contains(':') ? $"[{n.Address}]" : n.Address;
                    foreach (var p in ps) targets.Add(($"{Fmt.Flag(n.Country)}{n.Name}", $"{host}:{p}", n.Connected));
                }
            }
            _tg.Send(chat, targets.Count == 1 ? $"🔎 Проверяю {targets[0].target} из России… (до 30 сек)" : $"🔎 Проверяю {targets.Count} адрес(а) из России… (до 30 сек на каждый)", AdminKeyboard(chat));
            foreach (var t in targets)
            {
                try
                {
                    var r = await _check.CheckTcpAsync(t.target);
                    var ok = r.Points.Count(p => p.Ok);
                    var answered = r.Points.Count(p => p.Ms >= 0 || p.Ok);
                    var sb = new StringBuilder($"🔎 {t.title}" + (t.target.StartsWith(t.title + ":") || t.title == t.target ? t.target[t.title.Length..] : $" · {t.target}") + "\n");
                    foreach (var p in r.Points)
                        sb.AppendLine(p.Ok ? $"✅ {p.City} — {Math.Round(p.Ms)} мс" : p.Ms < 0 ? $"⏳ {p.City} — нет ответа от точки" : $"❌ {p.City} — {CheckHost.ErrorText(p.Error)}");
                    sb.AppendLine();
                    sb.AppendLine(answered == 0 ? "❔ Точки check-host не ответили — попробуйте позже."
                        : ok == answered ? "✅ Из России доступен."
                        : ok > 0 ? $"⚠️ Доступен из {ok} из {answered} точек — частичная блокировка или проблемы у части провайдеров."
                        : t.connected == true ? "❌ Из России недоступен, а панель с нодой связана — похоже на блокировку IP."
                        : t.connected == false ? "❌ Недоступен, и панель тоже не видит ноду — скорее проблема с сервером."
                        : "❌ Из России недоступен — блокировка, либо сервер/порт не отвечает.");
                    sb.Append("Отчёт: " + r.Link);
                    _tg.Send(chat, sb.ToString(), AdminKeyboard(chat));
                }
                catch (Exception ex)
                {
                    Log.Write($"check-host {t.target}: {ex.Message}");
                    _tg.Send(chat, $"⚠️ {t.target}: check-host не ответил ({ex.Message}). Попробуйте позже.", AdminKeyboard(chat));
                }
            }
        }
        catch (Exception ex) { Log.Write("проверка: " + ex.Message); }
    }

    List<Payment> PendingPayments()
    {
        lock (_lock) return _state.Payments.Where(p => p.Status == "sent").OrderBy(p => p.Created).ToList();
    }

    string BuildPayments()
    {
        lock (_lock)
        {
            if (!_cfg.PayEnabled) return "Оплата выключена (настройки программы → telegram-бот → оплата).";
            var list = _state.Payments.Where(p => p.Status is "sent" or "ok" or "no" or "busy").OrderByDescending(p => p.Created).ToList();
            var now = DateTime.Now;
            var month = list.Where(p => p.Status == "ok" && p.Done?.Year == now.Year && p.Done?.Month == now.Month).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"💳 Оплаты · за {now:MM.yyyy}: {month.Count} на {Money(month.Sum(p => p.Amount))}");
            var pending = list.Where(p => p.Status == "sent").ToList();
            if (pending.Count > 0) sb.AppendLine($"⏳ Ждут проверки: {pending.Count} — кнопка ниже покажет их с «Подтвердить / Отклонить»");
            sb.AppendLine();
            foreach (var p in list.Take(15))
                sb.AppendLine($"{(p.Status == "ok" ? "✅" : p.Status == "no" ? "❌" : "⏳")} {p.Created:dd.MM HH:mm} · {p.Username} · {MonthsText(p.Months)} · {Money(p.Amount)}"
                              + (p.Status == "ok" ? (p.Keys().Count > 1 ? $" → {p.NewExpire}" : $" → до {p.NewExpire}") : ""));
            if (list.Count == 0) sb.AppendLine("Заявок пока не было.");
            return sb.ToString().TrimEnd();
        }
    }

    // Кто из пользователей подключил бота (по Telegram ID из панели): подключили / не подключали / без Telegram ID
    string BuildBotUsers()
    {
        lock (_lock)
        {
            if (_snap == null) return NoData;
            var on = _byTg.Where(kv => _state.Started.Contains(kv.Key))
                .OrderByDescending(kv => _state.StartedAt.GetValueOrDefault(kv.Key)).ToList();
            var off = _byTg.Where(kv => !_state.Started.Contains(kv.Key)).OrderBy(kv => kv.Value.Min(u => u.Username)).ToList();
            var withTg = _byTg.Values.SelectMany(x => x).Select(Key).ToHashSet();
            var noTg = _snap.Users.Where(u => !withTg.Contains(Key(u)) && u.Status != "DISABLED").Select(u => u.Username).OrderBy(x => x).ToList();
            string Names(List<UserRow> l) => string.Join(", ", l.OrderBy(Unlimited).ThenBy(u => u.Username).Select(u => u.Username));

            var sb = new StringBuilder();
            sb.AppendLine($"🤝 Бот у пользователей: подключили {on.Count} из {_byTg.Count} Telegram ID");
            sb.AppendLine();
            sb.AppendLine($"✅ Подключили ({on.Count}):");
            if (on.Count == 0) sb.AppendLine("   пока никто");
            foreach (var (tg, users) in on)
                sb.AppendLine($"• {Names(users)}" + (_state.StartedAt.TryGetValue(tg, out var at) ? $" — с {at:dd.MM.yyyy}" : "")
                              + (_state.NoRemind.Contains(tg) ? " · 🔕 напоминания выкл" : ""));
            sb.AppendLine();
            sb.AppendLine($"⏳ Не подключали ({off.Count}):");
            if (off.Count == 0) sb.AppendLine("   все подключили");
            foreach (var (tg, users) in off) sb.AppendLine($"• {Names(users)} (ID {tg})");
            if (noTg.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"➖ Без Telegram ID в панели ({noTg.Count}): {string.Join(", ", noTg.Take(40))}" + (noTg.Count > 40 ? " …" : ""));
            }
            return sb.ToString().TrimEnd();
        }
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
