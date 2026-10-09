#!/usr/bin/env python3
# rw-checker — проверка VPN-подключений через Xray с сервера (в РФ) по подписке тестового пользователя Remnawave.
# Запускается таймером systemd. Результат: /var/lib/rw-checker/result.json (его забирает Remnawave Monitor по SSH).
import base64, json, os, socket, subprocess, sys, tempfile, time, urllib.parse, urllib.request
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone

CONFIG = os.environ.get("RW_CHECKER_CONFIG", "/etc/rw-checker/config.json")
DEFAULTS = {
    "sub_url": "",
    "xray": "/usr/local/lib/rw-checker/xray",
    "result": "/var/lib/rw-checker/result.json",
    "test_url": "https://www.gstatic.com/generate_204",
    "timeout": 12,
    "base_port": 20800,
    # сайты, которые открываются через каждое рабочее подключение (видно гео-блок и блокировки отдельных сервисов)
    "sites": [
        {"name": "YouTube", "url": "https://www.youtube.com/generate_204"},
        {"name": "Instagram", "url": "https://www.instagram.com/"},
        {"name": "ChatGPT", "url": "https://api.openai.com/v1/models"},
    ],
    "site_timeout": 10,
    # замер скорости через каждое рабочее подключение: раз в speed_interval_min минут (0 — выключено)
    "speed_url": "https://speed.cloudflare.com/__down?bytes=10000000",
    "speed_interval_min": 60,
    "speed_timeout": 25,
    "speed_state": "/var/lib/rw-checker/speed.json",
}

UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"


def now_iso():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def write_result(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=os.path.dirname(path))
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False)
    os.chmod(tmp, 0o644)
    os.replace(tmp, path)


# ---------- подписка ----------
def fetch_subscription(url):
    req = urllib.request.Request(url, headers={"User-Agent": "rw-checker/1.0", "Accept": "*/*"})
    with urllib.request.urlopen(req, timeout=20) as r:
        body = r.read().decode("utf-8", "replace").strip()
    if "://" not in body:  # обычный ответ подписки — base64 со списком ссылок
        try:
            body = base64.b64decode(body + "=" * (-len(body) % 4)).decode("utf-8", "replace")
        except Exception:
            pass
    return [l.strip() for l in body.splitlines() if "://" in l]


def q1(qs, key, default=""):
    v = qs.get(key)
    return v[0] if v else default


def parse_link(link):
    """vless:// и trojan:// -> описание и outbound для Xray; иначе None."""
    u = urllib.parse.urlsplit(link)
    proto = u.scheme.lower()
    name = urllib.parse.unquote(u.fragment) or f"{u.hostname}:{u.port}"
    if proto not in ("vless", "trojan"):
        return {"name": name, "protocol": proto, "address": u.hostname or "", "port": u.port or 0,
                "unsupported": True}
    qs = urllib.parse.parse_qs(u.query)
    secret = urllib.parse.unquote(u.username or "")
    host, port = u.hostname, u.port or 443
    net = q1(qs, "type", "tcp").lower()
    if net == "splithttp":
        net = "xhttp"
    sec = q1(qs, "security", "none").lower()

    if proto == "vless":
        user = {"id": secret, "encryption": q1(qs, "encryption", "none")}
        if q1(qs, "flow"):
            user["flow"] = q1(qs, "flow")
        settings = {"vnext": [{"address": host, "port": port, "users": [user]}]}
    else:
        settings = {"servers": [{"address": host, "port": port, "password": secret}]}

    stream = {"network": net, "security": sec}
    sni = q1(qs, "sni") or q1(qs, "host") or host
    if sec == "reality":
        stream["realitySettings"] = {
            "serverName": sni, "fingerprint": q1(qs, "fp", "chrome"),
            "publicKey": q1(qs, "pbk"), "shortId": q1(qs, "sid"), "spiderX": q1(qs, "spx", "/"),
        }
    elif sec == "tls":
        tls = {"serverName": sni, "fingerprint": q1(qs, "fp", "chrome")}
        if q1(qs, "alpn"):
            tls["alpn"] = q1(qs, "alpn").split(",")
        stream["tlsSettings"] = tls

    path, h = q1(qs, "path", "/"), q1(qs, "host")
    if net == "ws":
        stream["wsSettings"] = {"path": path, "host": h}
    elif net == "httpupgrade":
        stream["httpupgradeSettings"] = {"path": path, "host": h}
    elif net == "grpc":
        stream["grpcSettings"] = {"serviceName": q1(qs, "serviceName"), "multiMode": q1(qs, "mode") == "multi"}
    elif net == "xhttp":
        x = {"path": path, "host": h, "mode": q1(qs, "mode", "auto")}
        if q1(qs, "extra"):
            try:
                x["extra"] = json.loads(q1(qs, "extra"))
            except ValueError:
                pass
        stream["xhttpSettings"] = x
    elif net == "tcp" and q1(qs, "headerType") == "http":
        stream["tcpSettings"] = {"header": {"type": "http", "request": {
            "path": [path], "headers": {"Host": [h or host]}}}}

    return {"name": name, "protocol": proto, "address": host, "port": port, "network": net, "security": sec,
            "outbound": {"protocol": proto, "settings": settings, "streamSettings": stream}}


