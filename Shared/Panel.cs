using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

// Общий код программы на ПК и бота на сервере: API панели и разбор ответа.

// Откуда брать данные: адрес и токен панели, окно «онлайн»
public interface IPanelConfig
{
    string BaseUrl { get; }
    string ApiToken { get; }
    int OnlineWindowSec { get; }
}

public record UserRow(
    string Username, string Status, string Traffic, string LastNode, string Online, bool IsOnline, string LastNodeCountry,
    double TrafficBytes, DateTimeOffset? LastSeen, bool HasLimit, double TrafficPct, string TrafficLevel,
    string ExpireNote, string ExpireLevel, string SubscriptionUrl,
    string UserId = "", string UserUuid = "", string Ip = "", string IpTip = "");

public record NodeRow(
    string Name, string Country, string Status, string Xray, string Cpu,
    string MemText, string MemPctText, double MemPct, string MemLevel,
    string Load, string Down, string Up,
    string TrafficText, bool HasLimit, double TrafficPct, string TrafficLevel,
    int UsersOnline, bool Connected, bool XrayOk, string Address,
    string BillingText = "", string BillingLevel = "",
    string NodeVersion = "", string XrayVersion = "", string VersionText = "", string VersionLevel = "",
    string MutedText = "", string NodeUuid = "", string BillingShort = "")
{
    // Память — из данных панели
    public double RamVal => MemPct;
    public string RamPctText => MemPctText != "" ? MemPctText : "—";
    public string RamLevel => MemPctText != "" ? MemLevel : "none";
    public string RamTip => MemText != "—" ? MemText : "нет данных";
    // «2 vCPU · load 0.10 0.20 0.15»
    public string LoadText => string.Join(" · ", new[] { Cpu != "—" ? Cpu : "", Load }.Where(x => x != ""));
}
public record Snapshot(List<UserRow> Users, List<NodeRow> Nodes, DateTime At);

public class Api
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // IP активных подключений пользователей на ноде (панель 3.x: /api/connections, 2.x: /api/ip-control).
    // Панель опрашивает ноду асинхронно: сначала задание, потом результат. -> (id пользователя, IP, когда виден)
    public async Task<List<(string user, string ip, DateTimeOffset seen)>> FetchNodeIpsAsync(IPanelConfig s, string nodeUuid)
    {
        string jobJson;
        string resultPath;
        try
        {
            jobJson = await SendAsync(s, HttpMethod.Post, $"/api/connections/by-node/{nodeUuid}");
            resultPath = "/api/connections/by-node/";
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            jobJson = await SendAsync(s, HttpMethod.Post, $"/api/ip-control/fetch-users-ips/{nodeUuid}");
            resultPath = "/api/ip-control/fetch-users-ips/result/";
        }
        string jobId;
        using (var jd = JsonDocument.Parse(jobJson)) jobId = J.S(jd.RootElement, "response", "jobId");
        if (jobId == "") throw new Exception("панель не вернула jobId");

        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(1500);
            using var d = JsonDocument.Parse(await SendAsync(s, HttpMethod.Get, resultPath + jobId));
            var r = J.P(d.RootElement, "response");
            if (r is not JsonElement resp) break;
            if (J.P(resp, "isFailed") is { ValueKind: JsonValueKind.True }) throw new Exception("нода не ответила на запрос подключений");
            if (J.P(resp, "isCompleted") is not { ValueKind: JsonValueKind.True }) continue;
            var list = new List<(string, string, DateTimeOffset)>();
            if (J.P(resp, "result", "users") is { ValueKind: JsonValueKind.Array } users)
                foreach (var u in users.EnumerateArray())
                {
                    var id = J.S(u, "userId");
                    if (u.TryGetProperty("ips", out var ips) && ips.ValueKind == JsonValueKind.Array)
                        foreach (var ip in ips.EnumerateArray())
                        {
                            DateTimeOffset.TryParse(J.S(ip, "lastSeen"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var seen);
                            list.Add((id, J.S(ip, "ip"), seen));
                        }
                }
            return list;
        }
        throw new Exception("панель не дождалась ответа ноды");
    }

    // Запрос к панели. Внутри сети Docker (http://remnawave:3000) панель требует заголовки обратного прокси —
    // без них отвечает отказом, поэтому для адресов http:// они добавляются.
    static HttpRequestMessage Request(IPanelConfig s, HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, s.BaseUrl.TrimEnd('/') + path);
        if (!string.IsNullOrEmpty(s.ApiToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiToken);
        if (s.BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1");
            req.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        }
        return req;
    }

    async Task<string> SendAsync(IPanelConfig s, HttpMethod method, string path)
    {
        using var req = Request(s, method, path);
        if (method == HttpMethod.Post) req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync();
    }

    async Task<string> GetAsync(IPanelConfig s, string path, int attempts = 3)
    {
        Exception? last = null;
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                using var req = Request(s, HttpMethod.Get, path);
                using var res = await _http.SendAsync(req);
                res.EnsureSuccessStatusCode();
                return await res.Content.ReadAsStringAsync();
            }
            catch (Exception e) { last = e; await Task.Delay(500 * (1 << i)); }
        }
        throw last!;
    }

    // Публичный необязательный запрос (одна попытка): пустая строка при ошибке
    public Task<string> TryGetAsync(IPanelConfig s, string path) => GetOptionalAsync(s, path);

    // Необязательный запрос: при ошибке (старая версия панели и т.п.) просто пустая строка
    async Task<string> GetOptionalAsync(IPanelConfig s, string path)
    {
        try { return await GetAsync(s, path, 1); }
        catch { return ""; }
    }

    public async Task<(string users, string nodes, string metrics, string billing)> FetchAsync(IPanelConfig s)
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

public static class Fmt
{
    // Флаг страны эмодзи по коду (для Telegram)
    public static string Flag(string cc) =>
        cc.Length == 2 && cc.All(char.IsLetter)
            ? string.Concat(cc.ToUpperInvariant().Select(c => char.ConvertFromUtf32(0x1F1E6 + (c - 'A')))) + " "
            : "";
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
    public static Snapshot Build(string usersJson, string nodesJson, IPanelConfig s, string metricsJson = "", string billingJson = "")
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

            string billText = "", billLevel = "", billShort = "";
            if (billing.TryGetValue(uuid, out var bill))
            {
                var left = bill.date - DateTimeOffset.UtcNow;
                int days = (int)Math.Ceiling(left.TotalDays);
                billText = (left < TimeSpan.Zero ? "оплата просрочена" : days <= 0 ? "оплата сегодня" : $"оплата через {days} д")
                           + $" ({bill.date.ToLocalTime():dd.MM})" + (bill.provider != "" ? $" · {bill.provider}" : "");
                billLevel = left < TimeSpan.Zero || days <= 3 ? "crit" : days <= 7 ? "warn" : "ok";
                billShort = $"оплата {bill.date.ToLocalTime():dd.MM} · " + (left < TimeSpan.Zero ? "просрочена" : days <= 0 ? "сегодня" : $"{days} д");
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
                (int)J.N(n, "usersOnline"), connected, xrayOk, J.S(n, "address"), NodeUuid: uuid,
                BillingText: billText, BillingLevel: billLevel, BillingShort: billShort,
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
                traffic, seen, hasLimit, Math.Min(1, pct), level, note, noteLevel, J.S(u, "subscriptionUrl"),
                UserId: J.S(u, "id"), UserUuid: J.S(u, "uuid")));
        }
        users = users.OrderByDescending(x => x.IsOnline).ThenBy(x => x.Username).ToList();
        return new Snapshot(users, nodes, DateTime.Now);
    }
}

