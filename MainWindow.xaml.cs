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
        RestorePlacement();
        Demo.On = s.DemoMode;
        Loaded += (_, _) => ShowDemo();
        // Ctrl+D — режим демонстрации
        InputBindings.Add(new KeyBinding(new RelayCommand(ToggleDemo), Key.D, ModifierKeys.Control));
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
    public Action<UserRow>? OpenUserRequested;
    public Action? PaymentsOpened;

    // ---- вкладка «оплаты» ----
    public record PaymentRow(string Date, string Username, string Period, string Sum, string Status, string StatusText, string Until);

    public void SetPaymentsEnabled(bool on)
    {
        PaymentsTab.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on && PaymentsTab.IsChecked == true) UsersTab.IsChecked = true;
    }

    static string Months(int m) => m % 10 == 1 && m % 100 != 11 ? $"{m} месяц"
        : m % 10 is >= 2 and <= 4 && (m % 100 < 10 || m % 100 >= 20) ? $"{m} месяца" : $"{m} месяцев";

    PaymentsReport? _lastPayments;
    string _lastPayError = "";

    public void UpdatePayments(PaymentsReport? r, string error)
    {
        _lastPayments = r; _lastPayError = error;
        if (r == null)
        {
            PaySummary.Text = "Оплаты недоступны";
            PayNote.Text = error;
            return;
        }
        string Money(int v) => $"{v:N0} {r.Currency}";
        var ok = r.Payments.Where(p => p.Status == "ok").ToList();
        var now = DateTime.Now;
        var month = ok.Where(p => (p.Done ?? p.Created).Year == now.Year && (p.Done ?? p.Created).Month == now.Month).ToList();
        int pending = r.Payments.Count(p => p.Status == "sent");
        var culture = new System.Globalization.CultureInfo("ru-RU");
        PaySummary.Text = $"за {now.ToString("MMMM", culture)}: {month.Count} на {Money(month.Sum(p => p.Amount))}"
                          + $"   ·   всего: {ok.Count} на {Money(ok.Sum(p => p.Amount))}"
                          + (pending > 0 ? $"   ·   ждут проверки в боте: {pending}" : "");
        PayMonths.Children.Clear();
        foreach (var g in ok.GroupBy(p => new DateTime((p.Done ?? p.Created).Year, (p.Done ?? p.Created).Month, 1)).OrderByDescending(g => g.Key).Take(12))
        {
            var b = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 6) };
            b.SetResourceReference(Border.BorderBrushProperty, "Line");
            var t = new TextBlock { Text = $"{g.Key.ToString("MMM yyyy", culture)} · {Money(g.Sum(p => p.Amount))} ({g.Count()})", FontSize = 12 };
            b.Child = t;
            PayMonths.Children.Add(b);
        }
        PayNote.Text = error != "" ? error : $"обновлено {r.Generated:HH:mm:ss} · заявки подтверждаются в боте";
        PaymentsGrid.ItemsSource = r.Payments.OrderByDescending(p => p.Created).Select(p => new PaymentRow(
            p.Created.ToString("dd.MM.yy HH:mm"), Demo.Name(p.Username), Months(p.Months), Money(p.Amount), p.Status,
            p.Status switch { "ok" => "подтверждена", "no" => "отклонена", _ => "ждёт проверки" },
            p.NewExpire)).ToList();
    }

    // двойной клик по пользователю — карточка
    void Users_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindRow(e.OriginalSource as DependencyObject) is UserRow u) { OpenUserRequested?.Invoke(u); e.Handled = true; }
    }

    void MuteChanged(string status)
    {
        _s.Save();
        SetStatus(status);
        MutesChanged?.Invoke();
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
        "devices" => u.Devices,
        "node" => u.LastNode,
        _ => u.LastSeen ?? DateTimeOffset.MinValue,
    };

    void Apply(bool keepScroll = false)
    {
        if (_snap == null) return;

        Demo.SetUsers(_snap.Users);
        IEnumerable<UserRow> q = _snap.Users.Select(Demo.Mask);
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
            (HdrName, "name", "ПОЛЬЗОВАТЕЛЬ"), (HdrStatus, "status", "СТАТУС"), (HdrDevices, "devices", "УСТР."), (HdrTraffic, "traffic", "ТРАФИК"),
            (HdrNode, "node", "ПОСЛЕДНЯЯ НОДА · IP"), (HdrOnline, "online", "ОНЛАЙН"),
        };
        foreach (var (tb, key, label) in map)
            tb.Text = label + (_sortKey == key ? (_sortDesc ? " ▼" : " ▲") : "");
    }

    void Sort_Click(object sender, MouseButtonEventArgs e)
    {
        var key = (string)((FrameworkElement)sender).Tag;
        if (_sortKey == key) _sortDesc = !_sortDesc;
        else { _sortKey = key; _sortDesc = key is "traffic" or "online" or "devices"; }
        UpdateHeaders();
        Apply();
    }

    void Filter_Changed(object sender, RoutedEventArgs e) => Apply();

    // ---- контекстное меню строк (создаётся и открывается из кода) ----
    static object? FindRow(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement f && (f.DataContext is UserRow || f.DataContext is NodeRow)) return f.DataContext;
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
                    Add("открыть карточку", () => OpenUserRequested?.Invoke(u));
                    Add("копировать имя", () => CopyText(u.Username, u.Username));
                    if (u.Ip != "") Add("копировать IP", () => CopyText(u.Ip.Split(' ')[0], "IP"));
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
            }
            Add("открыть панель в браузере", OpenPanel);

            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
        }
        catch (Exception ex) { SetStatus("меню: " + ex.Message); }
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (UsersPanel == null || NodesPanel == null || PaymentsPanel == null || SearchBox == null || UsersFilters == null) return;
        bool users = UsersTab.IsChecked == true;
        bool nodes = NodesTab.IsChecked == true;
        bool pays = PaymentsTab.IsChecked == true;
        UsersPanel.Visibility = users ? Visibility.Visible : Visibility.Collapsed;
        NodesPanel.Visibility = nodes ? Visibility.Visible : Visibility.Collapsed;
        PaymentsPanel.Visibility = pays ? Visibility.Visible : Visibility.Collapsed;
        if (pays) PaymentsOpened?.Invoke();
        SearchBox.Visibility = users ? Visibility.Visible : Visibility.Hidden;
        UsersFilters.Visibility = users ? Visibility.Visible : Visibility.Collapsed;
    }

    void Search_Changed(object sender, TextChangedEventArgs e) { _debounce.Stop(); _debounce.Start(); }
    void Refresh_Click(object sender, RoutedEventArgs e) => _refresh();
    void Settings_Click(object sender, RoutedEventArgs e) => _openSettings();

    // ---- режим демонстрации ----
    public Action? DemoChanged;

    void Demo_Click(object sender, RoutedEventArgs e) => ToggleDemo();

    void ToggleDemo()
    {
        Demo.On = !Demo.On;
        _s.DemoMode = Demo.On;
        _s.Save();
        ShowDemo();
        Apply(keepScroll: true);
        if (_lastPayments != null) UpdatePayments(_lastPayments, _lastPayError);
        DemoChanged?.Invoke();
        SetStatus(Demo.On ? "режим демонстрации: имена и IP скрыты" : "режим демонстрации выключен");
    }

    void ShowDemo()
    {
        DemoBtn.Content = Demo.On ? "👁 демо: вкл" : "👁 демо";
        if (Demo.On) DemoBtn.SetResourceReference(ForegroundProperty, "Amber");
        else DemoBtn.ClearValue(ForegroundProperty);
        Search.Text = Demo.On ? "" : Search.Text;   // поиск по настоящему имени в демо выдавал бы людей
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        _s.Theme = _s.Theme == "light" ? "dark" : "light";
        _s.Save();
        App.ApplyTheme(_s.Theme);
    }

    // ---- положение и размер окна: восстанавливаются при запуске, сохраняются через секунду после изменения ----
    readonly DispatcherTimer _placeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _placeReady;

    void RestorePlacement()
    {
        Width = Math.Max(MinWidth, _s.Width); Height = Math.Max(MinHeight, _s.Height);
        // позиция применяется, только если окно хотя бы частично попадает на один из текущих мониторов
        if (_s.Left is double l && _s.Top is double t)
        {
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var title = new Rect(l + 40, t, Math.Max(1, Width - 80), 30);   // заголовок должен быть виден, чтобы окно можно было перетащить
            if (vs.IntersectsWith(title))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l; Top = t;
            }
        }
        if (_s.Maximized) WindowState = WindowState.Maximized;
        _placeTimer.Tick += (_, _) => { _placeTimer.Stop(); SavePlacement(); };
        LocationChanged += (_, _) => PlacementChanged();
        SizeChanged += (_, _) => PlacementChanged();
        StateChanged += (_, _) => PlacementChanged();
        ContentRendered += (_, _) => _placeReady = true;
    }

    void PlacementChanged()
    {
        if (!_placeReady || WindowState == WindowState.Minimized) return;
        _placeTimer.Stop(); _placeTimer.Start();
    }

    public void SavePlacement()
    {
        if (!_placeReady || WindowState == WindowState.Minimized) return;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (r.IsEmpty || r.Width <= 0) return;
        _s.Left = r.Left; _s.Top = r.Top; _s.Width = r.Width; _s.Height = r.Height;
        _s.Maximized = WindowState == WindowState.Maximized;
        _s.Save();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _placeTimer.Stop();
        SavePlacement();
        e.Cancel = true;
        Hide();
    }
}

// Простая команда для горячих клавиш
public class RelayCommand : ICommand
{
    readonly Action _run;
    public RelayCommand(Action run) => _run = run;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _run();
}
