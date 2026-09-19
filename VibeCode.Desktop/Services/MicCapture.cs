using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VibeCode.Services;

/// <summary>
/// Microphone capture that cannot lose the start of what you said.
///
/// ================================ WHY THIS EXISTS INSTEAD OF NAudio's WaveInEvent ================================
/// WaveInEvent.StartRecording does three things in this order (NAudio 2.2.1, NAudio.WinMM/WaveInEvent.cs):
///
///     OpenWaveInDevice();                                  // waveInOpen - slow, and slower on a busy box
///     MmException.Try(WaveInterop.waveInStart(handle));     // the device is LIVE from here
///     ThreadPool.QueueUserWorkItem(state => RecordThread());
///
/// and RecordThread's first act - the only place it ever happens - is to hand the wave-in buffers to the driver:
///
///     captureState = CaptureState.Capturing;
///     foreach (var buffer in buffers) if (!buffer.InQueue) buffer.Reuse();   // waveInAddBuffer
///
/// So between waveInStart and the moment the thread pool gets round to that work item, the microphone is running
/// with nowhere to write, and every word spoken in that window is discarded by the driver. On an idle machine the
/// pool answers in well under a millisecond and nobody notices. This app is not an idle machine: it runs agents,
/// pumps CLI processes and blocks on I/O, which is exactly how a thread pool gets starved - and the pool grows by
/// roughly one thread at a time when it does.
///
/// Measured on this box (12 threads, load from SEPARATE processes so no priority change could flatter it), a
/// 3-second recording, median of three, milliseconds of the window that came back as no audio:
///
///                                       WaveInEvent      this class      armed
///     idle                                     144              79          48
///     external CPU load                        104             107          45
///     CPU load + a starved thread pool        3066              95          58
///                                             ^ the whole recording. DataAvailable never fired once.
///
/// The differences that buy that:
///   * every buffer is prepared AND queued to the driver BEFORE waveInStart, so the device is never live with
///     nowhere to put audio - the driver has <see cref="Buffers"/> x <see cref="BufferMs"/> of headroom from the
///     first sample;
///   * the buffers are serviced by a dedicated Highest-priority thread, not a ThreadPool work item, so no amount
///     of pool starvation can stop them being recycled;
///   * <see cref="Arm"/> separates waveInOpen (38-184 ms measured) from the click, so the device is already open
///     by the time the user commits;
///   * headers and audio buffers live in unmanaged memory, so there is nothing for the GC to move while the
///     driver is writing into it.
///
/// It also opens WAVE_MAPPER rather than device 0. WaveInEvent defaults to DeviceNumber = 0, which is whatever
/// device happens to be enumerated first - on a machine with a virtual cable or a streaming input installed that
/// is frequently NOT the microphone the user actually speaks into. WAVE_MAPPER is the device they chose in
/// Windows' sound settings.
/// ================================================================================================================
/// </summary>
public sealed class MicCapture : IDisposable
{
    /// <summary>Fired on the capture thread with the buffer that was just filled. The array is REUSED between
    /// calls (as NAudio's DataAvailable is): copy anything you need to keep before returning.</summary>
    public event Action<byte[], int>? DataAvailable;

    public const int SampleRate = 16000;      // exactly what whisper wants, so nothing ever resamples
    public const int Channels = 1;
    public const int Bits = 16;

    /// <summary>50 ms per buffer, 8 buffers: 400 ms of driver headroom, against NAudio's default 3 x 100 ms that
    /// is not queued at all until a pool thread turns up.</summary>
    private const int BufferMs = 50;
    private const int Buffers = 8;