def is_placeholder(e):
    # информационные «ссылки» панели (срок подписки и т.п.) — не настоящие серверы
    a = (e.get("address") or "").lower()
    return a in ("", "0.0.0.0", "127.0.0.1", "localhost") or e.get("port") in (0, 1)


# ---------- проверки ----------
def tcp_check(host, port, timeout=5):
    t = time.monotonic()
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True, int((time.monotonic() - t) * 1000)
    except OSError:
        return False, None


def wait_port(port, timeout=5.0):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=0.3):
                return True
        except OSError:
            time.sleep(0.1)
    return False


def curl_via(port, url, timeout):
    """Два HTTP-запроса подряд через SOCKS-вход Xray в одном соединении. -> (ok, ms, error, connect_ms)
    connect_ms — первый запрос целиком: рукопожатие с нодой (Reality/TLS), соединение и TLS с сайтом.
    ms — второй запрос по уже открытому туннелю: реальная задержка через VPN (как «отклик» в клиентах)."""
    cmd = ["curl", "-sS", "-w", "%{http_code} %{time_total}\n", "--max-time", str(timeout),
           "--noproxy", "", "-x", f"socks5h://127.0.0.1:{port}",   # только через Xray, без системных исключений
           "-o", "/dev/null", url, "-o", "/dev/null", url]
    p = subprocess.run(cmd, capture_output=True, text=True)
    lines = [l.split() for l in p.stdout.strip().splitlines() if l.strip()]
    first = lines[0] if lines else ["000", "0"]
    code = first[0]
    if code.startswith(("2", "3")):
        connect_ms = int(float(first[1]) * 1000)
        ms = connect_ms
        if len(lines) > 1 and lines[1][0].startswith(("2", "3")):
            ms = int(float(lines[1][1]) * 1000)
        return True, ms, "", connect_ms
    err = p.stderr.strip().splitlines()[-1] if p.stderr.strip() else f"HTTP {code}"
    if p.returncode == 28:
        err = "таймаут"
    elif "Connection reset" in err or p.returncode == 56:
        err = "соединение сброшено"
    elif p.returncode == 52:
        err = "сервер закрыл соединение без ответа"
    elif p.returncode == 97 or "SOCKS" in err:
        err = "Xray не смог установить соединение"
    return False, None, err[:120], None


