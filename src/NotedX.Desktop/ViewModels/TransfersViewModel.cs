using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NotedX.Server;
using NotedX.Server.Realtime;
using NotedX.Desktop.Services;

namespace NotedX.Desktop.ViewModels;

/// <summary>Tracks uploads sent by paired phones and keeps speed, ETA, and completion state.</summary>
public sealed partial class TransfersViewModel : ObservableObject, IDisposable
{
    private readonly NotedXWebServer _server;
    private readonly ToastService _toasts;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TransfersViewModel> _logger;
    private readonly Dictionary<Guid, TransferSample> _samples = new();
    private readonly CancellationTokenSource _disposeCancellation = new();

    private bool _disposed;

    [ObservableProperty]
    private int _activeCount;

    [ObservableProperty]
    private bool _hasItems;

    [ObservableProperty]
    private bool _hasActive;

    /// <summary>Creates the live transfer list.</summary>
    public TransfersViewModel(
        NotedXWebServer server,
        ToastService toasts,
        TimeProvider timeProvider,
        ILogger<TransfersViewModel> logger)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _server.Realtime.TransferProgress += OnTransferProgress;
        _server.Realtime.TransferCompleted += OnTransferCompleted;
    }

    /// <summary>Uploads that are running or recently finished.</summary>
    public ObservableCollection<TransferItemViewModel> Items { get; } = new();

    /// <summary>Removes the finished uploads from the list.</summary>
    [RelayCommand]
    private void ClearCompleted()
    {
        foreach (var item in Items.Where(item => item.IsCompleted).ToArray())
        {
            Items.Remove(item);
        }

        RaiseSummary();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _server.Realtime.TransferProgress -= OnTransferProgress;
        _server.Realtime.TransferCompleted -= OnTransferCompleted;
        _disposeCancellation.Cancel();
        _disposeCancellation.Dispose();
    }

    private void OnTransferProgress(object? sender, TransferProgressEvent progress) =>
        Dispatch(() => ApplyProgress(progress));

    private void OnTransferCompleted(object? sender, TransferCompletedEvent completed) =>
        Dispatch(() => ApplyCompleted(completed));

    private void ApplyProgress(TransferProgressEvent progress)
    {
        if (_disposed || progress.TotalBytes <= 0)
        {
            return;
        }

        var item = FindOrCreate(progress.UploadId, progress.FileName, progress.DeviceName, progress.TotalBytes);
        var speed = UpdateSpeed(progress.UploadId, progress.ReceivedBytes);
        var remaining = Math.Max(0, progress.TotalBytes - progress.ReceivedBytes);
        var eta = speed > 1 ? TimeSpan.FromSeconds(remaining / speed) : (TimeSpan?)null;

        item.ApplyProgress(progress.ReceivedBytes, progress.TotalBytes, speed, eta);
        RaiseSummary();
    }

    private void ApplyCompleted(TransferCompletedEvent completed)
    {
        if (_disposed)
        {
            return;
        }

        var item = FindOrCreate(completed.UploadId, completed.FileName, completed.DeviceName, completed.SizeBytes);
        item.MarkCompleted(completed.SizeBytes);
        _samples.Remove(completed.UploadId);

        _toasts.ShowSuccess(
            $"{completed.FileName} was received from {completed.DeviceName}.",
            "File received");

        RaiseSummary();
        _ = RemoveAfterDelayAsync(item, TimeSpan.FromSeconds(12), _disposeCancellation.Token);
    }

    private async Task RemoveAfterDelayAsync(
        TransferItemViewModel item,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Dispatch(() =>
        {
            Items.Remove(item);
            RaiseSummary();
        });
    }

    private TransferItemViewModel FindOrCreate(Guid uploadId, string fileName, string deviceName, long totalBytes)
    {
        var existing = Items.FirstOrDefault(item => item.UploadId == uploadId);
        if (existing is not null)
        {
            return existing;
        }

        var item = new TransferItemViewModel(uploadId, fileName, deviceName, totalBytes);
        Items.Insert(0, item);

        while (Items.Count > 12)
        {
            var completedItem = Items.LastOrDefault(candidate => candidate.IsCompleted);
            Items.Remove(completedItem ?? Items[^1]);
        }

        return item;
    }

    private double UpdateSpeed(Guid uploadId, long receivedBytes)
    {
        var now = _timeProvider.GetTimestamp();
        var previousSpeed = Items.FirstOrDefault(item => item.UploadId == uploadId)?.Speed ?? 0d;

        if (!_samples.TryGetValue(uploadId, out var sample))
        {
            _samples[uploadId] = new TransferSample(receivedBytes, now);
            return 0d;
        }

        var seconds = _timeProvider.GetElapsedTime(sample.Timestamp, now).TotalSeconds;
        _samples[uploadId] = new TransferSample(receivedBytes, now);

        if (seconds < 0.05)
        {
            return previousSpeed;
        }

        var instant = Math.Max(0d, receivedBytes - sample.Bytes) / seconds;
        return previousSpeed <= 0d ? instant : (previousSpeed * 0.6d) + (instant * 0.4d);
    }

    private void RaiseSummary()
    {
        ActiveCount = Items.Count(item => !item.IsCompleted);
        HasItems = Items.Count > 0;
        HasActive = ActiveCount > 0;
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private readonly record struct TransferSample(long Bytes, long Timestamp);
}

