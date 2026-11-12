using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RemnawaveMonitor;

// Настройки бота — из переменных окружения (.env рядом с docker-compose.yml)
public class BotConfig : IPanelConfig
{
    public string BaseUrl { get; init; } = "http://remnawave:3000";
    public string ApiToken { get; init; } = "";
    public int OnlineWindowSec { get; init; } = 300;
    public string BotToken { get; init; } = "";
    public List<string> Chats { get; init; } = new();
    public string Proxy { get; init; } = "";
    public string TelegramApi { get; init; } = "https://api.telegram.org";   // для своего Bot API сервера или тестов
    public int IntervalSec { get; init; } = 30;
    public bool NotifyNodes { get; set; } = true;
    public bool NotifyLimits { get; set; } = true;
    public bool NotifyBilling { get; set; } = true;
    public bool NotifyUpdates { get; set; } = true;
    public bool NotifyUsers { get; set; } = true;      // события пользователей (вебхуки): создан, удалён, впервые подключился, новое устройство…
    public bool NotifyPanel { get; set; } = true;      // входы в панель, перезапуск панели, API-токены (вебхуки)
    // Дополнительные админы: чат → что присылать (nodes, limits, billing, updates, users, panel)
    public Dictionary<string, HashSet<string>> Observers { get; set; } = new();
    // Пользователи панели (по Telegram ID): личный кабинет и напоминания
    public bool UserCabinet { get; set; } = true;
    public bool UserReminders { get; set; } = true;
    public int RemindDays { get; set; } = 3;
    public int RemindTraffic { get; set; } = 90;
    // Вебхуки панели: бот слушает http://remnawave-bot:{WebhookPort}/webhook, подпись — WEBHOOK_SECRET
    public string WebhookSecret { get; init; } = "";
    public int WebhookPort { get; init; } = 8080;
    // Проверка доступности из РФ (check-host.net): порт, если у ноды не удалось узнать порты инбаундов
    public int CheckPort { get; init; } = 443;
    // Автопроверка доступности нод из РФ: минут между проверками (0 — выключена; не чаще раза в 10 минут)
    public int CheckIntervalMin { get; set; }
    // Статус сервиса для пользователей (кнопка в боте): включён, через сколько минут сбой появляется в статусе, названия нод «имя → название» («-» — не показывать)
    public bool StatusEnabled { get; set; }
    public int StatusDelayMin { get; set; } = 10;
    public Dictionary<string, string> StatusNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Оплата переводом с подтверждением админом: тарифы (месяцев, цена), реквизиты, токен с правом продления
    public bool PayEnabled { get; set; }
    public List<(int months, int price)> PayPlans { get; set; } = new();
    public string PayDetails { get; set; } = "";
    public string Currency { get; init; } = "₽";
    public string WriteToken { get; init; } = "";
    // API для программы на ПК (вкладка «оплаты»)
    public string ApiKey { get; init; } = "";
    public int ApiPort { get; init; } = 8090;
    public HashSet<int> QuietHours { get; set; } = new();
    public TimeSpan DndTime { get; set; } = TimeSpan.FromHours(8);
    public string DataDir { get; init; } = "/data";

    static string Env(string name, string def = "") => (Environment.GetEnvironmentVariable(name) ?? def).Trim();
    static bool Flag(string name, bool def) =>
        Env(name) is var v && v != "" ? v.ToLowerInvariant() is "1" or "true" or "yes" or "on" : def;
    static int Int(string name, int def, int min) => int.TryParse(Env(name), out var v) && v >= min ? v : def;

