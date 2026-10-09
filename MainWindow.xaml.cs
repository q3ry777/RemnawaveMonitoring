using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Collections.Generic;

namespace RemnawaveMonitor;

public partial class MainWindow : Window
{
    readonly Action _openSettings, _refresh;
    readonly Settings _s;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    Snapshot? _snap;
    string _sortKey = "online";
    bool _sortDesc = true;

    public MainWindow(Settings s, Action openSettings, Action refresh)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WinChrome.Apply(this);
        _s = s; _openSettings = openSettings; _refresh = refresh;
        Width = s.Width; Height = s.Height;
        _debounce.Tick += (_, _) => { _debounce.Stop(); Apply(); };
        UpdateHeaders();
        VersionBadge.Text = "V" + Updater.CurrentText;
    }

    public void Update(Snapshot snap, string? note = null)
    {
        _snap = snap;
        Apply(keepScroll: true);
        StatusText.Text = note ?? $"обновлено {snap.At:HH:mm:ss}";
    }

    public void SetStatus(string text) => StatusText.Text = text;

    public Action? InstallUpdateRequested;
    public Action? OpenJournalRequested;
    public Action? MutesChanged;

    void MuteChanged(string status)
    {
        _s.Save();
        SetStatus(status);
        MutesChanged?.Invoke();
    }

    // Плашка «VPN из РФ N/M»: text == null — скрыть
    public void SetVpn(string? text, string level, string tip)
    {
        VpnChip.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        if (text == null) return;
        VpnChipText.Text = text;
        VpnChip.ToolTip = tip;
        VpnDot.SetResourceReference(Shape.FillProperty, level switch { "ok" => "Green", "warn" => "Amber", "bad" => "Red", _ => "Dim" });
    }

    // Индикатор «не беспокоить»: null — скрыть
    public void SetDnd(string? text)
    {
        DndChip.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        if (text != null) DndText.Text = text;
    }

    public void SetJournalBadge(int unread, int active)
    {
        JournalBtn.Content = unread > 0 ? $"журнал ({unread})" : active > 0 ? $"журнал · активно {active}" : "журнал";
    }
    void Journal_Click(object sender, RoutedEventArgs e) => OpenJournalRequested?.Invoke();

    public void SetAppUpdate(string? text)
    {
        if (text == null) { AppUpdateChip.Visibility = Visibility.Collapsed; return; }
        AppUpdateText.Text = text;
        AppUpdateChip.Visibility = Visibility.Visible;
    }
    void AppUpdateChip_Click(object sender, MouseButtonEventArgs e) => InstallUpdateRequested?.Invoke();

    string _updateUrl = "";
    public void SetUpdates(string? text, string url)
    {
        if (text == null) { UpdateChip.Visibility = Visibility.Collapsed; return; }
        UpdateText.Text = text;
        _updateUrl = url;
        UpdateChip.Visibility = Visibility.Visible;
    }
    void UpdateChip_Click(object sender, MouseButtonEventArgs e) { if (_updateUrl != "") OpenUrl(_updateUrl); }

    public void SetBusy(bool busy)
    {
        RefreshBtn.IsEnabled = !busy;
        RefreshBtn.Content = busy ? "обновляю…" : "обновить";
        if (busy) StatusText.Text = "обновление…";
    }

    public void SetOffline(string? reason)
    {
        if (reason == null) { Banner.Visibility = Visibility.Collapsed; return; }
        BannerText.Text = _snap != null
            ? $"нет связи с панелью — показаны данные от {_snap.At:dd.MM HH:mm}  ·  {reason}"
            : $"нет связи с панелью  ·  {reason}";
        Banner.Visibility = Visibility.Visible;
    }


    static IComparable SortKey(UserRow u, string k) => k switch
    {
        "name" => u.Username.ToLowerInvariant(),
        "status" => u.Status,
        "traffic" => u.TrafficBytes,
        "node" => u.LastNode,
        _ => u.LastSeen ?? DateTimeOffset.MinValue,
    };

    void Apply(bool keepScroll = false)
    {
        if (_snap == null) return;

        IEnumerable<UserRow> q = _snap.Users;
        var text = Search.Text.Trim();
        if (text != "") q = q.Where(u => u.Username.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (FilterOnline.IsChecked == true) q = q.Where(u => u.IsOnline);
        if (FilterProblem.IsChecked == true)
            q = q.Where(u => u.Status != "ACTIVE" || u.TrafficLevel != "ok" || u.ExpireLevel != "");
        var ordered = _sortDesc ? q.OrderByDescending(u => SortKey(u, _sortKey)) : q.OrderBy(u => SortKey(u, _sortKey));
        var list = ordered.ThenBy(u => u.Username, StringComparer.OrdinalIgnoreCase).ToList();

        var sv = keepScroll ? FindScroll(UsersGrid) : null;
        double offset = sv?.VerticalOffset ?? 0;
        UsersGrid.ItemsSource = list;
        if (offset > 0)
        {
            UsersGrid.UpdateLayout();
            FindScroll(UsersGrid)?.ScrollToVerticalOffset(offset);
        }
        NodesGrid.ItemsSource = _snap.Nodes;

        OnlineText.Text = $"{_snap.Users.Count(u => u.IsOnline)} онлайн";
        int ok = _snap.Nodes.Count(n => n.Connected && n.XrayOk);
        NodesText.Text = $"ноды {ok}/{_snap.Nodes.Count}";
        NodesDot.SetResourceReference(Shape.FillProperty, ok == _snap.Nodes.Count ? "Green" : "Red");
    }

    static ScrollViewer? FindScroll(DependencyObject o)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var c = VisualTreeHelper.GetChild(o, i);
            if (c is ScrollViewer sv) return sv;
            var r = FindScroll(c);
            if (r != null) return r;
        }
        return null;
    }

    void UpdateHeaders()
    {
        var map = new (TextBlock tb, string key, string label)[]
        {
            (HdrName, "name", "ПОЛЬЗОВАТЕЛЬ"), (HdrStatus, "status", "СТАТУС"), (HdrTraffic, "traffic", "ТРАФИК"),
            (HdrNode, "node", "ПОСЛЕДНЯЯ НОДА"), (HdrOnline, "online", "ОНЛАЙН"),
        };
        foreach (var (tb, key, label) in map)
            tb.Text = label + (_sortKey == key ? (_sortDesc ? " ▼" : " ▲") : "");
    }

    void Sort_Click(object sender, MouseButtonEventArgs e)
    {
        var key = (string)((FrameworkElement)sender).Tag;
        if (_sortKey == key) _sortDesc = !_sortDesc;
        else { _sortKey = key; _sortDesc = key is "traffic" or "online"; }
        UpdateHeaders();
        Apply();
    }

    void Filter_Changed(object sender, RoutedEventArgs e) => Apply();

    // ---- контекстное меню строк (создаётся и открывается из кода) ----
    static object? FindRow(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement f && (f.DataContext is UserRow || f.DataContext is NodeRow || f.DataContext is ServiceRow)) return f.DataContext;
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    void CopyText(string text, string what)
    {
        try { Clipboard.SetText(text); StatusText.Text = $"скопировано: {what}"; }
        catch { StatusText.Text = "не удалось скопировать (буфер занят)"; }
    }

    void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("не удалось открыть: " + ex.Message); }
    }

    void OpenPanel() => OpenUrl(_s.BaseUrl);

    public void UpdateServices(System.Collections.Generic.List<ServiceRow> rows) => ServicesGrid.ItemsSource = rows;

    void List_RightClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var row = FindRow(e.OriginalSource as DependencyObject);
            if (row == null) return;

            var menu = new ContextMenu();
            if (TryFindResource(typeof(ContextMenu)) is Style cs) menu.Style = cs;
            void Add(string header, Action act)
            {
                var mi = new MenuItem { Header = header };
                if (TryFindResource(typeof(MenuItem)) is Style ms) mi.Style = ms;
                mi.Click += (_, _) => act();
                menu.Items.Add(mi);
            }

            switch (row)
            {
                case UserRow u:
                    Add("копировать имя", () => CopyText(u.Username, u.Username));
                    Add("копировать ссылку подписки", () =>
                    {
                        if (u.SubscriptionUrl == "") SetStatus("у пользователя нет ссылки подписки");
                        else CopyText(u.SubscriptionUrl, "ссылка подписки");
                    });
                    break;
                case NodeRow n:
                    Add("копировать имя", () => CopyText(n.Name, n.Name));
                    Add("копировать адрес ноды", () =>
                    {
                        if (n.Address == "") SetStatus("у ноды нет адреса");
                        else CopyText(n.Address, "адрес ноды");
                    });
                    // уведомления по ноде: щелчок включает или выключает
                    var scope = "node:" + n.Name;
                    var allOn = Settings.NodeMuteCats.All(c => !_s.IsMuted(scope, c.cat));
                    Add(allOn ? "🔕 отключить все уведомления по ноде" : "🔔 включить все уведомления по ноде", () =>
                    {
                        foreach (var c in Settings.NodeMuteCats)
                            if (_s.IsMuted(scope, c.cat) == allOn) _s.ToggleMute(scope, c.cat);
                        MuteChanged(allOn ? $"уведомления по {n.Name} отключены" : $"уведомления по {n.Name} включены");
                    });
                    foreach (var c in Settings.NodeMuteCats)
                    {
                        var muted = _s.IsMuted(scope, c.cat);
                        var cat = c.cat;
                        var label = c.label;
                        Add((muted ? "     🔕 " : "     🔔 ") + label + (muted ? " — выкл" : ""), () =>
                        {
                            _s.ToggleMute(scope, cat);
                            MuteChanged($"{n.Name}: {label} — уведомления {(muted ? "включены" : "отключены")}");
                        });
                    }
                    break;
                case ServiceRow sv:
                    Add("копировать адрес", () => CopyText(sv.Url, sv.Url));
                    Add("открыть в браузере", () => OpenUrl(sv.Url));
                    var sm = _s.IsMuted("site:" + sv.Url, "site");
                    Add(sm ? "🔔 включить уведомления по сайту" : "🔕 отключить уведомления по сайту", () =>
                    {
                        _s.ToggleMute("site:" + sv.Url, "site");
                        MuteChanged($"{sv.Name}: уведомления {(sm ? "включены" : "отключены")}");
                    });
                    break;
            }
            if (row is not ServiceRow) Add("открыть панель в браузере", OpenPanel);

            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
        }
        catch (Exception ex) { SetStatus("меню: " + ex.Message); }
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (UsersPanel == null || NodesPanel == null || ServicesPanel == null || SearchBox == null || UsersFilters == null) return;
        bool users = UsersTab.IsChecked == true;
        bool nodes = NodesTab.IsChecked == true;
        bool services = ServicesTab.IsChecked == true;
        UsersPanel.Visibility = users ? Visibility.Visible : Visibility.Collapsed;
        NodesPanel.Visibility = nodes ? Visibility.Visible : Visibility.Collapsed;
        ServicesPanel.Visibility = services ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = users ? Visibility.Visible : Visibility.Hidden;
        UsersFilters.Visibility = users ? Visibility.Visible : Visibility.Collapsed;
    }

    void Search_Changed(object sender, TextChangedEventArgs e) { _debounce.Stop(); _debounce.Start(); }
    void Refresh_Click(object sender, RoutedEventArgs e) => _refresh();
    void Settings_Click(object sender, RoutedEventArgs e) => _openSettings();

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        _s.Theme = _s.Theme == "light" ? "dark" : "light";
        _s.Save();
        App.ApplyTheme(_s.Theme);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _s.Width = ActualWidth; _s.Height = ActualHeight; _s.Save();
        e.Cancel = true;
        Hide();
    }
}