/// <summary>One upload shown in the transfers panel.</summary>
public sealed class TransferItemViewModel : ObservableObject
{
    private long _receivedBytes;
    private long _totalBytes;
    private double _progress;
    private double _speed;
    private string _speedText = string.Empty;
    private string _etaText = string.Empty;
    private string _progressText = string.Empty;
    private bool _isCompleted;

    /// <summary>Creates an upload entry.</summary>
    public TransferItemViewModel(Guid uploadId, string fileName, string deviceName, long totalBytes)
    {
        UploadId = uploadId;
        FileName = string.IsNullOrWhiteSpace(fileName) ? "Upload" : fileName;
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Paired device" : deviceName;
        _totalBytes = Math.Max(0, totalBytes);
        _progressText = $"0 B of {ByteFormatter.FormatSize(_totalBytes)}";
    }

    /// <summary>Upload identifier.</summary>
    public Guid UploadId { get; }

    /// <summary>File name reported by the phone.</summary>
    public string FileName { get; }

    /// <summary>Paired device that sent the upload.</summary>
    public string DeviceName { get; }

    /// <summary>Bytes received so far.</summary>
    public long ReceivedBytes
    {
        get => _receivedBytes;
        private set => SetProperty(ref _receivedBytes, value);
    }

    /// <summary>Total upload size.</summary>
    public long TotalBytes
    {
        get => _totalBytes;
        private set => SetProperty(ref _totalBytes, value);
    }

    /// <summary>Completion percentage from zero to one hundred.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    /// <summary>Smoothed transfer speed in bytes per second.</summary>
    public double Speed
    {
        get => _speed;
        private set => SetProperty(ref _speed, value);
    }

    /// <summary>Formatted speed.</summary>
    public string SpeedText
    {
        get => _speedText;
        private set => SetProperty(ref _speedText, value);
    }

    /// <summary>Formatted time remaining.</summary>
    public string EtaText
    {
        get => _etaText;
        private set => SetProperty(ref _etaText, value);
    }

    /// <summary>Formatted byte counters.</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>Whether the upload finished.</summary>
    public bool IsCompleted
    {
        get => _isCompleted;
        private set
        {
            if (SetProperty(ref _isCompleted, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>Short status line.</summary>
    public string StatusText => IsCompleted ? $"Received from {DeviceName}" : $"From {DeviceName}";

    /// <summary>Updates progress, speed, and estimated time remaining.</summary>
    public void ApplyProgress(long receivedBytes, long totalBytes, double speed, TimeSpan? eta)
    {
        if (IsCompleted)
        {
            return;
        }

        TotalBytes = Math.Max(0, totalBytes);
        ReceivedBytes = Math.Clamp(receivedBytes, 0, TotalBytes);
        Progress = TotalBytes <= 0 ? 0 : (ReceivedBytes * 100d) / TotalBytes;
        Speed = speed;
        SpeedText = speed > 0 ? ByteFormatter.FormatSpeed(speed) : "Starting…";
        EtaText = eta is { } remaining ? FormatEta(remaining) : string.Empty;
        ProgressText = $"{ByteFormatter.FormatSize(ReceivedBytes)} of {ByteFormatter.FormatSize(TotalBytes)}";
    }

    /// <summary>Marks the upload as received.</summary>
    public void MarkCompleted(long sizeBytes)
    {
        TotalBytes = Math.Max(sizeBytes, ReceivedBytes);
        ReceivedBytes = TotalBytes;
        Progress = 100;
        Speed = 0;
        SpeedText = string.Empty;
        EtaText = string.Empty;
        ProgressText = ByteFormatter.FormatSize(TotalBytes);
        IsCompleted = true;
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalHours >= 1)
        {
            return $"{(int)eta.TotalHours}h {eta.Minutes}m left";
        }

        if (eta.TotalMinutes >= 1)
        {
            return $"{eta.Minutes}m {eta.Seconds}s left";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(eta.TotalSeconds))}s left";
    }
}
