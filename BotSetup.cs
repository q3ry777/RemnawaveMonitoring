using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

// Telegram-бот на сервере с панелью: команда установки (Docker рядом с Remnawave) и проверка токена/чата
public static class BotSetup
{
    public const string Image = "ghcr.io/q3ry777/remnawave-bot:latest";

    public record Config(string ApiToken, string BotToken, string ChatId, int OnlineWindowSec,
        bool Nodes, bool Limits, bool Billing, bool Updates, List<int> QuietHours, string DndTime);

    // Часовой пояс для бота — как на этом компьютере (IANA, например Asia/Novosibirsk)
    public static string TimeZone()
    {
        var id = TimeZoneInfo.Local.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana)) return iana;
        return id.Contains('/') ? id : "UTC";
    }

    static string B(bool v) => v ? "true" : "false";

    public static string Command(Config c)
    {
        var env = new StringBuilder();
        env.AppendLine("# Remnawave bot — настройки. После изменения: docker compose up -d");
        env.AppendLine("# Панель внутри сети Docker (контейнер remnawave, порт 3000). Можно указать и внешний адрес https://...");
        env.AppendLine("PANEL_URL=http://remnawave:3000");
        env.AppendLine("REMNAWAVE_API_TOKEN=" + c.ApiToken);
        env.AppendLine("TELEGRAM_BOT_TOKEN=" + c.BotToken);
        env.AppendLine("TELEGRAM_CHAT_ID=" + c.ChatId);
        env.AppendLine("TZ=" + TimeZone());
        env.AppendLine("INTERVAL_SEC=30");
        env.AppendLine("ONLINE_WINDOW_SEC=" + c.OnlineWindowSec);
        env.AppendLine("NOTIFY_NODES=" + B(c.Nodes));
        env.AppendLine("NOTIFY_LIMITS=" + B(c.Limits));
        env.AppendLine("NOTIFY_BILLING=" + B(c.Billing));
        env.AppendLine("NOTIFY_UPDATES=" + B(c.Updates));
        env.AppendLine("# тихие часы: номера часов через запятую (23 = с 23:00 до 23:59), пусто — выключено");
        env.AppendLine("QUIET_HOURS=" + string.Join(",", c.QuietHours.OrderBy(h => h)));
        env.AppendLine("DND_TIME=" + c.DndTime);

        return $$"""
            bash <<'RWBOT'
            set -e
            echo "RW-BOT: установка…"
            command -v docker >/dev/null 2>&1 || { echo "RW-BOT: docker не найден"; exit 1; }
            docker network inspect remnawave-network >/dev/null 2>&1 || { echo "RW-BOT: сеть remnawave-network не найдена — панель Remnawave запущена на этом сервере через docker compose?"; exit 1; }
            D=/opt/remnawave-bot
            mkdir -p "$D/data"
            cd "$D"
            umask 077
            cat > .env <<'RWENV'
            {{env.ToString().TrimEnd()}}
            RWENV
            chmod 600 .env
            umask 022
            cat > docker-compose.yml <<'RWCOMPOSE'
            services:
              remnawave-bot:
                image: {{Image}}
                container_name: remnawave-bot
                restart: always
                env_file: .env
                volumes:
                  - ./data:/data
                networks:
                  - remnawave-network
                logging:
                  driver: json-file
                  options:
                    max-size: "5m"
                    max-file: "2"

            networks:
              remnawave-network:
                external: true
            RWCOMPOSE
            docker compose pull
            docker compose up -d --force-recreate
            sleep 5
            docker logs --tail 15 remnawave-bot
            echo "RW-BOT: готово. Журнал: docker logs -f remnawave-bot"
            RWBOT
            """;
    }

    static HttpClient Client() => new() { Timeout = TimeSpan.FromSeconds(20) };

    static string Describe(string body, System.Net.HttpStatusCode code)
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

    public static async Task<string?> SendOnceAsync(string token, string chat, string text)
    {
        try
        {
            using var http = Client();
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["chat_id"] = chat, ["text"] = text });
            using var res = await http.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content);
            return res.IsSuccessStatusCode ? null : Describe(await res.Content.ReadAsStringAsync(), res.StatusCode);
        }
        catch (Exception ex) { return ex.Message; }
    }

    // Находит чат по последнему сообщению, которое вы написали боту.
    // Если бот уже работает на сервере, он сам забирает сообщения — тогда чат здесь не найдётся.
    public static async Task<(string id, string name)?> FindChatAsync(string token)
    {
        using var http = Client();
        using var res = await http.GetAsync($"https://api.telegram.org/bot{token}/getUpdates?timeout=0");
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
