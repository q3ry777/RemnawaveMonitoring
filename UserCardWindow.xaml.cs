using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RemnawaveMonitor;

// Карточка пользователя: подписка и трафик, трафик по дням и нодам, IP активных подключений, устройства (HWID)
public partial class UserCardWindow : Window
{
    const int Days = 14;
    readonly Api _api;
    readonly IPanelConfig _cfg;
    readonly Func<UserRow, UserRow?> _fresh;   // свежая строка пользователя из последнего снимка
    UserRow _u;
    bool _loading;

    public UserCardWindow(Api api, IPanelConfig cfg, UserRow user, Func<UserRow, UserRow?> fresh)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WinChrome.Apply(this);
        _api = api; _cfg = cfg; _u = user; _fresh = fresh;
        ShowUser();
        Loaded += async (_, _) => await LoadExtraAsync();
    }

    public string UserKey => _u.UserUuid != "" ? _u.UserUuid : _u.Username;

    // ---- основное: из строки пользователя (без запросов) ----
    void ShowUser()
    {
        var u = _u;
        Title = Demo.Name(u.Username) + " — пользователь";
        NameText.Text = Demo.Name(u.Username);
        StatusText.Text = u.Status;
        bool active = u.Status == "ACTIVE";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, active ? "Green" : "Red");
        StatusPill.SetResourceReference(Border.BorderBrushProperty, active ? "Line" : "Red");
        OnlineDot.SetResourceReference(Shape.FillProperty, u.IsOnline ? "Green" : "Dim");
        SubText.Text = (u.IsOnline ? "онлайн сейчас" : u.Online == "—" ? "подключений не было" : $"был онлайн {u.Online}")
                       + (u.LastNode != "—" ? $" · последняя нода {u.LastNode}" : "");
        CopySubBtn.IsEnabled = u.SubscriptionUrl != "";

        JsonElement r = default;
        bool hasRaw = false;
        if (u.Raw != "")
            try { r = JsonDocument.Parse(u.Raw).RootElement.Clone(); hasRaw = true; } catch { }

        string S(params string[] path) => hasRaw ? J.S(r, path) : "";
        double N(params string[] path) => hasRaw ? J.N(r, path) : 0;
        string First(params string[][] paths) => paths.Select(p => S(p)).FirstOrDefault(x => x != "") ?? "";
        double FirstN(params string[][] paths) => paths.Select(p => N(p)).FirstOrDefault(x => x != 0);

        var rows = new List<(string label, string value, string brush)>();

        // подписка
        if (TryDate(S("expireAt"), out var exp))
        {
            var left = exp - Api.Now;
            string note = left < TimeSpan.Zero ? "истекла" : left.TotalDays >= 1 ? $"ещё {(int)left.TotalDays} д" : $"ещё {Math.Max(1, (int)left.TotalHours)} ч";
            rows.Add(("подписка до", $"{exp.ToLocalTime():dd.MM.yyyy HH:mm} · {note}",
                left < TimeSpan.Zero || left.TotalDays <= 3 ? "Red" : left.TotalDays <= 7 ? "Amber" : ""));
        }

        // трафик
        double used = FirstN(new[] { "userTraffic", "usedTrafficBytes" }, new[] { "usedTrafficBytes" });
        if (used == 0) used = u.TrafficBytes;
        double limit = N("trafficLimitBytes");
        rows.Add(("трафик", limit > 0 ? $"{J.Bytes(used)} из {J.Bytes(limit)} ({used / limit:P0})" : $"{J.Bytes(used)} · без лимита",
            u.TrafficLevel == "crit" ? "Red" : u.TrafficLevel == "warn" ? "Amber" : ""));
        TrafficBar.Visibility = limit > 0 ? Visibility.Visible : Visibility.Collapsed;
        TrafficBar.Value = limit > 0 ? Math.Min(1, used / limit) : 0;
        var strategy = S("trafficLimitStrategy");
        if (strategy != "") rows.Add(("сброс трафика", Strategy(strategy), ""));
        if (TryDate(S("lastTrafficResetAt"), out var reset)) rows.Add(("последний сброс", $"{reset.ToLocalTime():dd.MM.yyyy}", ""));
        double lifetime = FirstN(new[] { "userTraffic", "lifetimeUsedTrafficBytes" }, new[] { "lifetimeUsedTrafficBytes" });
        if (lifetime > 0) rows.Add(("за всё время", J.Bytes(lifetime), ""));

        // прочее
        var hwidLimit = S("hwidDeviceLimit");
        if (hwidLimit != "") rows.Add(("лимит устройств", hwidLimit, ""));
        if (TryDate(First(new[] { "userTraffic", "firstConnectedAt" }, new[] { "firstConnectedAt" }), out var first))
            rows.Add(("первое подключение", $"{first.ToLocalTime():dd.MM.yyyy HH:mm}", ""));
        if (TryDate(S("createdAt"), out var created)) rows.Add(("создан", $"{created.ToLocalTime():dd.MM.yyyy HH:mm}", ""));
        if (TryDate(S("subLastOpenedAt"), out var opened))
            rows.Add(("подписку открывали", $"{opened.ToLocalTime():dd.MM.yyyy HH:mm}" + (S("subLastUserAgent") is { Length: > 0 } ua ? $" · {ua}" : ""), ""));
        foreach (var (label, key) in new[] { ("описание", "description"), ("тег", "tag"), ("telegram id", "telegramId"), ("email", "email") })
            if (S(key) is { Length: > 0 } v) rows.Add((label, Demo.On ? "скрыто (режим демонстрации)" : v, ""));

        InfoGrid.Children.Clear();
        InfoGrid.RowDefinitions.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            InfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = rows[i].label, FontSize = 12, Margin = new Thickness(0, 3, 0, 3) };
            l.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var v = new TextBlock { Text = rows[i].value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
            if (rows[i].brush != "") v.SetResourceReference(TextBlock.ForegroundProperty, rows[i].brush);
            Grid.SetRow(l, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            InfoGrid.Children.Add(l); InfoGrid.Children.Add(v);
        }

        // IP
        IpList.Children.Clear();
        if (u.IpTip == "") IpList.Children.Add(Dim("нет активных подключений (обновляется раз в минуту)"));
        else
            foreach (var line in u.IpTip.Split('\n'))
            {
                var t = new TextBlock { Text = Demo.Ip(line), Margin = new Thickness(0, 2, 0, 2), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "щелчок — скопировать IP" };
                var ip = Demo.Ip(line.Split(' ')[0]);
                t.MouseLeftButtonUp += (_, _) => Copy(ip, "IP");
                IpList.Children.Add(t);
            }
    }

    static string Strategy(string s) => s switch
    {
        "NO_RESET" => "не сбрасывается",
        "DAY" => "каждый день",
        "WEEK" => "каждую неделю",
        "MONTH" => "каждый месяц (1-го числа)",
        "MONTH_ROLLING" => "каждый месяц (от даты создания)",
        "CALENDAR_MONTH" => "каждый календарный месяц",
        _ => s,
    };

    static bool TryDate(string s, out DateTimeOffset d) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out d);

    static TextBlock Dim(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        return t;
    }

    // ---- дополнительно: устройства и трафик по дням (запросы к панели) ----
    async Task LoadExtraAsync()
    {
        if (_loading) return;
        _loading = true;
        StatusLine.Text = "загрузка…";
        try
        {
            var devTask = _api.GetUserDevicesAsync(_cfg, _u.UserId, _u.UserUuid);
            var useTask = _api.GetUserUsageAsync(_cfg, _u.UserId, _u.UserUuid, Days);
            await Task.WhenAll(devTask, useTask);
            ShowDevices(devTask.Result);
            ShowUsage(useTask.Result);
            StatusLine.Text = $"обновлено {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { StatusLine.Text = "ошибка: " + ex.Message; }
        finally { _loading = false; }
    }

    List<UserDevice>? _devices;
    bool _devicesLoaded;

    void ShowDevices(List<UserDevice>? list)
    {
        _devices = list; _devicesLoaded = true;
        DevList.Children.Clear();
        var limit = "";
        try { if (_u.Raw != "") limit = J.S(JsonDocument.Parse(_u.Raw).RootElement, "hwidDeviceLimit"); } catch { }
        if (list == null)
        {
            DevHeader.Text = "УСТРОЙСТВА (HWID)";
            DevList.Children.Add(Dim("нет данных: в панели не включён учёт устройств (HWID) или у API-токена нет прав на чтение устройств"));
            return;
        }
        DevHeader.Text = $"УСТРОЙСТВА (HWID) · {list.Count}" + (limit != "" ? $" из {limit}" : "");
        if (list.Count == 0) { DevList.Children.Add(Dim("устройств нет")); return; }
        foreach (var d in list)
        {
            var title = string.Join(" · ", new[] { d.Model, d.Platform, d.Os }.Where(x => x != ""));
            var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            sp.Children.Add(new TextBlock { Text = title == "" ? "устройство" : title, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
            var meta = new List<string>();
            if (d.Updated is DateTimeOffset up) meta.Add($"последний раз {up.ToLocalTime():dd.MM.yyyy HH:mm}");
            if (d.Created is DateTimeOffset cr) meta.Add($"добавлено {cr.ToLocalTime():dd.MM.yyyy}");
            if (d.Ip != "") meta.Add(Demo.Ip(d.Ip));
            if (meta.Count > 0) sp.Children.Add(Dim(string.Join(" · ", meta)));
            if (d.Agent != "") sp.Children.Add(Dim(d.Agent));
            DevList.Children.Add(sp);
        }
    }

    void ShowUsage((List<DayUsage> days, List<NodeUsage> nodes)? data)
    {
        Chart.Children.Clear();
        Chart.ColumnDefinitions.Clear();
        NodeUsage.Children.Clear();
        if (data is not { } d)
        {
            UsageNote.Text = "нет данных: эта версия панели не отдаёт статистику по дням или у API-токена нет прав";
            UsageNote.Visibility = Visibility.Visible;
            Chart.Visibility = Visibility.Collapsed;
            UsageTotal.Text = "";
            return;
        }
        var days = d.days.TakeLast(Days).ToList();
        double total = days.Sum(x => x.Bytes), max = days.Count > 0 ? days.Max(x => x.Bytes) : 0;
        UsageTotal.Text = $"ВСЕГО {J.Bytes(total)} · В СРЕДНЕМ {J.Bytes(days.Count > 0 ? total / days.Count : 0)}/ДЕНЬ";
        UsageNote.Visibility = total > 0 ? Visibility.Collapsed : Visibility.Visible;
        UsageNote.Text = "за эти дни трафика не было";
        Chart.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;

        // столбики: высота по максимуму, подпись дня снизу, значение — при наведении и над самым большим
        const double barArea = 118;
        for (int i = 0; i < days.Count; i++)
        {
            Chart.ColumnDefinitions.Add(new ColumnDefinition());
            var x = days[i];
            var col = new Grid { Background = Brushes.Transparent, ToolTip = $"{x.Day:dd.MM, ddd}: {J.Bytes(x.Bytes)}" };
            col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var bar = new Border
            {
                Height = max > 0 ? Math.Max(x.Bytes > 0 ? 3 : 1, x.Bytes / max * barArea) : 1,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 3, 0), CornerRadius = new CornerRadius(3, 3, 0, 0),
            };
            bool today = x.Day == Api.Now.UtcDateTime.Date;
            bar.SetResourceReference(Border.BackgroundProperty, x.Bytes > 0 ? (today ? "Green" : "Amber") : "Line");
            col.Children.Add(bar);
            if (x.Bytes > 0 && x.Bytes == max)
            {
                var top = new TextBlock { Text = J.Compact(x.Bytes), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                                          Margin = new Thickness(0, 0, 0, bar.Height + 2) };
                top.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                col.Children.Add(top);
            }
            var lbl = new TextBlock { Text = x.Day.ToString("dd"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, today ? "Fg" : "Dim");
            Grid.SetRow(lbl, 1);
            col.Children.Add(lbl);
            Grid.SetColumn(col, i);
            Chart.Children.Add(col);
        }

        // по нодам
        var conv = new FlagConverter();
        foreach (var n in d.nodes)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 18, 4) };
            if (conv.Convert(n.Country, typeof(ImageSource), null!, CultureInfo.InvariantCulture) is ImageSource img)
                sp.Children.Add(new Image { Source = img, Height = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = n.Name, VerticalAlignment = VerticalAlignment.Center });
            var b = new TextBlock { Text = " " + J.Bytes(n.Bytes), VerticalAlignment = VerticalAlignment.Center };
            b.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            sp.Children.Add(b);
            NodeUsage.Children.Add(sp);
        }
    }

    // ---- кнопки ----
    async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_fresh(_u) is UserRow f) _u = f;
        ShowUser();
        await LoadExtraAsync();
    }

    // новые данные из главного окна (IP, онлайн) — без повторных запросов
    public void Update(UserRow u)
    {
        _u = u;
        ShowUser();
        if (_devicesLoaded) ShowDevices(_devices);   // режим демонстрации мог переключиться
    }

    void CopySub_Click(object sender, RoutedEventArgs e) => Copy(_u.SubscriptionUrl, "ссылка подписки");

    void Copy(string text, string what)
    {
        if (text == "") return;
        try { Clipboard.SetText(text); StatusLine.Text = "скопировано: " + what; }
        catch { StatusLine.Text = "не удалось скопировать (буфер занят)"; }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
