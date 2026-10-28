using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

// Приём вебхуков панели Remnawave (WEBHOOK_URL=http://remnawave-bot:8080/webhook).
// Подпись: X-Remnawave-Signature = HMAC-SHA256(тело, WEBHOOK_SECRET_HEADER панели) в hex.
public class Webhook
{
    readonly BotConfig _cfg;
    public Func<string, JsonElement, Task>? OnEvent;   // (событие, data)
    public DateTime? LastEvent { get; private set; }

    public Webhook(BotConfig cfg) => _cfg = cfg;

    public async Task RunAsync(CancellationToken ct)
    {
        if (_cfg.WebhookSecret.Length < 32) { Log.Write("вебхуки выключены: не задан WEBHOOK_SECRET"); return; }
        var http = new HttpListener();
        http.Prefixes.Add($"http://+:{_cfg.WebhookPort}/");
        try { http.Start(); }
        catch (Exception ex) { Log.Write("вебхуки: не удалось открыть порт " + _cfg.WebhookPort + ": " + ex.Message); return; }
        Log.Write($"вебхуки: жду события панели на порту {_cfg.WebhookPort}");
        using var reg = ct.Register(() => { try { http.Stop(); } catch { } });
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await http.GetContextAsync(); }
            catch { if (ct.IsCancellationRequested) break; continue; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod != "POST") { Reply(ctx, 200, "remnawave-bot"); return; }
            string body;
            using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
            var sig = ctx.Request.Headers["X-Remnawave-Signature"] ?? "";
            var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_cfg.WebhookSecret), Encoding.UTF8.GetBytes(body)));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected.ToLowerInvariant()), Encoding.ASCII.GetBytes(sig.Trim().ToLowerInvariant())))
            {
                Log.Write("вебхук с неверной подписью — отклонён (WEBHOOK_SECRET бота не совпадает с WEBHOOK_SECRET_HEADER панели?)");
                Reply(ctx, 401, "bad signature");
                return;
            }
            Reply(ctx, 200, "ok");
            LastEvent = DateTime.Now;
            using var doc = JsonDocument.Parse(body);
            var ev = J.S(doc.RootElement, "event");
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d.Clone() : default;
            if (OnEvent != null && ev != "") await OnEvent(ev, data);
        }
        catch (Exception ex)
        {
            Log.Write("вебхук: " + ex.Message);
            try { Reply(ctx, 500, "error"); } catch { }
        }
    }

    static void Reply(HttpListenerContext ctx, int code, string text)
    {
        try
        {
            var b = Encoding.UTF8.GetBytes(text);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentLength64 = b.Length;
            ctx.Response.OutputStream.Write(b);
            ctx.Response.Close();
        }
        catch { }
    }
}
