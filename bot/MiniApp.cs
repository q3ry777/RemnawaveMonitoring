using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace RemnawaveMonitor;

// Мини-приложение в Telegram. Пользователям: «Кабинет», «Статус», «Оплата». Админам: «Сводка», «Трафик», «Сбои», «Настройки» (настройки — только главному админу).
// Бот сам отдаёт страницу (GET /) и небольшой API (POST /api/…). Наружу его выставляет Caddy: reverse_proxy remnawave-bot:MINIAPP_PORT.
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
            if (method == "POST" && path.StartsWith("/api/"))
            {
                if (!MiniRate("ip:" + MiniIp(ctx), 60)) { MiniReply(ctx, 429, "application/json", "{\"error\":\"rate\"}"); return; }
                if (!MiniAuth(ctx.Request.Headers["X-Init-Data"] ?? "", out var userId)) { MiniReply(ctx, 401, "application/json", "{\"error\":\"auth\"}"); return; }
                var chat = userId.ToString();
                if (!MiniRate("u:" + chat, 60)) { MiniReply(ctx, 429, "application/json", "{\"error\":\"rate\"}"); return; }
                if (!IsAdmin(chat) && SubsOf(chat).Count == 0) { MiniReply(ctx, 403, "application/json", "{\"error\":\"no_access\"}"); return; }
                var (code, body) = await MiniApiAsync(chat, path, await MiniBodyAsync(ctx));
                MiniReply(ctx, code, "application/json", JsonSerializer.Serialize(body));
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
            main = _cfg.Chats.Contains(chat),
            statusEnabled = _cfg.StatusEnabled,
            pay = _cfg.PayEnabled && _cfg.PayPlans.Count > 0
                ? new { currency = _cfg.Currency, plans = _cfg.PayPlans.Select(x => new { months = x.months, price = x.price, label = MonthsText(x.months) }).ToList() }
                : null,
            subs = show ? SubsOf(chat).Select(u => MiniSub(u, now)).ToList() : new List<object>(),
        };
    }

    object MiniSub(UserRow u, DateTimeOffset now)
    {
        var exp = ExpireOf(u);
        var unlimited = Unlimited(u);
        return new
        {
            key = Key(u),
            expiring = Expiring(u),
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

    int _ruBusy;   // перепроверка из РФ по кнопке: одна за раз

    static async Task<JsonElement?> MiniBodyAsync(HttpListenerContext ctx)
    {
        if (ctx.Request.ContentLength64 > 20000) return null;
        using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var text = await sr.ReadToEndAsync();
        if (text.Length == 0 || text.Length > 20000) return null;
        try { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); } catch { return null; }
    }

    async Task<(int code, object body)> MiniApiAsync(string chat, string path, JsonElement? b)
    {
        bool admin = IsAdmin(chat), main = _cfg.Chats.Contains(chat);
        string Str(string k) => b is JsonElement e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int Num(string k, int def) => b is JsonElement e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : def;
        (int, object) Fail(string text, int code = 400) => (code, new { error = text });
        (int, object) Ok(object o) => (200, o);

        if (path.StartsWith("/api/admin/") && !admin) return Fail("admin", 403);
        switch (path)
        {
            case "/api/me": return Ok(BuildMini(chat));
            case "/api/status": return Ok(BuildMiniStatus());

            // ---- оплата (пользователь): заявка → «Я оплатил» / отмена; дальше как в чате: админ подтверждает кнопками ----
            case "/api/pay/create":
            {
                var keys = new List<string>();
                if (b is JsonElement be && be.ValueKind == JsonValueKind.Object && be.TryGetProperty("keys", out var ks) && ks.ValueKind == JsonValueKind.Array)
                    foreach (var k in ks.EnumerateArray().Take(20)) if (k.ValueKind == JsonValueKind.String) keys.Add(k.GetString() ?? "");
                var pay = CreatePayment(chat, keys.Where(x => x != "").Distinct().ToList(), Num("months", 0), out var payErr);
                if (pay == null) return Fail(payErr);
                return Ok(new { id = pay.Id, amount = Money(pay.Amount), months = pay.Months, names = pay.AllNames(),
                                details = _cfg.PayDetails != "" ? _cfg.PayDetails : "реквизиты уточните у администратора" });
            }
            case "/api/pay/confirm":
            case "/api/pay/cancel":
            {
                var err = FinishPay(chat, Str("id"), path.EndsWith("confirm"));
                return err == null ? Ok(new { ok = true }) : Fail(err);
            }

            // ---- админ ----
            case "/api/admin/summary": return Ok(BuildAdminSummary());
            case "/api/admin/traffic": return Ok(await BuildAdminTrafficAsync(Math.Clamp(Num("days", 7), 1, 30)));
            case "/api/admin/incidents": return Ok(BuildAdminIncidents());
            case "/api/admin/hide":
            {
                var node = FindNode(Str("id"));
                if (node == null) return Fail("Нода не найдена");
                return Ok(new { ok = true, message = SetStatusHide(node, Math.Clamp(Num("hours", 0), 0, 168)) });
            }
            case "/api/admin/recheck":
            {
                if (Interlocked.CompareExchange(ref _ruBusy, 1, 0) != 0) return Ok(new { busy = true });
                _ = Task.Run(async () =>
                {
                    try { await RuCheckOnceAsync(CancellationToken.None); }
                    catch (Exception ex) { Log.Write("перепроверка из РФ: " + ex.Message); }
                    finally { Interlocked.Exchange(ref _ruBusy, 0); }
                });
                return Ok(new { started = true });
            }
            case "/api/admin/settings":
                return main ? Ok(BuildAdminSettings()) : Fail("Настройки — только у главного админа", 403);
            case "/api/admin/set":
            {
                if (!main) return Fail("Настройки — только у главного админа", 403);
                var key = Str("key");
                var err = Str("op") == "t" ? ToggleSetting(key) : ApplyValue(key, Str("value"));
                return err == null ? Ok(new { ok = true }) : Fail(err);
            }
            default: return Fail("not found", 404);
        }
    }

    object BuildAdminSummary()
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            var nodes = (_snap?.Nodes ?? new List<NodeRow>()).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var hides = _state.StatusHides.Where(h => h.Until > now).ToList();
            return new
            {
                ready = _snap != null,
                updated = _snap?.At.ToString("HH:mm") ?? "",
                online = _snap?.Users.Count(u => u.IsOnline) ?? 0,
                ruMin = _cfg.CheckIntervalMin,
                nodes = nodes.Select(n => new
                {
                    id = NodeId(n), name = n.Name, connected = n.Connected, online = n.UsersOnline, mem = n.MemPctText,
                    ru = _state.RuCheck.TryGetValue(n.Name, out var lvl) ? lvl : "",
                    hidden = hides.Where(h => h.Node == n.Name).Select(h => h.Until.ToString("HH:mm")).FirstOrDefault() ?? "",
                }).ToList(),
                problems = _log.All().Where(i => i.End == null)
                    .Select(i => new { subject = i.Subject, text = i.Text, severity = i.Severity, since = i.Start.ToString("dd.MM HH:mm") }).ToList(),
            };
        }
    }

    async Task<object> BuildAdminTrafficAsync(int days)
    {
        NodeStats? st;
        try { st = await _api.GetNodesStatsAsync(_cfg, days); }
        catch (Exception ex) { Log.Write("мини-приложение: статистика: " + ex.Message); return new { available = false }; }
        if (st == null || st.Series.Count == 0) return new { available = false };
        var totals = st.DayTotals;
        int pk = Array.IndexOf(totals, totals.Max());
        string Day(DateTime d) => $"{d.Day}.{d.Month:00}";
        return new
        {
            available = true,
            days = st.Days.Select(Day).ToList(),
            series = st.Series.Select(x => new { name = x.Name, total = x.Total, daily = x.Daily }).ToList(),
            total = st.Total, peak = totals[pk], peakDay = Day(st.Days[pk]),
        };
    }

    object BuildAdminIncidents()
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            return new
            {
                items = _log.All().OrderByDescending(i => i.Start).Take(40).Select(i => new
                {
                    subject = i.Subject, text = i.Text, severity = i.Severity, active = i.End == null,
                    start = i.Start.ToString("dd.MM HH:mm"), end = i.End?.ToString("dd.MM HH:mm") ?? "", dur = IncidentLog.Dur((i.End ?? now) - i.Start),
                    kind = i.Key.EndsWith("|ru") ? "ru" : i.Key.EndsWith("|conn") ? "conn" : i.Key.EndsWith("|bill") ? "bill" : "other",
                }).ToList(),
            };
        }
    }

    // «23-8, 13-14» по набору часов — в том виде, в каком тихие часы вводятся
    static string QuietEdit(HashSet<int> hours)
    {
        if (hours.Count == 0) return "";
        if (hours.Count == 24) return "0-23, 23";
        var starts = hours.Where(h => !hours.Contains((h + 23) % 24)).OrderBy(h => h).ToList();
        return string.Join(", ", starts.Select(st =>
        {
            int e = st;
            while (hours.Contains((e + 1) % 24)) e = (e + 1) % 24;
            return $"{st}-{(e + 1) % 24}";
        }));
    }

    object BuildAdminSettings() => new
    {
        n_nodes = _cfg.NotifyNodes, n_limits = _cfg.NotifyLimits, n_billing = _cfg.NotifyBilling, n_updates = _cfg.NotifyUpdates,
        n_users = _cfg.NotifyUsers, n_panel = _cfg.NotifyPanel,
        st_on = _cfg.StatusEnabled, u_cab = _cfg.UserCabinet, u_rem = _cfg.UserReminders, pay_on = _cfg.PayEnabled,
        ru_min = _cfg.CheckIntervalMin, st_delay = _cfg.StatusDelayMin, u_days = _cfg.RemindDays, u_traf = _cfg.RemindTraffic,
        q_range = QuietEdit(_cfg.QuietHours), q_dnd = _cfg.DndTime.ToString(@"hh\:mm"),
        st_names = string.Join("\n", _cfg.StatusNames.Select(kv => $"{kv.Key} = {kv.Value}")),
        pay_plans = string.Join("\n", _cfg.PayPlans.Select(x => $"{x.months} {x.price}")),
        pay_details = _cfg.PayDetails,
        obs = string.Join("\n", _cfg.Observers.Select(kv => $"{kv.Key} {string.Join(",", kv.Value)}")),
    };
}
