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

public class Settings
{
    public string BaseUrl { get; set; } = "https://panel.example.com";
    public int IntervalSec { get; set; } = 30;
    public int OnlineWindowSec { get; set; } = 300;
    public string Theme { get; set; } = "dark";
    public double Width { get; set; } = 900;
    public double Height { get; set; } = 560;

    public bool ProbeEnabled { get; set; } = true;
    public bool RuCheckEnabled { get; set; } = true;
    public int ProbePort { get; set; } = 443;
    public string ServicesText { get; set; } =
        "https://www.broken-tech.ru\nhttps://status.broken-tech.ru\nhttps://chat.broken-tech.ru\nhttps://bforum.broken-tech.ru\nhttps://mail.broken-tech.ru\nhttps://admin.broken-tech.ru";

    public DateTime? MonitorSince { get; set; }
    public bool TgEnabled { get; set; }
    [JsonIgnore] public string TgToken { get; set; } = "";
    public string? TgTokenEnc { get; set; }
    public string TgChatId { get; set; } = "";
    public string TgProxy { get; set; } = "";
    public bool TgNodes { get; set; } = true;
    public bool TgServices { get; set; } = true;
    public bool TgLimits { get; set; } = true;
    public bool TgBilling { get; set; } = true;
    public bool TgUpdates { get; set; } = true;

    public string UpdateRepo { get; set; } = "";
    [JsonIgnore] public string UpdateToken { get; set; } = "";
    public string? UpdateTokenEnc { get; set; }

    public List<SshServer> SshServers { get; set; } = new();

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
            s.TgToken = Secret.Unprotect(s.TgTokenEnc);
            foreach (var x in s.SshServers) x.Password = Secret.Unprotect(x.PasswordEnc);
            if (s.ApiToken == "" && !string.IsNullOrEmpty(s.Token))
            {
                s.ApiToken = s.Token!;   // миграция со старой версии
                s.Save();
            }
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
        TgTokenEnc = Secret.Protect(TgToken);
        foreach (var x in SshServers) x.PasswordEnc = Secret.Protect(x.Password);
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}

public record UserRow(
    string Username, string Status, string Traffic, string LastNode, string Online, bool IsOnline, string LastNodeCountry,
    double TrafficBytes, DateTimeOffset? LastSeen, bool HasLimit, double TrafficPct, string TrafficLevel,
    string ExpireNote, string ExpireLevel, string SubscriptionUrl);

public record NodeRow(
    string Name, string Country, string Status, string Xray, string Cpu,
    string MemText, string MemPctText, double MemPct, string MemLevel,
    string Load, string Down, string Up,
    string TrafficText, bool HasLimit, double TrafficPct, string TrafficLevel,
    int UsersOnline, bool Connected, bool XrayOk, string Address,
    string PingText = "", string PingLevel = "", string BillingText = "", string BillingLevel = "",
    string RuText = "", string RuLevel = "", string NodeVersion = "", string XrayVersion = "", string VersionText = "", string VersionLevel = "",
    double SrvCpu = -1, double SrvRam = -1, double SrvDisk = -1, string SrvRamText = "", string SrvDiskText = "", string SrvNote = "")
{
    // Нагрузка сервера: CPU и диск — только по SSH; память — по SSH, а без него — из панели
    static string Lvl(double v, double warn, double crit) => v < 0 ? "none" : v >= crit ? "crit" : v >= warn ? "warn" : "ok";
    static string Pct(double v) => $"{v * 100:0}%";

    public double CpuVal => Math.Max(0, SrvCpu);
    public string CpuPctText => SrvCpu >= 0 ? Pct(SrvCpu) : "—";
    public string CpuLevel => Lvl(SrvCpu, 0.7, 0.9);
    public string CpuTip => string.Join(" · ", new[] { Cpu != "—" ? Cpu : "", Load, SrvCpu < 0 ? "загрузка CPU — по SSH (настройки → серверы)" : "" }.Where(x => x != ""));

    public double RamVal => SrvRam >= 0 ? SrvRam : MemPct;
    public string RamPctText => SrvRam >= 0 ? Pct(SrvRam) : MemPctText != "" ? MemPctText : "—";
    public string RamLevel => SrvRam >= 0 ? Lvl(SrvRam, 0.75, 0.9) : MemPctText != "" ? MemLevel : "none";
    public string RamTip => SrvRam >= 0 ? SrvRamText : MemText != "—" ? MemText + " (данные панели)" : "нет данных";

    public double DiskVal => Math.Max(0, SrvDisk);
    public string DiskPctText => SrvDisk >= 0 ? Pct(SrvDisk) : "—";
    public string DiskLevel => Lvl(SrvDisk, 0.8, 0.9);
    public string DiskTip => SrvDisk >= 0 ? SrvDiskText : "диск — по SSH (настройки → серверы)";
}
public record Snapshot(List<UserRow> Users, List<NodeRow> Nodes, DateTime At);

