using System;
using Microsoft.Toolkit.Uwp.Notifications;

namespace RemnawaveMonitor;

// Уведомления Windows нового вида: остаются в центре уведомлений, с кнопками.
// Если система их не поддерживает или что-то пошло не так — программа показывает старые всплывающие уведомления.
public static class Toast
{
    // действие по нажатию: "open", "journal", "user:<ключ>"
    public static Action<string>? Activated;
    static bool _init;

    public static void Init()
    {
        if (_init) return;
        _init = true;
        try
        {
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                var args = ToastArguments.Parse(e.Argument);
                Activated?.Invoke(args.TryGetValue("action", out var a) ? a : "open");
            };
        }
        catch { }
    }

    public static bool Show(string text, bool incident, string? userKey = null)
    {
        try
        {
            Init();
            var b = new ToastContentBuilder()
                .AddArgument("action", incident ? "journal" : userKey != null ? "user:" + userKey : "open")
                .AddText(incident ? "Remnawave — проблема" : "Remnawave")
                .AddText(text);
            if (incident) b.AddButton(new ToastButton().SetContent("Журнал").AddArgument("action", "journal"));
            if (userKey != null) b.AddButton(new ToastButton().SetContent("Карточка").AddArgument("action", "user:" + userKey));
            b.AddButton(new ToastButton().SetContent("Открыть").AddArgument("action", "open"));
            b.Show(t => { t.Group = "remnawave"; t.ExpirationTime = DateTimeOffset.Now.AddDays(2); });
            return true;
        }
        catch { return false; }
    }
}