    public static BotConfig Load()
    {
        var quiet = new HashSet<int>();
        foreach (var part in Env("QUIET_HOURS").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            // «23,0,1» или диапазоны «23-8»
            var r = part.Split('-');
            if (r.Length == 2 && int.TryParse(r[0], out var a) && int.TryParse(r[1], out var b) && a is >= 0 and < 24 && b is >= 0 and < 24)
                for (int h = a; h != b; h = (h + 1) % 24) quiet.Add(h);
            else if (int.TryParse(part, out var h) && h is >= 0 and < 24) quiet.Add(h);
        }
        var dnd = TimeSpan.FromHours(8);
        if (TimeSpan.TryParseExact(Env("DND_TIME", "08:00").Replace('.', ':'), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t)
            && t < TimeSpan.FromDays(1)) dnd = t;

        var checkMin = Int("CHECK_INTERVAL_MIN", 0, 0);
        return new BotConfig
        {
            BaseUrl = Env("PANEL_URL", "http://remnawave:3000"),
            ApiToken = Env("REMNAWAVE_API_TOKEN"),
            OnlineWindowSec = Int("ONLINE_WINDOW_SEC", 300, 30),
            BotToken = Env("TELEGRAM_BOT_TOKEN"),
            Chats = Env("TELEGRAM_CHAT_ID").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
            Proxy = Env("TELEGRAM_PROXY"),
            TelegramApi = Env("TELEGRAM_API_URL", "https://api.telegram.org"),
            IntervalSec = Int("INTERVAL_SEC", 30, 10),
            NotifyNodes = Flag("NOTIFY_NODES", true),
            NotifyLimits = Flag("NOTIFY_LIMITS", true),
            NotifyBilling = Flag("NOTIFY_BILLING", true),
            NotifyUpdates = Flag("NOTIFY_UPDATES", true),
            NotifyUsers = Flag("NOTIFY_USERS", true),
            NotifyPanel = Flag("NOTIFY_PANEL", true),
            Observers = ParseObservers(Env("TELEGRAM_OBSERVERS")),
            UserCabinet = Flag("USER_CABINET", true),
            UserReminders = Flag("USER_REMINDERS", true),
            RemindDays = Int("REMIND_DAYS", 3, 1),
            RemindTraffic = Math.Min(99, Int("REMIND_TRAFFIC", 90, 50)),
            WebhookSecret = Env("WEBHOOK_SECRET"),
            WebhookPort = Int("WEBHOOK_PORT", 8080, 1),
            CheckPort = Int("CHECK_PORT", 443, 1),
            CheckIntervalMin = checkMin == 0 ? 0 : Math.Min(Math.Max(checkMin, 10), 1440),
            StatusEnabled = Flag("STATUS_ENABLED", false),
            StatusDelayMin = Math.Min(Int("STATUS_DELAY_MIN", 10, 0), 1440),
            StatusNames = ParseNames(Env("STATUS_NAMES")),
            PayEnabled = Flag("PAY_ENABLED", false),
            PayPlans = ParsePlans(Env("PAY_PLANS", "1:150,3:400,6:750,12:1400")),
            PayDetails = Env("PAY_DETAILS").Replace("\\n", "\n"),
            Currency = Env("PAY_CURRENCY", "₽"),
            WriteToken = Env("REMNAWAVE_WRITE_TOKEN"),
            ApiKey = Env("BOT_API_KEY"),
            ApiPort = Int("BOT_API_PORT", 8090, 1),
            QuietHours = quiet,
            DndTime = dnd,
            DataDir = Env("DATA_DIR", "/data"),
        };
    }

    // ---- настройки, которые меняются в боте (/settings) и хранятся в {DataDir}/settings.json ----
    // При первом запуске файл создаётся из .env (перенос прежних значений), дальше значения из .env для этих настроек не читаются.
    string SettingsPath => Path.Combine(DataDir, "settings.json");

    public BotSettings ToSettings() => new()
    {
        NotifyNodes = NotifyNodes, NotifyLimits = NotifyLimits, NotifyBilling = NotifyBilling, NotifyUpdates = NotifyUpdates,
        NotifyUsers = NotifyUsers, NotifyPanel = NotifyPanel,
        Observers = string.Join(";", Observers.Select(kv => kv.Key + ":" + string.Join(",", kv.Value))),
        UserCabinet = UserCabinet, UserReminders = UserReminders, RemindDays = RemindDays, RemindTraffic = RemindTraffic,
        CheckIntervalMin = CheckIntervalMin, StatusEnabled = StatusEnabled, StatusDelayMin = StatusDelayMin,
        StatusNames = string.Join(";", StatusNames.Select(kv => kv.Key + "=" + kv.Value)),
        PayEnabled = PayEnabled, PayPlans = string.Join(",", PayPlans.Select(x => $"{x.months}:{x.price}")), PayDetails = PayDetails,
        QuietHours = QuietHours.OrderBy(h => h).ToList(), DndTime = DndTime.ToString(@"hh\:mm"),
    };

