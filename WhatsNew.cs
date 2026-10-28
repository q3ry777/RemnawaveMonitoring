using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace RemnawaveMonitor;

// Список изменений (CHANGELOG.md встроен в программу): разделы «## ГГГГ-ММ-ДД» и строки «- …»
public static class Changelog
{
    public record Entry(string Date, List<string> Items);

    public static List<Entry> Load()
    {
        try
        {
            using var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
            if (st == null) return new();
            using var r = new StreamReader(st);
            var list = new List<Entry>();
            Entry? cur = null;
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith("## ")) { cur = new Entry(line[3..].Trim(), new()); list.Add(cur); }
                else if (line.StartsWith("- ") && cur != null) cur.Items.Add(line[2..].Trim());
            }
            return list.Where(e => e.Items.Count > 0).OrderByDescending(e => e.Date, StringComparer.Ordinal).ToList();
        }
        catch { return new(); }
    }

    // После обновления — показать новое с прошлого раза. Первый запуск вообще — только запомнить.
    public static void ShowIfNew(Settings s, bool updated, Window? owner)
    {
        var all = Load();
        if (all.Count == 0) return;
        var newest = all[0].Date;
        List<Entry> fresh;
        if (s.LastChangelog == null) fresh = updated ? all.Take(1).ToList() : new();
        else fresh = all.Where(e => string.CompareOrdinal(e.Date, s.LastChangelog) > 0).ToList();
        if (s.LastChangelog != newest) { s.LastChangelog = newest; s.Save(); }
        if (fresh.Count > 0) WhatsNew.Show(owner, fresh, $"что нового в версии {Updater.CurrentText}");
    }
}

// Окно «что нового» — собирается в коде, в стиле программы
public static class WhatsNew
{
    public static void Show(Window? owner, List<Changelog.Entry> entries, string title)
    {
        var w = new Window
        {
            Title = title, Width = 620, Height = 520, MinWidth = 420, MinHeight = 300,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            FontSize = 13, UseLayoutRounding = true,
        };
        if (owner is { IsVisible: true }) w.Owner = owner;
        w.SetResourceReference(Window.BackgroundProperty, "Bg");
        w.SetResourceReference(Window.ForegroundProperty, "Fg");
        if (Application.Current.TryFindResource("Mono") is System.Windows.Media.FontFamily f) w.FontFamily = f;
        w.SourceInitialized += (_, _) => WinChrome.Apply(w);

        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 18) };
        var head = new TextBlock { FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 14) };
        head.Inlines.Add(new System.Windows.Documents.Run(title));
        var us = new System.Windows.Documents.Run("_");
        us.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Amber");
        head.Inlines.Add(us);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var ok = new Button { Content = "понятно", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        if (Application.Current.TryFindResource("BtnPrimary") is Style bs) ok.Style = bs;
        ok.Click += (_, _) => w.Close();
        DockPanel.SetDock(ok, Dock.Bottom);
        root.Children.Add(ok);

        var list = new StackPanel();
        foreach (var e in entries)
        {
            var d = DateTime.TryParse(e.Date, out var dt) ? dt.ToString("dd.MM.yyyy") : e.Date;
            var h = new TextBlock { Text = d, FontSize = 11, Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 14, 0, 6) };
            h.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            list.Children.Add(h);
            foreach (var it in e.Items)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var dot = new TextBlock { Text = "•", Margin = new Thickness(0, 0, 8, 0) };
                dot.SetResourceReference(TextBlock.ForegroundProperty, "Amber");
                row.Children.Add(dot);
                row.Children.Add(new TextBlock { Text = it, TextWrapping = TextWrapping.Wrap });
                list.Children.Add(row);
            }
        }
        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 14, 18, 14),
                                  Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        border.SetResourceReference(Border.BackgroundProperty, "Panel");
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        root.Children.Add(border);
        w.Content = root;
        w.Show();
    }
}
