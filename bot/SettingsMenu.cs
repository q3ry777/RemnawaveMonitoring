using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

// Настройки бота в самом боте: /settings (и кнопка «⚙️ Настройки»), только главный админ.
// Переключатели и числа — кнопками, списки и тексты — сообщением. Значения применяются сразу и хранятся в /data/settings.json.
// Подключения (токен бота, адрес и токены панели, порты, ключи) остаются в программе и .env.
public partial class Bot
{
    readonly Dictionary<string, string> _awaitSet = new();   // чат → какую настройку бот ждёт сообщением

    static string OnOff(bool v) => v ? "✅" : "⬜";

    static string PageOf(string key) =>
        key.StartsWith("n_") ? "notif" : key.StartsWith("st_") ? "status" : key == "ru_min" ? "ru" : key.StartsWith("q_") ? "quiet"
        : key.StartsWith("u_") ? "users" : key.StartsWith("pay_") ? "pay" : key == "obs" ? "obs" : "main";

    static string QuietText(HashSet<int> hours)
    {
        if (hours.Count == 0) return "выключены";
        if (hours.Count == 24) return "круглосуточно";
        var starts = hours.Where(h => !hours.Contains((h + 23) % 24)).OrderBy(h => h).ToList();
        return string.Join(", ", starts.Select(s =>
        {
            int e = s;
            while (hours.Contains((e + 1) % 24)) e = (e + 1) % 24;
            return $"{s:00}:00–{(e + 1) % 24:00}:00";
        }));
    }

    void ShowSettings(string chat)
    {
        var (text, markup) = SettingsPage("main");
        _tg.Send(chat, text, markup);
    }

