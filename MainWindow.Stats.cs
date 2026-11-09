using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RemnawaveMonitor;

// Вкладка «Статистика»: трафик нод по дням (столбцы со стеком) и рейтинг нод за период.
public partial class MainWindow
{
    public Action<int>? StatsRequested;   // попросить загрузить статистику за N дней
    NodeStats? _stats;
    int _statsDays = 7;

    static readonly CultureInfo Ru = new("ru-RU");
    static readonly string[] Palette =
        { "#FFB020", "#4ADE80", "#60A5FA", "#F472B6", "#A78BFA", "#FB923C", "#2DD4BF", "#FACC15", "#94A3B8", "#F87171" };
    static readonly Brush[] NodeBrushes = Palette.Select(c => (Brush)new BrushConverter().ConvertFromString(c)!).ToArray();
    static Brush NodeBrush(int i) => NodeBrushes[i % NodeBrushes.Length];
    static string Day(DateTime d) => d.ToString("d MMM", Ru);

    void StatsPeriod_Checked(object sender, RoutedEventArgs e)
    {
        if (StatsChart == null || sender is not RadioButton { Tag: string t } || !int.TryParse(t, out var d)) return;
        _statsDays = d;
        StatsRequested?.Invoke(d);
    }

    public void UpdateStats(NodeStats? st, string note)
    {
        _stats = st;
        StatsNote.Text = note;
        StatsNote.Visibility = note == "" ? Visibility.Collapsed : Visibility.Visible;
        RenderStats();
    }

    void RenderStats()
    {
        StatsRank.Children.Clear();
        var st = _stats;
        if (st == null || st.Series.Count == 0)
        {
            StatsTotal.Text = StatsAvg.Text = StatsPeak.Text = "—";
            StatsRange.Text = "";
            DrawChart();
            return;
        }
        var totals = st.DayTotals;
        double total = st.Total;
        int pk = Array.IndexOf(totals, totals.Max());
        StatsTotal.Text = J.Bytes(total);
        StatsAvg.Text = J.Bytes(total / st.Days.Count);
        StatsPeak.Text = $"{J.Bytes(totals[pk])} · {Day(st.Days[pk])}";
        StatsRange.Text = $"{Day(st.Days[0])} – {Day(st.Days[^1])}";

        for (int i = 0; i < st.Series.Count; i++)
        {
            var s = st.Series[i];
            double share = total > 0 ? s.Total / total : 0;
            var brush = NodeBrush(i);
            var row = new Grid { Height = 34, Margin = new Thickness(0, 0, 0, 5) };
            row.Children.Add(new Border { Background = (Brush)FindResource("BgAlt"), CornerRadius = new CornerRadius(5) });
            var bar = new Grid();
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(share, 0.004), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - share, 0.0001), GridUnitType.Star) });
            bar.Children.Add(new Border { Background = brush, Opacity = 0.35, CornerRadius = new CornerRadius(5) });
            row.Children.Add(bar);

            var line = new DockPanel { Margin = new Thickness(12, 0, 12, 0) };
            var val = new TextBlock { Text = $"{J.Bytes(s.Total)}  ·  {share.ToString("P1", Ru)}", VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(val, Dock.Right);
            line.Children.Add(val);
            var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (s.Country != "") name.Inlines.Add(new System.Windows.Documents.Run(s.Country.ToUpperInvariant() + "  ") { Foreground = brush, FontWeight = FontWeights.Bold });
            name.Inlines.Add(new System.Windows.Documents.Run(s.Name));
            line.Children.Add(name);
            row.Children.Add(line);
            StatsRank.Children.Add(row);
        }
        DrawChart();
    }

    void StatsChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    // «Круглый» верх шкалы в единицах, которые показывает J.Bytes (ГБ, ТБ…): 103 ГБ -> 120 ГБ
    static double NiceMax(double v)
    {
        if (v <= 0) return 1;
        double unit = Math.Pow(1024, Math.Max(0, Math.Floor(Math.Log(v, 1024))));
        double x = v / unit, mag = Math.Pow(10, Math.Floor(Math.Log10(x)));
        double m = x / mag;
        foreach (var step in new[] { 1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
            if (m <= step + 1e-9) return step * mag * unit;
        return 10 * mag * unit;
    }

    void DrawChart()
    {
        var c = StatsChart;
        if (c == null) return;
        c.Children.Clear();
        var st = _stats;
        if (st == null || st.Series.Count == 0 || c.ActualWidth < 120 || c.ActualHeight < 100) return;

        var dim = (Brush)FindResource("Dim");
        var lineBrush = (Brush)FindResource("Line");
        int n = st.Days.Count;
        double left = 62, right = 6, top = 22, bottom = 26;
        double w = c.ActualWidth - left - right, h = c.ActualHeight - top - bottom;
        var totals = st.DayTotals;
        double max = NiceMax(totals.Max());

        // сетка и шкала
        for (int g = 0; g <= 4; g++)
        {
            double y = top + h - h * g / 4;
            var ln = new Line { X1 = left, X2 = left + w, Y1 = y, Y2 = y, Stroke = lineBrush, StrokeThickness = 1 };
            if (g > 0) ln.StrokeDashArray = new DoubleCollection { 3, 3 };
            c.Children.Add(ln);
            var lb = new TextBlock { Text = g == 0 ? "0" : J.Bytes(max * g / 4), Foreground = dim, FontSize = 10 };
            Canvas.SetLeft(lb, 0); Canvas.SetTop(lb, y - 7);
            c.Children.Add(lb);
        }

        double slot = w / n, bw = Math.Min(slot * 0.62, 90);
        int labelStep = Math.Max(1, (int)Math.Ceiling(46 / slot));
        for (int d = 0; d < n; d++)
        {
            double x = left + slot * d + (slot - bw) / 2;

            // подсказка по дню: итог и вклад каждой ноды
            var tip = new StringBuilder($"{Day(st.Days[d])} — {J.Bytes(totals[d])}");
            foreach (var s in st.Series)
                if (s.Daily[d] > 0) tip.Append($"\n{s.Name}: {J.Bytes(s.Daily[d])}");

            // стек: самая «тяжёлая» нода внизу
            double y = top + h;
            for (int i = 0; i < st.Series.Count; i++)
            {
                double v = st.Series[i].Daily[d];
                if (v <= 0) continue;
                double sh = Math.Max(v / max * h, 1);
                y -= sh;
                var r = new Rectangle { Width = bw, Height = sh, Fill = NodeBrush(i), ToolTip = tip.ToString() };
                Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
                c.Children.Add(r);
            }

            if (slot >= 54 && totals[d] > 0)
            {
                var tv = new TextBlock { Text = J.Bytes(totals[d]), Foreground = dim, FontSize = 10, Width = slot, TextAlignment = TextAlignment.Center };
                Canvas.SetLeft(tv, left + slot * d); Canvas.SetTop(tv, y - 16);
                c.Children.Add(tv);
            }
            if (d % labelStep == 0 || d == n - 1)
            {
                var dl = new TextBlock { Text = Day(st.Days[d]), Foreground = dim, FontSize = 10, Width = Math.Max(slot, 46), TextAlignment = TextAlignment.Center };
                Canvas.SetLeft(dl, left + slot * d + slot / 2 - dl.Width / 2); Canvas.SetTop(dl, top + h + 6);
                c.Children.Add(dl);
            }
        }
    }
}
