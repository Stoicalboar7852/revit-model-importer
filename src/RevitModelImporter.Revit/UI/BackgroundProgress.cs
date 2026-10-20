using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using RevitModelImporter.Revit.Import;

namespace RevitModelImporter.Revit.UI;

/// <summary>
/// Progress window on its own UI thread, used while elements are created: the Revit thread is busy in the API
/// and cannot repaint, but this window stays responsive and its Cancel button sets a flag the importer checks
/// between elements.
///
/// This is the one window that is deliberately NOT owned by Revit's main window: a cross-thread owner relation
/// attaches the two threads' input queues, which would freeze this window (and its Cancel button) while Revit's
/// thread is busy. It is centred on Revit's window and kept topmost instead.
/// </summary>
internal sealed class BackgroundProgress : IImportProgress, IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private Dispatcher? _dispatcher;
    private ProgressWindow? _window;
    private volatile bool _cancel;
    private long _lastReport;

    public BackgroundProgress(string heading, IntPtr revitWindow)
    {
        var bounds = GetBounds(revitWindow);
        _thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            var window = new ProgressWindow(heading) { Topmost = true, WindowStartupLocation = WindowStartupLocation.Manual };
            window.CancelRequested += (_, _) =>
            {
                _cancel = true;
                window.ShowCancelling("Cancelling… everything created so far is being removed.");
            };
            window.SourceInitialized += (_, _) =>
            {
                if (bounds is { } b)
                {
                    window.Left = b.Left + (b.Width - window.ActualWidth) / 2;
                    window.Top = b.Top + (b.Height - Math.Max(window.ActualHeight, 140)) / 2;
                }
            };
            _window = window;
            window.Show();
            _ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Revit Model Importer progress",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(10));
    }

    public bool IsCancellationRequested => _cancel;

    public void Report(double fraction, string message)
    {
        // Throttle cross-thread updates to ~10 per second.
        var now = Stopwatch.GetTimestamp();
        if (now - Interlocked.Read(ref _lastReport) < Stopwatch.Frequency / 10 && fraction < 1)
        {
            return;
        }

        Interlocked.Exchange(ref _lastReport, now);
        _dispatcher?.BeginInvoke(() =>
        {
            if (!_cancel)
            {
                _window?.Update(message, Math.Max(fraction, 0.001));
            }
        });
    }

    public void Dispose()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
        {
            dispatcher.Invoke(() =>
            {
                if (_window is not null)
                {
                    _window.AllowClose = true;
                    _window.Close();
                }
            });
            dispatcher.InvokeShutdown();
        }

        _thread.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }

    private static Rect? GetBounds(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r))
        {
            return null;
        }

        // Window rect is in physical pixels; WPF uses device-independent units.
        var scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0)
        {
            scale = 1;
        }

        return new Rect(r.Left / scale, r.Top / scale, (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