    (string text, string markup) SettingsPage(string page)
    {
        var rows = new List<(string, string)[]>();
        var back = new[] { ("◀️ Назад", "set:p:main") };
        string text;
        switch (page)
        {
            case "notif":
                text = "🔔 Что присылать админам. Нажмите, чтобы включить или выключить.\nСобытия пользователей и входы в панель приходят через вебхуки панели.";
                rows.Add(new[] { ($"{OnOff(_cfg.NotifyNodes)} Ноды", "set:t:n_nodes"), ($"{OnOff(_cfg.NotifyLimits)} Лимиты и подписки", "set:t:n_limits") });
                rows.Add(new[] { ($"{OnOff(_cfg.NotifyBilling)} Оплата серверов", "set:t:n_billing"), ($"{OnOff(_cfg.NotifyUpdates)} Версии панели и нод", "set:t:n_updates") });
                rows.Add(new[] { ($"{OnOff(_cfg.NotifyUsers)} События пользователей", "set:t:n_users"), ($"{OnOff(_cfg.NotifyPanel)} Входы в панель", "set:t:n_panel") });
                rows.Add(back);
                break;
            case "quiet":
                text = $"🌙 Тихие часы: {QuietText(_cfg.QuietHours)} (часовой пояс бота). Ответы на команды приходят всегда.\nКнопка «не беспокоить» — тишина до {_cfg.DndTime:hh\\:mm}.";
                rows.Add(new[] { ("Выключить", "set:v:q_range:off"), ("Ночь 23–08", "set:v:q_range:23-8") });
                rows.Add(new[] { ("Свой диапазон…", "set:x:q_range"), ($"«Не беспокоить» до {_cfg.DndTime:hh\\:mm}…", "set:x:q_dnd") });
                rows.Add(back);
                break;
            case "ru":
                text = "🔎 Автопроверка доступности нод из России (check-host.net): " +
                       (_cfg.CheckIntervalMin > 0 ? $"каждые {_cfg.CheckIntervalMin} мин." : "выключена.") +
                       "\nПроверяются до двух портов каждой ноды; нода, которую панель уже считает отключённой, пропускается. О смене состояния придёт сообщение, смена подтверждается повторной проверкой.";
                rows.Add(new[] { ("Выкл", "set:v:ru_min:0"), ("30 мин", "set:v:ru_min:30"), ("1 ч", "set:v:ru_min:60"), ("3 ч", "set:v:ru_min:180") });
                rows.Add(new[] { ("Своё значение…", "set:x:ru_min") });
                rows.Add(back);
                break;
            case "status":
                text = $"📡 Статус сервиса для пользователей: {(_cfg.StatusEnabled ? "включён" : "выключен")}.\n" +
                       $"Сбой появляется в статусе через {_cfg.StatusDelayMin} мин.\n" +
                       "Названия нод: " + (_cfg.StatusNames.Count == 0 ? "по умолчанию (страна и номер)." :
                           "\n" + string.Join("\n", _cfg.StatusNames.Select(kv => $"• {kv.Key} → {(kv.Value == "-" ? "не показывать" : kv.Value)}"))) +
                       "\nПользователям ничего не рассылается — статус виден по кнопке. Блок «недоступна из РФ» работает при включённой автопроверке.";
                rows.Add(new[] { ($"{OnOff(_cfg.StatusEnabled)} Включён", "set:t:st_on") });
                rows.Add(new[] { ("Без задержки", "set:v:st_delay:0"), ("5 мин", "set:v:st_delay:5"), ("10 мин", "set:v:st_delay:10"), ("30 мин", "set:v:st_delay:30") });
                rows.Add(new[] { ("Своя задержка…", "set:x:st_delay"), ("Названия нод…", "set:x:st_names") });
                rows.Add(new[] { ("🙈 Скрыть сбой из статуса", "hidmenu:") });
                rows.Add(back);
                break;
            case "users":
                text = $"👤 Пользователи панели (по Telegram ID).\nНапоминания: за {_cfg.RemindDays} дн. до конца подписки и при трафике от {_cfg.RemindTraffic}%.";
                rows.Add(new[] { ($"{OnOff(_cfg.UserCabinet)} Личный кабинет", "set:t:u_cab"), ($"{OnOff(_cfg.UserReminders)} Напоминания", "set:t:u_rem") });
                rows.Add(new[] { ($"За сколько дней: {_cfg.RemindDays}…", "set:x:u_days"), ($"Трафик от {_cfg.RemindTraffic}%…", "set:x:u_traf") });
                rows.Add(back);
                break;
            case "pay":
                text = $"💳 Оплата переводом: {(_cfg.PayEnabled ? "включена" : "выключена")} (подтверждаете вы, подписка продлевается сама).\n" +
                       "Тарифы: " + (_cfg.PayPlans.Count == 0 ? "не заданы" : string.Join("; ", _cfg.PayPlans.Select(x => $"{x.months} мес — {x.price} {_cfg.Currency}"))) + "\n" +
                       "Реквизиты: " + (_cfg.PayDetails == "" ? "не заданы" : "\n" + _cfg.PayDetails) +
                       "\nТокен панели с правом продления задаётся в программе (команда установки).";
                rows.Add(new[] { ($"{OnOff(_cfg.PayEnabled)} Принимать оплату", "set:t:pay_on") });
                rows.Add(new[] { ("Тарифы…", "set:x:pay_plans"), ("Реквизиты…", "set:x:pay_details") });
                rows.Add(back);
                break;
            case "obs":
                text = "👥 Дополнительные админы: видят уведомления выбранных видов и команды просмотра.\n" +
                       (_cfg.Observers.Count == 0 ? "Список пуст." :
                           string.Join("\n", _cfg.Observers.Select(kv => $"• {kv.Key} — {(kv.Value.Count == BotConfig.Kinds.Length ? "всё" : string.Join(", ", kv.Value))}")));
                rows.Add(new[] { ("Изменить список…", "set:x:obs") });
                rows.Add(back);
                break;
            default:
                text = "⚙️ Настройки бота. Меняются сразу, перезапуск не нужен.\nПодключение (токены, адрес панели, порты) остаётся в программе.";
                rows.Add(new[] { ("🔔 Уведомления", "set:p:notif"), ("🌙 Тихие часы", "set:p:quiet") });
                rows.Add(new[] { ("🔎 Автопроверка из РФ", "set:p:ru"), ("📡 Статус сервиса", "set:p:status") });
                rows.Add(new[] { ("👤 Пользователи", "set:p:users"), ("💳 Оплата", "set:p:pay") });
                rows.Add(new[] { ("👥 Доп. админы", "set:p:obs") });
                break;
        }
        return (text, Telegram.InlineRows(rows));
    }

