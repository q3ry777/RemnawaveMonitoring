using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RemnawaveMonitor;

// Статус сервиса для пользователей: кнопка «📡 Статус сервиса» у тех, чей Telegram ID указан в панели.
// Показывает ноды под публичными названиями, «недоступна из РФ» — отдельным заметным блоком и инциденты за 7 дней.
// Бот ничего не рассылает пользователям: оповещать о сбоях админ будет сам. Сбой можно скрыть из статуса
// (ложная тревога, технические работы) — кнопкой под тревогой или командой /hide.
public partial class Bot
{
    static readonly Dictionary<string, string> CountryRu = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AT"] = "Австрия", ["AE"] = "ОАЭ", ["AM"] = "Армения", ["BG"] = "Болгария", ["BY"] = "Беларусь", ["CA"] = "Канада",
        ["CH"] = "Швейцария", ["CZ"] = "Чехия", ["DE"] = "Германия", ["DK"] = "Дания", ["EE"] = "Эстония", ["ES"] = "Испания",
        ["FI"] = "Финляндия", ["FR"] = "Франция", ["GB"] = "Великобритания", ["GE"] = "Грузия", ["HK"] = "Гонконг", ["IL"] = "Израиль",
        ["IN"] = "Индия", ["IT"] = "Италия", ["JP"] = "Япония", ["KZ"] = "Казахстан", ["LT"] = "Литва", ["LV"] = "Латвия",
        ["MD"] = "Молдова", ["NL"] = "Нидерланды", ["NO"] = "Норвегия", ["PL"] = "Польша", ["RO"] = "Румыния", ["RS"] = "Сербия",
        ["RU"] = "Россия", ["SE"] = "Швеция", ["SG"] = "Сингапур", ["TR"] = "Турция", ["UA"] = "Украина", ["US"] = "США",
    };

    // Состояние ноды в статусе: ok — работает, warn — перебои у части провайдеров РФ, ru — недоступна из РФ, down — не отвечает совсем
    record SvcNode(NodeRow Node, string Name, string State, DateTime Since);

    static string NodeId(NodeRow n) => n.NodeUuid != "" ? n.NodeUuid : n.Name;

    // Название ноды для пользователей: правило из STATUS_NAMES, иначе «Страна #N». null — нода скрыта правилом «имя=-»
    Dictionary<string, string?> PublicNames(List<NodeRow> nodes)
    {
        var map = new Dictionary<string, string?>();
        var counters = new Dictionary<string, int>();
        foreach (var n in nodes.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (_cfg.StatusNames.TryGetValue(n.Name, out var custom)) { map[n.Name] = custom == "-" ? null : custom; continue; }
            var country = CountryRu.TryGetValue(n.Country, out var c) ? c : n.Country != "" ? n.Country.ToUpperInvariant() : "Сервер";
            counters[country] = counters.GetValueOrDefault(country) + 1;
            map[n.Name] = $"{country} #{counters[country]}";
        }
        return map;
    }

    // Сбой скрыт админом: окно скрытия ноды пересекается со временем сбоя
    bool HiddenBy(string node, Incident i, DateTime now) =>
        _state.StatusHides.Any(h => h.Node == node && h.From <= (i.End ?? now) && h.Until >= i.Start);

    // Вызывать под _lock
    List<SvcNode> ServiceNodes(DateTime now)
    {
        var nodes = _snap?.Nodes ?? new List<NodeRow>();
        var names = PublicNames(nodes);
        var log = _log.All();
        var delay = TimeSpan.FromMinutes(_cfg.StatusDelayMin);   // сбой появляется в статусе не сразу: разовые сбои проверки не светятся
        var res = new List<SvcNode>();
        foreach (var n in nodes.Where(x => names[x.Name] != null).OrderBy(x => names[x.Name], StringComparer.OrdinalIgnoreCase))
        {
            var act = log.Where(i => i.End == null && (i.Key == n.Name + "|conn" || i.Key == n.Name + "|ru")
                                     && now - i.Start >= delay && !HiddenBy(n.Name, i, now)).ToList();
            var down = act.FirstOrDefault(i => i.Key.EndsWith("|conn"));
            var ru = act.FirstOrDefault(i => i.Key.EndsWith("|ru"));
            if (down != null) res.Add(new SvcNode(n, names[n.Name]!, "down", down.Start));
            else if (ru != null) res.Add(new SvcNode(n, names[n.Name]!, ru.Severity == "warn" ? "warn" : "ru", ru.Start));
            else res.Add(new SvcNode(n, names[n.Name]!, "ok", now));
        }
        return res;
    }

    static string Since(DateTime t, DateTime now) => t.Date == now.Date ? $"с {t:HH:mm}" : $"с {t:dd.MM HH:mm}";

    // admin: тот же статус, что видят пользователи, плюс список скрытых нод
    string BuildServiceStatus(bool admin)
    {
        lock (_lock)
        {
            if (!_cfg.StatusEnabled)
                return admin ? "Статус сервиса для пользователей выключен: настройки программы → telegram-бот → «статус сервиса для пользователей»." : "";
            if (_snap == null) return "Данные о серверах ещё не получены — попробуйте через минуту.";
            var now = DateTime.Now;
            var list = ServiceNodes(now);
            if (list.Count == 0) return "Список серверов пуст.";
            string Tag(SvcNode s) => Fmt.Flag(s.Node.Country) + s.Name;

            var sb = new StringBuilder("📡 Статус сервиса\n\n");
            var ruNodes = list.Where(s => s.State == "ru").ToList();
            var downNodes = list.Where(s => s.State == "down").ToList();
            var warnNodes = list.Where(s => s.State == "warn").ToList();
            if (ruNodes.Count + downNodes.Count + warnNodes.Count == 0) sb.AppendLine("✅ Все серверы работают.\n");
            if (ruNodes.Count > 0)
                sb.AppendLine("🚫 Недоступны из России: " + string.Join(", ", ruNodes.Select(Tag)) + ".\n" +
                              "Из России эти серверы сейчас не открываются — подключайтесь через другие серверы.\n");
            if (downNodes.Count > 0) sb.AppendLine("🔴 Не работают: " + string.Join(", ", downNodes.Select(Tag)) + ".\n");
            if (warnNodes.Count > 0) sb.AppendLine("🟡 Перебои у части провайдеров в России: " + string.Join(", ", warnNodes.Select(Tag)) + ".\n");

            sb.AppendLine("Серверы:");
            foreach (var s in list)
                sb.AppendLine($"{Tag(s)} — " + s.State switch
                {
                    "ru" => $"🚫 недоступен из России ({Since(s.Since, now)})",
                    "down" => $"🔴 не работает ({Since(s.Since, now)})",
                    "warn" => $"🟡 перебои у части провайдеров ({Since(s.Since, now)})",
                    _ => "✅ работает",
                });

            // инциденты за 7 дней (только видимые ноды, не скрытые админом, не короче задержки показа)
            var delay = TimeSpan.FromMinutes(_cfg.StatusDelayMin);
            var names = list.ToDictionary(s => s.Node.Name, s => Tag(s));
            var items = _log.All()
                .Where(i => (i.Key.EndsWith("|conn") || i.Key.EndsWith("|ru")) && i.Start >= now.AddDays(-7))
                .Select(i => (inc: i, node: i.Key[..i.Key.LastIndexOf('|')]))
                .Where(x => names.ContainsKey(x.node) && (x.inc.End ?? now) - x.inc.Start >= delay && !HiddenBy(x.node, x.inc, now))
                .OrderByDescending(x => x.inc.Start).Take(10).ToList();
            sb.AppendLine();
            if (items.Count == 0) sb.AppendLine("За последние 7 дней сбоев не было.");
            else
            {
                sb.AppendLine("Сбои за 7 дней:");
                foreach (var (inc, node) in items)
                {
                    var what = inc.Key.EndsWith("|ru")
                        ? (inc.Severity == "warn" ? "🟡 перебои у части провайдеров в России" : "🚫 недоступен из России")
                        : "🔴 сервер не отвечал";
                    var when = inc.End is DateTime end
                        ? $"{inc.Start:dd.MM HH:mm}–{end:HH:mm} ({IncidentLog.Dur(end - inc.Start)})"
                        : $"{Since(inc.Start, now)}, продолжается";
                    sb.AppendLine($"{names[node]} — {what}: {when}");
                }
            }

            if (admin)
            {
                var hidden = _state.StatusHides.Where(h => h.Until > now).GroupBy(h => h.Node).ToList();
                if (hidden.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("🙈 Скрыто для пользователей: " + string.Join(", ", hidden.Select(g => $"{g.Key} (до {g.Max(h => h.Until):HH:mm})")) + ". Команда /hide.");
                }
            }
            sb.AppendLine();
            sb.Append($"Обновлено {now:HH:mm}");
            return sb.ToString();
        }
    }

    // ---- скрыть сбой ноды из статуса (ложная тревога, работы) ----
    NodeRow? FindNode(string id)
    {
        lock (_lock) return _snap?.Nodes.FirstOrDefault(n => NodeId(n) == id);
    }

    // Кнопка под тревогой админу
    string? HideButton(string nodeName)
    {
        if (!_cfg.StatusEnabled) return null;
        var n = _snap?.Nodes.FirstOrDefault(x => x.Name == nodeName);
        return n == null ? null : Telegram.Inline(new[] { ("🙈 Скрыть из статуса", "hid:" + NodeId(n)) });
    }

    // hours <= 0 — показывать снова
    string SetStatusHide(NodeRow n, int hours)
    {
        string result;
        lock (_lock)
        {
            var now = DateTime.Now;
            _state.StatusHides.RemoveAll(h => h.Node == n.Name && h.Until > now);
            if (hours <= 0) result = $"«{n.Name}» снова показывается в статусе";
            else
            {
                // с начала текущего сбоя, чтобы скрыть его целиком; если сбоя нет — с этой минуты (работы заранее)
                var starts = _log.All().Where(i => i.End == null && (i.Key == n.Name + "|conn" || i.Key == n.Name + "|ru")).Select(i => i.Start).ToList();
                _state.StatusHides.Add(new StatusHide { Node = n.Name, From = starts.Count > 0 ? starts.Min() : now, Until = now.AddHours(hours) });
                result = $"«{n.Name}» скрыта из статуса на {hours} ч (до {now.AddHours(hours):HH:mm})";
            }
            _state.StatusHides.RemoveAll(h => h.Until < now.AddDays(-8));
        }
        Save();
        return result;
    }

    void ShowHideMenu(string chat)
    {
        if (!_cfg.StatusEnabled) { _tg.Send(chat, BuildServiceStatus(true), AdminKeyboard(chat)); return; }
        List<NodeRow> nodes;
        Dictionary<string, DateTime> hidden;
        var now = DateTime.Now;
        lock (_lock)
        {
            nodes = _snap?.Nodes.OrderBy(n => n.Name).ToList() ?? new();
            hidden = _state.StatusHides.Where(h => h.Until > now).GroupBy(h => h.Node).ToDictionary(g => g.Key, g => g.Max(h => h.Until));
        }
        if (nodes.Count == 0) { _tg.Send(chat, "Данные о нодах ещё не получены.", AdminKeyboard(chat)); return; }
        var rows = nodes.Select(n => new[] { (hidden.TryGetValue(n.Name, out var until) ? $"👁 {n.Name} — скрыта до {until:HH:mm}" : $"🙈 {n.Name}", "hid:" + NodeId(n)) }).ToList();
        _tg.Send(chat, "🙈 Скрыть сбой ноды из статуса для пользователей — при ложной тревоге или на время работ. Выберите ноду:", Telegram.InlineRows(rows));
    }

    async System.Threading.Tasks.Task HideCallbackAsync(string chat, string id, string data)
    {
        var parts = data.Split(':');   // hid:<id> или hid:<id>:<часов>
        var node = parts.Length > 1 ? FindNode(parts[1]) : null;
        if (!IsAdmin(chat) || node == null) { await _tg.AnswerCallbackAsync(id, node == null ? "Нода не найдена" : ""); return; }
        if (parts.Length < 3)
        {
            await _tg.AnswerCallbackAsync(id);
            _tg.Send(chat, $"🙈 На сколько скрыть «{node.Name}» из статуса? Пользователи не увидят ни текущий сбой, ни новые на это время.",
                Telegram.Inline(new[] { ("1 ч", $"hid:{parts[1]}:1"), ("6 ч", $"hid:{parts[1]}:6"), ("24 ч", $"hid:{parts[1]}:24"), ("👁 Показывать", $"hid:{parts[1]}:0") }));
            return;
        }
        await _tg.AnswerCallbackAsync(id, SetStatusHide(node, int.TryParse(parts[2], out var h) ? h : 0));
    }
}
