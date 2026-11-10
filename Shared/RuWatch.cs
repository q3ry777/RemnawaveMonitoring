using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

// Результат проверки ноды из РФ: уровень ok / warn / bad / none (none — check-host не ответил, судить нельзя)
public record RuNodeResult(NodeRow Node, string Level, int Ok, int Answered, string Target, string Link, string Verdict);
// Смена уровня ноды (после подтверждения повторной проверкой)
public record RuChange(NodeRow Node, string From, string To, RuNodeResult Result);

// Автопроверка доступности нод из РФ через check-host.net — общая часть для бота на сервере и программы на ПК.
// Нода проверяется по первым портам её инбаундов, уровень ноды — худший из портов (bad > warn > ok).
// Смена уровня подтверждается повторной проверкой через минуту: разовый сбой check-host тревогу не поднимает.
public class RuWatch
{
    readonly CheckHost _check;
    public RuWatch(CheckHost check) => _check = check;

    public TimeSpan ConfirmDelay { get; set; } = TimeSpan.FromSeconds(60);

    static int Rank(string level) => level switch { "bad" => 3, "warn" => 2, "ok" => 1, _ => 0 };

    // Изменился ли уровень: none ничего не значит; для ноды без истории «норма» — ok
    static bool Changed(string? prev, string level) => level != "none" && level != (prev ?? "ok");

    public async Task<RuNodeResult> CheckNodeAsync(NodeRow n, Dictionary<string, List<int>> ports, int maxPorts, int fallbackPort)
    {
        var ps = ports.TryGetValue(n.NodeUuid, out var l) && l.Count > 0 ? l.Take(maxPorts).ToList() : new List<int> { fallbackPort };
        var host = n.Address.Contains(':') ? $"[{n.Address}]" : n.Address;
        RuNodeResult? worst = null;
        foreach (var p in ps)
        {
            var target = $"{host}:{p}";
            try
            {
                var r = await _check.CheckTcpAsync(target);
                var (level, verdict) = CheckHost.Verdict(r, n.Connected);
                var res = new RuNodeResult(n, level, r.Points.Count(x => x.Ok), r.Points.Count(x => x.Ms >= 0 || x.Ok), target, r.Link, verdict);
                if (worst == null || Rank(res.Level) > Rank(worst.Level)) worst = res;
            }
            catch (Exception ex) { CheckHost.Logger?.Invoke($"автопроверка {target}: {ex.Message}"); }
        }
        return worst ?? new RuNodeResult(n, "none", 0, 0, "", "", "");
    }

    // Проверяет ноды по очереди (check-host не любит параллельные запросы). state: имя ноды → последний подтверждённый уровень,
    // обновляется на месте. Возвращает результаты и смены уровня, о которых нужно сообщить.
    public async Task<(List<RuNodeResult> results, List<RuChange> changes)> RunAsync(
        IEnumerable<NodeRow> nodes, Dictionary<string, string> state, Dictionary<string, List<int>> ports,
        int maxPorts, int fallbackPort, CancellationToken ct)
    {
        var results = new List<RuNodeResult>();
        var changes = new List<RuChange>();
        foreach (var n in nodes)
        {
            ct.ThrowIfCancellationRequested();
            var r = await CheckNodeAsync(n, ports, maxPorts, fallbackPort);
            state.TryGetValue(n.Name, out var prev);
            if (Changed(prev, r.Level))
            {
                await Task.Delay(ConfirmDelay, ct);   // подтверждение
                r = await CheckNodeAsync(n, ports, maxPorts, fallbackPort);
                if (Changed(prev, r.Level)) changes.Add(new RuChange(n, prev ?? "ok", r.Level, r));
            }
            if (r.Level != "none") state[n.Name] = r.Level;
            results.Add(r);
        }
        return (results, changes);
    }

    // Текст тревоги или восстановления без значка (значок добавляет бот или программа)
    public static string Text(RuChange c) => c.To switch
    {
        "ok" => $"{c.Node.Name}: снова доступен из России",
        "warn" => $"{c.Node.Name}: из России доступен частично — {c.Result.Ok} из {c.Result.Answered} точек ({c.Result.Target})",
        _ => $"{c.Node.Name}: {c.Result.Verdict} ({c.Result.Target})",
    };
}