    public void Apply(BotSettings s)
    {
        NotifyNodes = s.NotifyNodes; NotifyLimits = s.NotifyLimits; NotifyBilling = s.NotifyBilling; NotifyUpdates = s.NotifyUpdates;
        NotifyUsers = s.NotifyUsers; NotifyPanel = s.NotifyPanel;
        Observers = ParseObservers(s.Observers);
        UserCabinet = s.UserCabinet; UserReminders = s.UserReminders; RemindDays = s.RemindDays; RemindTraffic = s.RemindTraffic;
        CheckIntervalMin = s.CheckIntervalMin; StatusEnabled = s.StatusEnabled; StatusDelayMin = s.StatusDelayMin;
        StatusNames = ParseNames(s.StatusNames);
        PayEnabled = s.PayEnabled; PayPlans = ParsePlans(s.PayPlans); PayDetails = s.PayDetails;
        QuietHours = s.QuietHours.Where(h => h is >= 0 and < 24).ToHashSet();
        if (TimeSpan.TryParseExact(s.DndTime, @"hh\:mm", CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1)) DndTime = t;
    }

    // Файл есть — настройки из него; нет — создаётся из значений .env
    public void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<BotSettings>(File.ReadAllText(SettingsPath));
                if (s != null) { Apply(s); Log.Write("настройки: загружены из settings.json"); return; }
            }
            SaveSettings();
            Log.Write("настройки: settings.json создан из .env — дальше они меняются в боте (/settings)");
        }
        catch (Exception ex) { Log.Write("настройки: не удалось прочитать settings.json, работаю по .env: " + ex.Message); }
    }

    public void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(ToSettings(), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(tmp, SettingsPath, true);
        }
        catch (Exception ex) { Log.Write("настройки: не удалось сохранить settings.json: " + ex.Message); }
    }

    public static readonly string[] Kinds = { "nodes", "limits", "billing", "updates", "users", "panel" };

    // «123:nodes,billing;456:nodes» (пустой список видов — всё)
    static Dictionary<string, HashSet<string>> ParseObservers(string text)
    {
        var map = new Dictionary<string, HashSet<string>>();
        foreach (var part in text.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':', 2);
            var id = kv[0].Trim();
            if (id == "" || !long.TryParse(id, out _)) continue;
            var kinds = kv.Length > 1
                ? kv[1].Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).Where(Kinds.Contains).ToHashSet()
                : new HashSet<string>();
            map[id] = kinds.Count > 0 ? kinds : Kinds.ToHashSet();
        }
        return map;
    }

    // «Aeza_SWE=Швеция №1;Hostoff_NL=-» (имя ноды в панели = название для пользователей; «-» — не показывать)
    static Dictionary<string, string> ParseNames(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim() != "" && kv[1].Trim() != "") map[kv[0].Trim()] = kv[1].Trim();
        }
        return map;
    }

    // «1:150,3:400» — месяцев:цена
    static List<(int, int)> ParsePlans(string text)
    {
        var list = new List<(int, int)>();
        foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            if (kv.Length == 2 && int.TryParse(kv[0], out var m) && int.TryParse(kv[1], out var p) && m is > 0 and <= 36 && p >= 0) list.Add((m, p));
        }
        return list.OrderBy(x => x.Item1).ToList();
    }

    // Для продления подписки — отдельный токен с правом записи, если задан
    public IPanelConfig Writer => new WriterConfig(BaseUrl, WriteToken != "" ? WriteToken : ApiToken, OnlineWindowSec);
    record WriterConfig(string BaseUrl, string ApiToken, int OnlineWindowSec) : IPanelConfig;

    public IEnumerable<string> AllAdminChats => Chats.Concat(Observers.Keys).Distinct();

    // Вид тревоги включён для основных админов
    public bool KindEnabled(string kind) => kind switch
    {
        "nodes" => NotifyNodes, "limits" => NotifyLimits, "billing" => NotifyBilling, "updates" => NotifyUpdates,
        "users" => NotifyUsers, "panel" => NotifyPanel, _ => true,
    };

    public List<string> Problems()
    {
        var p = new List<string>();
        if (ApiToken == "") p.Add("не задан REMNAWAVE_API_TOKEN");
        if (BotToken == "") p.Add("не задан TELEGRAM_BOT_TOKEN");
        if (Chats.Count == 0) p.Add("не задан TELEGRAM_CHAT_ID");
        return p;
    }

    // Подпись настроек: при изменении бот присылает приветствие с меню
    public string Signature =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(BotToken + "|" + string.Join(",", Chats))))[..16];
}

