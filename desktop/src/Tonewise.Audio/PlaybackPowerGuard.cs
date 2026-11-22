using System.Runtime.InteropServices;

namespace Tonewise.Audio;

public interface IPlaybackPowerGuard : IDisposable
{
    bool IsActive { get; }
    void Acquire();
    void ReleaseImmediate();
    void ScheduleDeferredRelease(TimeSpan delay);
}

internal interface IPowerRequestBackend : IDisposable
{
    bool SetActive(bool active);
}

/// <summary>
/// Holds Win32 kernel power requests (<c>PowerRequestExecutionRequired</c> and
/// <c>PowerRequestSystemRequired</c>) while audio is actively playing so Windows 11
/// Modern Standby (S0 Low Power Idle) / DAM does not suspend playback when the screen
/// turns off or locks, while still permitting the monitor itself to sleep.
/// </summary>
public sealed class WindowsPlaybackPowerGuard : IPlaybackPowerGuard
{
    private readonly Lock _lock = new();
    private readonly IPowerRequestBackend _backend;
    private readonly System.Threading.Timer _deferredReleaseTimer;
    private bool _isActive;
    private bool _disposed;
    private long _releaseGen;
    private long _scheduledReleaseGen;

    public static int ReasonContextSizeBytes => Marshal.SizeOf<REASON_CONTEXT>();

    public WindowsPlaybackPowerGuard(string reason = "Tonewise Audio Playback")
        : this(new Win32PowerRequestBackend(string.IsNullOrWhiteSpace(reason) ? "Tonewise Audio Playback" : reason))
    {
    }

    internal WindowsPlaybackPowerGuard(IPowerRequestBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _deferredReleaseTimer = new System.Threading.Timer(OnDeferredReleaseTimerFired, null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _isActive;
            }
        }
    }

    public void Acquire()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _releaseGen++;
            CancelTimerLocked();
            if (!_isActive)
            {
                if (_backend.SetActive(true))
                {
                    _isActive = true;
                }
            }
        }
    }

    public void ReleaseImmediate()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _releaseGen++;
            CancelTimerLocked();
            if (_isActive)
            {
                _backend.SetActive(false);
                _isActive = false;
            }
        }
    }

    public void ScheduleDeferredRelease(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            ReleaseImmediate();
            return;
        }

        lock (_lock)
        {
            if (_disposed || !_isActive) return;
            long gen = ++_releaseGen;
            _scheduledReleaseGen = gen;
            long dueMs = Math.Clamp((long)Math.Ceiling(delay.TotalMilliseconds), 1L, int.MaxValue);
            try
            {
                _deferredReleaseTimer.Change(dueMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Ignore if concurrently disposed
            }
        }
    }

    private void OnDeferredReleaseTimerFired(object? _)
    {
        lock (_lock)
        {
            if (_disposed || !_isActive || _releaseGen != _scheduledReleaseGen)
            {
                return;
            }
            _backend.SetActive(false);
            _isActive = false;
        }
    }

    private void CancelTimerLocked()
    {
        try
        {
            _deferredReleaseTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Ignore during teardown
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _releaseGen++;
            try { _deferredReleaseTimer.Dispose(); } catch { }
            if (_isActive)
            {
                try { _backend.SetActive(false); } catch { }
                _isActive = false;
            }
            try { _backend.Dispose(); } catch { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DetailedReason
    {
        public IntPtr LocalizedReasonModule;
        public uint LocalizedReasonId;
        public uint ReasonStringCount;
        public IntPtr ReasonStrings;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct ReasonUnion
    {
        [FieldOffset(0)]
        public IntPtr SimpleReasonString;

        [FieldOffset(0)]
        public DetailedReason Detailed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        public ReasonUnion Reason;
    }

    private sealed class Win32PowerRequestBackend : IPowerRequestBackend
    {
        private const uint PowerRequestContextVersion = 0u;
        private const uint PowerRequestContextSimpleString = 1u;
        private const int PowerRequestSystemRequired = 1;
        private const int PowerRequestExecutionRequired = 3;
        private const uint EsContinuous = 0x80000000u;
        private const uint EsSystemRequired = 0x00000001u;

        private IntPtr _requestHandle;
        private bool _systemHeld;
        private bool _executionHeld;
        private bool _threadFallbackActive;
        private bool _disposed;

        public Win32PowerRequestBackend(string reason)
        {
            IntPtr reasonPtr = Marshal.StringToHGlobalUni(reason);
            try
            {
                var context = new REASON_CONTEXT
                {
                    Version = PowerRequestContextVersion,
                    Flags = PowerRequestContextSimpleString,
                };
                context.Reason.SimpleReasonString = reasonPtr;
                IntPtr handle = PowerCreateRequest(ref context);
                if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                {
                    _requestHandle = handle;
                }
            }
            catch
            {
                _requestHandle = IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(reasonPtr);
            }
        }

        public bool SetActive(bool active)
        {
            if (_disposed) return false;

            if (active)
            {
                if (_requestHandle != IntPtr.Zero)
                {
                    try
                    {
                        if (!_systemHeld)
                        {
                            _systemHeld = PowerSetRequest(_requestHandle, PowerRequestSystemRequired);
                        }
                        if (!_executionHeld)
                        {
                            _executionHeld = PowerSetRequest(_requestHandle, PowerRequestExecutionRequired);
                        }
                        if (_systemHeld || _executionHeld)
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // Fall through to thread execution state fallback below
                    }
                }

                try
                {
                    _ = SetThreadExecutionState(EsContinuous | EsSystemRequired);
                    _threadFallbackActive = true;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            else
            {
                bool cleared = true;
                if (_requestHandle != IntPtr.Zero)
                {
                    if (_executionHeld)
                    {
                        try { _ = PowerClearRequest(_requestHandle, PowerRequestExecutionRequired); } catch { cleared = false; }
                        _executionHeld = false;
                    }
                    if (_systemHeld)
                    {
                        try { _ = PowerClearRequest(_requestHandle, PowerRequestSystemRequired); } catch { cleared = false; }
                        _systemHeld = false;
                    }
                }
                if (_threadFallbackActive)
                {
                    try { _ = SetThreadExecutionState(EsContinuous); } catch { cleared = false; }
                    _threadFallbackActive = false;
                }
                return cleared;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            SetActive(false);
            _disposed = true;
            if (_requestHandle != IntPtr.Zero)
            {
                try { _ = CloseHandle(_requestHandle); } catch { }
                _requestHandle = IntPtr.Zero;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PowerSetRequest(IntPtr powerRequest, int requestType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PowerClearRequest(IntPtr powerRequest, int requestType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);
    }
}
