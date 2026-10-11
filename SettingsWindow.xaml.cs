using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RemnawaveMonitor;

public partial class SettingsWindow : Window
{
    readonly Settings _s;
    readonly List<SshServer> _servers = new();
    readonly ToggleButton[] _hours = new ToggleButton[24];
    SshServer? _cur;

    public SettingsWindow(Settings s, IEnumerable<(string name, string address)> nodes)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WinChrome.Apply(this);
        _s = s;

        // панель
        Url.Text = s.BaseUrl;
        Token.Password = s.ApiToken;
        Interval.Text = s.IntervalSec.ToString();
        OnlineBox.Text = s.OnlineWindowSec.ToString();

        // серверы: по одной записи на каждую ноду + сохранённые записи нод, которых уже нет в панели
        foreach (var (name, addr) in nodes)
        {
            var saved = s.SshServers.FirstOrDefault(x => x.Node == name);
            _servers.Add(saved?.Clone() ?? new SshServer { Node = name, Host = addr });
        }
        foreach (var x in s.SshServers.Where(x => _servers.All(y => y.Node != x.Node))) _servers.Add(x.Clone());
        SrvList.ItemsSource = _servers;
        if (_servers.Count > 0) SrvList.SelectedIndex = 0;
        else { SrvEmpty.Visibility = Visibility.Visible; SrvBlock.Visibility = Visibility.Collapsed; }


        // telegram
        TgBox.IsChecked = s.TgEnabled;
        TgTokenBox.Password = s.TgToken;
        TgChatBox.Text = s.TgChatId;
        TgProxyBox.Text = s.TgProxy;
        TgNodesBox.IsChecked = s.TgNodes;
        TgLimitsBox.IsChecked = s.TgLimits;
        TgBillingBox.IsChecked = s.TgBilling;
        TgUpdatesBox.IsChecked = s.TgUpdates;
        TgDndBox.IsChecked = s.TgDndActive;
        TgDndTimeBox.Text = s.TgDndTime;
        UpdateDndHint();
        for (int h = 0; h < 24; h++)
        {
            var b = new ToggleButton { Content = h.ToString("00"), Style = (Style)FindResource("HourToggle"), IsChecked = s.TgQuietHours.Contains(h) };
            b.Click += (_, _) => UpdateQuietHint();
            _hours[h] = b;
            QuietGrid.Children.Add(b);
        }
        TgQuietBox.IsChecked = s.TgQuietEnabled;
        UpdateQuietHint();

        // обновления и метрики
        UpdVerText.Text = $"Новые версии из релизов приватного репозитория GitHub. Сейчас установлена версия {Updater.CurrentText}.";
        UpdateRepoBox.Text = s.UpdateRepo;
        UpdateTokenBox.Password = s.UpdateToken;
        MetricsUrlBox.Text = s.MetricsUrl;
        MetricsUserBox.Text = s.MetricsUser;
        MetricsPassBox.Password = s.MetricsPass;