def site_via(port, site, timeout):
    """Открывается ли сайт через подключение. 2xx/3xx, а также 401/404/405/429 — сайт ответил;
    403/451 — сайт отказал (гео-блок, например «недоступно в вашей стране»)."""
    cmd = ["curl", "-sS", "-o", "/dev/null", "-w", "%{http_code} %{time_total}", "--max-time", str(timeout),
           "-A", UA, "--noproxy", "", "-x", f"socks5h://127.0.0.1:{port}", site["url"]]
    p = subprocess.run(cmd, capture_output=True, text=True)
    out = p.stdout.strip().split()
    code = int(out[0]) if out and out[0].isdigit() else 0
    ms = int(float(out[1]) * 1000) if len(out) > 1 else None
    r = {"name": site["name"], "code": code, "ms": None, "ok": False, "error": ""}
    if p.returncode == 0 and (code < 400 or code in (401, 404, 405, 429)) and code != 0:
        r.update(ok=True, ms=ms)
    elif code in (403, 451):
        r["error"] = f"сайт отказал (HTTP {code}) — похоже на гео-блок"
    elif p.returncode == 28:
        r["error"] = "таймаут"
    elif p.returncode in (7, 52, 56, 97):
        r["error"] = "сайт недоступен через это подключение"
    elif code:
        r["error"] = f"HTTP {code}"
    else:
        r["error"] = (p.stderr.strip().splitlines() or ["нет ответа"])[-1][:100]
    return r


def check_one(e, port, cfg):
    tcp_ok, tcp_ms = tcp_check(e["address"], e["port"])
    ok, ms, err, connect_ms = curl_via(port, cfg["test_url"], cfg["timeout"])
    if not ok:  # повтор — чтобы разовая потеря пакетов не давала ложную тревогу
        ok, ms, err, connect_ms = curl_via(port, cfg["test_url"], cfg["timeout"])
    if not ok and tcp_ok:
        err += " (порт доступен — похоже на блокировку протокола/SNI или ошибку настройки)"
    elif not ok and not tcp_ok:
        err += " (порт недоступен из этой сети)"
    sites = []
    if ok:  # сайты проверяем только через рабочее подключение
        for s in cfg.get("sites") or []:
            r = site_via(port, s, cfg["site_timeout"])
            if not r["ok"] and not r["error"].startswith("сайт отказал"):
                r = site_via(port, s, cfg["site_timeout"])  # повтор при сетевой ошибке
            sites.append(r)
    return {"ok": ok, "ms": ms, "connect_ms": connect_ms, "tcp_ok": tcp_ok, "tcp_ms": tcp_ms, "error": "" if ok else err, "sites": sites}


def speed_via(port, cfg):
    """Скачивание файла через подключение. -> (Мбит/с или None, ошибка)"""
    cmd = ["curl", "-sS", "-o", "/dev/null", "-w", "%{size_download} %{time_total}", "--max-time", str(cfg["speed_timeout"]),
           "-A", UA, "--noproxy", "", "-x", f"socks5h://127.0.0.1:{port}", cfg["speed_url"]]
    p = subprocess.run(cmd, capture_output=True, text=True)
    out = p.stdout.strip().split()
    try:
        size, t = float(out[0]), float(out[1])
    except (IndexError, ValueError):
        size, t = 0.0, 0.0
    # при таймауте считаем по тому, что успело скачаться (медленное подключение — тоже результат)
    if size >= 512 * 1024 and t > 0 and p.returncode in (0, 28):
        return round(size * 8 / t / 1e6, 1), ""
    return None, "таймаут" if p.returncode == 28 else ((p.stderr.strip().splitlines() or ["нет данных"])[-1][:100])


