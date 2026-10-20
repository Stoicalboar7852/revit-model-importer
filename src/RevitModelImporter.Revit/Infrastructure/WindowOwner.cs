using System.Windows;
using System.Windows.Interop;

namespace RevitModelImporter.Revit.Infrastructure;

internal static class WindowOwner
{
    /// <summary>Parents a WPF window to a native window (Revit's main window) so it is modal to it,
    /// stays on top of it and minimises with it.</summary>
    public static T OwnedBy<T>(this T window, IntPtr owner)
        where T : Window
    {
        if (owner != IntPtr.Zero)
        {
            new WindowInteropHelper(window).Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return window;
    }
}
