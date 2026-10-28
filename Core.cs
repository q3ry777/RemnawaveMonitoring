using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace RemnawaveMonitor;

public static class Secret
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(enc);
    }

    public static string Unprotect(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}

public class Settings : IPanelConfig
{
    public string BaseUrl { get; set; } = "https://panel.example.com";
    public int IntervalSec { get; set; } = 30;
    public int OnlineWindowSec { get; set; } = 300;
    public string Theme { get; set; } = "dark";
    public double Width { get; set; } = 900;
    public double Height { get; set; } = 560;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }


    // Бот на сервере: эти поля нужны только чтобы собрать команду установки (настройки → telegram-бот)
    [JsonIgnore] public string TgToken { get; set; } = "";
    public string? TgTokenEnc { get; set; }
    public string TgChatId { get; set; } = "";
    public bool TgNodes { get; set; } = true;
    public bool TgLimits { get; set; } = true;
    public bool TgBilling { get; set; } = true;
    public bool TgUpdates { get; set; } = true;
    public string TgDndTime { get; set; } = "08:00";            // кнопка «не беспокоить» в боте — до этого времени
    public bool TgQuietEnabled { get; set; }
    public List<int> TgQuietHours { get; set; } = new() { 23, 0, 1, 2, 3, 4, 5, 6, 7 };
    public bool TgUsersEvents { get; set; } = true;     // события пользователей (вебхуки)
    public bool TgPanelEvents { get; set; } = true;     // входы в панель и события панели (вебхуки)
    public string TgObservers { get; set; } = "";       // доп. админы: «ID виды» по строке
    public bool TgCabinet { get; set; } = true;         // личный кабинет пользователей
    public bool TgReminders { get; set; } = true;       // напоминания пользователям
    public int TgRemindDays { get; set; } = 3;
    public int TgRemindTraffic { get; set; } = 90;
    public bool TgWebhook { get; set; } = true;         // вебхуки панели (команда установки настраивает панель)
    [JsonIgnore] public string TgWebhookSecret { get; set; } = "";
    // Оплата переводом с подтверждением: тарифы «месяцев цена» по строке, реквизиты, токен с правом продления
    public bool TgPayEnabled { get; set; }
    public string TgPayPlans { get; set; } = "1 150\n3 400\n6 750\n12 1400";
    public string TgPayDetails { get; set; } = "";
    [JsonIgnore] public string TgWriteToken { get; set; } = "";
    public string? TgWriteTokenEnc { get; set; }
    public string? TgWebhookSecretEnc { get; set; }

    // Программа: уведомления Windows нового вида и «что нового» после обновления
    public bool ToastsEnabled { get; set; } = true;
    public string? LastChangelog { get; set; }

    // «23:00–08:00, 13:00–14:00» по набору часов
    public static string QuietRangesText(IEnumerable<int> hours)
    {
        var on = new bool[24];
        foreach (var h in hours) if (h is >= 0 and < 24) on[h] = true;
        if (on.All(x => x)) return "весь день";
        var parts = new List<string>();
        for (int h = 0; h < 24; h++)
        {
            if (!on[h] || on[(h + 23) % 24]) continue;   // начало непрерывного участка
            int len = 0;
            while (len < 24 && on[(h + len) % 24]) len++;
            parts.Add($"{h:00}:00–{(h + len) % 24:00}:00");
        }
        return string.Join(", ", parts);
    }

    public static bool TryParseTime(string text, out TimeSpan t)
    {
        t = default;
        var s = text.Trim().Replace('.', ':');
        if (!TimeSpan.TryParseExact(s, new[] { @"h\:mm", @"hh\:mm", "%h", "hh" }, CultureInfo.InvariantCulture, out t)) return false;
        return t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);
    }

    public string UpdateRepo { get; set; } = "";
    [JsonIgnore] public string UpdateToken { get; set; } = "";
    public string? UpdateTokenEnc { get; set; }

    // Отключённые уведомления: «node:<имя ноды>|<вид>»
    public List<string> Muted { get; set; } = new();

    public static readonly (string cat, string label, string shortLabel)[] NodeMuteCats =
    {
        ("node", "отключение ноды (не в сети)", "состояние"),
        ("bill", "оплата сервера", "оплата"),
    };

    public bool IsMuted(string scope, string cat) => Muted.Contains(scope + "|" + cat);

    public void ToggleMute(string scope, string cat)
    {
        var k = scope + "|" + cat;
        if (!Muted.Remove(k)) Muted.Add(k);
    }

    public string MetricsUrl { get; set; } = "";
    public string MetricsUser { get; set; } = "";

    // Секреты хранятся в файле только в зашифрованном виде (DPAPI, привязка к учётке Windows)
    [JsonIgnore] public string ApiToken { get; set; } = "";
    [JsonIgnore] public string MetricsPass { get; set; } = "";
    public string? TokenEnc { get; set; }
    public string? MetricsPassEnc { get; set; }

    // Старый открытый формат: только читаем для миграции, обратно не пишем
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Token { get; set; }

    public static string DataDir => Dir;
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemnawaveMonitor");
    static string FilePath => Path.Combine(Dir, "settings.json");
    public static string CachePath => Path.Combine(Dir, "cache.json");

    public static Settings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            s.ApiToken = Secret.Unprotect(s.TokenEnc);
            s.MetricsPass = Secret.Unprotect(s.MetricsPassEnc);
            s.UpdateToken = Secret.Unprotect(s.UpdateTokenEnc);
            s.TgWebhookSecret = Secret.Unprotect(s.TgWebhookSecretEnc);
            s.TgWriteToken = Secret.Unprotect(s.TgWriteTokenEnc);
            s.TgToken = Secret.Unprotect(s.TgTokenEnc);
            if (s.ApiToken == "" && !string.IsNullOrEmpty(s.Token))
            {
                s.ApiToken = s.Token!;   // миграция со старой версии
                s.Save();
            }
            s.Muted.RemoveAll(m => m.StartsWith("site:") || m.EndsWith("|vpn") || m.EndsWith("|sites") || m.EndsWith("|reach") || m.EndsWith("|ru"));
            s.Token = null;
            return s;
        }
        catch { return new(); }
    }

    public void Save()
    {
        Token = null;
        TokenEnc = Secret.Protect(ApiToken);
        MetricsPassEnc = Secret.Protect(MetricsPass);
        UpdateTokenEnc = Secret.Protect(UpdateToken);
        TgWebhookSecretEnc = Secret.Protect(TgWebhookSecret);
        TgWriteTokenEnc = Secret.Protect(TgWriteToken);
        TgTokenEnc = Secret.Protect(TgToken);
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}

public static class AutoRun
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "RemnawaveMonitor";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
        return k?.GetValue(ValueName) != null;
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath, true);
        if (k == null) return;
        if (on) k.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue(ValueName, false);
    }
}
