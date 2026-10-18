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

// Telegram: long polling команд и очередь отправки (с повтором, если API недоступен)
public class Telegram
{
    readonly BotConfig _cfg;
    readonly HttpClient _http;
    readonly ConcurrentQueue<(string chat, string text, DateTime at)> _queue = new();
    readonly SemaphoreSlim _signal = new(0);
    readonly DateTime _startedUtc = DateTime.UtcNow;
    long _offset;

    // (команда, аргумент, чат) -> ответ; null — не команда
    public Func<string, string, Task<string?>>? Handler;

    public static readonly (string cmd, string desc)[] Commands =
    {
        ("status", "сводка"), ("nodes", "подробности по нодам"), ("users", "кто сейчас онлайн"),
        ("problems", "активные проблемы"), ("version", "версии панели и нод"),
        ("dnd", "не беспокоить: /dnd 08:00 или /dnd off"), ("help", "список команд"),
    };

    public const string DndButton = "🔕 Не беспокоить";

    static readonly (string text, string cmd)[] Buttons =
    {
        ("📊 Сводка", "/status"), ("🖥 Ноды", "/nodes"), ("👥 Онлайн", "/users"),
        ("⚠️ Проблемы", "/problems"), ("ℹ️ Версии", "/version"), (DndButton, "/dnd"),
    };

    static string Norm(string s) => s.Replace("️", "").Trim();

    // клавиатура прикладывается к каждому сообщению: меню всегда на месте
    static readonly string KeyboardJson = JsonSerializer.Serialize(new
    {
        keyboard = new[]
        {
            new[] { "📊 Сводка", "🖥 Ноды", "👥 Онлайн" },
            new[] { "⚠️ Проблемы", "ℹ️ Версии", DndButton },
        },
        is_persistent = true,
        resize_keyboard = true,
        input_field_placeholder = "Выберите команду…",
    });

    public const string Help =
        "Команды (или кнопки внизу):\n" +
        "/status — сводка\n/nodes — подробности по нодам\n/users — кто онлайн и с каких IP\n/problems — активные проблемы\n" +
        "/version — версии\n/dnd — не беспокоить до утра (повторно — выключить), /dnd 10:30 — до указанного времени";

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

    public void Send(string text, string? chat = null)
    {
        if (text.Length > 3900) text = text[..3900] + "\n…";
        foreach (var c in chat != null ? new[] { chat } : _cfg.Chats.ToArray())
            _queue.Enqueue((c, text, DateTime.Now));
        _signal.Release();
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
                await Task.Delay(5000, ct);
            }
        }
        await sender;
    }

    async Task RegisterCommandsAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(new { commands = Commands.Select(c => new { command = c.cmd, description = c.desc }) });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync(Url("setMyCommands"), content);
            if (!res.IsSuccessStatusCode) Log.Write("setMyCommands: " + await Describe(res));
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
                if (DateTime.Now - m.at > TimeSpan.FromMinutes(30)) { _queue.TryDequeue(out _); continue; }   // устаревшее не шлём
                bool done;
                try
                {
                    using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                        { ["chat_id"] = m.chat, ["text"] = m.text, ["reply_markup"] = KeyboardJson });
                    using var res = await _http.PostAsync(Url("sendMessage"), content, ct);
                    int code = (int)res.StatusCode;
                    if (!res.IsSuccessStatusCode) Log.Write("sendMessage: " + await Describe(res));
                    done = code < 500 && code != 429;   // ошибки настройки (4xx) не повторяем бесконечно
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { Log.Write("sendMessage: " + ex.Message); done = false; }
                if (!done) { await Task.Delay(5000, ct); break; }
                _queue.TryDequeue(out _);
            }
        }
    }

    async Task PollAsync(CancellationToken ct)
    {
        var url = Url("getUpdates") + "?timeout=50&allowed_updates=%5B%22message%22%5D" + (_offset > 0 ? $"&offset={_offset}" : "");
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
            if (!u.TryGetProperty("message", out var m) || !m.TryGetProperty("chat", out var chatEl)) continue;
            var chat = chatEl.GetProperty("id").GetInt64().ToString();
            if (!_cfg.Chats.Contains(chat)) continue;   // чужим не отвечаем
            // сообщения, отправленные задолго до запуска бота, не выполняем
            if (m.TryGetProperty("date", out var dt) && DateTimeOffset.FromUnixTimeSeconds(dt.GetInt64()).UtcDateTime < _startedUtc.AddMinutes(-2)) continue;

            var raw = J.S(m, "text").Trim();
            if (raw == "") continue;

            string cmd, arg = "";
            if (raw.StartsWith('/'))
            {
                var parts = raw.Split(new[] { ' ', '\n' }, 2, StringSplitOptions.RemoveEmptyEntries);
                cmd = parts[0].Split('@')[0].ToLowerInvariant();
                if (parts.Length > 1) arg = parts[1].Trim();
            }
            else
            {
                var btn = Buttons.FirstOrDefault(b => Norm(b.text) == Norm(raw));
                if (btn.cmd != null) cmd = btn.cmd;
                else { Send("Не понял. Команды — кнопками внизу или /help.", chat); continue; }
            }

            string? reply;
            if (cmd is "/start" or "/help") reply = Help;
            else
            {
                try { reply = Handler == null ? null : await Handler(cmd, arg); }
                catch (Exception ex) { reply = "Ошибка: " + ex.Message; }
                reply ??= "Не знаю такой команды.\n\n" + Help;
            }
            Send(reply, chat);
        }
    }
}
