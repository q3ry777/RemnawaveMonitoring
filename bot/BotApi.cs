using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

// Данные для программы на ПК (вкладка «оплаты»): GET /api/payments?ts=…&sig=… на порту BOT_API_PORT.
// Подпись — HMAC от «payments|ts» ключом BOT_API_KEY (не старше 5 минут), ответ зашифрован (SecureBox).
public class BotApi
{
    readonly BotConfig _cfg;
    public Func<PaymentsReport>? Payments;

    public BotApi(BotConfig cfg) => _cfg = cfg;

    public async Task RunAsync(CancellationToken ct)
    {
        if (_cfg.ApiKey.Length < 32) return;
        var http = new HttpListener();
        http.Prefixes.Add($"http://+:{_cfg.ApiPort}/");
        try { http.Start(); }
        catch (Exception ex) { Log.Write("API для программы: не удалось открыть порт " + _cfg.ApiPort + ": " + ex.Message); return; }
        Log.Write($"API для программы (вкладка «оплаты»): порт {_cfg.ApiPort}");
        using var reg = ct.Register(() => { try { http.Stop(); } catch { } });
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await http.GetContextAsync(); }
            catch { if (ct.IsCancellationRequested) break; continue; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        try
        {
            var q = ctx.Request.QueryString;
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (path != "/api/payments") { Reply(ctx, 404, "not found"); return; }
            var ts = q["ts"] ?? "";
            var sig = q["sig"] ?? "";
            if (!long.TryParse(ts, out var t) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t) > 300 ||
                !SecureBox.Verify(_cfg.ApiKey, "payments|" + ts, sig))
            {
                Log.Write($"API: отказ {ctx.Request.RemoteEndPoint?.Address} (неверная подпись или время)");
                Reply(ctx, 401, "unauthorized");
                return;
            }
            var report = Payments?.Invoke() ?? new PaymentsReport(DateTime.Now, _cfg.Currency, new());
            Reply(ctx, 200, SecureBox.Seal(_cfg.ApiKey, JsonSerializer.Serialize(report), "payments|" + ts));
        }
        catch (Exception ex)
        {
            Log.Write("API: " + ex.Message);
            Reply(ctx, 500, "error");
        }
    }

    static void Reply(HttpListenerContext ctx, int code, string text)
    {
        try
        {
            var b = Encoding.UTF8.GetBytes(text);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = b.Length;
            ctx.Response.OutputStream.Write(b);
            ctx.Response.Close();
        }
        catch { }
    }
}
