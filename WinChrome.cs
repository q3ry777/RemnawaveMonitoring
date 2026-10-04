using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RemnawaveMonitor;

// Красит системный заголовок окна в цвета текущей темы (цвет заголовка — Windows 11, тёмный режим — Windows 10 2004+).
static class WinChrome
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static int Rgb(Color c) => c.R | (c.G << 8) | (c.B << 16);
    static Color Get(string key) => Application.Current.Resources[key] is SolidColorBrush b ? b.Color : Colors.Black;

    public static void Apply(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        var bg = Get("Bg");
        int dark = (bg.R * 299 + bg.G * 587 + bg.B * 114) / 1000 < 128 ? 1 : 0;
        int cap = Rgb(bg), txt = Rgb(Get("Fg"));
        DwmSetWindowAttribute(h, 20, ref dark, 4); // тёмный режим
        DwmSetWindowAttribute(h, 35, ref cap, 4);  // цвет заголовка
        DwmSetWindowAttribute(h, 34, ref cap, 4);  // цвет рамки
        DwmSetWindowAttribute(h, 36, ref txt, 4);  // цвет текста заголовка
    }
}