    private const int CallbackEvent = 0x50000;   // CALLBACK_EVENT
    private const uint WhdrDone = 0x00000001;    // WHDR_DONE
    private static readonly IntPtr WaveMapper = new(-1);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public short wFormatTag, nChannels;
        public int nSamplesPerSec, nAvgBytesPerSec;
        public short nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags, dwLoops;
        public IntPtr lpNext, reserved;
    }

    [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();
    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr h, IntPtr dev, ref WaveFormatEx f, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveInStop(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr h);

    /// <summary>True when Windows can see any recording device at all.</summary>
    public static bool AnyDevice => waveInGetNumDevs() > 0;

    private static readonly int HdrSize = Marshal.SizeOf<WaveHdr>();
    private static readonly int BlockAlign = Channels * Bits / 8;
    private static readonly int BytesPerSecond = SampleRate * BlockAlign;
    private static readonly int BufferBytes = BufferMs * BytesPerSecond / 1000 / BlockAlign * BlockAlign;

    private readonly object _gate = new();
    private IntPtr _handle;
    private IntPtr[] _headers = [];
    private AutoResetEvent? _ready;
    // Woken separately from _ready. Setting _ready would be consumed by whichever wait happens to be in flight
    // (it is the DRIVER's signal too, and auto-reset), which left Stop() waiting out the pump's 100 ms poll -
    // measured at 117 ms on the UI thread, for nothing.
    private ManualResetEvent? _stop;
    private Thread? _pump;
    private volatile bool _running;
    private bool _disposed;
    private int _next;                        // the buffer the driver will complete next; kept in strict rotation

    /// <summary>Open the device without recording anything, so the click that starts a dictation does not have to
    /// wait for waveInOpen. Safe to call repeatedly; a device that will not open is reported by
    /// <see cref="Start"/> instead, since arming is only ever an optimisation.</summary>
    public void Arm()
    {
        try { lock (_gate) { if (!_disposed) OpenLocked(); } }
        catch { /* the real attempt reports the real error */ }
    }

    /// <summary>Open (if <see cref="Arm"/> has not already), queue every buffer, then start the device. Throws
    /// with the winmm error if the microphone cannot be opened.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) return;
            OpenLocked();

            _headers = new IntPtr[Buffers];
            _next = 0;
            for (var i = 0; i < Buffers; i++)
            {
                var hdr = Marshal.AllocHGlobal(HdrSize);
                var data = Marshal.AllocHGlobal(BufferBytes);
                Marshal.StructureToPtr(new WaveHdr { lpData = data, dwBufferLength = (uint)BufferBytes }, hdr, false);
                Check(waveInPrepareHeader(_handle, hdr, HdrSize), "waveInPrepareHeader");
                Check(waveInAddBuffer(_handle, hdr, HdrSize), "waveInAddBuffer");
                _headers[i] = hdr;
            }

            // Up and waiting BEFORE the device is live, so the first full buffer is recycled the moment it lands.
            _stop ??= new ManualResetEvent(false);
            _stop.Reset();
            _running = true;
            _pump = new Thread(Pump)
            {
                IsBackground = true,
                Name = "VibeCode mic capture",
                Priority = ThreadPriority.Highest,
            };
            _pump.Start();

            Check(waveInStart(_handle), "waveInStart");
        }
    }

    /// <summary>Stop the device and release its buffers. Synchronous: when this returns, no further
    /// <see cref="DataAvailable"/> callback can arrive, so the caller can finalise its WAV immediately.
    /// Idempotent.</summary>
    public void Stop()
    {
        Thread? pump;
        lock (_gate)
        {
            if (!_running) return;
            _running = false;                 // the pump stops re-queueing from here
            waveInStop(_handle);
            waveInReset(_handle);             // hands every queued buffer back, marked DONE
            _stop?.Set();                     // ...and wake the pump so it notices at once
            pump = _pump;
            _pump = null;
        }

        // Joined OUTSIDE the lock: the pump takes the same lock on its way out to free the buffers, and holding
        // it here would deadlock. Bounded so a wedged driver can never freeze the UI thread that clicked stop.
        pump?.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Only safe once the pump has unprepared every header - waveInClose returns WAVERR_STILLPLAYING while
            // any buffer is prepared. If the pump did not finish (a wedged driver), leave the handle rather than
            // free memory the driver may still be writing into: a leaked handle beats an access violation.
            if (_handle != IntPtr.Zero && _headers.Length == 0)
            {
                waveInClose(_handle);
                _handle = IntPtr.Zero;
            }
            // Only once the pump is provably gone (it released the headers): disposing a handle it is still
            // waiting on would fault the capture thread rather than end it.
            if (_headers.Length == 0)
            {
                _ready?.Dispose(); _ready = null;
                _stop?.Dispose(); _stop = null;
            }
        }
    }

    private void OpenLocked()
    {
        if (_handle != IntPtr.Zero) return;
        if (waveInGetNumDevs() == 0) throw new InvalidOperationException("No microphone found.");
        _ready ??= new AutoResetEvent(false);
        var fmt = new WaveFormatEx
        {
            wFormatTag = 1,                       // WAVE_FORMAT_PCM
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            nAvgBytesPerSec = BytesPerSecond,
            nBlockAlign = (short)BlockAlign,
            wBitsPerSample = Bits,
            cbSize = 0,
        };
        Check(waveInOpen(out _handle, WaveMapper, ref fmt,
            _ready.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent), "waveInOpen");
    }

    private void Pump()
    {
        var scratch = new byte[BufferBytes];
        var waits = new WaitHandle[] { _ready!, _stop! };
        try
        {
            while (_running)
            {
                // Either the driver has a buffer for us or Stop wants us gone; the timeout is only a backstop for
                // a driver that stops signalling altogether.
                WaitHandle.WaitAny(waits, 100);

                // Strict rotation, never a scan. The driver fills the queue in the order buffers were added, so
                // the oldest outstanding buffer is always the next to complete: if IT is not done, nothing newer
                // can be either. Draining in index order instead would hand the consumer 50 ms chunks out of
                // sequence whenever the rotation happened to wrap mid-scan.
                while (_running)
                {
                    var ptr = _headers[_next];
                    var hdr = Marshal.PtrToStructure<WaveHdr>(ptr);
                    if ((hdr.dwFlags & WhdrDone) == 0) break;

                    Deliver(ptr, hdr, scratch);
                    if (!_running) break;      // re-checked: never re-queue into a device that is stopping
                    waveInPrepareHeader(_handle, ptr, HdrSize);   // documented no-op when already prepared
                    waveInAddBuffer(_handle, ptr, HdrSize);
                    _next = (_next + 1) % _headers.Length;
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine("mic pump: " + ex); }
        finally { ReleaseBuffers(); }
    }

    /// <summary>Hand one filled buffer to the consumer, then zero its recorded length.
    ///
    /// The zeroing is what makes the final drain safe. A buffer keeps WHDR_DONE and its byte count until it is
    /// handed back to the driver, so a buffer delivered by the loop and NOT re-queued (because Stop landed in
    /// between) would be delivered a second time by the drain - a duplicated 50 ms of audio, stuttered into the
    /// middle of the transcript. The driver rewrites the count every time it fills the buffer, so clearing it is
    /// only ever a "we already took this one" mark.</summary>
    private void Deliver(IntPtr hdrPtr, WaveHdr hdr, byte[] scratch)
    {
        var count = (int)hdr.dwBytesRecorded;
        if (count <= 0) return;
        if (count > scratch.Length) count = scratch.Length;
        Marshal.Copy(hdr.lpData, scratch, 0, count);
        Marshal.WriteInt32(hdrPtr, BytesRecordedOffset, 0);
        try { DataAvailable?.Invoke(scratch, count); }
        catch { /* a consumer fault must not take the mic down with it */ }
    }

    private static readonly int BytesRecordedOffset = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwBytesRecorded));

    /// <summary>Done on the pump thread, which is the only thread that ever hands buffers to the driver - so a
    /// buffer re-queued a moment before Stop() cannot be freed while it is still in the driver's queue.</summary>
    private void ReleaseBuffers()
    {
        lock (_gate)
        {
            if (_headers.Length == 0) return;
            waveInReset(_handle);              // flushes anything the loop queued on its way out

            // The last word, before the buffers are freed. waveInReset hands back every queued buffer marked DONE,
            // including the partly-filled one the device was in the middle of - up to BufferMs of speech that
            // simply vanished before this drain existed, which is the end of the user's last sentence.
            var scratch = new byte[BufferBytes];
            for (var k = 0; k < _headers.Length; k++)
            {
                var ptr = _headers[(_next + k) % _headers.Length];    // oldest first, same rotation as the loop
                var h = Marshal.PtrToStructure<WaveHdr>(ptr);
                if ((h.dwFlags & WhdrDone) != 0) Deliver(ptr, h, scratch);
            }

            foreach (var hdr in _headers)
            {
                waveInUnprepareHeader(_handle, hdr, HdrSize);
                var h = Marshal.PtrToStructure<WaveHdr>(hdr);
                Marshal.FreeHGlobal(h.lpData);
                Marshal.FreeHGlobal(hdr);
            }
            _headers = [];
            // Disposed while we were still winding down: nobody else can close the handle now, so do it here.
            if (_disposed && _handle != IntPtr.Zero)
            {
                waveInClose(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    private static void Check(int mmResult, string call)
    {
        if (mmResult == 0) return;
        // 4 == MMSYSERR_ALLOCATED: something else holds the mic. Worth saying plainly - it is the one failure the
        // user can actually do something about.
        throw new InvalidOperationException(mmResult == 4
            ? "The microphone is already in use by another app."
            : $"{call} failed ({mmResult}).");
    }
}
