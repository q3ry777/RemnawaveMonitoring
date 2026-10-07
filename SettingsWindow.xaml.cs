using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace RemnawaveMonitor;

public partial class SettingsWindow : Window
{
    readonly Settings _s;
    readonly List<SshServer> _servers = new();
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

        // проверки и сайты
        ProbeBox.IsChecked = s.ProbeEnabled;
        RuBox.IsChecked = s.RuCheckEnabled;
        ProbePortBox.Text = s.ProbePort.ToString();
        ServicesBox.Text = s.ServicesText.Replace("\n", "\r\n");

        // telegram
        TgBox.IsChecked = s.TgEnabled;
        TgTokenBox.Password = s.TgToken;
        TgChatBox.Text = s.TgChatId;
        TgProxyBox.Text = s.TgProxy;
        TgNodesBox.IsChecked = s.TgNodes;
        TgServicesBox.IsChecked = s.TgServices;
        TgLimitsBox.IsChecked = s.TgLimits;
        TgBillingBox.IsChecked = s.TgBilling;
        TgUpdatesBox.IsChecked = s.TgUpdates;

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
        foreach (var name in new[] { "PagePanel", "PageSsh", "PageChecks", "PageSites", "PageTg", "PageUpdates", "PageMetrics", "PageGeneral" })
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
            string P(double v) => v >= 0 ? $"{v * 100:0}%" : "—";
            if (srv == _cur)
            {
                Result($"✓ подключено · CPU {P(st.Cpu)} · RAM {P(st.Ram)} ({st.RamText}) · диск {P(st.Disk)} ({st.DiskText})", "Green");
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
        _s.TgServices = TgServicesBox.IsChecked == true;
        _s.TgLimits = TgLimitsBox.IsChecked == true;
        _s.TgBilling = TgBillingBox.IsChecked == true;
        _s.TgUpdates = TgUpdatesBox.IsChecked == true;
        _s.ProbeEnabled = ProbeBox.IsChecked == true;
        _s.RuCheckEnabled = RuBox.IsChecked == true;
        _s.ProbePort = int.TryParse(ProbePortBox.Text, out var pp) && pp is >= 0 and <= 65535 ? pp : 443;
        _s.ServicesText = ServicesBox.Text.Replace("\r\n", "\n").Trim();
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
