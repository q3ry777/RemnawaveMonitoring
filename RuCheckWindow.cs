using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace RemnawaveMonitor;

// Проверка доступности нод из РФ: TCP-доступность адреса и портов с российских точек check-host.net.
// Проверяет сам check-host, а не этот компьютер. Адреса проверяются по очереди (у check-host лимиты), результаты появляются по мере готовности.
public class RuCheckWindow : Window
{
    readonly Api _api;
    readonly Settings _s;
    readonly CheckHost _check;
    readonly List<NodeRow> _nodes;
    readonly StackPanel _list = new();
    readonly TextBlock _status = new() { FontSize = 12, Margin = new Thickness(0, 8, 0, 14), TextWrapping = TextWrapping.Wrap };
    bool _closed;

    // Итог для строки состояния на вкладке «ноды»
    public event Action<string>? Done;

    public RuCheckWindow(Api api, Settings s, CheckHost check, List<NodeRow> nodes)
    {
        _api = api; _s = s; _check = check; _nodes = nodes;
        Title = "проверка из РФ";
        Width = 820; Height = 620; MinWidth = 600; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        SetResourceReference(FontFamilyProperty, "Mono");
        SourceInitialized += (_, _) => WinChrome.Apply(this);
        Closed += (_, _) => _closed = true;
        Loaded += async (_, _) => await RunAsync();

        var title = new TextBlock { FontSize = 15, FontWeight = FontWeights.Bold };
        title.Inlines.Add(new Run("проверка из РФ"));
        title.Inlines.Add(new Run("_") { Foreground = Res("Amber") });
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var head = new StackPanel();
        head.Children.Add(title);
        head.Children.Add(_status);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list };
        var root = new Grid { Margin = new Thickness(24, 20, 24, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(head);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Content = root;
    }

    Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    TextBlock Line(string text, string brush) =>
        new() { Text = text, FontSize = 12, Margin = new Thickness(0, 3, 0, 0), Foreground = Res(brush), TextWrapping = TextWrapping.Wrap };

    // Карточка адреса: заголовок и строка «проверяю…», которую потом заменят результаты
    StackPanel AddCard(string name, string target)
    {
        var body = new StackPanel();
        var head = new TextBlock { FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        head.Inlines.Add(new Run(name));
        head.Inlines.Add(new Run("  " + Demo.Ip(target)) { Foreground = Res("Dim"), FontWeight = FontWeights.Normal });
        body.Children.Add(head);
        body.Children.Add(Line("проверяю… (до 30 сек)", "Dim"));
        var card = new Border { Child = body, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 10) };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        _list.Children.Add(card);
        return body;
    }

    static void ClearBody(StackPanel body)
    {
        while (body.Children.Count > 1) body.Children.RemoveAt(1);
    }

    string Fill(StackPanel body, NodeRow n, CheckHost.Result r)
    {
        ClearBody(body);
        foreach (var p in r.Points)
            body.Children.Add(p.Ok ? Line($"✔ {p.City} — {Math.Round(p.Ms)} мс", "Green")
                : p.Ms < 0 ? Line($"… {p.City} — нет ответа от точки", "Dim")
                : Line($"✖ {p.City} — {CheckHost.ErrorText(p.Error)}", "Red"));

        var (level, text) = CheckHost.Verdict(r, n.Connected);
        var verdict = Line(text, level == "ok" ? "Green" : level == "warn" ? "Amber" : level == "bad" ? "Red" : "Dim");
        verdict.FontWeight = FontWeights.Bold;
        verdict.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(verdict);

        if (!Demo.On && r.Link != "")
        {
            var link = new TextBlock { FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
            var h = new Hyperlink(new Run("отчёт на check-host.net")) { Cursor = System.Windows.Input.Cursors.Hand };
            h.SetResourceReference(Hyperlink.ForegroundProperty, "Amber");
            h.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(r.Link) { UseShellExecute = true }); } catch { } };
            link.Inlines.Add(h);
            body.Children.Add(link);
        }
        return level;
    }

    async Task RunAsync()
    {
        try
        {
            // какие порты проверять: активные inbound'ы ноды (до 3), иначе 443
            Dictionary<string, List<int>> ports;
            try { ports = await _api.GetNodePortsAsync(_s); }
            catch { ports = new Dictionary<string, List<int>>(); }

            var targets = new List<(NodeRow node, string target)>();
            foreach (var node in _nodes.Where(x => x.Address != ""))
            {
                var ps = ports.TryGetValue(node.NodeUuid, out var l) && l.Count > 0 ? l.Take(3).ToList() : new List<int> { 443 };
                var host = node.Address.Contains(':') ? $"[{node.Address}]" : node.Address;
                foreach (var p in ps) targets.Add((node, $"{host}:{p}"));
            }
            if (targets.Count == 0) { _status.Text = "у выбранных нод нет адреса"; return; }

            var levels = new Dictionary<string, List<string>>();
            int i = 0;
            foreach (var (node, target) in targets)
            {
                if (_closed) return;
                i++;
                _status.Text = $"check-host.net · проверяю {i} из {targets.Count}: {node.Name} (до 30 сек на адрес)";
                var body = AddCard($"{node.Name}", target);
                string level;
                try
                {
                    var r = await _check.CheckTcpAsync(target);
                    if (_closed) return;
                    level = Fill(body, node, r);
                }
                catch (Exception ex)
                {
                    if (_closed) return;
                    ClearBody(body);
                    body.Children.Add(Line("check-host не ответил: " + ex.Message + ". Попробуйте позже.", "Red"));
                    level = "none";
                }
                if (!levels.TryGetValue(node.Name, out var list)) levels[node.Name] = list = new List<string>();
                list.Add(level);
            }

            int okNodes = levels.Count(kv => kv.Value.All(x => x == "ok"));
            var bad = levels.Where(kv => kv.Value.Any(x => x == "bad")).Select(kv => kv.Key).ToList();
            var summary = $"из РФ: {okNodes}/{levels.Count} нод доступны · {DateTime.Now:HH:mm}"
                + (bad.Count > 0 ? " · недоступны: " + string.Join(", ", bad) : "");
            _status.Text = "готово · " + DateTime.Now.ToString("HH:mm:ss");
            Done?.Invoke(summary);
        }
        catch (Exception ex)
        {
            if (!_closed) _status.Text = "ошибка проверки: " + ex.Message;
        }
    }
}
