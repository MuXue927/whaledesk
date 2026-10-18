namespace WhaleDesk;

/// <summary>
/// One pet per session, plus a way back.
///
/// The first version simply refused to start a second instance, which turned
/// "hide" into a dead end: the hidden window could not be summoned by launching
/// the app again (the user reported exactly this). Now a second launch raises a
/// named event that the running instance listens for and answers by showing
/// itself.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\WhaleDesk.SingleInstance";
    private const string ShowEventName = @"Local\WhaleDesk.ShowRequest";
    private const string QuitEventName = @"Local\WhaleDesk.QuitRequest";

    private readonly Mutex _mutex;
    private EventWaitHandle? _event;
    private EventWaitHandle? _quitEvent;
    private Thread? _listener;
    private volatile bool _stopping;

    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsFirst = createdNew;
    }

    /// <summary>Background wait on the show-request event; the callback runs on the caller's thread.</summary>
    public void ListenForShowRequest(Action onShowRequest, Log log, Action? onQuitRequest = null)
    {
        try
        {
            _event = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        }
        catch (Exception ex)
        {
            log.Warn("could not create the show-request event: " + ex.Message);
            return;
        }
        _listener = new Thread(() =>
        {
            while (!_stopping)
            {
                try
                {
                    if (_event.WaitOne(200)) onShowRequest();
                    if (_quitEvent is not null && _quitEvent.WaitOne(200)) onQuitRequest?.Invoke();
                }
                catch (Exception ex)
                {
                    log.Warn("show-request listener error: " + ex.Message);
                    Thread.Sleep(500);
                }
            }
        })
        { IsBackground = true, Name = "WhaleDesk.ShowRequest" };
        _listener.Start();
        log.Info("listening for show requests (launching the app again summons the pet)");
    }

    /// <summary>Called by a second instance: returns true when a running instance answered.</summary>
    public static bool SignalRunningInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ShowEventName, out var handle)) return false;
            using (handle)
            {
                handle.Set();
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Called by a second instance started with --quit.</summary>
    public static bool SignalQuit()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var handle)) return false;
            using (handle)
            {
                handle.Set();
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        try { _listener?.Join(500); } catch { }
        try { _event?.Dispose(); } catch { }
        try { _quitEvent?.Dispose(); } catch { }
        try { _mutex.Dispose(); } catch { }
    }
}