def run_speed(check, res, cfg):
    """Замер по очереди (параллельно подключения делили бы канал сервера) раз в speed_interval_min минут.
    Последние значения хранятся между запусками в speed.json."""
    try:
        with open(cfg["speed_state"], encoding="utf-8") as f:
            state = json.load(f)
    except (OSError, ValueError):
        state = {}
    interval = int(cfg.get("speed_interval_min") or 0)
    due = interval > 0 and time.time() - state.get("_at", 0) >= interval * 60
    if due:
        for i, e in enumerate(check):
            if res.get(i, {}).get("ok"):
                mbps, err = speed_via(cfg["base_port"] + i, cfg)
                state[e["name"]] = {"mbps": mbps, "error": err, "at": now_iso()}
        state["_at"] = time.time()
        try:
            write_result(cfg["speed_state"], state)
        except OSError:
            pass
    for i, e in enumerate(check):
        s = state.get(e["name"]) if interval > 0 else None
        if s and i in res:
            res[i]["speed_mbps"], res[i]["speed_error"], res[i]["speed_at"] = s.get("mbps"), s.get("error", ""), s.get("at", "")


def main():
    cfg = dict(DEFAULTS)
    with open(CONFIG, encoding="utf-8") as f:
        cfg.update(json.load(f))
    result = {"version": 3, "sites": [s["name"] for s in cfg.get("sites") or []], "checked_at": now_iso(), "server": socket.gethostname(), "test_url": cfg["test_url"],
              "results": [], "error": ""}
    try:
        links = fetch_subscription(cfg["sub_url"])
    except Exception as ex:
        result["error"] = f"не удалось получить подписку: {ex}"
        write_result(cfg["result"], result)
        return 1

    entries = [e for e in (parse_link(l) for l in links) if e and not is_placeholder(e)]
    if not entries:
        result["error"] = "в подписке нет подключений (проверьте тестового пользователя: активен ли, есть ли у него доступ к нодам)"
        write_result(cfg["result"], result)
        return 1

    check = [e for e in entries if not e.get("unsupported")]
    conf = {"log": {"loglevel": "warning"}, "inbounds": [], "outbounds": [], "routing": {"rules": []}}
    for i, e in enumerate(check):
        p = cfg["base_port"] + i
        conf["inbounds"].append({"tag": f"in{i}", "listen": "127.0.0.1", "port": p, "protocol": "socks",
                                 "settings": {"udp": False}})
        conf["outbounds"].append(dict(e["outbound"], tag=f"out{i}"))
        conf["routing"]["rules"].append({"type": "field", "inboundTag": [f"in{i}"], "outboundTag": f"out{i}"})

    res = {}
    proc = None
    with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as tf:
        json.dump(conf, tf)
        conf_path = tf.name
    try:
        if check:
            proc = subprocess.Popen([cfg["xray"], "run", "-c", conf_path],
                                    stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True)
            if not wait_port(cfg["base_port"] + len(check) - 1):
                err = ""
                if proc.poll() is not None:
                    err = (proc.stderr.read() or "").strip().splitlines()[-1:] or [""]
                    err = err[0]
                result["error"] = f"Xray не запустился: {err}"[:300]
            else:
                with ThreadPoolExecutor(max_workers=min(16, len(check))) as pool:
                    futs = {pool.submit(check_one, e, cfg["base_port"] + i, cfg): i for i, e in enumerate(check)}
                    for f, i in futs.items():
                        res[i] = f.result()
                run_speed(check, res, cfg)
    finally:
        if proc and proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(5)
            except subprocess.TimeoutExpired:
                proc.kill()
        os.unlink(conf_path)

    for i, e in enumerate(check):
        r = res.get(i, {"ok": False, "ms": None, "tcp_ok": None, "tcp_ms": None, "error": result["error"] or "не проверено", "sites": []})
        result["results"].append(dict({k: e.get(k) for k in ("name", "protocol", "address", "port", "network", "security")}, **r))
    for e in entries:
        if e.get("unsupported"):
            result["results"].append({"name": e["name"], "protocol": e["protocol"], "address": e["address"],
                                      "port": e["port"], "ok": None, "error": "протокол не поддерживается проверкой"})
    result["checked_at"] = now_iso()
    write_result(cfg["result"], result)
    return 0


if __name__ == "__main__":
    sys.exit(main())
