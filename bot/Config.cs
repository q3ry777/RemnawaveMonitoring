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
    public bool NotifyNodes { get; init; } = true;
    public bool NotifyLimits { get; init; } = true;
    public bool NotifyBilling { get; init; } = true;
    public bool NotifyUpdates { get; init; } = true;
    public bool NotifyUsers { get; init; } = true;      // события пользователей (вебхуки): создан, удалён, впервые подключился, новое устройство…
    public bool NotifyPanel { get; init; } = true;      // входы в панель, перезапуск панели, API-токены (вебхуки)
    // Дополнительные админы: чат → что присылать (nodes, limits, billing, updates, users, panel)
    public Dictionary<string, HashSet<string>> Observers { get; init; } = new();
    // Пользователи панели (по Telegram ID): личный кабинет и напоминания
    public bool UserCabinet { get; init; } = true;
    public bool UserReminders { get; init; } = true;
    public int RemindDays { get; init; } = 3;
    public int RemindTraffic { get; init; } = 90;
    // Вебхуки панели: бот слушает http://remnawave-bot:{WebhookPort}/webhook, подпись — WEBHOOK_SECRET
    public string WebhookSecret { get; init; } = "";
    public int WebhookPort { get; init; } = 8080;
    // Оплата переводом с подтверждением админом: тарифы (месяцев, цена), реквизиты, токен с правом продления
    public bool PayEnabled { get; init; }
    public List<(int months, int price)> PayPlans { get; init; } = new();
    public string PayDetails { get; init; } = "";
    public string Currency { get; init; } = "₽";
    public string WriteToken { get; init; } = "";
    public HashSet<int> QuietHours { get; init; } = new();
    public TimeSpan DndTime { get; init; } = TimeSpan.FromHours(8);
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
            PayEnabled = Flag("PAY_ENABLED", false),
            PayPlans = ParsePlans(Env("PAY_PLANS", "1:150,3:400,6:750,12:1400")),
            PayDetails = Env("PAY_DETAILS").Replace("\\n", "\n"),
            Currency = Env("PAY_CURRENCY", "₽"),
            WriteToken = Env("REMNAWAVE_WRITE_TOKEN"),
            QuietHours = quiet,
            DndTime = dnd,
            DataDir = Env("DATA_DIR", "/data"),
        };
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
    public List<string> NoRemind { get; set; } = new();     // отключили напоминания
    public List<string> Reminded { get; set; } = new();     // уже отправленные напоминания (ключи событий)
    public List<Payment> Payments { get; set; } = new();     // заявки на оплату
    public Dictionary<string, string> AwaitProof { get; set; } = new();
    public Dictionary<string, DateTime> Strangers { get; set; } = new();   // посторонние, о которых уже сообщили (не чаще раза в сутки)   // чат → заявка, к которой ждём скриншот
    public List<string> Alerted { get; set; } = new();     // уже отправленные тревоги по пользователям (лимит, подписка)
    public bool Seeded { get; set; }                         // первый снимок уже был — дальше всё новое считается событием
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
}

public static class Log
{
    public static void Write(string text) => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}");
}
