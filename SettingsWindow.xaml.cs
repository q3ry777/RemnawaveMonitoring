using System;
using System.IO;
using System.Windows;

namespace RemnawaveMonitor;

public partial class SettingsWindow : Window
{
    readonly Settings _s;

    public SettingsWindow(Settings s)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WinChrome.Apply(this);
        _s = s;
        Url.Text = s.BaseUrl;
        Token.Password = s.ApiToken;
        Interval.Text = s.IntervalSec.ToString();
        OnlineBox.Text = s.OnlineWindowSec.ToString();
        MetricsUrlBox.Text = s.MetricsUrl;
        MetricsUserBox.Text = s.MetricsUser;
        MetricsPassBox.Password = s.MetricsPass;
        UpdVerText.Text = $"ОБНОВЛЕНИЯ ПРОГРАММЫ · СЕЙЧАС ВЕРСИЯ {Updater.CurrentText}";
        UpdateRepoBox.Text = s.UpdateRepo;
        TgBox.IsChecked = s.TgEnabled;
        TgTokenBox.Password = s.TgToken;
        TgChatBox.Text = s.TgChatId;
        TgProxyBox.Text = s.TgProxy;
        TgNodesBox.IsChecked = s.TgNodes;
        TgServicesBox.IsChecked = s.TgServices;
        TgLimitsBox.IsChecked = s.TgLimits;
        TgBillingBox.IsChecked = s.TgBilling;
        TgUpdatesBox.IsChecked = s.TgUpdates;
        UpdateTokenBox.Password = s.UpdateToken;
        ProbeBox.IsChecked = s.ProbeEnabled;
        RuBox.IsChecked = s.RuCheckEnabled;
        ProbePortBox.Text = s.ProbePort.ToString();
        ServicesBox.Text = s.ServicesText.Replace("\n", "\r\n");
        (s.Theme == "light" ? LightRb : DarkRb).IsChecked = true;
        AutoBox.IsChecked = AutoRun.IsEnabled();
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        _s.BaseUrl = Url.Text.Trim();
        _s.ApiToken = Token.Password.Trim();
        _s.IntervalSec = int.TryParse(Interval.Text, out var v) && v >= 5 ? v : 30;
        _s.OnlineWindowSec = int.TryParse(OnlineBox.Text, out var w) && w >= 30 ? w : 300;
        _s.MetricsUrl = MetricsUrlBox.Text.Trim();
        _s.MetricsUser = MetricsUserBox.Text.Trim();
        _s.MetricsPass = MetricsPassBox.Password;
        _s.UpdateRepo = UpdateRepoBox.Text.Trim();
        _s.TgEnabled = TgBox.IsChecked == true;
        _s.TgToken = TgTokenBox.Password.Trim();
        _s.TgChatId = TgChatBox.Text.Trim();
        _s.TgProxy = TgProxyBox.Text.Trim();
        _s.TgNodes = TgNodesBox.IsChecked == true;
        _s.TgServices = TgServicesBox.IsChecked == true;
        _s.TgLimits = TgLimitsBox.IsChecked == true;
        _s.TgBilling = TgBillingBox.IsChecked == true;
        _s.TgUpdates = TgUpdatesBox.IsChecked == true;
        _s.UpdateToken = UpdateTokenBox.Password.Trim();
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
