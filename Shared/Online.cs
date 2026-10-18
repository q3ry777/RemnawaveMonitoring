using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

// Онлайн по активным подключениям. Поле onlineAt панель обновляет с задержкой (раз в минуту и реже, и только при трафике),
// поэтому подключённый, но тихий клиент по нему выглядит офлайн. IP из активных подключений нод — точнее:
// если у пользователя есть свежий IP, он онлайн, даже если onlineAt старше окна.
public record IpSeen(string Ip, DateTimeOffset Seen, string Node);

public static class Online
{
    // IP считается свежим, если виден не раньше, чем столько секунд назад (не меньше окна «онлайн»)
    const int MinIpWindowSec = 300;

    public static async Task<(Dictionary<string, List<IpSeen>> map, string error)> FetchIpsAsync(Api api, IPanelConfig cfg, IEnumerable<NodeRow> nodes)
    {
        var list = nodes.Where(n => n.Connected && n.UsersOnline > 0 && n.NodeUuid != "").ToList();
        var res = await Task.WhenAll(list.Select(async n =>
        {
            try { return (n.Name, items: await api.FetchNodeIpsAsync(cfg, n.NodeUuid), err: ""); }
            catch (Exception ex) { return (n.Name, items: new List<(string user, string ip, DateTimeOffset seen)>(), err: ex.Message); }
        }));
        var map = new Dictionary<string, List<IpSeen>>();
        foreach (var (node, items, _) in res)
            foreach (var (user, ip, seen) in items)
            {
                if (!map.TryGetValue(user, out var l)) map[user] = l = new();
                l.Add(new IpSeen(ip, seen, node));
            }
        var err = res.Select(r => r.err).FirstOrDefault(e => e != "") ?? "";
        if (err.Contains("403")) err = "у API-токена нет прав на подключения (connections)";
        return (map, err);
    }

    // IP в строку пользователя и поправка «онлайн» по свежим IP
    public static List<UserRow> Apply(List<UserRow> users, Dictionary<string, List<IpSeen>> ips, int onlineWindowSec)
    {
        var now = Api.Now;
        var window = TimeSpan.FromSeconds(Math.Max(onlineWindowSec, MinIpWindowSec));
        return users.Select(u =>
        {
            var key = u.UserId != "" ? u.UserId : u.UserUuid;
            if (key == "" || !ips.TryGetValue(key, out var list) || list.Count == 0) return u;
            var distinct = list.OrderByDescending(x => x.Seen).GroupBy(x => x.Ip).Select(g => g.First()).ToList();
            var text = distinct[0].Ip + (distinct.Count > 1 ? $" +{distinct.Count - 1}" : "");
            var tip = string.Join("\n", distinct.Select(x => $"{x.Ip} — {x.Node}, {x.Seen.ToLocalTime():dd.MM HH:mm:ss}"));
            // нет времени у IP — считаем свежим: панель вернула его среди активных
            bool fresh = distinct.Any(x => x.Seen == default || now - x.Seen < window);
            var row = u with { Ip = text, IpTip = tip };
            if (fresh && !u.IsOnline)
            {
                var last = distinct.Where(x => x.Seen != default).Select(x => (DateTimeOffset?)x.Seen).FirstOrDefault();
                row = row with
                {
                    IsOnline = true, Online = "сейчас",
                    LastSeen = last is DateTimeOffset l && (u.LastSeen == null || l > u.LastSeen) ? l : u.LastSeen,
                };
            }
            return row;
        }).ToList();
    }
}
