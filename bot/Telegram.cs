using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

// Telegram: long polling (сообщения и нажатия inline-кнопок) и очередь отправки с повтором, если API недоступен.
// Кто пишет и что ему отвечать — решает Bot.
public class Telegram
{
    readonly BotConfig _cfg;
    readonly HttpClient _http;
    readonly ConcurrentQueue<Outgoing> _queue = new();
    readonly SemaphoreSlim _signal = new(0);
    readonly DateTime _startedUtc = DateTime.UtcNow;
    long _offset;

    record Outgoing(string Chat, string Text, string? Markup, byte[]? Photo, DateTime At, string? FromChat = null, long MessageId = 0);

    // (чат, текст, id сообщения, есть ли вложение — фото/файл) — входящее сообщение;
    // (чат, id нажатия, данные кнопки) — нажатие inline-кнопки
    public Func<string, string, long, bool, string, Task>? OnMessage;   // + имя отправителя «Имя Фамилия (@ник)»
    public Func<string, string, string, Task>? OnCallback;

    public static readonly (string cmd, string desc)[] AdminCommands =
    {
        ("status", "сводка"), ("nodes", "подробности по нодам"), ("users", "кто сейчас онлайн"),
        ("problems", "активные проблемы"), ("version", "версии панели и нод"),
        ("dnd", "не беспокоить: /dnd 08:00 или /dnd off"), ("my", "мои подписки"), ("payments", "оплаты"), ("help", "список команд"),
    };

