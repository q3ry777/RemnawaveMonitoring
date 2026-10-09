using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

public record SiteCheck(string Name, bool Ok, int? Ms, string Error);
public record VpnCheck(string Name, string Address, int Port, string Network, string Security, bool? Ok, int? Ms, bool? TcpOk, string Error,
    List<SiteCheck> Sites, int? ConnectMs = null, double? SpeedMbps = null, DateTime? SpeedAt = null, string SpeedError = "");
public record CheckerReport(DateTime CheckedAt, string Server, string Error, List<VpnCheck> Results, int Version);

// Xray-чекер на сервере в РФ: установка и чтение результата по SSH
public static class Checker
{
    public static Task<(CheckerReport? report, string key)> FetchAsync(SshServer srv) => Task.Run(() =>
    {
        var (output, key) = SshStats.Exec(srv, "result");
        return (Parse(output), key);
    });

    // null — результата ещё нет (чекер не успел отработать)
    public static CheckerReport? Parse(string json)
    {
        json = json.Trim();
        if (json == "") return null;
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        DateTime.TryParse(J.S(r, "checked_at"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at);
        var list = new List<VpnCheck>();
        if (r.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var x in arr.EnumerateArray())
            {
                bool? B(string k) => J.P(x, k) is { } v && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
                int? I(string k) => J.P(x, k) is { ValueKind: JsonValueKind.Number } v ? (int)v.GetDouble() : null;
                var sites = new List<SiteCheck>();
                if (x.TryGetProperty("sites", out var sa) && sa.ValueKind == JsonValueKind.Array)
                    foreach (var s in sa.EnumerateArray())
                        sites.Add(new SiteCheck(J.S(s, "name"), J.P(s, "ok") is { ValueKind: JsonValueKind.True },
                            J.P(s, "ms") is { ValueKind: JsonValueKind.Number } m ? (int)m.GetDouble() : null, J.S(s, "error")));
                list.Add(new VpnCheck(J.S(x, "name"), J.S(x, "address"), I("port") ?? 0, J.S(x, "network"), J.S(x, "security"),
                    B("ok"), I("ms"), B("tcp_ok"), J.S(x, "error"), sites, I("connect_ms"),
                    J.P(x, "speed_mbps") is { ValueKind: JsonValueKind.Number } sp ? sp.GetDouble() : null,
                    DateTime.TryParse(J.S(x, "speed_at"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var sat) ? sat : null,
                    J.S(x, "speed_error")));
            }
        return new CheckerReport(at, J.S(r, "server"), J.S(r, "error"), list, (int)J.N(r, "version"));
    }

    static string PyScript()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("rw-checker.py")
                      ?? throw new Exception("в сборке нет rw-checker.py");
        using var rd = new StreamReader(s);
        return rd.ReadToEnd().Replace("\r\n", "\n").TrimEnd('\n');
    }

    // Команда установки на сервер (выполняется один раз под root)
    // «YouTube https://www.youtube.com/generate_204» по строке -> (название, адрес)
    public static List<(string name, string url)> ParseSites(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l != "" && !l.StartsWith('#')).Select(l =>
        {
            int i = l.LastIndexOf(' ');
            var url = (i < 0 ? l : l[(i + 1)..]).Trim();
            if (!url.Contains("://")) url = "https://" + url;
            var name = i < 0 ? (Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url) : l[..i].Trim();
            return (name, url);
        }).ToList();

    public static string SetupCommand(string sshUser, string password, string subUrl, int intervalMin, string sitesText, int speedMin)
    {
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        var cfg = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["sub_url"] = subUrl,
            ["speed_interval_min"] = speedMin,
            ["sites"] = ParseSites(sitesText).Select(s => new Dictionary<string, string> { ["name"] = s.name, ["url"] = s.url }).ToList(),
        });
        var text = $$"""
            bash <<'RWSETUP'
            set -e
            U={{Q(sshUser)}}
            echo "RW-CHECKER: установка…"
            if command -v apt-get >/dev/null 2>&1; then
              apt-get update -qq >/dev/null && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq curl unzip python3 >/dev/null
            elif command -v dnf >/dev/null 2>&1; then dnf install -y -q curl unzip python3 >/dev/null
            elif command -v yum >/dev/null 2>&1; then yum install -y -q curl unzip python3 >/dev/null
            fi
            for b in curl unzip python3 systemctl; do command -v $b >/dev/null 2>&1 || { echo "RW-CHECKER: не найдено $b"; exit 1; }; done
            case "$(uname -m)" in x86_64|amd64) A=64 ;; aarch64|arm64) A=arm64-v8a ;; *) echo "RW-CHECKER: архитектура $(uname -m) не поддерживается"; exit 1 ;; esac
            if id -u "$U" >/dev/null 2>&1 && [ "$(cat /etc/rw-checker/ssh-user 2>/dev/null)" != "$U" ]; then
              echo "RW-CHECKER: пользователь $U уже существует и создан не этой командой — выберите другое имя"; exit 1
            fi
            D=/usr/local/lib/rw-checker
            mkdir -p "$D" /etc/rw-checker /var/lib/rw-checker
            T=$(mktemp -d)
            curl -fsSL --retry 3 -o "$T/xray.zip" "https://github.com/XTLS/Xray-core/releases/latest/download/Xray-linux-$A.zip" \
              || { echo "RW-CHECKER: не удалось скачать Xray с GitHub"; rm -rf "$T"; exit 1; }
            unzip -o -q "$T/xray.zip" xray -d "$T"
            install -m 755 "$T/xray" "$D/xray"
            rm -rf "$T"
            echo "RW-CHECKER: $("$D/xray" version | head -1)"
            cat > "$D/rw-checker.py" <<'RWPY'
            {{PyScript()}}
            RWPY
            chmod 755 "$D/rw-checker.py"
            id -u rw-checker >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin rw-checker
            cat > /etc/rw-checker/config.json <<'RWCFG'
            {{cfg}}
            RWCFG
            chown root:rw-checker /etc/rw-checker/config.json
            chmod 640 /etc/rw-checker/config.json
            chown rw-checker:rw-checker /var/lib/rw-checker
            chmod 755 /var/lib/rw-checker
            cat > /etc/systemd/system/rw-checker.service <<'RWSVC'
            [Unit]
            Description=Remnawave Monitor: VPN check via Xray
            Wants=network-online.target
            After=network-online.target

            [Service]
            Type=oneshot
            User=rw-checker
            Group=rw-checker
            ExecStart=/usr/bin/env python3 /usr/local/lib/rw-checker/rw-checker.py
            TimeoutStartSec=900
            NoNewPrivileges=yes
            PrivateTmp=yes
            ProtectSystem=strict
            ProtectHome=yes
            ReadWritePaths=/var/lib/rw-checker
            RWSVC
            cat > /etc/systemd/system/rw-checker.timer <<'RWTMR'
            [Unit]
            Description=Remnawave Monitor: VPN check every {{intervalMin}} min

            [Timer]
            OnBootSec=1min
            OnUnitActiveSec={{intervalMin}}min
            AccuracySec=10s

            [Install]
            WantedBy=timers.target
            RWTMR
            systemctl daemon-reload
            systemctl enable --now rw-checker.timer >/dev/null 2>&1
            id -u "$U" >/dev/null 2>&1 || useradd -m -s /bin/sh "$U"
            echo "$U" > /etc/rw-checker/ssh-user
            echo {{Q(sshUser + ":" + password)}} | chpasswd
            CFG=/etc/ssh/sshd_config
            cp "$CFG" "$CFG.rw-checker-backup"
            sed -i '/^# rw-checker begin/,/^# rw-checker end/d' "$CFG"
            printf '\n# rw-checker begin\nMatch User %s\n    PasswordAuthentication yes\n    ForceCommand cat /var/lib/rw-checker/result.json\n    AllowTcpForwarding no\n    AllowAgentForwarding no\n    X11Forwarding no\n    PermitTTY no\n# rw-checker end\n' "$U" >> "$CFG"
            if "$(command -v sshd || echo /usr/sbin/sshd)" -t; then
              systemctl reload ssh 2>/dev/null || systemctl reload sshd 2>/dev/null || true
              grep -qiE '^[[:space:]]*(AllowUsers|AllowGroups)' "$CFG" && echo "RW-CHECKER: внимание — в $CFG задан AllowUsers/AllowGroups, добавьте туда $U"
            else
              cp "$CFG.rw-checker-backup" "$CFG"; echo "RW-CHECKER: ошибка в конфиге SSH, изменения SSH отменены"; exit 1
            fi
            echo "RW-CHECKER: первая проверка…"
            systemctl start rw-checker.service || true
            python3 - <<'RWSUM' || true
            import json
            d = json.load(open("/var/lib/rw-checker/result.json"))
            r = [x for x in d["results"] if x.get("ok") is not None]
            print("RW-CHECKER: ошибка: " + d["error"] if d["error"] else "RW-CHECKER: проверено %d, работают %d" % (len(r), sum(1 for x in r if x["ok"])))
            for x in r:
                print("  %s %s %s" % ("OK " if x["ok"] else "ERR", x["name"], ("%s мс" % x["ms"]) if x["ok"] else x["error"]))
            RWSUM
            echo "RW-CHECKER: готово"
            RWSETUP
            """;
        return text.Replace("\r\n", "\n");
    }
}
