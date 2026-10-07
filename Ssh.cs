using System;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemnawaveMonitor;

// Учётная запись для сбора статистики с сервера ноды по SSH (отдельный пользователь с паролем, не root)
public class SshServer
{
    public string Node { get; set; } = "";
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "monitor";
    [JsonIgnore] public string Password { get; set; } = "";
    public string? PasswordEnc { get; set; }
    public string HostKey { get; set; } = "";   // отпечаток SHA256 ключа сервера, запоминается при первом подключении

    public SshServer Clone() => (SshServer)MemberwiseClone();
}

public record ServerStats(double Cpu, double Ram, double Disk, string RamText, string DiskText);

public static class SshStats
{
    // Сбор статистики. Этот же скрипт ставится на сервер как /usr/local/bin/rw-stats (ForceCommand),
    // а если его там нет — выполняется напрямую. Совместим с /bin/sh.
    const string ScriptRaw = """
        read -r x a b c d e f g h y < /proc/stat
        t1=$((a+b+c+d+e+f+g+h)); i1=$((d+e))
        sleep 1
        read -r x a b c d e f g h y < /proc/stat
        t2=$((a+b+c+d+e+f+g+h)); i2=$((d+e))
        echo "cpu $((t2-t1)) $((i2-i1))"
        awk '/^MemTotal:/{t=$2} /^MemAvailable:/{a=$2} END{print "mem", t, a}' /proc/meminfo
        df -P / | awk 'NR==2{print "disk", $3, $4}'
        """;

    // исходник может прийти с CRLF (git на Windows) — на сервер уходят только LF
    public static string Script => ScriptRaw.Replace("\r\n", "\n");

    public static Task<(ServerStats stats, string hostKey)> CollectAsync(SshServer srv) => Task.Run(() => Collect(srv));

    static (ServerStats, string) Collect(SshServer srv)
    {
        string seenKey = "";
        bool mismatch = false;

        var pwd = new PasswordAuthenticationMethod(srv.User, srv.Password);
        var kbd = new KeyboardInteractiveAuthenticationMethod(srv.User);
        kbd.AuthenticationPrompt += (_, e) => { foreach (var p in e.Prompts) p.Response = srv.Password; };
        var ci = new ConnectionInfo(srv.Host, srv.Port, srv.User, pwd, kbd) { Timeout = TimeSpan.FromSeconds(10) };

        using var client = new SshClient(ci);
        client.HostKeyReceived += (_, e) =>
        {
            seenKey = Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
            mismatch = srv.HostKey != "" && srv.HostKey != seenKey;
            e.CanTrust = !mismatch;
        };

        try { client.Connect(); }
        catch (Exception) when (mismatch)
        {
            throw new Exception("ключ сервера изменился — если сервер переустанавливали, сбросьте ключ в настройках");
        }
        catch (SshAuthenticationException) { throw new Exception("неверный логин или пароль"); }
        catch (SshOperationTimeoutException) { throw new Exception("таймаут подключения"); }
        catch (SocketException ex) { throw new Exception("нет соединения: " + ex.Message); }

        using var cmd = client.CreateCommand(Script);
        cmd.CommandTimeout = TimeSpan.FromSeconds(15);
        var output = cmd.Execute();
        client.Disconnect();
        return (Parse(output), seenKey);
    }

    static ServerStats Parse(string output)
    {
        double cpu = -1, ram = -1, disk = -1;
        string ramText = "", diskText = "";
        foreach (var line in output.Split('\n'))
        {
            var p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3) continue;
            if (!double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ||
                !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) continue;
            switch (p[0])
            {
                case "cpu":    // a — всего тиков, b — простой
                    if (a > 0) cpu = Math.Clamp((a - b) / a, 0, 1);
                    break;
                case "mem":    // a — всего КБ, b — доступно КБ
                    if (a > 0) { ram = Math.Clamp((a - b) / a, 0, 1); ramText = $"{J.Bytes((a - b) * 1024)} / {J.Bytes(a * 1024)}"; }
                    break;
                case "disk":   // a — занято КБ, b — свободно КБ (как в df)
                    if (a + b > 0) { disk = Math.Clamp(a / (a + b), 0, 1); diskText = $"{J.Bytes(a * 1024)} / {J.Bytes((a + b) * 1024)}"; }
                    break;
            }
        }
        if (cpu < 0 && ram < 0 && disk < 0) throw new Exception("сервер не вернул статистику");
        return new ServerStats(cpu, ram, disk, ramText, diskText);
    }

    public static string NewPassword()
    {
        const string abc = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var chars = new char[24];
        for (int i = 0; i < chars.Length; i++) chars[i] = abc[RandomNumberGenerator.GetInt32(abc.Length)];
        return new string(chars);
    }

    // Команда настройки сервера: выполняется один раз под root. Создаёт пользователя, который может только выдать статистику.
    public static string SetupCommand(string user, string password)
    {
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        var text = $$"""
            bash <<'RWSETUP'
            set -e
            U={{Q(user)}}
            if id -u "$U" >/dev/null 2>&1 && [ "$(cat /etc/rw-monitor-user 2>/dev/null)" != "$U" ]; then
              echo "RW-MONITOR: пользователь $U уже существует и создан не этой командой — выберите другое имя"; exit 1
            fi
            id -u "$U" >/dev/null 2>&1 || useradd -m -s /bin/sh "$U"
            echo "$U" > /etc/rw-monitor-user
            echo {{Q(user + ":" + password)}} | chpasswd
            cat > /usr/local/bin/rw-stats <<'RWSTATS'
            #!/bin/sh
            {{Script}}
            RWSTATS
            chmod 755 /usr/local/bin/rw-stats
            CFG=/etc/ssh/sshd_config
            cp "$CFG" "$CFG.rw-backup"
            sed -i '/^# rw-monitor begin/,/^# rw-monitor end/d' "$CFG"
            printf '\n# rw-monitor begin\nMatch User %s\n    PasswordAuthentication yes\n    ForceCommand /usr/local/bin/rw-stats\n    AllowTcpForwarding no\n    AllowAgentForwarding no\n    X11Forwarding no\n    PermitTTY no\n# rw-monitor end\n' "$U" >> "$CFG"
            if "$(command -v sshd || echo /usr/sbin/sshd)" -t; then
              systemctl reload ssh 2>/dev/null || systemctl reload sshd 2>/dev/null || true
              grep -qiE '^[[:space:]]*(AllowUsers|AllowGroups)' "$CFG" && echo "RW-MONITOR: внимание — в $CFG задан AllowUsers/AllowGroups, добавьте туда $U"
              echo "RW-MONITOR: готово"
            else
              cp "$CFG.rw-backup" "$CFG"; echo "RW-MONITOR: ошибка в конфиге SSH, изменения отменены"; exit 1
            fi
            RWSETUP
            """;
        return text.Replace("\r\n", "\n");
    }
}
