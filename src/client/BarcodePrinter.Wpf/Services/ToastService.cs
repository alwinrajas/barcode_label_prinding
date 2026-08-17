using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace BarcodePrinter.Wpf.Services;

public enum ToastKind { Success, Error, Warning, Info }

/// <summary>One transient notification. Reference carries the server's support
/// reference for errors, so the operator can quote it verbatim.</summary>
public sealed record Toast(ToastKind Kind, string Message, string? Reference = null);

/// <summary>
/// App-wide transient notifications, rendered by the shell's ToastHost.
/// Exposed as a static Instance so ViewModels can raise toasts this phase
/// without every constructor being rewired; also registered in DI.
/// </summary>
public sealed class ToastService
{
    private const int MaxVisible = 4;

    public static ToastService Instance { get; } = new();

    public ObservableCollection<Toast> Toasts { get; } = [];

    public void Success(string message) => Show(new Toast(ToastKind.Success, message), TimeSpan.FromSeconds(5));

    public void Error(string message, string? reference = null) =>
        Show(new Toast(ToastKind.Error, message, reference), TimeSpan.FromSeconds(8));

    public void Warning(string message) => Show(new Toast(ToastKind.Warning, message), TimeSpan.FromSeconds(5));

    public void Info(string message) => Show(new Toast(ToastKind.Info, message), TimeSpan.FromSeconds(5));

    public void Dismiss(Toast toast)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Toasts.Remove(toast);
        }
        else
        {
            dispatcher.BeginInvoke(() => Toasts.Remove(toast));
        }
    }

    private void Show(Toast toast, TimeSpan lifetime)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;   // no UI (e.g. unit tests) — nothing to show
        }
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ShowCore(toast, lifetime));
            return;
        }
        ShowCore(toast, lifetime);
    }

    private void ShowCore(Toast toast, TimeSpan lifetime)
    {
        while (Toasts.Count >= MaxVisible)
        {
            Toasts.RemoveAt(0);   // oldest first — newest information wins
        }
        Toasts.Add(toast);

        var timer = new DispatcherTimer { Interval = lifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Toasts.Remove(toast);
        };
        timer.Start();
    }
}
