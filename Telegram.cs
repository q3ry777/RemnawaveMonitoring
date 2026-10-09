using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

// Уведомления в Telegram, команды только на чтение и постоянное меню с кнопками. Работает, пока запущена программа.
public class TelegramService
{
    HttpClient _http = CreateClient("");
    string _token = "", _chat = "", _sig = "";
    bool _enabled, _primed, _busy, _commandsRegistered, _awaitUser;
    long _offset;
    readonly Queue<(string text, DateTime at)> _queue = new();

    // (команда, аргумент) -> ответ; null — команда неизвестна
    public Func<string, string, string?>? Handler;

    static readonly (string cmd, string desc)[] Commands =
    {
        ("status", "сводка"), ("nodes", "подробности по нодам"), ("users", "кто сейчас онлайн"),
        ("problems", "активные проблемы"), ("services", "сайты и сертификаты"), ("expiring", "что скоро истекает"),
        ("top", "топ-10 по трафику"), ("user", "карточка пользователя"), ("billing", "оплата серверов"),
        ("uptime", "аптайм за 7 и 30 дней"), ("version", "версии панели, нод и программы"), ("vpn", "проверка VPN из РФ"), ("help", "список команд"),
    };

    static readonly (string text, string cmd)[] Buttons =
    {
        ("📊 Сводка", "/status"), ("🖥 Ноды", "/nodes"), ("👥 Онлайн", "/users"),
        ("⚠️ Проблемы", "/problems"), ("🌐 Сайты", "/services"), ("⏳ Истекает", "/expiring"),
        ("📈 Топ", "/top"), ("💳 Оплата", "/billing"), ("🕒 Аптайм", "/uptime"),
        ("👤 Пользователь", "/user"), ("ℹ️ Версии", "/version"), ("🛡 VPN из РФ", "/vpn"),
    };

    static string Norm(string s) => s.Replace("\uFE0F", "").Trim();

    // клавиатура прикладывается к каждому сообщению: меню всегда на месте
    static readonly string KeyboardJson = JsonSerializer.Serialize(new
    {
        keyboard = new[]
        {
            new[] { "📊 Сводка", "🖥 Ноды", "👥 Онлайн" },
            new[] { "⚠️ Проблемы", "🌐 Сайты", "⏳ Истекает" },
            new[] { "📈 Топ", "💳 Оплата", "🕒 Аптайм" },
            new[] { "🛡 VPN из РФ", "👤 Пользователь", "ℹ️ Версии" },
        },
        is_persistent = true,
        resize_keyboard = true,
        input_field_placeholder = "Выберите команду…",
    });

    const string Help =
        "Команды (или кнопки внизу):\n" +
        "/status — сводка\n/nodes — подробности по нодам\n/users — кто онлайн\n/problems — активные проблемы\n" +
        "/services — сайты и сертификаты\n/expiring — что скоро истекает\n/top — топ-10 по трафику\n" +
        "/user имя — карточка пользователя\n/billing — оплата серверов\n/uptime — аптайм за 7 и 30 дней\n/version — версии\n/vpn — проверка VPN из РФ";

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
        _commandsRegistered = false;

        // после смены бота/чата/включения присылаем приветствие — вместе с ним появляется меню с кнопками
        var sig = $"{_enabled}|{_token}|{_chat}";
        bool changed = _sig != "" && sig != _sig && _enabled;
        _sig = sig;
        if (changed) Enqueue("✅ Remnawave Monitor подключён. Меню с кнопками — внизу.");
    }

    public void Enqueue(string text)
    {
        if (!_enabled) return;
        if (text.Length > 3900) text = text[..3900] + "\n…";
        _queue.Enqueue((text, DateTime.Now));
    }

    // Вызывается таймером раз в несколько секунд: читает команды и отправляет очередь
    public async Task TickAsync()
    {
        if (!_enabled || _busy) return;
        _busy = true;
        try
        {
            if (!_commandsRegistered) await RegisterCommandsAsync();
            await PollAsync();
            await FlushAsync();
        }
        catch { /* нет сети — повторим на следующем тике */ }
        finally { _busy = false; }
    }

    async Task RegisterCommandsAsync()
    {
        var json = JsonSerializer.Serialize(new { commands = Commands.Select(c => new { command = c.cmd, description = c.desc }) });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var res = await _http.PostAsync($"https://api.telegram.org/bot{_token}/setMyCommands", content);
        if (res.IsSuccessStatusCode) _commandsRegistered = true;
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

    static FormUrlEncodedContent Form(string chat, string text) =>
        new(new Dictionary<string, string> { ["chat_id"] = chat, ["text"] = text, ["reply_markup"] = KeyboardJson });

    static async Task<bool> SendAsync(HttpClient http, string token, string chat, string text)
    {
        using var content = Form(chat, text);
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
                else if (_awaitUser) { cmd = "/user"; arg = raw; }
                else continue;
            }

            string? reply;
            if (cmd is "/start" or "/help") { _awaitUser = false; reply = Help; }
            else if (cmd == "/user" && arg == "") { _awaitUser = true; reply = "👤 Введите имя пользователя (или его часть)"; }
            else
            {
                _awaitUser = false;
                reply = Handler?.Invoke(cmd, arg) ?? (raw.StartsWith('/') ? "Не знаю такой команды.\n\n" + Help : null);
            }
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
            using var content = Form(s.TgChatId, text);
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