    // null — применено, иначе причина отказа
    string? ToggleSetting(string key)
    {
        switch (key)
        {
            case "n_nodes": _cfg.NotifyNodes = !_cfg.NotifyNodes; break;
            case "n_limits": _cfg.NotifyLimits = !_cfg.NotifyLimits; break;
            case "n_billing": _cfg.NotifyBilling = !_cfg.NotifyBilling; break;
            case "n_updates": _cfg.NotifyUpdates = !_cfg.NotifyUpdates; break;
            case "n_users": _cfg.NotifyUsers = !_cfg.NotifyUsers; break;
            case "n_panel": _cfg.NotifyPanel = !_cfg.NotifyPanel; break;
            case "st_on": _cfg.StatusEnabled = !_cfg.StatusEnabled; break;
            case "u_cab": _cfg.UserCabinet = !_cfg.UserCabinet; break;
            case "u_rem": _cfg.UserReminders = !_cfg.UserReminders; break;
            case "pay_on":
                if (!_cfg.PayEnabled && (_cfg.PayPlans.Count == 0 || _cfg.PayDetails == "")) return "Сначала задайте тарифы и реквизиты";
                _cfg.PayEnabled = !_cfg.PayEnabled;
                break;
            default: return "Неизвестная настройка";
        }
        _cfg.SaveSettings();
        return null;
    }

