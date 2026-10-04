using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

public record ServiceRow(string Name, string Url, string StatusText, string Level, string Time, string Cert, string CertLevel);

// Доступность ноды с ЭТОГО компьютера: ICMP-пинг и TCP-порт
public static class NodeProbe
{
    public static async Task<(int? icmp, int? tcp)> RunAsync(string host, int port)
    {
        var icmp = Icmp(host);
        var tcp = port > 0 ? Tcp(host, port) : Task.FromResult<int?>(null);
        await Task.WhenAll(icmp, tcp);
        return (icmp.Result, tcp.Result);
    }

    static async Task<int?> Icmp(string host)
    {
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(host, 1500);
            if (r.Status == IPStatus.Success) return (int)r.RoundtripTime;
            return null;
        }
        catch { return null; }
    }

    static async Task<int?> Tcp(string host, int port)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var c = new TcpClient();
            using var cts = new CancellationTokenSource(1500);
            await c.ConnectAsync(host, port, cts.Token);
            return (int)sw.ElapsedMilliseconds;
        }
        catch { return null; }
    }
}

// Проверка сайтов: код ответа, время ответа, срок действия TLS-сертификата
public static class ServiceMonitor
{
    static string Short(string s) => s.Length > 38 ? s[..38] + "…" : s;

    public static async Task<ServiceRow> CheckAsync(string url)
    {
        var name = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
        X509Certificate2? cert = null;
        bool tlsOk = true;

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            SslOptions =
            {
                // сертификат запоминаем и проверяем сами; запрос ничего секретного не несёт
                RemoteCertificateValidationCallback = (_, c, _, errors) =>
                {
                    if (c != null) cert = new X509Certificate2(c);
                    tlsOk = errors == SslPolicyErrors.None;
                    return true;
                },
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RemnawaveMonitor/1.1");

        string status, level, time = "—";
        var sw = Stopwatch.StartNew();
        try
        {
            using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            sw.Stop();
            int code = (int)res.StatusCode;
            status = $"{code} {res.ReasonPhrase}".Trim();
            level = code < 400 ? "ok" : code < 500 ? "warn" : "bad";
            time = $"{sw.ElapsedMilliseconds} мс";
        }
        catch (Exception ex)
        {
            level = "bad";
            status = ex is TaskCanceledException ? "таймаут" : Short(ex.GetBaseException().Message);
        }

        string certText = "", certLevel = "";
        if (cert != null)
        {
            int days = (int)Math.Floor((cert.NotAfter - DateTime.Now).TotalDays);
            if (!tlsOk) { certText = "TLS ошибка"; certLevel = "crit"; }
            else if (days < 0) { certText = "TLS истёк"; certLevel = "crit"; }
            else { certText = $"TLS {days} д"; certLevel = days <= 3 ? "crit" : days <= 14 ? "warn" : "ok"; }
        }
        return new ServiceRow(name, url, status, level, time, certText, certLevel);
    }
}

// Доступность ноды из России через check-host.net (TCP-проверка с российских точек)
public static class RuCheck
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static List<string>? _nodes;

    static async Task<JsonDocument> GetJson(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.UserAgent.ParseAdd("RemnawaveMonitor/1.2");
        using var res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
    }

    static async Task<List<string>> RuNodesAsync()
    {
        if (_nodes != null) return _nodes;
        var list = new List<string>();
        try
        {
            using var d = await GetJson("https://check-host.net/nodes/hosts");
            if (d.RootElement.TryGetProperty("nodes", out var ns) && ns.ValueKind == JsonValueKind.Object)
                foreach (var p in ns.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("location", out var loc)
                        && loc.ValueKind == JsonValueKind.Array && loc.GetArrayLength() > 0 && loc[0].GetString() == "ru")
                        list.Add(p.Name);
        }
        catch { }
        if (list.Count > 0) return _nodes = list.Take(4).ToList();
        return new List<string> { "ru1.node.check-host.net", "ru2.node.check-host.net", "ru3.node.check-host.net" };
    }

    // (сколько точек достучались, сколько ответило всего, среднее время мс) или null, если проверку не удалось выполнить
    public static async Task<(int ok, int total, int avgMs)?> CheckAsync(string host, int port)
    {
        var nodes = await RuNodesAsync();
        var url = new StringBuilder($"https://check-host.net/check-tcp?host={Uri.EscapeDataString($"{host}:{port}")}");
        foreach (var n in nodes) url.Append("&node=").Append(n);

        string? id;
        using (var start = await GetJson(url.ToString()))
            id = start.RootElement.TryGetProperty("request_id", out var rid) ? rid.GetString() : null;
        if (string.IsNullOrEmpty(id)) return null;

        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(3000);
            using var res = await GetJson($"https://check-host.net/check-result/{id}");
            int ok = 0, total = 0, sumMs = 0;
            bool pending = false;
            foreach (var p in res.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Null) { pending = true; continue; }
                JsonElement first = default;
                if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0) first = p.Value[0];
                if (first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0) first = first[0];
                if (first.ValueKind != JsonValueKind.Object) continue;
                total++;
                if (first.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number)
                {
                    ok++;
                    sumMs += (int)(t.GetDouble() * 1000);
                }
            }
            if (!pending || i == 7) return total == 0 ? null : (ok, total, ok > 0 ? sumMs / ok : 0);
        }
        return null;
    }
}

// Последние релизы Remnawave на GitHub
public static class ReleaseCheck
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<string?> LatestAsync(string repo)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("RemnawaveMonitor");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            using var d = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return d.RootElement.GetProperty("tag_name").GetString()?.Trim().TrimStart('v');
        }
        catch { return null; }
    }

    static bool TryV(string? s, out Version v)
    {
        if (Version.TryParse(s?.Trim().TrimStart('v'), out var x)) { v = x; return true; }
        v = new Version(0, 0);
        return false;
    }

    public static bool Newer(string? latest, string? current) =>
        TryV(latest, out var a) && TryV(current, out var b) && a > b;
}
