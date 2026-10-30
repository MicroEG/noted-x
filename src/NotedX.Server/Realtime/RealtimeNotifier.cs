using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace NotedX.Server.Realtime;

/// <summary>Pushes domain events to SignalR clients without letting delivery failures break requests.</summary>
public sealed class RealtimeNotifier
{
    private readonly IHubContext<NotedXHub> _hub;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RealtimeNotifier> _logger;

    /// <summary>Creates the notifier.</summary>
    public RealtimeNotifier(
        IHubContext<NotedXHub> hub,
        TimeProvider timeProvider,
        ILogger<RealtimeNotifier> logger)
    {
        _hub = hub;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Raised in-process so the desktop UI can react without a SignalR client.</summary>
    public event EventHandler<PageChangedEvent>? PageChanged;

    /// <summary>Raised in-process for every accepted upload chunk.</summary>
    public event EventHandler<TransferProgressEvent>? TransferProgress;

    /// <summary>Raised in-process when an upload becomes an attachment.</summary>
    public event EventHandler<TransferCompletedEvent>? TransferCompleted;

    /// <summary>Broadcasts a page change to every connected device.</summary>
    public async Task PageChangedAsync(Guid pageId, string change, CancellationToken cancellationToken = default)
    {
        var payload = new PageChangedEvent(pageId, change, _timeProvider.GetUtcNow());
        Raise(PageChanged, payload, nameof(PageChanged));
        await SendAsync("PageChanged", payload, cancellationToken);
    }

    /// <summary>Broadcasts upload progress.</summary>
    public async Task TransferProgressAsync(
        Guid uploadId,
        string fileName,
        long receivedBytes,
        long totalBytes,
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        var payload = new TransferProgressEvent(
            uploadId,
            fileName,
            receivedBytes,
            totalBytes,
            deviceName,
            _timeProvider.GetUtcNow());

        Raise(TransferProgress, payload, nameof(TransferProgress));
        await SendAsync("TransferProgress", payload, cancellationToken);
    }

    /// <summary>Broadcasts a completed upload.</summary>
    public async Task TransferCompletedAsync(
        Guid uploadId,
        Guid attachmentId,
        Guid? pageId,
        string fileName,
        long sizeBytes,
        string contentHash,
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        var payload = new TransferCompletedEvent(
            uploadId,
            attachmentId,
            pageId,
            fileName,
            sizeBytes,
            contentHash,
            deviceName,
            _timeProvider.GetUtcNow());

        Raise(TransferCompleted, payload, nameof(TransferCompleted));
        await SendAsync("TransferCompleted", payload, cancellationToken);
    }

    private void Raise<TEvent>(EventHandler<TEvent>? handlers, TEvent payload, string eventName)
        where TEvent : class
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<TEvent>>())
        {
            try
            {
                handler(this, payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "An in-process {RealtimeEvent} handler failed.", eventName);
            }
        }
    }

    private async Task SendAsync(string method, object payload, CancellationToken cancellationToken)
    {
        try
        {
            await _hub.Clients.All.SendAsync(method, payload, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The caller or connection went away; nothing useful to do.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not broadcast {RealtimeMethod}.", method);
        }
    }
}