public class Api
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    async Task<string> GetAsync(Settings s, string path, int attempts = 3)
    {
        Exception? last = null;
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, s.BaseUrl.TrimEnd('/') + path);
                if (!string.IsNullOrEmpty(s.ApiToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiToken);
                using var res = await _http.SendAsync(req);
                res.EnsureSuccessStatusCode();
                return await res.Content.ReadAsStringAsync();
            }
            catch (Exception e) { last = e; await Task.Delay(500 * (1 << i)); }
        }
        throw last!;
    }

    // Публичный необязательный запрос (одна попытка): пустая строка при ошибке
    public Task<string> TryGetAsync(Settings s, string path) => GetOptionalAsync(s, path);

    // Необязательный запрос: при ошибке (старая версия панели и т.п.) просто пустая строка
    async Task<string> GetOptionalAsync(Settings s, string path)
    {
        try { return await GetAsync(s, path, 1); }
        catch { return ""; }
    }

    public async Task<(string users, string nodes, string metrics, string billing)> FetchAsync(Settings s)
    {
        var u = GetAsync(s, "/api/users?size=1000&start=0");
        var n = GetAsync(s, "/api/nodes");
        var m = GetOptionalAsync(s, "/api/system/nodes/metrics");
        var b = GetOptionalAsync(s, "/api/infra-billing/nodes");
        await Task.WhenAll(u, n, m, b);
        return (u.Result, n.Result, m.Result, b.Result);
    }

    // Сырой текст Prometheus-метрик (Basic Auth) — пока только для сохранения в файл и разбора формата
    public static async Task<string> FetchMetricsAsync(string url, string user, string pass)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (user != "")
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
        using var res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync();
    }
}

