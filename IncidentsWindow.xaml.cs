using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace RemnawaveMonitor;

public partial class IncidentsWindow : Window
{
    readonly IncidentLog _log;
    readonly Action _ackAll;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    List<IncidentRow> _last = new();

    public IncidentsWindow(IncidentLog log, Action ackAll)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WinChrome.Apply(this);
        _log = log;
        _ackAll = ackAll;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    public void Refresh()
    {
        var rows = _log.Rows();
        SummaryText.Text = $"активно сейчас: {_log.Active}  ·  новых: {_log.Unread}  ·  всего: {rows.Count}";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.SequenceEqual(_last)) return;   // не трогаем список, если ничего не изменилось (сохраняется прокрутка)
        _last = rows;
        IncList.ItemsSource = rows;
    }

    void AckAll_Click(object sender, RoutedEventArgs e) { _ackAll(); Refresh(); }
    void Clear_Click(object sender, RoutedEventArgs e) { _log.ClearResolved(); Refresh(); }
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
