using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Lumen;

public static class Win
{
    [StructLayout(LayoutKind.Sequential)]
    struct Margins { public int L, R, T, B; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins m);

    public static bool Light() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is int i && i == 1;

    // Dark title bar + Mica on Windows 11; solid themed background on Windows 10.
    public static void Mica(Window w, bool dark)
    {
        var h = new WindowInteropHelper(w).Handle;
        int d = dark ? 1 : 0;
        DwmSetWindowAttribute(h, 20, ref d, 4);
        if (Environment.OSVersion.Version.Build >= 22621)
        {
            HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent;
            var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
            DwmExtendFrameIntoClientArea(h, ref m);
            int t = 2; // Mica
            DwmSetWindowAttribute(h, 38, ref t, 4);
            w.Background = Brushes.Transparent;
        }
        else
        {
            w.Background = (Brush)Application.Current.Resources["Bg"];
        }
    }
}
