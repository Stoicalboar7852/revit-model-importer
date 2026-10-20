using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using RevitModelImporter.Core.WorkerProtocol;
using RevitModelImporter.Revit.Infrastructure;

namespace RevitModelImporter.Revit.UI;

/// <summary>Progress with a Cancel button. <see cref="Run{T}"/> shows it modally (owned by Revit) while an
/// asynchronous worker run is awaited; cancelling kills the worker process.</summary>
public partial class ProgressWindow : Window
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private bool _completed;

    public ProgressWindow(string heading)
    {
        InitializeComponent();
        HeadingText.Text = heading;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => ElapsedText.Text = $"{_elapsed.Elapsed:m\\:ss}", Dispatcher);
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    public event EventHandler? CancelRequested;

    /// <summary>Allows the window to close (otherwise closing counts as Cancel).</summary>
    public bool AllowClose
    {
        get => _completed;
        set => _completed = value;
    }

    public void Update(string? stage, double? fraction)
    {
        if (!string.IsNullOrWhiteSpace(stage))
        {
            StageText.Text = stage;
        }

        if (fraction is { } f && f > 0)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = Math.Clamp(f, 0, 1);
        }
    }

    public void ShowCancelling(string text)
    {
        CancelButton.IsEnabled = false;
        StageText.Text = text;
        Bar.IsIndeterminate = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_completed)
        {
            e.Cancel = true;
            RequestCancel();
        }

        base.OnClosing(e);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => RequestCancel();

    private void RequestCancel()
    {
        if (CancelButton.IsEnabled)
        {
            CancelButton.IsEnabled = false;
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Shows a modal progress window owned by <paramref name="owner"/> while <paramref name="work"/> runs.
    /// Throws <see cref="OperationCanceledException"/> if the user cancels; rethrows the work's exceptions.</summary>
    public static T Run<T>(IntPtr owner, string heading, Func<IProgress<WorkerProgress>, CancellationToken, Task<T>> work)
    {
        var window = new ProgressWindow(heading).OwnedBy(owner);
        using var cts = new CancellationTokenSource();
        T result = default!;
        ExceptionDispatchInfo? error = null;
        window.CancelRequested += (_, _) =>
        {
            window.ShowCancelling("Cancelling…");
            cts.Cancel();
        };
        window.Loaded += async (_, _) =>
        {
            var progress = new Progress<WorkerProgress>(p => window.Update(p.Message ?? p.Stage, p.Fraction));
            try
            {
                result = await work(progress, cts.Token).ConfigureAwait(true);
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                window._completed = true;
                window.Close();
            }
        };
        window.ShowDialog();
        error?.Throw();
        return result;
    }
}
