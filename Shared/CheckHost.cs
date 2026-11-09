using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

// Проверка TCP-доступности адреса с российских точек check-host.net (по запросу админа).
// Общий код: бот на сервере (/check) и программа на ПК (кнопка «проверить из РФ» на вкладке «ноды»).
public class CheckHost
{
    static readonly string Base = (Environment.GetEnvironmentVariable("CHECKHOST_URL") is { Length: > 0 } u ? u : "https://check-host.net").TrimEnd('/');
    static readonly string[] Fallback = { "ru1.node.check-host.net", "ru2.node.check-host.net", "ru3.node.check-host.net", "ru4.node.check-host.net" };

    // Бот пишет предупреждения в свой журнал, программа на ПК их не пишет
    public static Action<string>? Logger;
    static void Warn(string text) => Logger?.Invoke(text);

    readonly HttpClient _http;
    readonly SemaphoreSlim _one = new(1, 1);   // одна проверка за раз — у check-host лимиты
    List<string>? _ru;
    DateTime _ruAt;

    public CheckHost()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("remnawave-monitor/1.0");
    }

    public record Point(string Node, string City, bool Ok, double Ms, string Error);
    public record Result(string Target, List<Point> Points, string Link);

    // Российские точки check-host (список кэшируется на 6 часов)
    async Task<List<string>> RuNodesAsync()
    {
        if (_ru != null && DateTime.Now - _ruAt < TimeSpan.FromHours(6)) return _ru;
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(Base + "/nodes/hosts"));
            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Object)
                foreach (var n in nodes.EnumerateObject())
                    if (n.Value.ValueKind == JsonValueKind.Object && n.Value.TryGetProperty("location", out var loc)
                        && loc.ValueKind == JsonValueKind.Array && loc.GetArrayLength() > 0
                        && string.Equals(loc[0].GetString(), "ru", StringComparison.OrdinalIgnoreCase))
                        list.Add(n.Name);
            if (list.Count > 0) { _ru = list; _ruAt = DateTime.Now; return list; }
        }
        catch (Exception ex) { Warn("check-host: список точек: " + ex.Message); }
        return _ru ?? Fallback.ToList();
    }

    public bool Busy => _one.CurrentCount == 0;

    public async Task<Result> CheckTcpAsync(string target)
    {
        await _one.WaitAsync();
        try
        {
            var nodes = await RuNodesAsync();
            var url = Base + "/check-tcp?host=" + Uri.EscapeDataString(target) + string.Concat(nodes.Select(n => "&node=" + Uri.EscapeDataString(n)));
            string id, link;
            var cities = new Dictionary<string, string>();
            using (var doc = JsonDocument.Parse(await _http.GetStringAsync(url)))
            {
                var r = doc.RootElement;
                id = J.S(r, "request_id");
                if (id == "") throw new Exception("check-host не принял запрос" + (J.S(r, "error") is var e && e != "" ? ": " + e : ""));
                link = J.S(r, "permanent_link");
                if (link == "") link = Base + "/check-report/" + id;
                if (r.TryGetProperty("nodes", out var ns) && ns.ValueKind == JsonValueKind.Object)
                    foreach (var n in ns.EnumerateObject())
                        cities[n.Name] = n.Value.ValueKind == JsonValueKind.Array && n.Value.GetArrayLength() > 2 ? n.Value[2].GetString() ?? n.Name : n.Name;
            }
            if (cities.Count == 0) foreach (var n in nodes) cities[n] = n;

            // ждём результаты: точки отвечают за несколько секунд, null — ещё проверяет
            var points = new Dictionary<string, Point>();
            var until = DateTime.Now.AddSeconds(25);
            while (DateTime.Now < until)
            {
                await Task.Delay(2500);
                try
                {
                    using var doc = JsonDocument.Parse(await _http.GetStringAsync(Base + "/check-result/" + Uri.EscapeDataString(id)));
                    foreach (var n in doc.RootElement.EnumerateObject())
                    {
                        if (!cities.ContainsKey(n.Name) || points.ContainsKey(n.Name)) continue;
                        if (n.Value.ValueKind != JsonValueKind.Array || n.Value.GetArrayLength() == 0) continue;
                        var item = n.Value[0];
                        if (item.ValueKind == JsonValueKind.Null) continue;
                        var city = cities[n.Name];
                        var err = item.ValueKind == JsonValueKind.Object ? J.S(item, "error") : "";
                        if (item.ValueKind == JsonValueKind.Object && err == "" && item.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number)
                            points[n.Name] = new Point(n.Name, city, true, t.GetDouble() * 1000, "");
                        else
                            points[n.Name] = new Point(n.Name, city, false, 0, err != "" ? err : "ошибка");
                    }
                }
                catch (Exception ex) { Warn("check-host: результат: " + ex.Message); }
                if (points.Count >= cities.Count) break;
            }
            var list = cities.Keys.Select(k => points.TryGetValue(k, out var p) ? p : new Point(k, cities[k], false, -1, "нет ответа от точки"))
                .Select(p => p with { City = Ru(p.City) }).ToList();
            // одинаковые города — с номером точки: «Москва (ru1)»
            list = list.Select(p => list.Count(x => x.City == p.City) > 1 ? p with { City = $"{p.City} ({p.Node.Split('.')[0]})" } : p)
                .OrderBy(p => p.City).ToList();
            return new Result(target, list, link);
        }
        finally { _one.Release(); }
    }

    static readonly Dictionary<string, string> Cities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Moscow"] = "Москва", ["Saint Petersburg"] = "Санкт-Петербург", ["St Petersburg"] = "Санкт-Петербург", ["Ekaterinburg"] = "Екатеринбург",
        ["Yekaterinburg"] = "Екатеринбург", ["Novosibirsk"] = "Новосибирск", ["Kazan"] = "Казань", ["Nizhny Novgorod"] = "Нижний Новгород",
        ["Krasnodar"] = "Краснодар", ["Rostov-on-Don"] = "Ростов-на-Дону", ["Samara"] = "Самара", ["Chelyabinsk"] = "Челябинск",
        ["Krasnoyarsk"] = "Красноярск", ["Vladivostok"] = "Владивосток", ["Khabarovsk"] = "Хабаровск", ["Omsk"] = "Омск", ["Perm"] = "Пермь",
        ["Ufa"] = "Уфа", ["Voronezh"] = "Воронеж", ["Volgograd"] = "Волгоград", ["Irkutsk"] = "Иркутск", ["Tyumen"] = "Тюмень",
        ["Kaliningrad"] = "Калининград", ["Saratov"] = "Саратов", ["Tomsk"] = "Томск", ["Petropavlovsk-Kamchatsky"] = "Петропавловск-Камчатский",
    };
    static string Ru(string city) => Cities.TryGetValue(city.Trim(), out var r) ? r : city;

    // Вывод по результатам: уровень (ok / warn / bad / none) и текст. panelConnected — видит ли ноду сама панель (null — неизвестно)
    public static (string level, string text) Verdict(Result r, bool? panelConnected)
    {
        int ok = r.Points.Count(p => p.Ok);
        int answered = r.Points.Count(p => p.Ms >= 0 || p.Ok);
        if (answered == 0) return ("none", "Точки check-host не ответили — попробуйте позже.");
        if (ok == answered) return ("ok", "Из России доступен.");
        if (ok > 0) return ("warn", $"Доступен из {ok} из {answered} точек — частичная блокировка или проблемы у части провайдеров.");
        return ("bad", panelConnected == true ? "Из России недоступен, а панель с нодой связана — похоже на блокировку IP."
            : panelConnected == false ? "Недоступен, и панель тоже не видит ноду — скорее проблема с сервером."
            : "Из России недоступен — блокировка, либо сервер/порт не отвечает.");
    }

    public static string ErrorText(string e) =>
        e.Contains("timed out", StringComparison.OrdinalIgnoreCase) ? "таймаут" :
        e.Contains("refused", StringComparison.OrdinalIgnoreCase) ? "соединение отклонено" :
        e.Contains("reset", StringComparison.OrdinalIgnoreCase) ? "соединение сброшено" :
        e.Contains("No route", StringComparison.OrdinalIgnoreCase) || e.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ? "нет маршрута" : e;
}
