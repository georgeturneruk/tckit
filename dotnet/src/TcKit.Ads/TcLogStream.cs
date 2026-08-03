using TwinCAT.Ads;

namespace TcKit.Ads;

/// <summary>Streams a target's TwinCAT message log (the LogView stream: system service messages,
/// licence warnings, PLC <c>ADSLOGSTR</c> output) over ADS. Subscribes a device notification on the
/// logger service (AMS port 100, index group 1 / offset 0xFFFF, 1024-byte payload — the established
/// community mechanism) and keeps the parsed <see cref="AdsLogEntry"/> lines in a capped ring buffer
/// for poll-style consumers, alongside a push event for reactive ones.
///
/// Long-lived, one per target. A restart of the target silently kills the server-side notification,
/// so the owner calls <see cref="EnsureSubscribed"/> once per poll: it probes the logger port and
/// rebuilds the subscription when the target stopped answering, and precautionarily rebuilds after
/// a long quiet spell in case a restart fell entirely between two polls. Never throws; an
/// unreachable target just retries on the next poll.</summary>
public sealed class TcLogStream : IDisposable
{
    private const uint LoggerIndexGroup = 0x0001;
    private const uint LoggerIndexOffset = 0xFFFF;
    private const int NotificationLength = 1024;
    private const int DefaultCapacity = 250;
    private const int TimeoutMs = 2000;
    private static readonly TimeSpan DefaultRebuildAfterQuiet = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(10);

    private readonly AmsNetId _netId;
    private readonly int _capacity;
    private readonly TimeSpan _rebuildAfterQuiet;

    // Two locks, never nested: connection lifecycle vs the line buffer, so a notification landing
    // mid-rebuild only ever waits on the buffer lock.
    private readonly object _clientSync = new();
    private readonly object _linesSync = new();
    private readonly Queue<AdsLogEntry> _lines;

    private AdsClient? _client;
    private uint _handle;
    private DateTime _quietSinceUtc;
    private DateTime _nextAttemptUtc;

    /// <summary>Raised on the ADS notification thread for each received log line.</summary>
    public event Action<AdsLogEntry>? LogReceived;

    public TcLogStream(string netId, int capacity = DefaultCapacity, TimeSpan? rebuildAfterQuiet = null)
    {
        _netId = new AmsNetId(netId);
        _capacity = Math.Max(1, capacity);
        _rebuildAfterQuiet = rebuildAfterQuiet ?? DefaultRebuildAfterQuiet;
        _lines = new Queue<AdsLogEntry>(_capacity);
    }

    /// <summary>The subscription is currently registered (as far as the last
    /// <see cref="EnsureSubscribed"/> could tell).</summary>
    public bool Subscribed
    {
        get
        {
            lock (_clientSync)
            {
                return _client is not null;
            }
        }
    }

    /// <summary>Make the subscription live, (re)building it as needed. Call once per poll cycle.
    /// Returns whether the subscription is registered. Never throws.</summary>
    public bool EnsureSubscribed()
    {
        lock (_clientSync)
        {
            if (_client is not null)
            {
                // A target restart tears down the server-side notification without telling us. The
                // down window (TwinCAT takes tens of seconds to come back) is far longer than a
                // poll interval, so a dead probe catches nearly every restart; the quiet-spell
                // rebuild is the backstop for one that fell entirely between two polls.
                if (ProbeAlive(_client) && DateTime.UtcNow - _quietSinceUtc < _rebuildAfterQuiet)
                {
                    return true;
                }

                TeardownLocked();
            }
            else if (DateTime.UtcNow < _nextAttemptUtc)
            {
                // The last attempt failed against a dead target; registering times out (~2 s), so
                // don't pay that on every poll — back off between attempts.
                return false;
            }

            AdsClient? client = null;
            try
            {
                client = new AdsClient { Timeout = TimeoutMs };
                client.Connect(_netId, (int)AmsPort.Logger);
                client.AdsNotification += OnNotification;
                _handle = client.AddDeviceNotification(
                    LoggerIndexGroup, LoggerIndexOffset, NotificationLength,
                    new NotificationSettings(AdsTransMode.Cyclic, 0, 0), userData: null!);
                _client = client;
                _quietSinceUtc = DateTime.UtcNow;
                return true;
            }
#pragma warning disable CA1031 // Target down or unreachable; retry after the backoff.
            catch (Exception)
#pragma warning restore CA1031
            {
                client?.Dispose();
                _client = null;
                _handle = 0;
                _nextAttemptUtc = DateTime.UtcNow + RetryBackoff;
                return false;
            }
        }
    }

    /// <summary>The buffered lines, newest first.</summary>
    public IReadOnlyList<AdsLogEntry> Snapshot()
    {
        lock (_linesSync)
        {
            var lines = _lines.ToArray();
            Array.Reverse(lines);
            return lines;
        }
    }

    public void Dispose()
    {
        lock (_clientSync)
        {
            TeardownLocked();
        }
    }

    /// <summary>Buffer one parsed line and raise the event. The seam the notification handler
    /// feeds, kept separate so the ring-buffer semantics are testable.</summary>
    internal void Accept(AdsLogEntry entry)
    {
        // A too-short payload parses to an all-default entry; not worth a row.
        if (entry.Message.Length == 0 && entry.Source.Length == 0)
        {
            return;
        }

        lock (_linesSync)
        {
            _lines.Enqueue(entry);
            while (_lines.Count > _capacity)
            {
                _lines.Dequeue();
            }
        }

        _quietSinceUtc = DateTime.UtcNow;

        try
        {
            LogReceived?.Invoke(entry);
        }
#pragma warning disable CA1031 // A throwing consumer must not kill the dispatcher.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Consumer's problem, not the stream's.
        }
    }

    /// <summary>The target answered on the logger port — even an ADS error code is an answer (it
    /// came from the target's router). Only no-answer-at-all (route down, TwinCAT restarting,
    /// timeout) reads as dead.</summary>
    private static bool ProbeAlive(AdsClient client)
    {
        try
        {
            var code = client.TryReadState(out _);
            return code is AdsErrorCode.NoError or AdsErrorCode.DeviceServiceNotSupported;
        }
#pragma warning disable CA1031 // No answer at all reads as dead.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private void OnNotification(object? sender, AdsNotificationEventArgs e)
    {
        AdsLogEntry entry;
        try
        {
            entry = AdsLogEntry.Parse(e.Data.Span);
        }
#pragma warning disable CA1031 // One malformed line must not tear down the subscription.
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        Accept(entry);
    }

    private void TeardownLocked()
    {
        if (_client is { } client)
        {
            try
            {
                client.AdsNotification -= OnNotification;
                if (_handle != 0 && client.IsConnected)
                {
                    client.DeleteDeviceNotification(_handle);
                }
            }
#pragma warning disable CA1031 // Best-effort: the target may already be gone.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Nothing left to unsubscribe.
            }

            client.Dispose();
        }

        _client = null;
        _handle = 0;
    }
}
