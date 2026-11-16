using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RemnawaveMonitor;

// Тихие часы и время «не беспокоить»: разбор и вывод в одном месте (.env бота, /settings и мини-приложение)
public static class QuietFmt
{
    // «23-8, 13-14», «23» или пусто → набор часов. false и bad — первая нераспознанная часть.
    // lenient: нераспознанное пропускается, разбор идёт дальше (так читается .env)
    public static bool TryParseRanges(string text, out HashSet<int> hours, out string bad, bool lenient = false)
    {
        hours = new HashSet<int>();
        bad = "";
        foreach (var part in text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var r = part.Split('-');
            if (r.Length == 2 && int.TryParse(r[0], out var a) && int.TryParse(r[1], out var b) && a is >= 0 and < 24 && b is >= 0 and < 24)
                for (int h = a; h != b; h = (h + 1) % 24) hours.Add(h);
            else if (int.TryParse(part, out var one) && one is >= 0 and < 24) hours.Add(one);
            else
            {
                if (bad == "") bad = part;
                if (!lenient) return false;
            }
        }
        return bad == "" || lenient;
    }

    // Непрерывные участки: (с какого часа, до какого часа не включая)
    static List<(int from, int to)> Runs(HashSet<int> on)
    {
        var res = new List<(int, int)>();
        foreach (var s in on.Where(h => !on.Contains((h + 23) % 24)).OrderBy(h => h))
        {
            int e = s;
            while (on.Contains((e + 1) % 24)) e = (e + 1) % 24;
            res.Add((s, (e + 1) % 24));
        }
        return res;
    }

    // Набор часов → «23-8, 13-14» — в том виде, в каком вводится; 24 часа — «0-23, 23»
    public static string Edit(IEnumerable<int> hours)
    {
        var on = hours.Where(h => h is >= 0 and < 24).ToHashSet();
        if (on.Count == 0) return "";
        if (on.Count == 24) return "0-23, 23";
        return string.Join(", ", Runs(on).Select(r => $"{r.from}-{r.to}"));
    }

    // Набор часов → «23:00–08:00, 13:00–14:00»; пусто — «», 24 часа — «круглосуточно»
    public static string Display(IEnumerable<int> hours)
    {
        var on = hours.Where(h => h is >= 0 and < 24).ToHashSet();
        if (on.Count == 0) return "";
        if (on.Count == 24) return "круглосуточно";
        return string.Join(", ", Runs(on).Select(r => $"{r.from:00}:00–{r.to:00}:00"));
    }

    // «8:00», «08:00» или «8.00» → время суток
    public static bool TryParseTime(string text, out TimeSpan t) =>
        TimeSpan.TryParseExact(text.Trim().Replace('.', ':'), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out t)
        && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);

    public static string TimeText(TimeSpan t) => t.ToString(@"hh\:mm");
}
