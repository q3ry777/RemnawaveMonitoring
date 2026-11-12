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
        TgWebhookBox.IsChecked = s.TgWebhook;
        ToastsBox.IsChecked = s.ToastsEnabled;
        RuCheckBox.Text = s.RuCheckMin.ToString();
        TgWriteTokenBox.Password = s.TgWriteToken;
        TgApiBox.IsChecked = s.TgApiEnabled;
        TgApiHostBox.Text = s.TgApiHost != "" ? s.TgApiHost : Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var pu) ? pu.Host : "";
        TgApiPortBox.Text = s.TgApiPort.ToString();

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
        _s.TgWebhook = TgWebhookBox.IsChecked == true;
        _s.ToastsEnabled = ToastsBox.IsChecked == true;
        _s.RuCheckMin = RuMin(RuCheckBox.Text, _s.RuCheckMin);
        _s.TgWriteToken = TgWriteTokenBox.Password.Trim();
        _s.TgApiEnabled = TgApiBox.IsChecked == true;
        _s.TgApiHost = TgApiHostBox.Text.Trim();
        _s.TgApiPort = ApiPort();
        _s.Theme = LightRb.IsChecked == true ? "light" : "dark";
        try { AutoRun.Set(AutoBox.IsChecked == true); }
        catch (Exception ex) { MessageBox.Show("Автозапуск: " + ex.Message); }
        App.ApplyTheme(_s.Theme);
        DialogResult = true;
    }

    // ---- telegram-бот ----
    void TgResultText(string text, string brush)
    {
        TgResult.Text = text;
        TgResult.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    int ApiPort() => int.TryParse(TgApiPortBox.Text.Trim(), out var p) && p is > 1024 and <= 65535 && p != 8080 ? p : 8090;

    // 0 — выключено, иначе от 10 минут до суток; мусор в поле — прежнее значение
    static int RuMin(string text, int def) =>
        int.TryParse(text.Trim(), out var v) && v >= 0 ? (v == 0 ? 0 : Math.Min(Math.Max(v, 10), 1440)) : def;



    void WhatsNew_Click(object sender, RoutedEventArgs e) => WhatsNew.Show(this, Changelog.Load().Take(6).ToList(), "что нового");

    void TgCopy_Click(object sender, RoutedEventArgs e)
    {
        var token = TgTokenBox.Password.Trim();
        var chat = TgChatBox.Text.Trim();
        var api = Token.Password.Trim();
        if (token == "" || chat == "") { TgResultText("Нужны токен бота и ID чата.", "Red"); return; }
        if (api == "") { TgResultText("Нужен API-токен панели (раздел «панель»).", "Red"); return; }
        if (TgApiBox.IsChecked == true && _s.TgApiKey == "") { _s.TgApiKey = BotSetup.NewSecret(); _s.Save(); }   // ключ вкладки «оплаты»
        if (_s.TgWebhookSecret == "") { _s.TgWebhookSecret = BotSetup.NewSecret(); _s.Save(); }   // секрет вебхуков сразу сохраняется
        var cfg = new BotSetup.Config(api, token, chat,
            int.TryParse(OnlineBox.Text, out var w) && w >= 30 ? w : 300,
            TgWebhookBox.IsChecked == true, _s.TgWebhookSecret, TgWriteTokenBox.Password.Trim(),
            TgApiBox.IsChecked == true, _s.TgApiKey, ApiPort());
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
