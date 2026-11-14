namespace RemnawaveMonitor;

// Страница мини-приложения: пользователям «Кабинет», «Статус», «Оплата»; админам «Сводка», «Трафик», «Сбои», «Настройки». Отдаётся ботом по GET /; все данные — из подписанных запросов /api/*.
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
.stats{display:flex;gap:10px}.stats .card{flex:1;padding:14px}.stats .big{font-size:22px;margin-top:6px}
.mono{font-family:'JetBrains Mono',ui-monospace,monospace;font-size:12px}
.nrow{padding:11px 0;border-top:1px solid #252c36}.nrow:first-child{border-top:none}
.rt{display:flex;flex-direction:column;align-items:flex-end;gap:2px}
.seg{display:flex;border:1px solid #2f3846;border-radius:12px;overflow:hidden}
.seg button{flex:1;min-height:44px;background:#171c23;border:none;color:#cbd2dc;font:600 13px Inter,system-ui,sans-serif;cursor:pointer}
.seg .act{background:#ffb020;color:#1a1200}
.plot{display:flex;align-items:flex-end;gap:3px;height:190px;border-bottom:1px solid #3a4452}
.col{flex:1;min-width:0;display:flex;flex-direction:column;align-items:center;justify-content:flex-end;gap:3px}
.cv{font-size:10px;color:#aab3c0}
.stk{display:flex;flex-direction:column;width:100%;border-radius:3px 3px 0 0;overflow:hidden}.stk i{display:block}
.dl{display:flex;gap:3px;margin-top:6px;font-size:10px;color:#9aa4b2;text-align:center}.dl span{flex:1;min-width:0}
.dot{display:inline-block;width:10px;height:10px;border-radius:3px;margin-right:8px}
.trow{display:flex;justify-content:space-between;align-items:center;gap:12px;width:100%;min-height:48px;padding:6px 0;background:none;border:none;border-top:1px solid #252c36;color:#e6e9ee;font:14px Inter,system-ui,sans-serif;text-align:left;cursor:pointer}
.trow:first-of-type{border-top:none}
.sw{flex:none;position:relative;width:46px;height:28px;border-radius:14px;border:1px solid #2f3846;background:#252c36}
.sw i{position:absolute;top:3px;left:3px;width:20px;height:20px;border-radius:50%;background:#9aa4b2}
.sw.on{background:#ffb020;border-color:#ffb020}.sw.on i{left:21px;background:#1a1200}
.fld{margin-top:12px}.fld label{display:block;font-size:13px;color:#9aa4b2;margin-bottom:6px;line-height:1.35}
.in{width:100%;min-height:46px;border-radius:10px;border:1px solid #2f3846;background:#0f1318;color:#e6e9ee;padding:0 12px;font:14px 'JetBrains Mono',ui-monospace,monospace}
.ta{min-height:96px;padding:10px 12px;resize:vertical}
.fld .btn{margin-top:8px;flex:none;width:100%}
.opt{display:flex;justify-content:space-between;align-items:center;gap:10px;width:100%;min-height:48px;padding:8px 12px;margin-top:8px;border-radius:12px;border:1px solid #2f3846;background:#1d232c;color:#e6e9ee;font:600 14px Inter,system-ui,sans-serif;text-align:left;cursor:pointer}
.opt.sel{border-color:#ffb020;box-shadow:inset 0 0 0 1px #ffb020}
.ov{position:fixed;inset:0;background:rgba(0,0,0,.55);display:flex;align-items:flex-end;z-index:5}
.sh{width:100%;background:#171c23;border-radius:16px 16px 0 0;padding:18px 16px calc(18px + env(safe-area-inset-bottom));display:flex;flex-direction:column;gap:10px}
pre.det{white-space:pre-wrap;word-break:break-word;font:13px 'JetBrains Mono',ui-monospace,monospace;background:#0f1318;border:1px solid #252c36;border-radius:10px;padding:12px;margin:10px 0 0}
.toast{position:fixed;left:50%;bottom:84px;transform:translateX(-50%);background:#e6e9ee;color:#0f1318;padding:10px 16px;border-radius:12px;font-weight:600}
</style>
</head>
<body>
<div id="app"></div>
<script>
(function () {
  var tg = window.Telegram && window.Telegram.WebApp;
  var app = document.getElementById('app');
  var PAL = ['#ffb020', '#4ade80', '#60a5fa', '#f472b6', '#a78bfa', '#fb923c', '#2dd4bf', '#facc15', '#94a3b8', '#f87171'];
  var S = { me: null, st: null, tab: '', err: '', data: {}, days: 7, draft: {}, pay: { step: 'form', sel: null, months: 0, created: null, busy: false }, busyRu: false };
  if (tg) { try { tg.ready(); tg.expand(); tg.setHeaderColor('#0f1318'); tg.setBackgroundColor('#0f1318'); } catch (e) {} }

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }
  function pill(text, cls) { return el('span', 'pill ' + cls, text); }
  function btn(text, cls, fn) { var b = el('button', cls || 'btn', text); if (fn) b.onclick = fn; return b; }
  function gib(b) { var g = b / 1073741824; return g >= 1000 ? (g / 1024).toFixed(2) + ' ТиБ' : (g >= 10 ? g.toFixed(1) : g.toFixed(2)) + ' ГиБ'; }

  function api(path, body) {
    return fetch(path, { method: 'POST', headers: { 'X-Init-Data': (tg && tg.initData) || '', 'Content-Type': 'application/json' }, body: JSON.stringify(body || {}) })
      .then(function (r) {
        if (r.ok) return r.json();
        return r.json().catch(function () { return {}; }).then(function (j) { var e = new Error(j.error || 'http'); e.code = r.status; throw e; });
      });
  }

  function copy(text) {
    function fallback() {
      var t = document.createElement('textarea');
      t.value = text; t.style.position = 'fixed'; t.style.opacity = '0';
      document.body.appendChild(t); t.select();
      var ok = false;
      try { ok = document.execCommand('copy'); } catch (e) {}
      document.body.removeChild(t);
      return ok;
    }
    if (navigator.clipboard && navigator.clipboard.writeText) {
      return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return fallback(); });
    }
    return Promise.resolve(fallback());
  }

  function toast(text, ms) {
    var t = el('div', 'toast', text);
    document.body.appendChild(t);
    setTimeout(function () { if (t.parentNode) t.parentNode.removeChild(t); }, ms || 2000);
  }
  function fail(e) { toast(e && e.code === 401 ? 'Сессия устарела: откройте приложение заново' : (e && e.message && e.message !== 'http' ? e.message : 'Не удалось выполнить запрос'), 3500); }

  // ---------- вкладки и загрузка ----------
  function tabList() {
    var m = S.me, t = [], subs = m.subs || [];
    if (m.role === 'admin') {
      t.push(['sum', 'Сводка'], ['tr', 'Трафик'], ['inc', 'Сбои']);
      if (m.main) t.push(['set', 'Настройки']);
      if (subs.length) t.push(['cab', 'Кабинет']);
    } else {
      t.push(['cab', 'Кабинет']);
      if (m.statusEnabled) t.push(['st', 'Статус']);
      if (m.pay && subs.some(function (u) { return u.canPay; })) t.push(['pay', 'Оплата']);
    }
    return t;
  }
  var TITLES = { cab: 'Мой кабинет', st: 'Статус сервиса', pay: 'Оплата', sum: 'Сводка', tr: 'Трафик', inc: 'Сбои', set: 'Настройки' };

  function loadTab() {
    var q = { sum: ['/api/admin/summary'], tr: ['/api/admin/traffic', { days: S.days }], inc: ['/api/admin/incidents'], set: ['/api/admin/settings'] }[S.tab];
    if (!q) return Promise.resolve();
    var t = S.tab;
    return api(q[0], q[1]).then(function (d) { S.data[t] = d; });
  }

  function load() {
    if (!tg || !tg.initData) { S.err = 'Откройте приложение из Telegram: кнопка «Кабинет» в боте.'; render(); return; }
    api('/api/me').then(function (me) {
      S.me = me; S.err = '';
      var tabs = tabList();
      if (!tabs.some(function (x) { return x[0] === S.tab; })) S.tab = tabs[0][0];
      return me.statusEnabled ? api('/api/status').then(function (st) { S.st = st; }) : (S.st = null);
    }).then(function () { return loadTab(); }).catch(function (e) {
      S.err = e.code === 403 ? 'Нет доступа. Мини-приложение доступно пользователям, у которых Telegram ID указан в подписке.'
        : e.code === 401 ? 'Сессия устарела. Закройте и откройте приложение заново.'
        : e.code === 429 ? 'Слишком много запросов. Подождите минуту.'
        : 'Не удалось загрузить данные. Попробуйте позже.';
    }).then(function () { render(); });
  }

  function go(t) {
    S.tab = t; render();
    loadTab().then(render).catch(function (e) { fail(e); });
  }

  // ---------- кабинет и статус (пользователь) ----------
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
    var cp = btn('Скопировать ссылку', 'btn pri', function () {
      copy(u.url).then(function (ok) {
        if (ok) toast('Ссылка скопирована');
        else if (tg && tg.showAlert) tg.showAlert(u.url);
      });
    });
    if (!u.url) cp.disabled = true;
    bt.appendChild(cp);
    if (u.canPay) {
      var inUser = S.me.role !== 'admin';
      bt.appendChild(btn('Продлить', 'btn', function () {
        if (inUser && S.me.pay) { S.pay = { step: 'form', sel: {}, months: 0, created: null, busy: false }; S.pay.sel[u.key] = true; go('pay'); }
        else if (tg) tg.close();
      }));
    }
    c.appendChild(bt);
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
    if (tabList().some(function (x) { return x[0] === 'st'; })) c.appendChild(btn('Открыть статус', 'lnk', function () { go('st'); }));
    return c;
  }

  function cabinet(main) {
    var subs = S.me.subs || [];
    if (!S.me.cabinet) main.appendChild(el('div', 'card dim', 'Просмотр подписок выключен администратором.'));
    else if (!subs.length) main.appendChild(el('div', 'card dim', 'К вашему Telegram не привязано ни одной подписки.'));
    subs.forEach(function (u) { main.appendChild(subCard(u)); });
    if (S.me.role !== 'admin' && S.st && S.st.enabled && S.st.ready) main.appendChild(summary());
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
    if (!st || !st.ready) { main.appendChild(el('div', 'card dim', 'Данные о серверах ещё не получены. Попробуйте через минуту.')); return; }
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

  // ---------- оплата (пользователь) ----------
  function payView(main) {
    var P = S.pay, me = S.me, subs = (me.subs || []).filter(function (u) { return u.canPay; });
    if (P.step === 'done') {
      var d = el('div', 'card');
      d.appendChild(el('b', '', 'Отправлено на проверку'));
      d.appendChild(el('p', 'dim', 'После подтверждения администратором подписки продлятся сами, бот напишет в чат. Если есть скриншот или чек перевода — пришлите его боту в чате: это ускорит проверку.'));
      d.appendChild(btn('Вернуться в чат', 'btn pri', function () { if (tg) tg.close(); }));
      main.appendChild(d);
      return;
    }
    if (P.step === 'created') {
      var c = P.created, k = el('div', 'card');
      k.appendChild(el('div', 'lbl', 'К оплате'));
      k.appendChild(el('div', 'big mt', c.amount));
      k.appendChild(el('div', 'dim mt', 'Продление на ' + c.months + ' мес: ' + c.names.join(', ')));
      k.appendChild(el('div', 'lbl sp', 'Реквизиты для перевода'));
      k.appendChild(el('pre', 'det', c.details));
      k.appendChild(el('div', 'dim note', 'Переведите всю сумму одним платежом и нажмите «Я оплатил». Администратор проверит поступление.'));
      var bt = el('div', 'btns');
      bt.appendChild(btn('Я оплатил', 'btn pri', function () {
        if (P.busy) return; P.busy = true;
        api('/api/pay/confirm', { id: c.id }).then(function () { P.step = 'done'; P.busy = false; render(); }).catch(function (e) { P.busy = false; fail(e); });
      }));
      bt.appendChild(btn('Отмена', 'btn', function () {
        api('/api/pay/cancel', { id: c.id }).then(function () { S.pay = { step: 'form', sel: null, months: 0, created: null, busy: false }; render(); }).catch(fail);
      }));
      k.appendChild(bt);
      main.appendChild(k);
      return;
    }
    if (!me.pay || !subs.length) { main.appendChild(el('div', 'card dim', 'Продлевать здесь нечего: оплата выключена или у подписок нет срока.')); return; }
    if (!P.sel) {
      P.sel = {};
      var exp = subs.filter(function (u) { return u.expiring; });
      (exp.length ? exp : subs.length === 1 ? subs : []).forEach(function (u) { P.sel[u.key] = true; });
    }
    var n = subs.filter(function (u) { return P.sel[u.key]; }).length;
    var c1 = el('div', 'card');
    c1.appendChild(el('div', 'lbl', 'Что продлить'));
    subs.forEach(function (u) {
      var o = el('button', 'opt' + (P.sel[u.key] ? ' sel' : ''));
      var l = el('span', '', u.name), r = el('span', 'dim', u.daysLeft > 0 ? 'ещё ' + u.daysLeft + ' дн' : 'истекла');
      o.appendChild(l); o.appendChild(r);
      o.onclick = function () { P.sel[u.key] = !P.sel[u.key]; render(); };
      c1.appendChild(o);
    });
    main.appendChild(c1);
    var c2 = el('div', 'card');
    c2.appendChild(el('div', 'lbl', 'На какой срок'));
    me.pay.plans.forEach(function (p) {
      var o = el('button', 'opt' + (P.months === p.months ? ' sel' : ''));
      o.appendChild(el('span', '', p.label));
      o.appendChild(el('span', 'dim', (p.price * Math.max(1, n)) + ' ' + me.pay.currency + (n > 1 ? ' (' + n + ' × ' + p.price + ')' : '')));
      o.onclick = function () { P.months = p.months; render(); };
      c2.appendChild(o);
    });
    main.appendChild(c2);
    var go2 = btn('Оформить заявку', 'btn pri', function () {
      if (P.busy) return; P.busy = true;
      var keys = subs.filter(function (u) { return P.sel[u.key]; }).map(function (u) { return u.key; });
      api('/api/pay/create', { keys: keys, months: P.months }).then(function (r) { P.created = r; P.step = 'created'; P.busy = false; render(); })
        .catch(function (e) { P.busy = false; fail(e); });
    });
    go2.disabled = !n || !P.months;
    var wrap = el('div', 'btns'); wrap.style.marginTop = '0'; wrap.appendChild(go2);
    main.appendChild(wrap);
  }

  // ---------- админ: сводка ----------
  function sheet(n) {
    var ov = el('div', 'ov'), sh = el('div', 'sh');
    ov.onclick = function (e) { if (e.target === ov) document.body.removeChild(ov); };
    sh.appendChild(el('b', '', 'Скрыть «' + n.name + '» из статуса'));
    sh.appendChild(el('div', 'dim', 'Пользователи не увидят ни текущий сбой, ни новые на это время. Вам тревоги приходят как обычно.'));
    [[1, '1 час'], [6, '6 часов'], [24, '24 часа'], [0, 'Показывать как обычно']].forEach(function (x) {
      sh.appendChild(btn(x[1], x[0] ? 'btn' : 'btn pri', function () {
        document.body.removeChild(ov);
        api('/api/admin/hide', { id: n.id, hours: x[0] }).then(function (r) { toast(r.message, 2500); return loadTab(); }).then(render).catch(fail);
      }));
    });
    sh.appendChild(btn('Отмена', 'lnk', function () { document.body.removeChild(ov); }));
    ov.appendChild(sh); document.body.appendChild(ov);
  }

  function sumView(main) {
    var d = S.data.sum;
    if (!d) { main.appendChild(el('div', 'card dim', 'Загрузка…')); return; }
    if (!d.ready) { main.appendChild(el('div', 'card dim', 'Данные панели ещё не получены. Попробуйте через минуту.')); return; }
    var up = d.nodes.filter(function (n) { return n.connected; }).length;
    var st = el('div', 'stats');
    [['Ноды', up + '/' + d.nodes.length, false], ['Онлайн', String(d.online), false], ['Проблем', String(d.problems.length), d.problems.length > 0]].forEach(function (x) {
      var c = el('div', 'card'); c.appendChild(el('div', 'lbl', x[0]));
      var v = el('div', 'big', x[1]); if (x[2]) v.style.color = '#f87171';
      c.appendChild(v); st.appendChild(c);
    });
    main.appendChild(st);
    var list = el('div', 'card list');
    d.nodes.forEach(function (n) {
      var r = el('div', 'row nrow'), l = el('div');
      l.appendChild(el('div', '', n.name));
      l.appendChild(el('div', 'mono dim', 'онлайн ' + n.online + ' · RAM ' + n.mem));
      var rt = el('div', 'rt');
      rt.appendChild(!n.connected ? pill('не в сети', 'bad') : n.ru === 'bad' ? pill('недоступен из РФ', 'bad') : n.ru === 'warn' ? pill('частично из РФ', 'warn') : pill('работает', 'ok'));
      rt.appendChild(btn(n.hidden ? 'скрыта до ' + n.hidden : 'скрыть из статуса', 'lnk', function () { sheet(n); }));
      r.appendChild(l); r.appendChild(rt); list.appendChild(r);
    });
    main.appendChild(list);
    var c = el('div', 'card');
    var top = el('div', 'row');
    top.appendChild(el('div', 'lbl', 'Автопроверка из РФ'));
    top.appendChild(el('span', 'mono dim', d.ruMin > 0 ? 'каждые ' + d.ruMin + ' мин' : 'выключена'));
    c.appendChild(top);
    var pr = d.problems.slice(0, 5);
    c.appendChild(el('div', 'sp', pr.length ? '' : 'Активных проблем нет.')).style.marginTop = '12px';
    pr.forEach(function (p) { var t = el('div', 'dim', p.subject + ' — ' + p.text + ' (с ' + p.since + ')'); t.style.marginTop = '8px'; c.appendChild(t); });
    var bt = el('div', 'btns');
    var rc = btn(S.busyRu ? 'Идёт проверка…' : 'Перепроверить', 'btn', function () {
      if (S.busyRu) return; S.busyRu = true; render();
      api('/api/admin/recheck').then(function (r) {
        toast(r.busy ? 'Проверка уже идёт' : 'Проверка запущена: о смене состояния придёт сообщение', 3000);
        setTimeout(function () { S.busyRu = false; render(); }, 8000);
      }).catch(function (e) { S.busyRu = false; fail(e); render(); });
    });
    rc.disabled = S.busyRu;
    bt.appendChild(rc); c.appendChild(bt);
    main.appendChild(c);
    main.appendChild(el('div', 'dim note', 'Обновлено ' + d.updated));
  }

  // ---------- админ: трафик ----------
  function trView(main) {
    var seg = el('div', 'seg');
    [[7, '7 дн'], [14, '14 дн'], [30, '30 дн']].forEach(function (x) {
      seg.appendChild(btn(x[1], S.days === x[0] ? 'act' : '', function () { S.days = x[0]; S.data.tr = null; render(); loadTab().then(render).catch(fail); }));
    });
    main.appendChild(seg);
    var d = S.data.tr;
    if (!d) { main.appendChild(el('div', 'card dim', 'Загрузка…')); return; }
    if (!d.available) { main.appendChild(el('div', 'card dim', 'Статистика недоступна: нужна панель 3.x и токен с правом чтения статистики трафика.')); return; }
    var c = el('div', 'card');
    c.appendChild(el('div', 'lbl', 'Всего за период'));
    c.appendChild(el('div', 'big mt', gib(d.total)));
    c.appendChild(el('div', 'mono dim mt', 'Ø ' + gib(d.total / d.days.length) + ' / день · пик ' + gib(d.peak) + ' (' + d.peakDay + ')'));
    main.appendChild(c);
    var tot = d.days.map(function (_, i) { return d.series.reduce(function (a, s) { return a + s.daily[i]; }, 0); });
    var max = Math.max.apply(null, tot) || 1;
    var ch = el('div', 'card'), plot = el('div', 'plot'), lab = el('div', 'dl'), many = d.days.length > 14;
    d.days.forEach(function (day, i) {
      var col = el('div', 'col');
      if (!many) col.appendChild(el('span', 'cv', String(Math.round(tot[i] / 1073741824))));
      var stk = el('div', 'stk');
      for (var k = d.series.length - 1; k >= 0; k--) {
        var v = d.series[k].daily[i];
        if (v > 0) { var seg2 = el('i'); seg2.style.height = Math.max(1, Math.round(v / max * 150)) + 'px'; seg2.style.background = PAL[k % PAL.length]; stk.appendChild(seg2); }
      }
      col.appendChild(stk); plot.appendChild(col);
      lab.appendChild(el('span', '', (!many || i % 3 === 0) ? day : ''));
    });
    ch.appendChild(plot); ch.appendChild(lab); main.appendChild(ch);
    var rk = el('div', 'card list');
    d.series.forEach(function (s, k) {
      var r = el('div', 'row'), l = el('span');
      var dot = el('i', 'dot'); dot.style.background = PAL[k % PAL.length];
      l.appendChild(dot); l.appendChild(document.createTextNode(s.name));
      r.appendChild(l); r.appendChild(el('span', 'mono', gib(s.total))); rk.appendChild(r);
    });
    main.appendChild(rk);
  }

  // ---------- админ: сбои ----------
  function incView(main) {
    var d = S.data.inc;
    if (!d) { main.appendChild(el('div', 'card dim', 'Загрузка…')); return; }
    if (!d.items.length) { main.appendChild(el('div', 'card dim', 'Журнал пуст.')); return; }
    d.items.forEach(function (i) {
      var c = el('div', 'card'), top = el('div', 'row');
      top.appendChild(el('b', '', i.subject));
      top.appendChild(i.active ? pill('идёт', i.severity === 'warn' ? 'warn' : 'bad') : pill('закрыт', 'ok'));
      c.appendChild(top);
      c.appendChild(el('div', 'dim mt', i.text));
      c.appendChild(el('div', 'mono dim mt', i.start + (i.end ? ' – ' + i.end : '') + ' · ' + i.dur));
      main.appendChild(c);
    });
  }

  // ---------- админ: настройки (главный админ) ----------
  var SCHEMA = [
    ['Уведомления админам', [['t', 'n_nodes', 'Ноды'], ['t', 'n_limits', 'Лимиты и подписки'], ['t', 'n_billing', 'Оплата серверов'], ['t', 'n_updates', 'Версии панели и нод'], ['t', 'n_users', 'События пользователей'], ['t', 'n_panel', 'Входы в панель']]],
    ['Тихие часы', [['x', 'q_range', 'Диапазон, например 23-8 или «23-8, 13-14»; пусто — выключены'], ['x', 'q_dnd', 'Кнопка «не беспокоить» действует до (ЧЧ:ММ)']]],
    ['Автопроверка из РФ', [['x', 'ru_min', 'Раз в сколько минут проверять (0 — выключена, иначе от 10)']]],
    ['Статус сервиса', [['t', 'st_on', 'Включён'], ['x', 'st_delay', 'Показывать сбой не раньше чем через, мин'], ['a', 'st_names', 'Названия нод: по строке «имя в панели = название»; «-» вместо названия — не показывать']]],
    ['Пользователи', [['t', 'u_cab', 'Личный кабинет'], ['t', 'u_rem', 'Напоминания'], ['x', 'u_days', 'Напомнить за, дней до конца подписки'], ['x', 'u_traf', 'Напомнить при расходе трафика от, %']]],
    ['Оплата', [['t', 'pay_on', 'Принимать оплату'], ['a', 'pay_plans', 'Тарифы: по строке «месяцев цена», например «3 400»'], ['a', 'pay_details', 'Реквизиты для перевода']]],
    ['Дополнительные админы', [['a', 'obs', 'По строке: Telegram ID и виды (nodes, limits, billing, updates, users, panel); без видов — все']]]
  ];

  function setView(main) {
    var d = S.data.set;
    if (!d) { main.appendChild(el('div', 'card dim', 'Загрузка…')); return; }
    SCHEMA.forEach(function (g) {
      var c = el('div', 'card');
      c.appendChild(el('div', 'lbl', g[0]));
      g[1].forEach(function (f) {
        var type = f[0], key = f[1];
        if (type === 't') {
          var on = !!d[key], row = el('button', 'trow');
          row.setAttribute('role', 'switch'); row.setAttribute('aria-checked', on ? 'true' : 'false');
          row.appendChild(el('span', '', f[2]));
          var sw = el('span', 'sw' + (on ? ' on' : '')); sw.appendChild(el('i')); row.appendChild(sw);
          row.onclick = function () {
            api('/api/admin/set', { op: 't', key: key }).then(function () { d[key] = !on; render(); }).catch(fail);
          };
          c.appendChild(row);
          return;
        }
        var w = el('div', 'fld'), id = 'f_' + key;
        var lb = el('label', '', f[2]); lb.setAttribute('for', id); w.appendChild(lb);
        var val = S.draft[key] != null ? S.draft[key] : String(d[key] == null ? '' : d[key]);
        var inp = type === 'a' ? el('textarea', 'in ta') : el('input', 'in');
        inp.id = id; inp.value = val; inp.autocomplete = 'off';
        inp.oninput = function () { S.draft[key] = inp.value; };
        w.appendChild(inp);
        w.appendChild(btn('Сохранить', 'btn', function () {
          api('/api/admin/set', { op: 'v', key: key, value: inp.value }).then(function () {
            d[key] = inp.value; delete S.draft[key]; toast('Сохранено'); render();
          }).catch(fail);
        }));
        c.appendChild(w);
      });
      main.appendChild(c);
    });
  }

  // ---------- каркас ----------
  function render() {
    var scroll = app.scrollTop;
    app.textContent = '';
    var hd = el('div', 'hd'), t = el('div', 't', TITLES[S.tab] || 'Мой кабинет');
    t.appendChild(el('i', '', '_')); hd.appendChild(t); app.appendChild(hd);
    var main = el('div', 'main'); app.appendChild(main);
    if (S.err) { main.appendChild(el('div', 'card dim', S.err)); return; }
    if (!S.me) { main.appendChild(el('div', 'card dim', 'Загрузка…')); return; }
    ({ cab: cabinet, st: status, pay: payView, sum: sumView, tr: trView, inc: incView, set: setView }[S.tab] || cabinet)(main);
    var tabs = tabList();
    if (tabs.length > 1) {
      var bar = el('div', 'tabs');
      tabs.forEach(function (x) { bar.appendChild(btn(x[1], 'tab' + (S.tab === x[0] ? ' on' : ''), function () { go(x[0]); })); });
      app.appendChild(bar);
    }
    app.scrollTop = scroll;
  }

  function refresh() { if (S.tab !== 'set' && S.tab !== 'pay' && !document.querySelector('.ov')) load(); }
  load();
  setInterval(refresh, 60000);
})();
</script>
</body>
</html>
""";
}