// Состояние между перезапусками (/data/state.json)
public class BotState
{
    public DateTime? DndUntil { get; set; }                  // старый формат (один на всех) — переносится в Dnd
    public Dictionary<string, DateTime> Dnd { get; set; } = new();   // «не беспокоить» по чатам
    public List<string> Started { get; set; } = new();      // пользователи, нажавшие /start (им можно писать первым)
    public Dictionary<string, DateTime> StartedAt { get; set; } = new();   // когда подключили бота
    public List<string> NoRemind { get; set; } = new();     // отключили напоминания
    public List<string> Reminded { get; set; } = new();     // уже отправленные напоминания (ключи событий)
    public List<Payment> Payments { get; set; } = new();     // заявки на оплату
    public Dictionary<string, string> AwaitProof { get; set; } = new();
    public Dictionary<string, DateTime> Strangers { get; set; } = new();   // посторонние, о которых уже сообщили (не чаще раза в сутки)   // чат → заявка, к которой ждём скриншот
    public List<string> Alerted { get; set; } = new();     // уже отправленные тревоги по пользователям (лимит, подписка)
    public bool Seeded { get; set; }                         // первый снимок уже был — дальше всё новое считается событием
    public List<StatusHide> StatusHides { get; set; } = new();           // скрытые админом из статуса сервиса сбои нод (окна времени)
    public Dictionary<string, string> RuCheck { get; set; } = new();   // автопроверка из РФ: нода → последний подтверждённый уровень (ok / warn / bad)
    public string? LatestNode { get; set; }
    public string? LatestBackend { get; set; }
    public string Signature { get; set; } = "";
    public DateTime Since { get; set; } = DateTime.Now;

    static string PathIn(string dir) => System.IO.Path.Combine(dir, "state.json");

    public static BotState Load(string dir)
    {
        try { return JsonSerializer.Deserialize<BotState>(File.ReadAllText(PathIn(dir))) ?? new(); }
        catch { return new(); }
    }

    public void Save(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var tmp = PathIn(dir) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, PathIn(dir), true);
        }
        catch (Exception ex) { Log.Write("не удалось сохранить состояние: " + ex.Message); }
    }
}

// Содержимое settings.json: то, что настраивается в боте. Списки хранятся в тех же текстовых форматах, что были в .env
public class BotSettings
{
    public bool NotifyNodes { get; set; } = true;
    public bool NotifyLimits { get; set; } = true;
    public bool NotifyBilling { get; set; } = true;
    public bool NotifyUpdates { get; set; } = true;
    public bool NotifyUsers { get; set; } = true;
    public bool NotifyPanel { get; set; } = true;
    public string Observers { get; set; } = "";      // «123:nodes,billing;456:nodes»
    public bool UserCabinet { get; set; } = true;
    public bool UserReminders { get; set; } = true;
    public int RemindDays { get; set; } = 3;
    public int RemindTraffic { get; set; } = 90;
    public int CheckIntervalMin { get; set; }
    public bool StatusEnabled { get; set; }
    public int StatusDelayMin { get; set; } = 10;
    public string StatusNames { get; set; } = "";    // «Aeza_SWE=Швеция №1;Hostoff_NL=-»
    public bool PayEnabled { get; set; }
    public string PayPlans { get; set; } = "";       // «1:150,3:400»
    public string PayDetails { get; set; } = "";
    public List<int> QuietHours { get; set; } = new();
    public string DndTime { get; set; } = "08:00";
}

// Окно скрытия ноды из статуса: сбои ноды, пересекающиеся с [From, Until], пользователям не показываются
public class StatusHide
{
    public string Node { get; set; } = "";
    public DateTime From { get; set; }
    public DateTime Until { get; set; }
}

// Заявка на оплату: пользователь выбрал тариф → «я оплатил» → админ подтверждает → подписка продлевается
public class Payment
{
    public string Id { get; set; } = "";
    public string Chat { get; set; } = "";
    public string UserUuid { get; set; } = "";
    public string Username { get; set; } = "";
    public int Months { get; set; }
    public int Amount { get; set; }
    public string Status { get; set; } = "new";   // new — выбран тариф, sent — ждёт проверки, ok — подтверждена, no — отклонена, cancel — отменена
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? Done { get; set; }
    public string By { get; set; } = "";
    public string NewExpire { get; set; } = "";
    // Несколько подписок в одной заявке (массовая оплата). Пусто — старая заявка на одну подписку (UserUuid/Username)
    public List<string> Items { get; set; } = new();
    public List<string> Names { get; set; } = new();
    public Dictionary<string, string> DoneItems { get; set; } = new();   // продлённые: ключ подписки → новая дата
    public int Price { get; set; }                                        // цена за одну подписку

    public List<string> Keys() => Items.Count > 0 ? Items : new List<string> { UserUuid };
    public List<string> AllNames() => Names.Count > 0 ? Names : new List<string> { Username };
}

public static class Log
{
    public static void Write(string text) => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}");
}
