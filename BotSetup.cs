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
        bool Nodes, bool Limits, bool Billing, bool Updates, List<int> QuietHours, string DndTime,
        bool UsersEvents, bool PanelEvents, string Observers, bool Cabinet, bool Reminders, int RemindDays, int RemindTraffic,
        bool Webhook, string WebhookSecret,
        bool Pay, string PayPlans, string PayDetails, string WriteToken,
        bool Api, string ApiKey, int ApiPort);

    // «1 150» / «3 400» по строке → «1:150,3:400». error — что не так
    public static string ParsePlans(string text, out string error)
    {
        error = "";
        var parts = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line == "") continue;
            var t = line.Split(new[] { ' ', '\t', ':', '-', '—' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2 || !int.TryParse(t[0], out var m) || m is < 1 or > 36 ||
                !int.TryParse(new string(t[1].Where(char.IsDigit).ToArray()), out var p))
            { error = $"«{line}»: нужно «месяцев цена», например «3 400»"; continue; }
            parts.Add($"{m}:{p}");
        }
        if (parts.Count == 0 && error == "") error = "добавьте хотя бы один тариф, например «1 150»";
        return string.Join(",", parts);
    }

    public static readonly string[] Kinds = { "nodes", "limits", "billing", "updates", "users", "panel" };

    // Секрет подписи вебхуков: 64 символа a-z, A-Z, 0-9 (требование панели)
    public static string NewSecret()
    {
        const string abc = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(abc, 64));
    }

    // «123456789 nodes,billing» по строке → «123456789:nodes,billing;…». error — что не так
    public static string ParseObservers(string text, out string error)
    {
        error = "";
        var parts = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line == "" || line.StartsWith('#')) continue;
            var t = line.Split(new[] { ' ', '\t', ':', ';' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (!long.TryParse(t[0], out _)) { error = $"«{line}»: в начале строки должен быть числовой Telegram ID"; continue; }
            var kinds = t.Length > 1
                ? t[1].Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).ToList()
                : new List<string>();
            var bad = kinds.Where(k => !Kinds.Contains(k)).ToList();
            if (bad.Count > 0) error = $"«{line}»: неизвестно {string.Join(", ", bad)} (можно: {string.Join(", ", Kinds)})";
            parts.Add(t[0] + ":" + string.Join(",", kinds.Where(Kinds.Contains)));
        }
        return string.Join(";", parts);
    }

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
        env.AppendLine("# события из вебхуков панели: пользователи (создан, удалён, впервые подключился, новое устройство…) и панель (входы, перезапуск, API-токены)");
        env.AppendLine("NOTIFY_USERS=" + B(c.UsersEvents));
        env.AppendLine("NOTIFY_PANEL=" + B(c.PanelEvents));
        env.AppendLine("# дополнительные админы: ID:виды;ID:виды (виды: " + string.Join(",", Kinds) + "; пусто — все)");
        env.AppendLine("TELEGRAM_OBSERVERS=" + c.Observers);
        env.AppendLine("# пользователи панели с Telegram ID: личный кабинет и напоминания");
        env.AppendLine("USER_CABINET=" + B(c.Cabinet));
        env.AppendLine("USER_REMINDERS=" + B(c.Reminders));
        env.AppendLine("REMIND_DAYS=" + c.RemindDays);
        env.AppendLine("REMIND_TRAFFIC=" + c.RemindTraffic);
        env.AppendLine("# вебхуки панели: секрет совпадает с WEBHOOK_SECRET_HEADER в /opt/remnawave/.env; пусто — выключено");
        env.AppendLine("WEBHOOK_SECRET=" + (c.Webhook ? c.WebhookSecret : ""));
        env.AppendLine("WEBHOOK_PORT=8080");
        env.AppendLine("# оплата переводом с подтверждением админом: тарифы месяцев:цена, реквизиты (\\n — перенос строки)");
        env.AppendLine("PAY_ENABLED=" + B(c.Pay));
        env.AppendLine("PAY_PLANS=" + c.PayPlans);
        env.AppendLine("PAY_DETAILS=" + c.PayDetails.Replace("\r", "").Replace("\n", "\\n").Replace("#", "№"));
        env.AppendLine("# токен панели с правом изменять пользователей (для продления после оплаты); пусто — основной токен");
        env.AppendLine("REMNAWAVE_WRITE_TOKEN=" + c.WriteToken);
        env.AppendLine("# API для вкладки «оплаты» в программе на ПК (порт открыт наружу; запросы подписаны, ответы зашифрованы этим ключом); пусто — выключено");
        env.AppendLine("BOT_API_KEY=" + (c.Api ? c.ApiKey : ""));
        env.AppendLine("BOT_API_PORT=" + c.ApiPort);

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
            {{(c.Webhook ? WebhookScript(c.WebhookSecret) : "")}}
            umask 022
            cat > docker-compose.yml <<'RWCOMPOSE'
            services:
              remnawave-bot:
                image: {{Image}}
                container_name: remnawave-bot
                restart: always
                env_file: .env
            {{(c.Api ? $"    ports:\n      - \"{c.ApiPort}:{c.ApiPort}\"" : "")}}
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
            docker compose pull || { echo "RW-BOT: не удалось скачать образ. Если пакет приватный — сделайте его публичным: github.com/users/q3ry777/packages/container/package/remnawave-bot → Package settings → Change visibility → Public (в образе нет токенов), или выполните: docker login ghcr.io -u q3ry777 (пароль — токен с правом read:packages)"; exit 1; }
            docker compose up -d --force-recreate
            # автообновление: каждый день в 04:17 (время сервера) скачивается новая версия, если она вышла
            if command -v crontab >/dev/null 2>&1; then
              (crontab -l 2>/dev/null | grep -v remnawave-bot; echo '17 4 * * * cd /opt/remnawave-bot && docker compose pull -q && docker compose up -d && docker image prune -f >/dev/null 2>&1 # remnawave-bot') | crontab -
              echo "RW-BOT: автообновление включено (каждый день в 04:17 по времени сервера)"
            else
              echo "RW-BOT: cron не найден — автообновление не включено, обновляйте вручную"
            fi
            {{(c.Api ? ApiScript(c.ApiPort) : "")}}
            sleep 5
            docker logs --tail 15 remnawave-bot
            echo "RW-BOT: готово. Журнал: docker logs -f remnawave-bot"
            RWBOT
            """;
    }

    // Включение вебхуков в панели: /opt/remnawave/.env (с резервной копией) и перезапуск панели, если что-то поменялось.
    // Если у панели уже есть свои вебхуки — адрес бота добавляется к ним, и бот берёт их секрет.
    static string WebhookScript(string secret)
    {
        var lines = new[]
        {
            "P=/opt/remnawave/.env",
            "if [ -f \"$P\" ]; then",
            "  val(){ grep -E \"^$1=\" \"$P\" | tail -1 | cut -d= -f2- | tr -d \"\\\"' \\r\"; }",
            "  CUR_EN=$(val WEBHOOK_ENABLED); CUR_URL=$(val WEBHOOK_URL); CUR_SECRET=$(val WEBHOOK_SECRET_HEADER)",
            "  SECRET=" + secret,
            "  URL=http://remnawave-bot:8080/webhook",
            "  if [ \"$CUR_EN\" = \"true\" ] && [ ${#CUR_SECRET} -ge 32 ]; then SECRET=\"$CUR_SECRET\"; fi",
            "  case \",$CUR_URL,\" in *\",$URL,\"*) NEWURL=\"$CUR_URL\" ;; *) if [ \"$CUR_EN\" = \"true\" ] && [ -n \"$CUR_URL\" ]; then NEWURL=\"$CUR_URL,$URL\"; else NEWURL=\"$URL\"; fi ;; esac",
            "  sed -i \"s|^WEBHOOK_SECRET=.*|WEBHOOK_SECRET=$SECRET|\" .env",
            "  if [ \"$CUR_EN\" != \"true\" ] || [ \"$CUR_URL\" != \"$NEWURL\" ] || [ \"$CUR_SECRET\" != \"$SECRET\" ]; then",
            "    cp \"$P\" \"$P.rw-bot-backup\"",
            "    setkv(){ if grep -qE \"^$1=\" \"$P\"; then sed -i \"s|^$1=.*|$1=$2|\" \"$P\"; else printf '\\n%s=%s\\n' \"$1\" \"$2\" >> \"$P\"; fi; }",
            "    setkv WEBHOOK_ENABLED true; setkv WEBHOOK_URL \"$NEWURL\"; setkv WEBHOOK_SECRET_HEADER \"$SECRET\"",
            "    echo \"RW-BOT: включаю вебхуки в /opt/remnawave/.env (копия: .env.rw-bot-backup) и перезапускаю панель…\"",
            "    (cd /opt/remnawave && docker compose up -d) || echo \"RW-BOT: не удалось перезапустить панель — выполните: cd /opt/remnawave && docker compose up -d\"",
            "  else",
            "    echo \"RW-BOT: вебхуки панели уже настроены\"",
            "  fi",
            "else",
            "  echo \"RW-BOT: /opt/remnawave/.env не найден — вебхуки не настроены. В .env панели задайте: WEBHOOK_ENABLED=true, WEBHOOK_URL=http://remnawave-bot:8080/webhook, WEBHOOK_SECRET_HEADER=<как WEBHOOK_SECRET в /opt/remnawave-bot/.env>\"",
            "fi",
        };
        return string.Join("\n", lines);
    }

    // Порт для программы: открыть в ufw (если он включён) и показать адрес сервера
    static string ApiScript(int port) => string.Join("\n", new[]
    {
        $"if command -v ufw >/dev/null 2>&1 && ufw status | grep -q 'Status: active'; then ufw allow {port}/tcp >/dev/null && echo \"RW-BOT: порт {port} открыт в ufw\"; fi",
        "IP=$(curl -s -4 --max-time 5 https://ifconfig.me || hostname -I | awk '{print $1}')",
        $"echo \"RW-BOT: для вкладки «оплаты» в программе укажите адрес сервера $IP и порт {port}\"",
    });

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
