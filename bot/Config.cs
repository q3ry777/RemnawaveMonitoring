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
            QuietHours = quiet,
            DndTime = dnd,
            DataDir = Env("DATA_DIR", "/data"),
        };
    }

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
    public DateTime? DndUntil { get; set; }
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

public static class Log
{
    public static void Write(string text) => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}");
}
