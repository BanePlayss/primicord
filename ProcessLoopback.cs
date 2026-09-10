using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Primicord;

/// <summary>
/// WASAPI process loopback (Windows build 20348+). An application includes only
/// its process tree; explicit system capture excludes Primicord and its children.
/// Unsupported capture fails closed, without switching to a different source.
/// </summary>
public sealed class ProcessLoopbackCapture : IWaveIn
{
    public enum Mode { IncludeProcessTree = 0, ExcludeProcessTree = 1 } // PROCESS_LOOPBACK_MODE

    private const string VirtualDevice = "VAD\\Process_Loopback"; // VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK
    private const int ActivationTypeProcessLoopback = 1;          // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
    private const ushort VtBlob = 65;                             // VARENUM VT_BLOB
    private static readonly TimeSpan ActivateTimeout = TimeSpan.FromSeconds(5);

    private readonly uint _targetPid;
    private readonly Mode _mode;
    private AudioClient? _client;
    private AudioCaptureClient? _capture;
    private EventWaitHandle? _frameEvent;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly object _lifecycle = new();
    private bool _disposed;
    private WaveFormat _waveFormat = new(48000, 16, 1);
    internal Action? ValidateSource { get; set; }

    /// <summary>Formato PEDIDO ao engine (GetMixFormat nao existe no device virtual;
    /// o engine converte pro que pedirmos). Setar ANTES de Prepare/StartRecording.</summary>
    public WaveFormat WaveFormat
    {
        get => _waveFormat;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lifecycle)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_client != null) throw new InvalidOperationException("O formato nao pode mudar durante a captura.");
                _waveFormat = value;
            }
        }
    }

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public const string SupportMessage = "Audio isolado por aplicativo requer Windows 11 ou Windows build 20348+. A fonte nao sera trocada automaticamente.";
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    public ProcessLoopbackCapture(uint targetProcessId, Mode mode)
    {
        if (targetProcessId == 0) throw new ArgumentOutOfRangeException(nameof(targetProcessId));
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        _targetPid = targetProcessId;
        _mode = mode;
    }

    /// <summary>Fix do eco: tudo do sistema MENOS o playback do proprio Primicord.</summary>
    public static ProcessLoopbackCapture ExcludingSelf()
        => new((uint)Environment.ProcessId, Mode.ExcludeProcessTree);

    public static ProcessLoopbackCapture IncludingProcess(uint processId)
        => new(processId, Mode.IncludeProcessTree);

    /// <summary>Ativa a fonte exata; falhas nunca autorizam outro dispositivo.</summary>
    public void Prepare()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client != null) return;
            if (!IsSupported) throw new PlatformNotSupportedException(SupportMessage);
            ValidateSource?.Invoke();
            try
            {
                _client = Activate(_targetPid, _mode);
                // Shared-mode engine period, not a fixed 200 ms buffer. WASAPI
                // performs conversion/resampling once into the wire PCM format.
                _client.Initialize(AudioClientShareMode.Shared,
                    AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback
                    | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                    0, 0, WaveFormat, Guid.Empty);
                _frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                _client.SetEventHandle(_frameEvent.SafeWaitHandle.DangerousGetHandle());
                _capture = _client.AudioCaptureClient;
            }
            catch
            {
                ReleaseNative();
                throw;
            }
        }
    }

    public void StartRecording()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread != null) throw new InvalidOperationException("Captura ja iniciada ou ainda encerrando.");
            Prepare();
            try
            {
                _client!.Start();
                _stop = false;
                _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Primicord-ProcLoopback" };
                _thread.Start();
            }
            catch
            {
                _thread = null;
                ReleaseNative();
                throw;
            }
        }
    }

    public void StopRecording()
    {
        Thread? thread;
        lock (_lifecycle)
        {
            _stop = true;
            try { _frameEvent?.Set(); } catch (ObjectDisposedException) { }
            thread = _thread;
        }
        // If a subscriber stalls, the worker retains ownership of COM handles
        // until it leaves its callback. Never release a buffer still in use.
        if (thread != null && thread != Thread.CurrentThread) thread.Join(2000);
    }

    public void Dispose()
    {
        lock (_lifecycle) _disposed = true;
        StopRecording();
        lock (_lifecycle)
            if (_thread == null) ReleaseNative();
    }

    private void ReleaseNative()
    {
        try { _client?.Stop(); } catch { }
        try { _capture?.Dispose(); } catch { } _capture = null;
        try { _client?.Dispose(); } catch { } _client = null;
        try { _frameEvent?.Dispose(); } catch { } _frameEvent = null;
    }

    private void CaptureLoop()
    {
        Exception? err = null;
        try
        {
            var cap = _capture!;
            int blockAlign = WaveFormat.BlockAlign;
            byte[] buf = new byte[Math.Max(WaveFormat.AverageBytesPerSecond / 4, 1 << 16)];
            long nextSourceCheck = 0;
            while (!_stop)
            {
                // TIMEOUT obrigatorio: sistema em silencio => NENHUM evento/pacote
                // (process loopback nao gera pacotes de silencio).
                _frameEvent!.WaitOne(100);
                if (_stop) break;
                long now = Environment.TickCount64;
                if (now >= nextSourceCheck)
                {
                    ValidateSource?.Invoke();
                    nextSourceCheck = now + 500;
                }
                while (!_stop && cap.GetNextPacketSize() > 0)
                {
                    IntPtr p = cap.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    int bytes;
                    try
                    {
                        bytes = checked(frames * blockAlign);
                        if (buf.Length < bytes) buf = new byte[bytes];
                        if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(buf, 0, bytes);
                        else Marshal.Copy(p, buf, 0, bytes);
                    }
                    finally { cap.ReleaseBuffer(frames); }
                    // Contrato identico ao WasapiLoopbackCapture: buffer REUTILIZADO,
                    // o handler copia sincronamente (OnLoopbackData ja copia).
                    DataAvailable?.Invoke(this, new WaveInEventArgs(buf, bytes));
                }
            }
        }
        catch (Exception ex) { err = ex; }
        finally
        {
            lock (_lifecycle)
            {
                ReleaseNative();
                _thread = null;
            }
        }
        try { RecordingStopped?.Invoke(this, new StoppedEventArgs(err)); } catch { }
    }

    // ----- ativacao (unico interop novo) -----
    private static AudioClient Activate(uint pid, Mode mode)
    {
        var pars = new ActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = pid,
            ProcessLoopbackMode = (int)mode,
        };
        int size = Marshal.SizeOf<ActivationParams>();
        IntPtr pPars = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(pars, pPars, false);
        var pv = new PropVariantBlob { vt = VtBlob, blobSize = (uint)size, blobData = pPars };
        var handler = new Handler(); // agil (IAgileObject): callback chega em MTA worker
        Guid iid = typeof(IAudioClient).GUID; // IID_IAudioClient via tipo publico do NAudio
        int hr = ActivateAudioInterfaceAsync(VirtualDevice, ref iid, ref pv, handler, out var op);
        if (hr < 0)
        {
            Marshal.FreeHGlobal(pPars);
            Marshal.ThrowExceptionForHR(hr);
        }
        bool done = Task.WhenAny(handler.Completion, Task.Delay(ActivateTimeout))
            .GetAwaiter().GetResult() == handler.Completion;
        GC.KeepAlive(op); // op+handler vivos ate o callback (senao GC mata o CCW)
        if (!done)
        {
            // NAO liberar o blob agora (ativacao ainda em voo -> use-after-free). Quando o
            // callback finalmente completar, liberamos o blob E, se ele produziu uma
            // interface tardia, soltamos o COM object p/ nao vazar handle nativo.
            handler.Completion.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result is object late)
                {
                    try { Marshal.FinalReleaseComObject(late); } catch { }
                }
                Marshal.FreeHGlobal(pPars);
                GC.KeepAlive(op);
                GC.KeepAlive(handler);
            }, TaskScheduler.Default);
            throw new TimeoutException("ActivateAudioInterfaceAsync nao completou");
        }
        try
        {
            object itf = handler.Completion.GetAwaiter().GetResult(); // relanca erro do callback
            return new AudioClient((IAudioClient)itf); // ctor publico do NAudio 2.3.0
        }
        finally
        {
            Marshal.FreeHGlobal(pPars); // completou (sucesso ou erro): blob ja nao esta em uso
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams // AUDIOCLIENT_ACTIVATION_PARAMS (union achatada)
    {
        public int ActivationType;
        public uint TargetProcessId;     // AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob // braco BLOB do PROPVARIANT; layout ok em x86 e x64
    {
        public ushort vt; public ushort r1, r2, r3;
        public uint blobSize; public IntPtr blobData;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PropVariantBlob activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, // interface PUBLICA do NAudio
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    private sealed class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<object> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<object> Completion => _tcs.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out int hr, out object itf);
                if (hr < 0) _tcs.TrySetException(
                    Marshal.GetExceptionForHR(hr) ?? new COMException("GetActivateResult", hr));
                else _tcs.TrySetResult(itf);
            }
            catch (Exception ex) { _tcs.TrySetException(ex); }
        }
    }
}
