namespace RemnawaveMonitor;

// Страница мини-приложения (этап 1: «Кабинет» и «Статус сервиса»). Отдаётся ботом по GET /; все данные — из подписанных запросов /api/*.
// Любой текст из панели вставляется только через textContent, поэтому подставить в страницу разметку нельзя.
public static class MiniAppPage
{
    public const string Html = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<title>Кабинет</title>
<script src="https://telegram.org/js/telegram-web-app.js"></script>
<link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;600&family=JetBrains+Mono:wght@400;700&display=swap" rel="stylesheet">
<style>
*{box-sizing:border-box}
body{margin:0;background:#0f1318;color:#e6e9ee;font-family:Inter,system-ui,sans-serif;font-size:14px}
.hd{padding:22px 20px 12px;display:flex;justify-content:space-between;align-items:center}
.t{font-family:'JetBrains Mono',ui-monospace,monospace;font-size:18px;font-weight:700}
.t i{color:#ffb020;font-style:normal}
.main{padding:4px 16px 100px;display:flex;flex-direction:column;gap:12px}
.card{background:#171c23;border:1px solid #252c36;border-radius:14px;padding:16px;line-height:1.45}
.lbl{font-family:'JetBrains Mono',ui-monospace,monospace;font-size:11px;letter-spacing:.08em;color:#9aa4b2;text-transform:uppercase}
.big{font-family:'JetBrains Mono',ui-monospace,monospace;font-size:26px;font-weight:700}
.dim{color:#9aa4b2}
.sp{margin-top:18px}.mt{margin-top:6px}
.note{font-size:12px;margin-top:10px}
.pill{display:inline-block;padding:4px 10px;border-radius:999px;font-size:12px;font-weight:600}
.ok{background:#16281f;color:#4ade80}.warn{background:#2a2512;color:#facc15}.bad{background:#2c1a1d;color:#f87171}
.row{display:flex;justify-content:space-between;align-items:center;gap:8px}
.list .row{padding:12px 0;border-top:1px solid #252c36}.list .row:first-child{border-top:none}
.btns{display:flex;gap:10px;margin-top:16px}
.btn{flex:1;min-height:46px;padding:0 16px;border-radius:12px;border:1px solid #2f3846;background:#1d232c;color:#e6e9ee;font:600 14px Inter,system-ui,sans-serif;cursor:pointer}
.btn:disabled{opacity:.5}
.pri{background:#ffb020;border-color:#ffb020;color:#1a1200}
.lnk{background:none;border:none;color:#ffb020;font:600 14px Inter,system-ui,sans-serif;padding:8px 0;cursor:pointer}
.bar{height:8px;border-radius:4px;background:#252c36;overflow:hidden;margin-top:8px}
.bar b{display:block;height:100%;background:#ffb020}
.alert{border-radius:14px;padding:16px;line-height:1.45}
.tabs{position:fixed;left:0;right:0;bottom:0;display:flex;border-top:1px solid #252c36;background:#12161c;padding-bottom:env(safe-area-inset-bottom)}
.tab{flex:1;min-height:58px;background:none;border:none;color:#9aa4b2;font:600 13px Inter,system-ui,sans-serif;cursor:pointer}
.on{color:#ffb020;box-shadow:inset 0 2px #ffb020}
.toast{position:fixed;left:50%;bottom:84px;transform:translateX(-50%);background:#e6e9ee;color:#0f1318;padding:10px 16px;border-radius:12px;font-weight:600}
</style>
</head>
<body>
<div id="app"></div>
<script>
(function () {
  var tg = window.Telegram && window.Telegram.WebApp;
  var app = document.getElementById('app');
  var S = { me: null, st: null, tab: 'cab', err: '' };
  if (tg) { try { tg.ready(); tg.expand(); tg.setHeaderColor('#0f1318'); tg.setBackgroundColor('#0f1318'); } catch (e) {} }

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }
  function pill(text, cls) { return el('span', 'pill ' + cls, text); }

  function api(path) {
    return fetch(path, { method: 'POST', headers: { 'X-Init-Data': (tg && tg.initData) || '' } }).then(function (r) {
      if (!r.ok) { var e = new Error('http'); e.code = r.status; throw e; }
      return r.json();
    });
  }

  function copy(text) {
    var done = function (ok) { return Promise.resolve(ok); };
    if (navigator.clipboard && navigator.clipboard.writeText) {
      return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return fallback(); });
    }
    return done(fallback());
    function fallback() {
      var t = document.createElement('textarea');
      t.value = text; t.style.position = 'fixed'; t.style.opacity = '0';
      document.body.appendChild(t); t.select();
      var ok = false;
      try { ok = document.execCommand('copy'); } catch (e) {}
      document.body.removeChild(t);
      return ok;
    }
  }

  function toast(text) {
    var t = el('div', 'toast', text);
    document.body.appendChild(t);
    setTimeout(function () { if (t.parentNode) t.parentNode.removeChild(t); }, 2000);
  }

  function load() {
    if (!tg || !tg.initData) { S.err = 'Откройте приложение из Telegram: кнопка «Кабинет» в боте.'; render(); return; }
    api('/api/me').then(function (me) {
      S.me = me; S.err = '';
      if (!me.statusEnabled) { S.st = null; return null; }
      return api('/api/status').then(function (st) { S.st = st; });
    }).catch(function (e) {
      S.err = e.code === 403 ? 'Нет доступа. Мини-приложение доступно пользователям, у которых Telegram ID указан в подписке.'
        : e.code === 401 ? 'Сессия устарела. Закройте и откройте приложение заново.'
        : e.code === 429 ? 'Слишком много запросов. Подождите минуту.'
        : 'Не удалось загрузить данные. Попробуйте позже.';
    }).then(function () { render(); });
  }

  function problems() {
    var n = (S.st && S.st.nodes) || [];
    var by = function (s) { return n.filter(function (x) { return x.state === s; }).map(function (x) { return x.name; }); };
    return { ru: by('ru'), down: by('down'), warn: by('warn') };
  }

  function subCard(u) {
    var c = el('div', 'card');
    var top = el('div', 'row');
    top.appendChild(el('b', '', u.name));
    top.appendChild(pill(u.statusText, u.status === 'ACTIVE' ? 'ok' : 'bad'));
    c.appendChild(top);
    c.appendChild(el('div', 'lbl sp', 'Осталось'));
    var r = el('div', 'row mt');
    if (u.unlimited) r.appendChild(el('div', 'big', 'бессрочно'));
    else {
      r.appendChild(el('div', 'big', u.daysLeft > 0 ? u.daysLeft + ' дн' : 'истекла'));
      r.appendChild(el('div', 'dim', 'до ' + u.expire));
    }
    c.appendChild(r);
    c.appendChild(el('div', 'lbl sp', 'Трафик'));
    var tr = el('div', 'row mt');
    tr.appendChild(el('b', '', u.traffic));
    if (!u.hasLimit) tr.appendChild(el('span', 'dim', 'без лимита'));
    c.appendChild(tr);
    if (u.hasLimit) {
      var bar = el('div', 'bar'), b = el('b');
      b.style.width = Math.max(0, Math.min(100, u.pct)) + '%';
      bar.appendChild(b); c.appendChild(bar);
    }
    var bt = el('div', 'btns');
    var cp = el('button', 'btn pri', 'Скопировать ссылку');
    if (!u.url) cp.disabled = true;
    cp.onclick = function () {
      copy(u.url).then(function (ok) {
        if (ok) toast('Ссылка скопирована');
        else if (tg && tg.showAlert) tg.showAlert(u.url);
      });
    };
    bt.appendChild(cp);
    if (u.canPay) {
      var pay = el('button', 'btn', 'Продлить в чате');
      pay.onclick = function () { if (tg) tg.close(); };
      bt.appendChild(pay);
    }
    c.appendChild(bt);
    if (u.canPay) c.appendChild(el('div', 'dim note', 'Оплата и продление — в чате с ботом, кнопка «Продлить».'));
    return c;
  }

  function summary() {
    var p = problems(), c = el('div', 'card');
    var top = el('div', 'row');
    top.appendChild(el('div', 'lbl', 'Статус сервиса'));
    var bad = p.ru.length + p.down.length, warn = p.warn.length;
    top.appendChild(pill(bad ? 'есть проблемы' : warn ? 'перебои' : 'всё работает', bad ? 'bad' : warn ? 'warn' : 'ok'));
    c.appendChild(top);
    var text = p.ru.length ? p.ru.join(', ') + ': недоступно из России. Подключайтесь через другие серверы.'
      : p.down.length ? p.down.join(', ') + ': не работает.'
      : p.warn.length ? p.warn.join(', ') + ': перебои у части провайдеров.'
      : 'Все серверы работают.';
    c.appendChild(el('p', '', text)).style.margin = '12px 0 4px';
    var go = el('button', 'lnk', 'Открыть статус');
    go.onclick = function () { S.tab = 'st'; render(); };
    c.appendChild(go);
    return c;
  }

  function cabinet(main) {
    var subs = S.me.subs || [];
    if (!S.me.cabinet) main.appendChild(el('div', 'card dim', 'Просмотр подписок выключен администратором.'));
    else if (!subs.length) main.appendChild(el('div', 'card dim', 'К вашему Telegram не привязано ни одной подписки.'));
    subs.forEach(function (u) { main.appendChild(subCard(u)); });
    if (S.st && S.st.enabled && S.st.ready) main.appendChild(summary());
  }

  function block(main, title, names, tail, bg, border, color, big) {
    if (!names.length) return;
    var a = el('div', 'alert');
    a.style.background = bg; a.style.border = '1px solid ' + border;
    var l = el('div', 'lbl', title); l.style.color = color; a.appendChild(l);
    var n = el('div', '', names.join(', ')); n.style.cssText = 'font-weight:600;margin:8px 0 ' + (tail ? '8px' : '0') + ';font-size:' + (big ? '18px' : '16px');
    a.appendChild(n);
    if (tail) a.appendChild(el('div', '', tail)).style.color = '#f3c5c5';
    main.appendChild(a);
  }

  function status(main) {
    var st = S.st;
    if (!st.ready) { main.appendChild(el('div', 'card dim', 'Данные о серверах ещё не получены. Попробуйте через минуту.')); return; }
    var p = problems();
    if (!p.ru.length && !p.down.length && !p.warn.length) main.appendChild(el('div', 'card', 'Все серверы работают.'));
    block(main, 'Недоступны из России', p.ru, 'Из России эти серверы сейчас не открываются. Подключайтесь через другие серверы.', '#2c1a1d', '#5a2a31', '#f87171', true);
    block(main, 'Не работают', p.down, '', '#2c1a1d', '#5a2a31', '#f87171', false);
    block(main, 'Перебои у части провайдеров', p.warn, '', '#2a2512', '#574c1e', '#facc15', false);
    var names = { ok: ['работает', 'ok'], ru: ['недоступен из РФ', 'bad'], down: ['не работает', 'bad'], warn: ['перебои', 'warn'] };
    var list = el('div', 'card list');
    st.nodes.forEach(function (n) {
      var r = el('div', 'row'), m = names[n.state] || names.ok;
      r.appendChild(el('span', '', n.name)); r.appendChild(pill(m[0], m[1])); list.appendChild(r);
    });
    main.appendChild(list);
    main.appendChild(el('div', 'dim note', 'Обновлено ' + st.updated + ' · сбой появляется в статусе через ' + st.delayMin + ' мин'));
  }

  function render() {
    app.textContent = '';
    var showSt = S.tab === 'st' && S.st && S.st.enabled;
    var hd = el('div', 'hd'), t = el('div', 't', showSt ? 'Статус сервиса' : 'Мой кабинет');
    t.appendChild(el('i', '', '_')); hd.appendChild(t); app.appendChild(hd);
    var main = el('div', 'main'); app.appendChild(main);
    if (S.err) { main.appendChild(el('div', 'card dim', S.err)); return; }
    if (!S.me) { main.appendChild(el('div', 'card dim', 'Загрузка…')); return; }
    if (showSt) status(main); else cabinet(main);
    if (S.st && S.st.enabled) {
      var tabs = el('div', 'tabs');
      [['cab', 'Кабинет'], ['st', 'Статус']].forEach(function (x) {
        var b = el('button', 'tab' + (S.tab === x[0] ? ' on' : ''), x[1]);
        b.onclick = function () { S.tab = x[0]; render(); };
        tabs.appendChild(b);
      });
      app.appendChild(tabs);
    }
  }

  load();
  setInterval(load, 60000);
})();
</script>
</body>
</html>
""";
}
