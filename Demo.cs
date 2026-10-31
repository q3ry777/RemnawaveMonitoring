using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RemnawaveMonitor;

// Режим демонстрации: на экране вместо имён пользователей — «Пользователь 001», вместо IP — «скрыто».
// Номера постоянные (по UUID), поэтому один и тот же человек остаётся под одним номером, пока режим включён.
public static class Demo
{
    public static bool On { get; set; }
    static Dictionary<string, string> _alias = new();

    public static void SetUsers(IEnumerable<UserRow> users)
    {
        var map = new Dictionary<string, string>();
        int i = 1;
        foreach (var u in users.OrderBy(u => u.UserUuid != "" ? u.UserUuid : u.Username, StringComparer.Ordinal))
        {
            map[u.Username] = $"Пользователь {i:000}";
            i++;
        }
        _alias = map;
    }

    public static string Name(string username) =>
        !On || username == "" ? username : _alias.TryGetValue(username, out var a) ? a : "Пользователь";

    const string Hidden = "•••.•••.•••.•••";
    // IPv4 и IPv6 (у IPv6 обязательно «::» или буква a-f — чтобы не задеть время вида 18:35:20)
    static readonly Regex IpRx = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b|(?<![\w:])(?=[0-9a-fA-F:]*(?:::|[a-fA-F]))[0-9a-fA-F]{0,4}(?::[0-9a-fA-F]{0,4}){2,7}(?![\w:])");

    // IP в любом тексте → «•••.•••.•••.•••»
    public static string Ip(string text) => !On || text == "" ? text : IpRx.Replace(text, Hidden);

    // Произвольный текст: IP скрываются, известные имена пользователей заменяются номерами
    public static string Text(string text)
    {
        if (!On || text == "") return text;
        text = Ip(text);
        foreach (var kv in _alias.OrderByDescending(k => k.Key.Length))
            if (kv.Key.Length >= 2) text = Regex.Replace(text, $@"(?<![\w]){Regex.Escape(kv.Key)}(?![\w])", kv.Value);
        return text;
    }

    public static UserRow Mask(UserRow u) => !On ? u : u with
    {
        Username = Name(u.Username),
        Ip = Ip(u.Ip),
        IpTip = Ip(u.IpTip),
    };
}