    static string[] Lines(string v) => v.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x != "").ToArray();
    static bool IsClear(string v) => v is "-" or "—" || v.Equals("сбросить", StringComparison.OrdinalIgnoreCase) || v.Equals("очистить", StringComparison.OrdinalIgnoreCase);

    // null — применено, иначе причина отказа
    string? ApplyValue(string key, string raw)
    {
        var v = raw.Trim();
        switch (key)
        {
            case "ru_min":
                if (!int.TryParse(v, out var m) || m < 0 || m is > 0 and < 10 || m > 1440) return "Нужно число: 0 — выключить, иначе от 10 до 1440 минут.";
                _cfg.CheckIntervalMin = m;
                break;
            case "st_delay":
                if (!int.TryParse(v, out var d) || d is < 0 or > 1440) return "Нужно число минут от 0 до 1440.";
                _cfg.StatusDelayMin = d;
                break;
            case "u_days":
                if (!int.TryParse(v, out var days) || days is < 1 or > 30) return "Нужно число дней от 1 до 30.";
                _cfg.RemindDays = days;
                break;
            case "u_traf":
                if (!int.TryParse(v.TrimEnd('%').Trim(), out var tr) || tr is < 50 or > 99) return "Нужен процент от 50 до 99.";
                _cfg.RemindTraffic = tr;
                break;
            case "q_range":
            {
                var hours = new HashSet<int>();
                if (!(IsClear(v) || v.Equals("off", StringComparison.OrdinalIgnoreCase) || v.Equals("выкл", StringComparison.OrdinalIgnoreCase)))
                    foreach (var part in v.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var r = part.Split('-');
                        if (r.Length == 2 && int.TryParse(r[0], out var a) && int.TryParse(r[1], out var b) && a is >= 0 and < 24 && b is >= 0 and < 24)
                            for (int h = a; h != b; h = (h + 1) % 24) hours.Add(h);
                        else if (int.TryParse(part, out var one) && one is >= 0 and < 24) hours.Add(one);
                        else return $"«{part}»: нужен диапазон «23-8» (с 23:00 до 08:00), час «13» или слово off.";
                    }
                _cfg.QuietHours = hours;
                break;
            }
            case "q_dnd":
                if (!TimeSpan.TryParseExact(v.Replace('.', ':'), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t) || t >= TimeSpan.FromDays(1))
                    return "Время в формате ЧЧ:ММ, например 08:00.";
                _cfg.DndTime = t;
                break;
            case "st_names":
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!IsClear(v))
                    foreach (var line in Lines(v))
                    {
                        var kv = line.Split('=', 2);
                        if (kv.Length < 2 || kv[0].Trim() == "" || kv[1].Trim() == "" || line.Contains(';'))
                            return $"«{line}»: нужно «имя ноды = название», например «Aeza_SWE = Швеция №1» (или «-» вместо названия, чтобы не показывать ноду).";
                        map[kv[0].Trim()] = kv[1].Trim();
                    }
                _cfg.StatusNames = map;
                break;
            }
            case "pay_plans":
            {
                var plans = new List<(int months, int price)>();
                foreach (var line in Lines(v))
                {
                    var p = line.Split(new[] { ' ', '\t', ':', '-', '—' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 2 || !int.TryParse(p[0], out var mo) || mo is < 1 or > 36 || !int.TryParse(new string(p[1].Where(char.IsDigit).ToArray()), out var pr))
                        return $"«{line}»: нужно «месяцев цена», например «3 400».";
                    plans.Add((mo, pr));
                }
                if (plans.Count == 0) return "Добавьте хотя бы один тариф, например «1 150».";
                _cfg.PayPlans = plans;
                break;
            }
            case "pay_details":
                if (IsClear(v))
                {
                    if (_cfg.PayEnabled) return "Пока оплата включена, реквизиты нужны. Сначала выключите оплату.";
                    _cfg.PayDetails = "";
                }
                else _cfg.PayDetails = v.Replace("\r\n", "\n");
                break;
            case "obs":
            {
                var map = new Dictionary<string, HashSet<string>>();
                if (!IsClear(v))
                    foreach (var line in Lines(v))
                    {
                        var parts = line.Split(new[] { ' ', ':', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 0 || !long.TryParse(parts[0], out _)) return $"«{line}»: строка должна начинаться с Telegram ID, например «123456789 nodes,billing».";
                        var kinds = parts.Length > 1
                            ? parts[1].Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).ToList()
                            : new List<string>();
                        var bad = kinds.FirstOrDefault(k => !BotConfig.Kinds.Contains(k));
                        if (bad != null) return $"«{bad}»: такого вида нет. Доступны: {string.Join(", ", BotConfig.Kinds)}.";
                        map[parts[0]] = kinds.Count > 0 ? kinds.ToHashSet() : BotConfig.Kinds.ToHashSet();
                    }
                _cfg.Observers = map;
                break;
            }
            default: return "Неизвестная настройка";
        }
        _cfg.SaveSettings();
        return null;
    }

    string InputPrompt(string key) => key switch
    {
        "ru_min" => "Через сколько минут проверять ноды из РФ? 0 — выключить, иначе от 10 до 1440.",
        "st_delay" => "Через сколько минут сбой появляется в статусе? Число от 0 до 1440.",
        "u_days" => "За сколько дней до конца подписки напоминать? Число от 1 до 30.",
        "u_traf" => "При каком расходе трафика напоминать, в процентах? От 50 до 99.",
        "q_range" => "Тихие часы: диапазон «23-8» (с 23:00 до 08:00), несколько через запятую «23-8, 13-14», один час «13» или off, чтобы выключить.",
        "q_dnd" => "До какого времени действует кнопка «не беспокоить»? Формат ЧЧ:ММ, например 08:00.",
        "st_names" => "Названия нод в статусе — по строке «имя ноды в панели = название», например «Aeza_SWE = Швеция №1». «-» вместо названия — не показывать ноду. Чтобы сбросить всё — «-».\n\nСейчас:\n" +
                      (_cfg.StatusNames.Count == 0 ? "(по умолчанию)" : string.Join("\n", _cfg.StatusNames.Select(kv => $"{kv.Key} = {kv.Value}"))),
        "pay_plans" => "Тарифы — по строке «месяцев цена», например «3 400».\n\nСейчас:\n" + string.Join("\n", _cfg.PayPlans.Select(x => $"{x.months} {x.price}")),
        "pay_details" => "Реквизиты для перевода — то, что увидит пользователь. Можно несколько строк. Чтобы очистить — «-».\n\nСейчас:\n" + (_cfg.PayDetails == "" ? "(не заданы)" : _cfg.PayDetails),
        "obs" => $"Дополнительные админы — по строке «Telegram ID виды». Виды: {string.Join(", ", BotConfig.Kinds)} (без списка — все). Пример: 123456789 nodes,billing. Чтобы очистить список — «-».\n\nСейчас:\n" +
                 (_cfg.Observers.Count == 0 ? "(пусто)" : string.Join("\n", _cfg.Observers.Select(kv => $"{kv.Key} {string.Join(",", kv.Value)}"))),
        _ => "Введите значение.",
    };

    void AskInput(string chat, string key)
    {
        lock (_lock) _awaitSet[chat] = key;
        _tg.Send(chat, "✏️ " + InputPrompt(key), Telegram.Inline(new[] { ("✖ Отмена", "set:x:cancel") }));
    }

    // Админ прислал текст, пока бот ждёт значение настройки. true — сообщение обработано
    bool HandleSettingInput(string chat, string raw)
    {
        string? key;
        lock (_lock) { if (!_awaitSet.TryGetValue(chat, out key)) return false; }
        var t = Norm(raw);
        // команда или кнопка меню — ввод отменяется, команда выполняется как обычно
        if (raw.StartsWith('/') || AdminButtons.Any(b => Norm(b.text) == t)) { lock (_lock) _awaitSet.Remove(chat); return false; }
        if (t.Equals("отмена", StringComparison.OrdinalIgnoreCase)) { lock (_lock) _awaitSet.Remove(chat); _tg.Send(chat, "Отменено.", AdminKeyboard(chat)); return true; }
        var err = ApplyValue(key, raw);
        if (err != null)
        {
            _tg.Send(chat, "⚠️ " + err + "\nПопробуйте ещё раз или нажмите «Отмена».", Telegram.Inline(new[] { ("✖ Отмена", "set:x:cancel") }));
            return true;
        }
        lock (_lock) _awaitSet.Remove(chat);
        var (text, markup) = SettingsPage(PageOf(key));
        _tg.Send(chat, "✅ Сохранено.\n\n" + text, markup);
        return true;
    }

    async Task SettingsCallbackAsync(string chat, string id, string data, long messageId)
    {
        if (!_cfg.Chats.Contains(chat)) { await _tg.AnswerCallbackAsync(id, "Настройки — только у главного админа"); return; }
        var p = data.Split(':', 4);   // set : вид : ключ : значение
        string kind = p.Length > 1 ? p[1] : "", key = p.Length > 2 ? p[2] : "", val = p.Length > 3 ? p[3] : "";
        string page = "main", toast = "";
        switch (kind)
        {
            case "p": page = key; break;
            case "t": toast = ToggleSetting(key) ?? ""; page = PageOf(key); break;
            case "v": toast = ApplyValue(key, val) ?? "Сохранено"; page = PageOf(key); break;
            case "x":
                if (key == "cancel")
                {
                    lock (_lock) _awaitSet.Remove(chat);
                    await _tg.AnswerCallbackAsync(id, "Отменено");
                    return;
                }
                await _tg.AnswerCallbackAsync(id);
                AskInput(chat, key);
                return;
        }
        await _tg.AnswerCallbackAsync(id, toast);
        var (text, markup) = SettingsPage(page);
        await _tg.EditAsync(chat, messageId, text, markup);
    }
}