    public Telegram(BotConfig cfg)
    {
        _cfg = cfg;
        var handler = new HttpClientHandler();
        if (cfg.Proxy != "")
        {
            try { handler.Proxy = new WebProxy(cfg.Proxy); handler.UseProxy = true; }
            catch (Exception ex) { Log.Write("TELEGRAM_PROXY не разобран: " + ex.Message); }
        }
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    string Url(string method) => $"{_cfg.TelegramApi.TrimEnd('/')}/bot{_cfg.BotToken}/{method}";

    // Постоянная клавиатура внизу чата
    public static string Keyboard(params string[][] rows) => JsonSerializer.Serialize(new
    {
        keyboard = rows,
        is_persistent = true,
        resize_keyboard = true,
        input_field_placeholder = "Выберите команду…",
    });

    // Кнопки под сообщением: (текст, данные)
    public static string Inline(IEnumerable<(string text, string data)> buttons, int perRow = 2) => JsonSerializer.Serialize(new
    {
        inline_keyboard = buttons.Select((b, i) => (b, i)).GroupBy(x => x.i / perRow)
            .Select(g => g.Select(x => new { text = x.b.text, callback_data = x.b.data }).ToArray()).ToArray(),
    });

    // Кнопки под сообщением по строкам
    public static string InlineRows(IEnumerable<(string text, string data)[]> rows) => JsonSerializer.Serialize(new
    {
        inline_keyboard = rows.Select(r => r.Select(b => new { text = b.text, callback_data = b.data }).ToArray()).ToArray(),
    });

    public void Send(string chat, string text, string? markup)
    {
        if (text.Length > 3900) text = text[..3900] + "\n…";
        _queue.Enqueue(new Outgoing(chat, text, markup, null, DateTime.Now));
        _signal.Release();
    }

    public void SendPhoto(string chat, byte[] png, string caption, string? markup)
    {
        if (caption.Length > 1000) caption = caption[..1000] + "…";
        _queue.Enqueue(new Outgoing(chat, caption, markup, png, DateTime.Now));
        _signal.Release();
    }

    // Копия сообщения (например, скриншот оплаты) в другой чат — с подписью и кнопками
    public void Copy(string toChat, string fromChat, long messageId, string caption, string? markup)
    {
        if (caption.Length > 1000) caption = caption[..1000] + "…";
        _queue.Enqueue(new Outgoing(toChat, caption, markup, null, DateTime.Now, fromChat, messageId));
        _signal.Release();
    }

    public async Task AnswerCallbackAsync(string id, string text = "")
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["callback_query_id"] = id, ["text"] = text });
            using var res = await _http.PostAsync(Url("answerCallbackQuery"), content);
        }
        catch { }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await RegisterCommandsAsync();
        var sender = Task.Run(() => SendLoopAsync(ct), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await PollAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Write("telegram: " + ex.Message);
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
            }
        }
        await sender;
    }

    // Меню «/» — только для админов; остальным Telegram показывает пустой список
    async Task RegisterCommandsAsync()
    {
        try
        {
            foreach (var chat in _cfg.AllAdminChats)
            {
                var json = JsonSerializer.Serialize(new
                {
                    commands = AdminCommands.Select(c => new { command = c.cmd, description = c.desc }),
                    scope = new { type = "chat", chat_id = long.TryParse(chat, out var id) ? id : 0 },
                });
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var res = await _http.PostAsync(Url("setMyCommands"), content);
                if (!res.IsSuccessStatusCode) Log.Write("setMyCommands: " + await Describe(res));
            }
            var user = JsonSerializer.Serialize(new { commands = new[] { new { command = "my", description = "мои подписки" }, new { command = "pay", description = "продлить подписку" } } });
            using var c2 = new StringContent(user, Encoding.UTF8, "application/json");
            using var r2 = await _http.PostAsync(Url("setMyCommands"), c2);
        }
        catch (Exception ex) { Log.Write("setMyCommands: " + ex.Message); }
    }

    static async Task<string> Describe(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        try
        {
            using var d = JsonDocument.Parse(body);
            var t = J.S(d.RootElement, "description");
            if (t != "") return $"{(int)res.StatusCode} {t}";
        }
        catch { }
        return ((int)res.StatusCode).ToString();
    }

    async Task SendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { return; }
            while (_queue.TryPeek(out var m))
            {
                if (DateTime.Now - m.At > TimeSpan.FromMinutes(30)) { _queue.TryDequeue(out _); continue; }   // устаревшее не шлём
                bool done;
                try
                {
                    HttpResponseMessage res;
                    if (m.FromChat != null)
                    {
                        var fields = new Dictionary<string, string>
                            { ["chat_id"] = m.Chat, ["from_chat_id"] = m.FromChat, ["message_id"] = m.MessageId.ToString(), ["caption"] = m.Text };
                        if (m.Markup != null) fields["reply_markup"] = m.Markup;
                        using var content = new FormUrlEncodedContent(fields);
                        res = await _http.PostAsync(Url("copyMessage"), content, ct);
                    }
                    else if (m.Photo != null)
                    {
                        using var form = new MultipartFormDataContent
                        {
                            { new StringContent(m.Chat), "chat_id" },
                            { new StringContent(m.Text), "caption" },
                            { new ByteArrayContent(m.Photo), "photo", "qr.png" },
                        };
                        if (m.Markup != null) form.Add(new StringContent(m.Markup), "reply_markup");
                        res = await _http.PostAsync(Url("sendPhoto"), form, ct);
                    }
                    else
                    {
                        var fields = new Dictionary<string, string> { ["chat_id"] = m.Chat, ["text"] = m.Text, ["disable_web_page_preview"] = "true" };
                        if (m.Markup != null) fields["reply_markup"] = m.Markup;
                        using var content = new FormUrlEncodedContent(fields);
                        res = await _http.PostAsync(Url("sendMessage"), content, ct);
                    }
                    using (res)
                    {
                        int code = (int)res.StatusCode;
                        if (!res.IsSuccessStatusCode) Log.Write($"отправка в {m.Chat}: " + await Describe(res));
                        done = code < 500 && code != 429;   // ошибки настройки и «бот заблокирован» (4xx) не повторяем
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { Log.Write("отправка: " + ex.Message); done = false; }
                if (!done) { try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; } break; }
                _queue.TryDequeue(out _);
                await Task.Delay(40, ct);   // не упираться в лимиты Telegram при рассылке
            }
        }
    }

    async Task PollAsync(CancellationToken ct)
    {
        var url = Url("getUpdates") + "?timeout=50&allowed_updates=" + Uri.EscapeDataString("[\"message\",\"callback_query\"]")
                  + (_offset > 0 ? $"&offset={_offset}" : "");
        using var res = await _http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode)
        {
            var why = await Describe(res);
            Log.Write("getUpdates: " + why);
            // 409 — этим ботом пользуется кто-то ещё (например, старая версия программы на ПК с включённым Telegram)
            await Task.Delay(why.StartsWith("409") ? 30000 : 5000, ct);
            return;
        }
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("result", out var arr) || arr.ValueKind != JsonValueKind.Array) return;

        foreach (var u in arr.EnumerateArray())
        {
            long id = u.GetProperty("update_id").GetInt64();
            _offset = Math.Max(_offset, id + 1);
            try
            {
                if (u.TryGetProperty("callback_query", out var cb))
                {
                    var chat = J.S(cb, "message", "chat", "id");
                    if (chat == "") chat = J.S(cb, "from", "id");
                    if (OnCallback != null) await OnCallback(chat, J.S(cb, "id"), J.S(cb, "data"));
                    continue;
                }
                if (!u.TryGetProperty("message", out var m) || !m.TryGetProperty("chat", out var chatEl)) continue;
                if (J.S(chatEl, "type") is var type && type != "" && type != "private" && !_cfg.AllAdminChats.Contains(J.S(chatEl, "id"))) continue;   // группы — только админские
                // сообщения, отправленные задолго до запуска бота, не выполняем
                if (m.TryGetProperty("date", out var dt) && DateTimeOffset.FromUnixTimeSeconds(dt.GetInt64()).UtcDateTime < _startedUtc.AddMinutes(-2)) continue;
                var raw = (J.S(m, "text") is { Length: > 0 } tx ? tx : J.S(m, "caption")).Trim();
                bool media = m.TryGetProperty("photo", out _) || m.TryGetProperty("document", out _);
                if ((raw == "" && !media) || OnMessage == null) continue;
                var from = m.TryGetProperty("from", out var f) ? f : chatEl;
                var who = string.Join(" ", new[] { J.S(from, "first_name"), J.S(from, "last_name") }.Where(x => x != ""))
                          + (J.S(from, "username") is { Length: > 0 } nick ? $" (@{nick})" : "");
                await OnMessage(J.S(chatEl, "id"), raw, m.TryGetProperty("message_id", out var mid) ? mid.GetInt64() : 0, media, who.Trim());
            }
            catch (Exception ex) { Log.Write("обработка сообщения: " + ex.Message); }
        }
    }
}