        // общие
        (s.Theme == "light" ? LightRb : DarkRb).IsChecked = true;
        AutoBox.IsChecked = AutoRun.IsEnabled();
    }

    // ---- боковое меню ----
    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string page) return;
        foreach (var name in new[] { "PagePanel", "PageSsh", "PageTg", "PageUpdates", "PageMetrics", "PageGeneral" })
            if (FindName(name) is UIElement el)
                el.Visibility = name == page ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- серверы (SSH) ----
    void StoreForm()
    {
        if (_cur == null) return;
        _cur.Enabled = SrvEnabled.IsChecked == true;
        _cur.Host = SrvHost.Text.Trim();
        _cur.Port = int.TryParse(SrvPort.Text.Trim(), out var p) && p is > 0 and <= 65535 ? p : 22;
        _cur.User = SrvUser.Text.Trim();
        _cur.Password = SrvPass.Password;
    }

    void LoadForm()
    {
        SrvForm.IsEnabled = _cur != null;
        if (_cur == null) return;
        SrvTitle.Text = _cur.Node;
        SrvEnabled.IsChecked = _cur.Enabled;
        SrvHost.Text = _cur.Host;
        SrvPort.Text = _cur.Port.ToString();
        SrvUser.Text = _cur.User;
        SrvPass.Password = _cur.Password;
        SrvResult.Text = "";
        ShowKey();
    }

    void ShowKey() => SrvKey.Text = _cur == null || _cur.HostKey == ""
        ? "ключ сервера: запомнится при первом подключении"
        : "ключ сервера: SHA256:" + _cur.HostKey;

    void Result(string text, string brush)
    {
        SrvResult.Text = text;
        SrvResult.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    void SrvList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StoreForm();
        _cur = SrvList.SelectedItem as SshServer;
        LoadForm();
    }

    void SrvEnabled_Click(object sender, RoutedEventArgs e)
    {
        StoreForm();
        SrvList.Items.Refresh();
    }

    void SrvGen_Click(object sender, RoutedEventArgs e)
    {
        SrvPass.Password = SshStats.NewPassword();
        Result("Пароль создан. Теперь скопируйте команду установки — пароль уже в ней.", "Dim");
    }

    bool ValidUser(out string error)
    {
        var u = SrvUser.Text.Trim();
        error = u == "" ? "Укажите имя пользователя."
            : u == "root" ? "Нужен отдельный пользователь, не root: он сможет только выдать статистику."
            : !Regex.IsMatch(u, "^[a-z_][a-z0-9_-]{0,31}$") ? "Имя пользователя: латиница в нижнем регистре, цифры, «_» и «-»."
            : "";
        return error == "";
    }

    void SrvCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_cur == null) return;
        if (!ValidUser(out var err)) { Result(err, "Red"); return; }
        if (SrvPass.Password == "") SrvPass.Password = SshStats.NewPassword();
        StoreForm();
        try
        {
            Clipboard.SetText(SshStats.SetupCommand(_cur.User, _cur.Password));
            Result("Команда скопирована. Вставьте её в консоль сервера под root и дождитесь «RW-MONITOR: готово», затем нажмите «проверить подключение».", "Green");
        }
        catch (Exception ex) { Result("Не удалось скопировать: " + ex.Message, "Red"); }
    }

    async void SrvTest_Click(object sender, RoutedEventArgs e)
    {
        if (_cur == null) return;
        if (!ValidUser(out var err)) { Result(err, "Red"); return; }
        StoreForm();
        if (_cur.Host == "" || _cur.Password == "") { Result("Нужны адрес и пароль.", "Red"); return; }
        var srv = _cur;
        Result("подключаюсь…", "Dim");
        try
        {
            var (st, key) = await SshStats.CollectAsync(srv.Clone());
            if (srv.HostKey == "" && key != "") srv.HostKey = key;
            string P(double v) => v >= 0 ? SshStats.Pct(v) : "—";
            if (srv == _cur)
            {
                var text = $"✓ подключено · CPU {P(st.Cpu)} · RAM {P(st.Ram)} ({st.RamText}) · диск {P(st.Disk)} ({st.DiskText})";
                if (st.Legacy)
                    Result(text + ". На сервере старый скрипт: CPU меряется грубо. Нажмите «скопировать команду установки» и выполните её на сервере ещё раз.", "Amber");
                else
                    Result(text + ". В карточке ноды CPU — среднее за минуту.", "Green");
                ShowKey();
            }
        }
        catch (Exception ex) { if (srv == _cur) Result("✗ " + ex.Message, "Red"); }
    }

    void SrvResetKey_Click(object sender, RoutedEventArgs e)
    {
        if (_cur == null) return;
        _cur.HostKey = "";
        ShowKey();
    }

    // ---- сохранение ----
    void Save_Click(object sender, RoutedEventArgs e)
    {
        StoreForm();
        _s.BaseUrl = Url.Text.Trim();
        _s.ApiToken = Token.Password.Trim();
        _s.IntervalSec = int.TryParse(Interval.Text, out var v) && v >= 5 ? v : 30;
        _s.OnlineWindowSec = int.TryParse(OnlineBox.Text, out var w) && w >= 30 ? w : 300;
        _s.SshServers = _servers.Select(x => x.Clone()).ToList();
        _s.MetricsUrl = MetricsUrlBox.Text.Trim();
        _s.MetricsUser = MetricsUserBox.Text.Trim();
        _s.MetricsPass = MetricsPassBox.Password;
        _s.UpdateRepo = UpdateRepoBox.Text.Trim();
        _s.UpdateToken = UpdateTokenBox.Password.Trim();
        _s.TgEnabled = TgBox.IsChecked == true;
        _s.TgToken = TgTokenBox.Password.Trim();
        _s.TgChatId = TgChatBox.Text.Trim();
        _s.TgProxy = TgProxyBox.Text.Trim();
        _s.TgNodes = TgNodesBox.IsChecked == true;
        _s.TgLimits = TgLimitsBox.IsChecked == true;
        _s.TgBilling = TgBillingBox.IsChecked == true;
        _s.TgUpdates = TgUpdatesBox.IsChecked == true;
        if (Settings.TryParseTime(TgDndTimeBox.Text, out var dnd)) _s.TgDndTime = dnd.ToString(@"hh\:mm");
        _s.TgDnd = TgDndBox.IsChecked == true && Settings.TryParseTime(TgDndTimeBox.Text, out _);
        _s.TgDndUntil = _s.TgDnd ? Settings.NextAt(dnd) : null;
        _s.TgQuietHours = SelectedHours();
        _s.TgQuietEnabled = TgQuietBox.IsChecked == true && _s.TgQuietHours.Count > 0;
        _s.Theme = LightRb.IsChecked == true ? "light" : "dark";
        try { AutoRun.Set(AutoBox.IsChecked == true); }
        catch (Exception ex) { MessageBox.Show("Автозапуск: " + ex.Message); }
        App.ApplyTheme(_s.Theme);
        DialogResult = true;
    }

    // ---- telegram ----
    Settings TgTemp() => new Settings
    {
        TgToken = TgTokenBox.Password.Trim(), TgChatId = TgChatBox.Text.Trim(), TgProxy = TgProxyBox.Text.Trim(),
    };

    void TgDnd_Click(object sender, RoutedEventArgs e) => UpdateDndHint();
    void TgDndTime_Changed(object sender, TextChangedEventArgs e) => UpdateDndHint();

    // Подсказка считается по часам компьютера: до какого момента уведомления не придут
    void UpdateDndHint()
    {
        if (TgDndHint == null) return;
        var now = DateTime.Now;
        if (!Settings.TryParseTime(TgDndTimeBox.Text, out var t))
        {
            TgDndHint.Text = "Время в формате ЧЧ:ММ, например 08:00.";
            TgDndHint.SetResourceReference(TextBlock.ForegroundProperty, "Red");
            return;
        }
        TgDndHint.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        if (TgDndBox.IsChecked != true)
        {
            TgDndHint.Text = $"Выключено. Сейчас на компьютере {now:HH:mm}.";
            return;
        }
        var until = Settings.NextAt(t);
        var day = until.Date == now.Date ? "сегодня" : "завтра";
        TgDndHint.Text = $"Уведомления не придут до {until:HH:mm} ({day}, {until:dd.MM}) — ещё {Left(until - now)}. " +
                         $"Сейчас на компьютере {now:HH:mm}. Ответы на команды бота приходят как обычно, события пишутся в журнал.";
    }

    // ---- тихие часы ----
    List<int> SelectedHours() => Enumerable.Range(0, 24).Where(h => _hours[h]?.IsChecked == true).ToList();

    void TgQuiet_Click(object sender, RoutedEventArgs e) => UpdateQuietHint();

    void QuietNight_Click(object sender, RoutedEventArgs e)
    {
        for (int h = 0; h < 24; h++) _hours[h].IsChecked = h >= 23 || h < 8;
        UpdateQuietHint();
    }

    void QuietClear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var b in _hours) b.IsChecked = false;
        UpdateQuietHint();
    }

    // Подсказка по часам компьютера: идут ли тихие часы сейчас и когда начнутся / закончатся
    void UpdateQuietHint()
    {
        if (TgQuietHint == null || _hours[23] == null) return;
        var now = DateTime.Now;
        var sel = SelectedHours();
        TgQuietHint.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        if (sel.Count == 0)
        {
            TgQuietHint.Text = "Часы не выбраны. Нажмите на часы, когда уведомления не нужны, или «ночь 23–08».";
            if (TgQuietBox.IsChecked == true) TgQuietHint.SetResourceReference(TextBlock.ForegroundProperty, "Red");
            return;
        }
        var ranges = Settings.QuietRangesText(sel);
        if (TgQuietBox.IsChecked != true)
        {
            TgQuietHint.Text = $"Тихо: {ranges}. Сейчас выключено. Время на компьютере {now:HH:mm}.";
            return;
        }
        var probe = new Settings { TgQuietEnabled = true, TgQuietHours = sel };
        string state;
        if (probe.SilentUntil(now) is DateTime until)
            state = until == DateTime.MaxValue ? "Тихо весь день." : $"Сейчас тихо — до {Settings.WhenText(until)}.";
        else
        {
            var next = Enumerable.Range(1, 23).Select(i => now.Date.AddHours(now.Hour + i)).First(t => sel.Contains(t.Hour));
            state = $"Сейчас уведомления приходят, тишина начнётся {(next.Date == now.Date ? "сегодня" : "завтра")} в {next:HH:mm}.";
        }
        TgQuietHint.Text = $"Тихо каждый день: {ranges}. {state} Время на компьютере {now:HH:mm}.";
    }

    static string Left(TimeSpan d) => d.TotalHours >= 1 ? $"{(int)d.TotalHours} ч {d.Minutes} мин" : $"{Math.Max(1, d.Minutes)} мин";

    async void TgBind_Click(object sender, RoutedEventArgs e)
    {
        var tmp = TgTemp();
        if (tmp.TgToken == "") { MessageBox.Show("Сначала вставьте токен бота."); return; }
        try
        {
            var r = await TelegramService.FindChatAsync(tmp);
            if (r == null)
                MessageBox.Show("Сообщений боту не найдено. Напишите ему любое сообщение в Telegram и нажмите ещё раз.");
            else
            {
                TgChatBox.Text = r.Value.id;
                MessageBox.Show($"Привязан чат: {r.Value.name} ({r.Value.id}). Нажмите «сохранить».");
            }
        }
        catch (Exception ex) { MessageBox.Show("Ошибка: " + ex.Message); }
    }

    async void TgTest_Click(object sender, RoutedEventArgs e)
    {
        var tmp = TgTemp();
        if (tmp.TgToken == "" || tmp.TgChatId == "") { MessageBox.Show("Нужны токен бота и ID чата."); return; }
        var err = await TelegramService.SendOnceAsync(tmp, "✅ Remnawave Monitor: тестовое сообщение");
        MessageBox.Show(err == null ? "Сообщение отправлено — проверьте Telegram." : "Не удалось отправить: " + err);
    }

    // ---- обновления ----
    async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var tmp = new Settings { UpdateRepo = UpdateRepoBox.Text.Trim(), UpdateToken = UpdateTokenBox.Password.Trim() };
        if (tmp.UpdateRepo == "") { MessageBox.Show("Укажите репозиторий вида owner/repo."); return; }
        try
        {
            var u = await Updater.CheckAsync(tmp);
            MessageBox.Show(u == null
                ? $"У вас последняя версия ({Updater.CurrentText})."
                : $"Доступна версия {u.Version}. Сохраните настройки — в шапке окна появится кнопка установки.");
        }
        catch (Exception ex) { MessageBox.Show("Не удалось проверить: " + ex.Message); }
    }

    // ---- метрики ----
    async void Dump_Click(object sender, RoutedEventArgs e)
    {
        var url = MetricsUrlBox.Text.Trim();
        if (url == "") { MessageBox.Show("Укажите URL метрик."); return; }
        try
        {
            var text = await Api.FetchMetricsAsync(url, MetricsUserBox.Text.Trim(), MetricsPassBox.Password);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemnawaveMonitor");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "metrics.txt");
            File.WriteAllText(path, text);
            MessageBox.Show($"Сохранено:\n{path}");
        }
        catch (Exception ex) { MessageBox.Show("Ошибка: " + ex.Message); }
    }
}
