using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RemnawaveMonitor;

public class Incident
{
    public string Key { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Text { get; set; } = "";
    public string Severity { get; set; } = "bad";   // bad | warn
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public bool Unread { get; set; }
}

public record IncidentRow(string Time, string Subject, string Text, string Status, string Level, bool Active, bool Unread);

// Журнал событий: падения и восстановления нод (на ПК — %APPDATA%\RemnawaveMonitor, у бота — /data)
public class IncidentLog
{
    string _dir = "";
    string FilePath => Path.Combine(_dir, "incidents.json");
    List<Incident> _items = new();

    public int Unread => _items.Count(x => x.Unread);
    public int Active => _items.Count(x => x.End == null);
    public IEnumerable<string> ActiveKeys => _items.Where(x => x.End == null).Select(x => x.Key).Distinct().ToList();

    // closeActive: программа на ПК была закрыта — что с открытыми проблемами сейчас, неизвестно, актуальные появятся заново.
    // Бот на сервере открытые проблемы сохраняет, чтобы после перезапуска не присылать те же тревоги повторно.
    public static IncidentLog Load(string dir, bool closeActive = true)
    {
        var log = new IncidentLog { _dir = dir };
        try
        {
            log._items = JsonSerializer.Deserialize<List<Incident>>(File.ReadAllText(log.FilePath)) ?? new();
            if (closeActive) foreach (var x in log._items.Where(y => y.End == null)) x.End = DateTime.Now;
        }
        catch { }
        return log;
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_items));
        }
        catch { }
    }

    public void Open(string key, string subject, string text, string severity, bool unread)
    {
        _items.Add(new Incident { Key = key, Subject = subject, Text = text, Severity = severity, Start = DateTime.Now, Unread = unread });
        if (_items.Count > 300) _items.RemoveRange(0, _items.Count - 300);
        Save();
    }

    public TimeSpan? Resolve(string key)
    {
        TimeSpan? downtime = null;
        foreach (var x in _items.Where(y => y.Key == key && y.End == null))
        {
            x.End = DateTime.Now;
            downtime = x.End - x.Start;
        }
        Save();
        return downtime;
    }

    // Суммарный простой объекта (по ключам вида "имя|conn") с момента from и число падений
    public (TimeSpan down, int count) Downtime(string subject, string[] keySuffixes, DateTime from)
    {
        var now = DateTime.Now;
        var sum = TimeSpan.Zero;
        int n = 0;
        foreach (var x in _items.Where(y => y.Subject == subject && keySuffixes.Any(k => y.Key.EndsWith(k))))
        {
            var end = x.End ?? now;
            if (end <= from) continue;
            sum += end - (x.Start < from ? from : x.Start);
            n++;
        }
        return (sum, n);
    }

    public void MarkAllRead()
    {
        foreach (var x in _items) x.Unread = false;
        Save();
    }

    public void ClearResolved()
    {
        _items.RemoveAll(x => x.End != null);
        Save();
    }

    public List<IncidentRow> Rows()
    {
        var now = DateTime.Now;
        return _items.OrderByDescending(x => x.Start).Select(x =>
        {
            bool active = x.End == null;
            string status = active ? $"идёт {Dur(now - x.Start)}" : $"решено · {Dur(x.End!.Value - x.Start)}";
            return new IncidentRow(x.Start.ToString("dd.MM HH:mm"), x.Subject, x.Text, status, active ? x.Severity : "done", active, x.Unread);
        }).ToList();
    }

    public static string Dur(TimeSpan t) =>
        t.TotalMinutes < 1 ? "<1 мин"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} мин"
        : t.TotalDays < 1 ? $"{(int)t.TotalHours} ч {t.Minutes} мин"
        : $"{(int)t.TotalDays} д {t.Hours} ч";
}
