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
    readonly ToggleButton[] _hours = new ToggleButton[24];

    public SettingsWindow(Settings s)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WinChrome.Apply(this);
        _s = s;

        // панель
        Url.Text = s.BaseUrl;
        Token.Password = s.ApiToken;
        Interval.Text = s.IntervalSec.ToString();
        OnlineBox.Text = s.OnlineWindowSec.ToString();

        // telegram
        TgTokenBox.Password = s.TgToken;
        TgChatBox.Text = s.TgChatId;
        TgNodesBox.IsChecked = s.TgNodes;
        TgLimitsBox.IsChecked = s.TgLimits;
        TgBillingBox.IsChecked = s.TgBilling;
        TgUpdatesBox.IsChecked = s.TgUpdates;
        TgDndTimeBox.Text = s.TgDndTime;
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
        foreach (var name in new[] { "PagePanel", "PageTg", "PageUpdates", "PageMetrics", "PageGeneral" })
            if (FindName(name) is UIElement el)
                el.Visibility = name == page ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- сохранение ----
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
        _s.UpdateToken = UpdateTokenBox.Password.Trim();
        _s.TgToken = TgTokenBox.Password.Trim();
        _s.TgChatId = TgChatBox.Text.Trim();
        _s.TgNodes = TgNodesBox.IsChecked == true;
        _s.TgLimits = TgLimitsBox.IsChecked == true;
        _s.TgBilling = TgBillingBox.IsChecked == true;
        _s.TgUpdates = TgUpdatesBox.IsChecked == true;
        if (Settings.TryParseTime(TgDndTimeBox.Text, out var dnd)) _s.TgDndTime = dnd.ToString(@"hh\:mm");
        _s.TgQuietHours = SelectedHours();
        _s.TgQuietEnabled = TgQuietBox.IsChecked == true && _s.TgQuietHours.Count > 0;
        _s.Theme = LightRb.IsChecked == true ? "light" : "dark";
        try { AutoRun.Set(AutoBox.IsChecked == true); }
        catch (Exception ex) { MessageBox.Show("Автозапуск: " + ex.Message); }
        App.ApplyTheme(_s.Theme);
        DialogResult = true;
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

    // Подсказка: какие часы выбраны (время — по часовому поясу сервера, он берётся с этого компьютера)
    void UpdateQuietHint()
    {
        if (TgQuietHint == null || _hours[23] == null) return;
        var sel = SelectedHours();
        TgQuietHint.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        if (sel.Count == 0)
        {
            TgQuietHint.Text = "Часы не выбраны. Нажмите на часы, когда уведомления не нужны, или «ночь 23–08».";
            if (TgQuietBox.IsChecked == true) TgQuietHint.SetResourceReference(TextBlock.ForegroundProperty, "Red");
            return;
        }
        var ranges = Settings.QuietRangesText(sel);
        TgQuietHint.Text = TgQuietBox.IsChecked == true
            ? $"Тихо каждый день: {ranges} (часовой пояс {BotSetup.TimeZone()}). Ответы на команды приходят всегда."
            : $"Выбрано: {ranges}. Сейчас выключено.";
    }

    // ---- telegram-бот ----
    void TgResultText(string text, string brush)
    {
        TgResult.Text = text;
        TgResult.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    void TgCopy_Click(object sender, RoutedEventArgs e)
    {
        var token = TgTokenBox.Password.Trim();
        var chat = TgChatBox.Text.Trim();
        var api = Token.Password.Trim();
        if (token == "" || chat == "") { TgResultText("Нужны токен бота и ID чата.", "Red"); return; }
        if (api == "") { TgResultText("Нужен API-токен панели (раздел «панель»).", "Red"); return; }
        if (!Settings.TryParseTime(TgDndTimeBox.Text, out var dnd)) { TgResultText("Время «не беспокоить» — в формате ЧЧ:ММ, например 08:00.", "Red"); return; }
        var cfg = new BotSetup.Config(api, token, chat,
            int.TryParse(OnlineBox.Text, out var w) && w >= 30 ? w : 300,
            TgNodesBox.IsChecked == true, TgLimitsBox.IsChecked == true, TgBillingBox.IsChecked == true, TgUpdatesBox.IsChecked == true,
            TgQuietBox.IsChecked == true ? SelectedHours() : new List<int>(), dnd.ToString(@"hh\:mm"));
        try
        {
            Clipboard.SetText(BotSetup.Command(cfg));
            TgResultText("Команда скопирована. Выполните её на сервере с панелью под root и дождитесь «RW-BOT: готово». Не забудьте нажать «сохранить».", "Green");
        }
        catch (Exception ex) { TgResultText("Не удалось скопировать: " + ex.Message, "Red"); }
    }

    async void TgBind_Click(object sender, RoutedEventArgs e)
    {
        var token = TgTokenBox.Password.Trim();
        if (token == "") { MessageBox.Show("Сначала вставьте токен бота."); return; }
        try
        {
            var r = await BotSetup.FindChatAsync(token);
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
        var token = TgTokenBox.Password.Trim();
        var chat = TgChatBox.Text.Trim();
        if (token == "" || chat == "") { MessageBox.Show("Нужны токен бота и ID чата."); return; }
        var err = await BotSetup.SendOnceAsync(token, chat, "✅ Remnawave Monitor: тестовое сообщение");
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