static class J
{
    public static JsonElement? P(JsonElement e, params string[] path)
    {
        foreach (var k in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(k, out e)) return null;
        return e;
    }
    public static string S(JsonElement e, params string[] p)
    {
        if (P(e, p) is not JsonElement x || x.ValueKind == JsonValueKind.Null) return "";
        return x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString();
    }
    public static double N(JsonElement e, params string[] p)
    {
        if (P(e, p) is not JsonElement x) return 0;
        if (x.ValueKind == JsonValueKind.Number) return x.GetDouble();
        if (x.ValueKind == JsonValueKind.String &&
            double.TryParse(x.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return 0;
    }
    public static IEnumerable<JsonElement> Items(JsonElement root, string key)
    {
        var r = root.TryGetProperty("response", out var x) ? x : root;
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty(key, out var y)) r = y;
        return r.ValueKind == JsonValueKind.Array ? r.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }
    // Рекурсивный поиск поля по имени: пути к system.stats у разных версий панели отличаются
    public static JsonElement? Find(JsonElement e, string name, int depth = 0)
    {
        if (depth > 6 || e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty(name, out var v)) return v;
        foreach (var p in e.EnumerateObject())
        {
            var r = Find(p.Value, name, depth + 1);
            if (r != null) return r;
        }
        return null;
    }
    public static string FindS(JsonElement e, string name)
    {
        if (Find(e, name) is not JsonElement x || x.ValueKind != JsonValueKind.String) return "";
        return x.GetString() ?? "";
    }
    public static double Num(JsonElement? e)
    {
        if (e is not JsonElement x) return 0;
        if (x.ValueKind == JsonValueKind.Number) return x.GetDouble();
        if (x.ValueKind == JsonValueKind.String &&
            double.TryParse(x.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return 0;
    }
    public static string Compact(double b)
    {
        string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        int i = 0;
        while (b >= 1024 && i < 4) { b /= 1024; i++; }
        return (b >= 100 ? $"{b:0}" : b >= 10 ? $"{b:0.#}" : $"{b:0.##}") + " " + u[i];
    }
    // "287.02 MiB" / "9.76 GiB" / "0" -> байты
    public static double ParseSize(string s)
    {
        var m = Regex.Match(s.Trim(), @"^([\d.]+)\s*([KMGT]?i?B)?$");
        if (!m.Success) return 0;
        double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "KiB" => v * 1024, "MiB" => v * 1024 * 1024, "GiB" => v * Math.Pow(1024, 3), "TiB" => v * Math.Pow(1024, 4), _ => v,
        };
    }
    public static string Bytes(double b)
    {
        string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        int i = 0;
        while (b >= 1024 && i < 4) { b /= 1024; i++; }
        return $"{b:0.##} {u[i]}";
    }
}

public static class SnapshotBuilder
{
    public static Snapshot Build(string usersJson, string nodesJson, Settings s, string metricsJson = "", string billingJson = "")
    {
        using var ud = JsonDocument.Parse(usersJson);
        using var nd = JsonDocument.Parse(nodesJson);

        // трафик по нодам: сумма исходящих (outbounds) за период, который ведёт сама панель
        var nodeTraffic = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(metricsJson))
        {
            try
            {
                using var md = JsonDocument.Parse(metricsJson);
                foreach (var m in J.Items(md.RootElement, "nodes"))
                {
                    double down = 0, up = 0;
                    if (m.TryGetProperty("outboundsStats", out var os) && os.ValueKind == JsonValueKind.Array)
                        foreach (var o in os.EnumerateArray())
                        {
                            down += J.ParseSize(J.S(o, "download"));
                            up += J.ParseSize(J.S(o, "upload"));
                        }
                    nodeTraffic[J.S(m, "nodeUuid")] = $"↓{J.Compact(down)} ↑{J.Compact(up)}";
                }
            }
            catch { }
        }

        // даты оплаты нод из биллинга панели (если он там настроен)
        var billing = new Dictionary<string, (DateTimeOffset date, string provider)>();
        if (!string.IsNullOrEmpty(billingJson))
        {
            try
            {
                using var bd = JsonDocument.Parse(billingJson);
                foreach (var bn in J.Items(bd.RootElement, "billingNodes"))
                {
                    var id = J.S(bn, "nodeUuid");
                    if (id == "") id = J.S(bn, "node", "uuid");
                    if (id != "" && DateTimeOffset.TryParse(J.S(bn, "nextBillingAt"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var due))
                        billing[id] = (due, J.S(bn, "provider", "name"));
                }
            }
            catch { }
        }

        var names = new Dictionary<string, string>();
        var countries = new Dictionary<string, string>();
        var nodes = new List<NodeRow>();
        foreach (var n in J.Items(nd.RootElement, "nodes"))
        {
            var name = J.S(n, "name");
            names[J.S(n, "uuid")] = name;
            countries[J.S(n, "uuid")] = J.S(n, "countryCode");

            bool connected = J.P(n, "isConnected") is { ValueKind: JsonValueKind.True };
            double up = J.N(n, "xrayUptime");
            bool xrayOk = up > 0;
            double cpus = J.N(n, "system", "info", "cpus");
            if (cpus == 0) cpus = J.N(n, "cpuCount");
            double used = J.N(n, "system", "stats", "memoryUsed");
            double total = J.N(n, "system", "info", "memoryTotal");
            if (total == 0) total = J.N(n, "totalRam");
            var t = TimeSpan.FromSeconds(up);

            double rx = 0, tx = 0; string load = "";
            if (J.P(n, "system") is JsonElement sys)
            {
                rx = J.Num(J.Find(sys, "rxBytesPerSec"));
                tx = J.Num(J.Find(sys, "txBytesPerSec"));
                if (J.Find(sys, "loadAvg") is { ValueKind: JsonValueKind.Array } la)
                    load = "load " + string.Join(" ", la.EnumerateArray().Take(3).Select(x =>
                        x.ValueKind == JsonValueKind.Number ? x.GetDouble().ToString("0.00", CultureInfo.InvariantCulture) : "?"));
            }

            double memPct = total > 0 ? Math.Min(1, used / total) : 0;
            string memLevel = memPct >= 0.9 ? "crit" : memPct >= 0.75 ? "warn" : "ok";

            var uuid = J.S(n, "uuid");
            bool hasUsed = n.TryGetProperty("trafficUsedBytes", out var tu) && tu.ValueKind != JsonValueKind.Null;
            double tUsed = J.N(n, "trafficUsedBytes"), tLimit = J.N(n, "trafficLimitBytes");
            bool hasLimit = tLimit > 0;
            double tPct = hasLimit ? Math.Min(1, tUsed / tLimit) : 0;
            string tLevel = tPct >= 0.95 ? "crit" : tPct >= 0.8 ? "warn" : "ok";
            string trafficText = hasUsed
                ? $"{J.Bytes(tUsed)} / {(hasLimit ? J.Bytes(tLimit) : "∞")}"
                : nodeTraffic.GetValueOrDefault(uuid, "—");

            string billText = "", billLevel = "";
            if (billing.TryGetValue(uuid, out var bill))
            {
                var left = bill.date - DateTimeOffset.UtcNow;
                int days = (int)Math.Ceiling(left.TotalDays);
                billText = (left < TimeSpan.Zero ? "оплата просрочена" : days <= 0 ? "оплата сегодня" : $"оплата через {days} д")
                           + $" ({bill.date.ToLocalTime():dd.MM})" + (bill.provider != "" ? $" · {bill.provider}" : "");
                billLevel = left < TimeSpan.Zero || days <= 3 ? "crit" : days <= 7 ? "warn" : "ok";
            }

            nodes.Add(new NodeRow(
                name, J.S(n, "countryCode"),
                connected ? "В сети" : "Не в сети",
                xrayOk ? $"xray {(int)t.TotalDays}д {t.Hours}ч {t.Minutes}м" : "xray остановлен",
                cpus > 0 ? $"{cpus} vCPU" : "—",
                total > 0 ? $"{J.Bytes(used)} / {J.Bytes(total)}" : "—",
                total > 0 ? $"{memPct * 100:0}%" : "", memPct, memLevel,
                load,
                rx > 0 || tx > 0 ? $"↓ {J.Compact(rx)}/с" : "",
                rx > 0 || tx > 0 ? $"↑ {J.Compact(tx)}/с" : "",
                trafficText, hasLimit, tPct, tLevel,
                (int)J.N(n, "usersOnline"), connected, xrayOk, J.S(n, "address"),
                BillingText: billText, BillingLevel: billLevel,
                NodeVersion: J.FindS(n, "nodeVersion"), XrayVersion: J.FindS(n, "xrayVersion")));
        }

        var users = new List<UserRow>();
        foreach (var u in J.Items(ud.RootElement, "users"))
        {
            double traffic = J.N(u, "userTraffic", "usedTrafficBytes");
            if (traffic == 0) traffic = J.N(u, "usedTrafficBytes");
            double limit = J.N(u, "trafficLimitBytes");
            bool hasLimit = limit > 0;
            double pct = hasLimit ? traffic / limit : 0;
            string level = pct >= 0.95 ? "crit" : pct >= 0.8 ? "warn" : "ok";

            var uuid = J.S(u, "userTraffic", "lastConnectedNodeUuid");
            if (uuid == "") uuid = J.S(u, "lastConnectedNodeUuid");
            var onlineAt = J.S(u, "userTraffic", "onlineAt");
            if (onlineAt == "") onlineAt = J.S(u, "onlineAt");

            bool online = false; string last = "—"; DateTimeOffset? seen = null;
            if (DateTimeOffset.TryParse(onlineAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts))
            {
                seen = ts;
                online = (DateTimeOffset.UtcNow - ts).TotalSeconds < s.OnlineWindowSec;
                last = online ? "сейчас" : ts.ToLocalTime().ToString("dd.MM HH:mm");
            }

            string note = "", noteLevel = "";
            if (DateTimeOffset.TryParse(J.S(u, "expireAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exp))
            {
                var left = exp - DateTimeOffset.UtcNow;
                if (left < TimeSpan.Zero) { note = "истёк"; noteLevel = "crit"; }
                else if (left.TotalDays <= 7)
                {
                    note = left.TotalDays >= 1 ? $"ещё {(int)left.TotalDays} д" : $"ещё {Math.Max(1, (int)left.TotalHours)} ч";
                    noteLevel = left.TotalDays <= 3 ? "crit" : "warn";
                }
            }

            users.Add(new UserRow(
                J.S(u, "username"), J.S(u, "status"),
                hasLimit ? $"{J.Bytes(traffic)} / {J.Bytes(limit)}" : J.Bytes(traffic),
                names.GetValueOrDefault(uuid, "—"), last, online, countries.GetValueOrDefault(uuid, ""),
                traffic, seen, hasLimit, Math.Min(1, pct), level, note, noteLevel, J.S(u, "subscriptionUrl")));
        }
        users = users.OrderByDescending(x => x.IsOnline).ThenBy(x => x.Username).ToList();
        return new Snapshot(users, nodes, DateTime.Now);
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
