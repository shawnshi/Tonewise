using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Tonewise.Audio;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class PlaybackPowerAndRecoveryTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "protocol", "schemas", "manifest.sql")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root");
    }

    private sealed class FakePowerRequestBackend : IPowerRequestBackend
    {
        private readonly ManualResetEventSlim _clearedEvent = new(false);

        public int SetCalls { get; private set; }
        public int ClearCalls { get; private set; }
        public bool ActiveState { get; private set; }
        public bool Disposed { get; private set; }

        public bool WaitUntilCleared(int timeoutMs) => _clearedEvent.Wait(timeoutMs);

        public void ResetClearedSignal() => _clearedEvent.Reset();

        public bool SetActive(bool active)
        {
            if (active)
            {
                SetCalls++;
                ActiveState = true;
                _clearedEvent.Reset();
                return true;
            }

            ClearCalls++;
            ActiveState = false;
            _clearedEvent.Set();
            return true;
        }

        public void Dispose()
        {
            Disposed = true;
            _clearedEvent.Dispose();
        }
    }

    private sealed class RecordingPowerGuard : IPlaybackPowerGuard
    {
        public bool IsActive { get; private set; }
        public int AcquireCount { get; private set; }
        public int ReleaseImmediateCount { get; private set; }
        public int DeferredReleaseCount { get; private set; }
        public bool Disposed { get; private set; }

        public void Acquire()
        {
            AcquireCount++;
            IsActive = true;
        }

        public void ReleaseImmediate()
        {
            ReleaseImmediateCount++;
            IsActive = false;
        }

        public void ScheduleDeferredRelease(TimeSpan delay)
        {
            DeferredReleaseCount++;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    [Fact]
    public void WindowsPlaybackPowerGuard_StructSizeAndNativeLifecycle_Succeeds()
    {
        Assert.Equal(IntPtr.Size == 8 ? 32 : 24, WindowsPlaybackPowerGuard.ReasonContextSizeBytes);

        using var guard = new WindowsPlaybackPowerGuard("Tonewise Unit Test Playback");
        Assert.False(guard.IsActive);

        guard.Acquire();
        Assert.True(guard.IsActive);

        // Idempotent second acquire keeps active cleanly
        guard.Acquire();
        Assert.True(guard.IsActive);

        guard.ReleaseImmediate();
        Assert.False(guard.IsActive);

        guard.Acquire();
        Assert.True(guard.IsActive);
        guard.Dispose();
        Assert.False(guard.IsActive);
    }

    [Fact]
    public void WindowsPlaybackPowerGuard_DeferredRelease_CancelledByAcquire_AndReleasesWhenIdle()
    {
        using var backend = new FakePowerRequestBackend();
        using var guard = new WindowsPlaybackPowerGuard(backend);

        guard.Acquire();
        guard.Acquire();
        Assert.True(guard.IsActive);
        Assert.Equal(1, backend.SetCalls);
        Assert.Equal(0, backend.ClearCalls);

        // Deferred release followed quickly by Acquire must cancel the pending release with zero gap
        guard.ScheduleDeferredRelease(TimeSpan.FromMilliseconds(200));
        Thread.Sleep(35);
        guard.Acquire();
        Thread.Sleep(240);

        Assert.True(guard.IsActive);
        Assert.True(backend.ActiveState);
        Assert.Equal(1, backend.SetCalls);
        Assert.Equal(0, backend.ClearCalls);

        // Now allow deferred release to expire naturally
        backend.ResetClearedSignal();
        guard.ScheduleDeferredRelease(TimeSpan.FromMilliseconds(40));
        Assert.True(backend.WaitUntilCleared(2000));
        Assert.False(guard.IsActive);
        Assert.False(backend.ActiveState);
        Assert.Equal(1, backend.ClearCalls);

        guard.Dispose();
        Assert.True(backend.Disposed);
        Assert.Equal(1, backend.ClearCalls);
    }

    [Fact]
    public void AudioEndpointReconciler_InPlaceDiff_PreservesSelectedEndpointAndAvoidsReset()
    {
        var realtek = new AudioOutputEndpoint("id-realtek", "Speakers (Realtek(R) Audio)", IsUsbDevice: false, IsDefault: true);
        var hdmi = new AudioOutputEndpoint("id-hdmi", "MSI Monitor (AMD High Definition Audio Device)", IsUsbDevice: false, IsDefault: false);
        var usbDac = new AudioOutputEndpoint("id-usb", "Topping D90SE USB DAC", IsUsbDevice: true, IsDefault: false);

        var current = new ObservableCollection<AudioOutputEndpoint> { realtek, hdmi, usbDac };
        var actions = new List<NotifyCollectionChangedAction>();
        current.CollectionChanged += (_, e) => actions.Add(e.Action);

        // Case A: identical list produces zero CollectionChanged events
        var resSame = AudioEndpointReconciler.ReconcileInPlace(
            current,
            [realtek, hdmi, usbDac],
            realtek.Id);
        Assert.False(resSame.CollectionMutated);
        Assert.False(resSame.LostPreviousSelection);
        Assert.Equal(realtek.Id, resSame.TargetSelection?.Id);
        Assert.Empty(actions);

        // Case B: unrelated HDMI monitor endpoint disconnects on display off while Realtek stays active
        var resHdmiOff = AudioEndpointReconciler.ReconcileInPlace(
            current,
            [realtek, usbDac],
            realtek.Id);
        Assert.True(resHdmiOff.CollectionMutated);
        Assert.False(resHdmiOff.LostPreviousSelection);
        Assert.Equal(realtek.Id, resHdmiOff.TargetSelection?.Id);
        Assert.Equal(NotifyCollectionChangedAction.Remove, Assert.Single(actions));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.Equal(2, current.Count);
        Assert.Equal(realtek.Id, current[0].Id);

        // Case C: selected USB DAC disappears -> falls back cleanly to Realtek without Reset
        actions.Clear();
        var spdif = new AudioOutputEndpoint("id-spdif", "Realtek Digital Output", IsUsbDevice: false, IsDefault: false);
        var resUsbLost = AudioEndpointReconciler.ReconcileInPlace(
            current,
            [realtek, spdif],
            usbDac.Id);
        Assert.True(resUsbLost.CollectionMutated);
        Assert.True(resUsbLost.LostPreviousSelection);
        Assert.Equal(realtek.Id, resUsbLost.TargetSelection?.Id);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.Equal(2, current.Count);
        Assert.Contains(current, e => e.Id == spdif.Id);
    }

    [Fact]
    public void WasapiMediaCategoryHelper_StructLayoutAndNullGuard()
    {
        Assert.Equal(16, WasapiMediaCategoryHelper.AudioClientPropertiesSizeBytes);
        Assert.Equal(11, WasapiMediaCategoryHelper.AudioCategoryMedia);
        Assert.False(WasapiMediaCategoryHelper.TryConfigureMediaCategory(null));
    }

    [Fact]
    public void TonewiseAudioEngine_PowerGuardLifecycle_AndStructuralContracts()
    {
        var powerGuard = new RecordingPowerGuard();
        using (var engine = new TonewiseAudioEngine(powerGuard))
        {
            engine.ReleasePowerKeepAlive();
            Assert.Equal(1, powerGuard.ReleaseImmediateCount);

            engine.Stop();
            Assert.Equal(2, powerGuard.ReleaseImmediateCount);
        }
        Assert.Equal(3, powerGuard.ReleaseImmediateCount);
        Assert.False(powerGuard.Disposed); // Caller-owned injected guard is released, not disposed by engine

        using (var defaultEngine = new TonewiseAudioEngine())
        {
            defaultEngine.Stop();
        }

        string repoRoot = FindRepoRoot();
        string engineSrc = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Audio", "TonewiseAudioEngine.cs"));
        Assert.Contains("WasapiMediaCategoryHelper.TryConfigureMediaCategory(wasapi);", engineSrc);
        Assert.Contains("WasapiMediaCategoryHelper.TryConfigureMediaCategory(shared);", engineSrc);
        Assert.Contains("capturedPlayer.PlaybackState == PlaybackState.Stopped", engineSrc);
        Assert.Contains("!_pauseRequested", engineSrc);

        int capPosIdx = engineSrc.IndexOf("CapturePositionSafely(", StringComparison.Ordinal);
        int detachIdx = engineSrc.IndexOf("errorDetach = DetachPlaybackHandlesLocked();", StringComparison.Ordinal);
        Assert.True(capPosIdx > 0 && detachIdx > capPosIdx, "Interrupted position must be captured before handles are detached");

        string dspVmSrc = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop", "ViewModels", "MainViewModel.Dsp.cs"));
        Assert.Contains("AudioEndpointReconciler.ReconcileInPlace(", dspVmSrc);
        Assert.DoesNotContain("OutputEndpoints.Clear()", dspVmSrc);

        string playbackVmSrc = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop", "ViewModels", "MainViewModel.Playback.cs"));
        Assert.Contains("HandlePlaybackInterrupted(", playbackVmSrc);
        Assert.Contains("_pendingRestorePositionSec", playbackVmSrc);
        Assert.Contains("allowExclusive:", playbackVmSrc);
    }
}
