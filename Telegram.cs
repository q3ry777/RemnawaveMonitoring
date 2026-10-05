using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

// Уведомления в Telegram и команды /status, /problems. Работает, пока запущена программа.
public class TelegramService
{
    HttpClient _http = CreateClient("");
    string _token = "", _chat = "";
    bool _enabled, _primed, _busy;
    long _offset;
    readonly Queue<(string text, DateTime at)> _queue = new();

    public Func<string>? StatusProvider;
    public Func<string>? ProblemsProvider;

    public static HttpClient CreateClient(string proxy)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            try { handler.Proxy = new WebProxy(proxy.Trim()); handler.UseProxy = true; }
            catch { }
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public void Configure(Settings s)
    {
        _token = s.TgToken;
        _chat = s.TgChatId;
        _enabled = s.TgEnabled && _token != "" && _chat != "";
        _http = CreateClient(s.TgProxy);
        _primed = false;
    }

    public void Enqueue(string text)
    {
        if (_enabled) _queue.Enqueue((text, DateTime.Now));
    }

    // Вызывается таймером раз в несколько секунд: читает команды и отправляет очередь
    public async Task TickAsync()
    {
        if (!_enabled || _busy) return;
        _busy = true;
        try
        {
            await PollAsync();
            await FlushAsync();
        }
        catch { /* нет сети — повторим на следующем тике */ }
        finally { _busy = false; }
    }

    async Task FlushAsync()
    {
        while (_queue.Count > 0)
        {
            var (text, at) = _queue.Peek();
            if (DateTime.Now - at > TimeSpan.FromMinutes(30)) { _queue.Dequeue(); continue; }   // устаревшее не шлём
            if (!await SendAsync(_http, _token, _chat, text)) return;
            _queue.Dequeue();
        }
    }

    static async Task<bool> SendAsync(HttpClient http, string token, string chat, string text)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["chat_id"] = chat, ["text"] = text });
        using var res = await http.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content);
        int code = (int)res.StatusCode;
        return code < 500 && code != 429;   // ошибки настройки (4xx) не повторяем бесконечно
    }

    async Task PollAsync()
    {
        var url = $"https://api.telegram.org/bot{_token}/getUpdates?timeout=0" + (_offset > 0 ? $"&offset={_offset}" : "");
        using var res = await _http.GetAsync(url);
        if (!res.IsSuccessStatusCode) return;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("result", out var arr) || arr.ValueKind != JsonValueKind.Array) return;

        foreach (var u in arr.EnumerateArray())
        {
            long id = u.GetProperty("update_id").GetInt64();
            _offset = Math.Max(_offset, id + 1);
            if (!_primed) continue;   // сообщения, пришедшие до запуска, игнорируем
            if (!u.TryGetProperty("message", out var m) || !m.TryGetProperty("chat", out var chat)) continue;
            if (chat.GetProperty("id").GetInt64().ToString() != _chat) continue;   // чужим не отвечаем

            var text = J.S(m, "text");
            var cmd = text.Split(' ')[0].Split('@')[0].ToLowerInvariant();
            string? reply = cmd switch
            {
                "/status" => StatusProvider?.Invoke(),
                "/problems" => ProblemsProvider?.Invoke(),
                "/start" or "/help" => "Команды:\n/status — сводка\n/problems — активные проблемы",
                _ => null,
            };
            if (reply != null) Enqueue(reply);
        }
        _primed = true;
    }

    // ---- для окна настроек ----
    static string Describe(string body, HttpStatusCode code)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            var t = J.S(d.RootElement, "description");
            if (t != "") return t;
        }
        catch { }
        return code.ToString();
    }

    public static async Task<string?> SendOnceAsync(Settings s, string text)
    {
        try
        {
            using var http = CreateClient(s.TgProxy);
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["chat_id"] = s.TgChatId, ["text"] = text });
            using var res = await http.PostAsync($"https://api.telegram.org/bot{s.TgToken}/sendMessage", content);
            return res.IsSuccessStatusCode ? null : Describe(await res.Content.ReadAsStringAsync(), res.StatusCode);
        }
        catch (Exception ex) { return ex.Message; }
    }

    // Находит чат по последнему сообщению, которое вы написали боту
    public static async Task<(string id, string name)?> FindChatAsync(Settings s)
    {
        using var http = CreateClient(s.TgProxy);
        using var res = await http.GetAsync($"https://api.telegram.org/bot{s.TgToken}/getUpdates?timeout=0");
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception(Describe(body, res.StatusCode));
        using var doc = JsonDocument.Parse(body);
        (string id, string name)? found = null;
        foreach (var u in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            if (!u.TryGetProperty("message", out var m) || !m.TryGetProperty("chat", out var c) || !c.TryGetProperty("id", out var idEl)) continue;
            var name = J.S(c, "first_name");
            if (name == "") name = J.S(c, "username");
            if (name == "") name = J.S(c, "title");
            found = (idEl.GetInt64().ToString(), name);
        }
        return found;
    }
}
