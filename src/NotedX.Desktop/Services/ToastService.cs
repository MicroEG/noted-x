using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace NotedX.Desktop.Services;

/// <summary>Visual tone of a toast notification.</summary>
public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>A transient message shown in the lower right corner.</summary>
/// <param name="Id">Identifier used for dismissal.</param>
/// <param name="Kind">Visual tone.</param>
/// <param name="Title">Short heading.</param>
/// <param name="Message">Body text.</param>
public sealed record ToastItem(Guid Id, ToastKind Kind, string Title, string Message);

/// <summary>Queues short-lived, user-facing messages on the UI thread.</summary>
public sealed class ToastService : IDisposable
{
    private const int MaximumVisibleToasts = 4;
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);

    private readonly ILogger<ToastService> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<Guid, CancellationTokenSource> _dismissals = new();

    /// <summary>Creates the service bound to the WPF dispatcher.</summary>
    public ToastService(ILogger<ToastService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    /// <summary>Toasts currently visible in the shell.</summary>
    public ObservableCollection<ToastItem> Items { get; } = new();

    /// <summary>Shows an informational toast.</summary>
    public void ShowInfo(string message, string? title = null) =>
        Show(ToastKind.Info, message, title);

    /// <summary>Shows a success toast.</summary>
    public void ShowSuccess(string message, string? title = null) =>
        Show(ToastKind.Success, message, title);

    /// <summary>Shows a warning toast.</summary>
    public void ShowWarning(string message, string? title = null) =>
        Show(ToastKind.Warning, message, title);

    /// <summary>Shows an error toast.</summary>
    public void ShowError(string message, string? title = null) =>
        Show(ToastKind.Error, message, title);

    /// <summary>Shows a toast with the requested tone and duration.</summary>
    public void Show(
        ToastKind kind,
        string message,
        string? title = null,
        TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var cleanTitle = string.IsNullOrWhiteSpace(title) ? DefaultTitle(kind) : title.Trim();
        var cleanMessage = message.Trim();

        if (_dispatcher.CheckAccess())
        {
            Add(kind, cleanTitle, cleanMessage, duration ?? DefaultDuration);
        }
        else
        {
            _dispatcher.BeginInvoke(() => Add(kind, cleanTitle, cleanMessage, duration ?? DefaultDuration));
        }
    }

    /// <summary>Removes a toast immediately.</summary>
    public void Dismiss(Guid id)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => Dismiss(id));
            return;
        }

        if (_dismissals.Remove(id, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        var item = Items.FirstOrDefault(toast => toast.Id == id);
        if (item is not null)
        {
            Items.Remove(item);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var cancellation in _dismissals.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        _dismissals.Clear();
        Items.Clear();
    }

    private void Add(ToastKind kind, string title, string message, TimeSpan duration)
    {
        var item = new ToastItem(Guid.NewGuid(), kind, title, message);
        Items.Add(item);

        while (Items.Count > MaximumVisibleToasts)
        {
            Dismiss(Items[0].Id);
        }

        var cancellation = new CancellationTokenSource();
        _dismissals[item.Id] = cancellation;
        _ = DismissAfterAsync(item.Id, duration, cancellation.Token);
    }

    private async Task DismissAfterAsync(Guid id, TimeSpan duration, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A toast dismissal timer failed.");
            return;
        }

        _ = _dispatcher.BeginInvoke(() => Dismiss(id));
    }

    private static string DefaultTitle(ToastKind kind) => kind switch
    {
        ToastKind.Success => "Done",
        ToastKind.Warning => "Heads-up",
        ToastKind.Error => "Something went wrong",
        _ => "noted-x"
    };
}
