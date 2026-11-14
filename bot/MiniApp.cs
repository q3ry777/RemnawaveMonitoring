using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace RemnawaveMonitor;

// Мини-приложение в Telegram (этап 1: «Кабинет» и «Статус сервиса» для пользователей и админов).
// Бот сам отдаёт страницу (GET /) и небольшой API (POST /api/me, /api/status). Наружу его выставляет Caddy: reverse_proxy remnawave-bot:MINIAPP_PORT.
// Кто открыл приложение, определяется подписью Telegram (initData): без неё и с чужой подписью API не отвечает, подписки видит только их владелец.
public partial class Bot
{
    readonly Dictionary<string, (int n, DateTime at)> _miniRate = new();

    async Task MiniAppServerAsync(CancellationToken ct)
    {
        if (_cfg.MiniAppUrl == "") return;
        var http = new HttpListener();
        http.Prefixes.Add($"http://+:{_cfg.MiniAppPort}/");
        try { http.Start(); }
        catch (Exception ex) { Log.Write("мини-приложение: не удалось открыть порт " + _cfg.MiniAppPort + ": " + ex.Message); return; }
        Log.Write($"мини-приложение: {_cfg.MiniAppUrl} → порт {_cfg.MiniAppPort}");
        _ = _tg.SetMenuButtonAsync(_cfg.MiniAppUrl, "Кабинет");
        using var reg = ct.Register(() => { try { http.Stop(); } catch { } });
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await http.GetContextAsync(); }
            catch { if (ct.IsCancellationRequested) break; continue; }
            _ = Task.Run(() => MiniHandleAsync(ctx));
        }
    }

    async Task MiniHandleAsync(HttpListenerContext ctx)
    {
        await Task.Yield();
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var method = ctx.Request.HttpMethod;
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            if (method == "GET" && (path == "/" || path == "/index.html"))
            {
                ctx.Response.Headers["Content-Security-Policy"] =
                    "default-src 'none'; script-src 'unsafe-inline' https://telegram.org; style-src 'unsafe-inline' https://fonts.googleapis.com; font-src https://fonts.gstatic.com; connect-src 'self'; base-uri 'none'";
                MiniReply(ctx, 200, "text/html; charset=utf-8", MiniAppPage.Html);
                return;
            }
            if (method == "GET" && path == "/healthz") { MiniReply(ctx, 200, "text/plain", "ok"); return; }
            if (method == "POST" && (path == "/api/me" || path == "/api/status"))
            {
                if (!MiniRate("ip:" + MiniIp(ctx), 60)) { MiniReply(ctx, 429, "application/json", "{\"error\":\"rate\"}"); return; }
                if (!MiniAuth(ctx.Request.Headers["X-Init-Data"] ?? "", out var userId)) { MiniReply(ctx, 401, "application/json", "{\"error\":\"auth\"}"); return; }
                var chat = userId.ToString();
                if (!MiniRate("u:" + chat, 60)) { MiniReply(ctx, 429, "application/json", "{\"error\":\"rate\"}"); return; }
                if (!IsAdmin(chat) && SubsOf(chat).Count == 0) { MiniReply(ctx, 403, "application/json", "{\"error\":\"no_access\"}"); return; }
                object body = path == "/api/me" ? BuildMini(chat) : BuildMiniStatus();
                MiniReply(ctx, 200, "application/json", JsonSerializer.Serialize(body));
                return;
            }
            MiniReply(ctx, 404, "text/plain", "not found");
        }
        catch (Exception ex)
        {
            Log.Write("мини-приложение: " + ex.Message);
            try { MiniReply(ctx, 500, "text/plain", "error"); } catch { }
        }
    }

    static void MiniReply(HttpListenerContext ctx, int code, string type, string body)
    {
        try
        {
            var b = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = type;
            ctx.Response.ContentLength64 = b.Length;
            ctx.Response.OutputStream.Write(b);
            ctx.Response.Close();
        }
        catch { }
    }

    // Запросы приходят от Caddy, он добавляет X-Forwarded-For; порт бота наружу не опубликован
    static string MiniIp(HttpListenerContext ctx)
    {
        var xff = ctx.Request.Headers["X-Forwarded-For"];
        if (!string.IsNullOrEmpty(xff)) return xff.Split(',')[0].Trim();
        return ctx.Request.RemoteEndPoint?.Address.ToString() ?? "?";
    }

    // не больше max запросов в минуту на ключ (IP или пользователя)
    bool MiniRate(string key, int max)
    {
        lock (_miniRate)
        {
            var now = DateTime.UtcNow;
            if (_miniRate.Count > 5000) foreach (var k in _miniRate.Where(x => now - x.Value.at > TimeSpan.FromMinutes(1)).Select(x => x.Key).ToList()) _miniRate.Remove(k);
            if (!_miniRate.TryGetValue(key, out var e) || now - e.at > TimeSpan.FromMinutes(1)) e = (0, now);
            e.n++;
            _miniRate[key] = e;
            return e.n <= max;
        }
    }

    // Проверка подписи Telegram: HMAC-SHA256 над отсортированными полями initData, ключ — HMAC("WebAppData", токен бота)
    bool MiniAuth(string init, out long userId)
    {
        userId = 0;
        if (init.Length == 0 || init.Length > 4096 || _cfg.BotToken == "") return false;
        var q = HttpUtility.ParseQueryString(init);
        var hash = (q["hash"] ?? "").ToLowerInvariant();
        if (hash.Length != 64) return false;
        var lines = q.AllKeys.Where(k => k != null && k != "hash").OrderBy(k => k, StringComparer.Ordinal).Select(k => $"{k}={q[k!]}");
        var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(_cfg.BotToken));
        var calc = Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(string.Join("\n", lines)))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(calc), Encoding.ASCII.GetBytes(hash))) return false;
        if (!long.TryParse(q["auth_date"], out var at) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - at) > 86400) return false;
        try
        {
            using var u = JsonDocument.Parse(q["user"] ?? "");
            userId = u.RootElement.GetProperty("id").GetInt64();
        }
        catch { return false; }
        return userId != 0;
    }

    object BuildMini(string chat)
    {
        var admin = IsAdmin(chat);
        var show = _cfg.UserCabinet || admin;
        var now = Api.Now;
        return new
        {
            role = admin ? "admin" : "user",
            cabinet = show,
            statusEnabled = _cfg.StatusEnabled,
            subs = show ? SubsOf(chat).Select(u => MiniSub(u, now)).ToList() : new List<object>(),
        };
    }

    object MiniSub(UserRow u, DateTimeOffset now)
    {
        var exp = ExpireOf(u);
        var unlimited = Unlimited(u);
        return new
        {
            name = u.Username,
            status = u.Status,
            statusText = StatusRu(u.Status),
            unlimited,
            expire = !unlimited && exp is DateTimeOffset e ? e.ToLocalTime().ToString("dd.MM.yyyy") : null,
            daysLeft = !unlimited && exp is DateTimeOffset e2 ? (int?)Math.Ceiling((e2 - now).TotalDays) : null,
            traffic = u.Traffic,
            hasLimit = u.HasLimit,
            pct = (int)Math.Round(u.TrafficPct * 100),
            url = u.SubscriptionUrl,
            canPay = Payable(u),
        };
    }

    // То же, что видят пользователи в «Статусе сервиса» бота: публичные названия, блок «недоступна из РФ», без IP и UUID
    object BuildMiniStatus()
    {
        lock (_lock)
        {
            if (!_cfg.StatusEnabled) return new { enabled = false };
            if (_snap == null) return new { enabled = true, ready = false };
            var now = DateTime.Now;
            var nodes = ServiceNodes(now).Select(s => new { name = s.Name, state = s.State }).ToList();
            return new { enabled = true, ready = true, updated = now.ToString("HH:mm"), delayMin = _cfg.StatusDelayMin, nodes };
        }
    }
}
