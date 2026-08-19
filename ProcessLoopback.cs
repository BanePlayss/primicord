using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Primicord;

/// <summary>
/// WASAPI process loopback (Windows build 20348+): captura o audio do sistema EXCLUINDO a
/// arvore de processos alvo. Com ExcludingSelf() o "modo DJ" NUNCA
/// recaptura a voz dos outros tocada pelo proprio Primicord (mata o eco por construcao).
/// Drop-in de WasapiLoopbackCapture via IWaveIn. Reusa os tipos publicos do NAudio;
/// o unico interop novo e ActivateAudioInterfaceAsync com PROPVARIANT(VT_BLOB).
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

    /// <summary>Formato PEDIDO ao engine (GetMixFormat nao existe no device virtual;
    /// o engine converte pro que pedirmos). Setar ANTES de Prepare/StartRecording.</summary>
    public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    // O process-loopback apareceu no SDK 20348. O TFM 19041 continua valido para
    // o restante do app, mas tentar esta ativacao antes do 20348 so produz um erro
    // generico do COM.
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    public ProcessLoopbackCapture(uint targetProcessId, Mode mode)
    {
        _targetPid = targetProcessId;
        _mode = mode;
    }

    /// <summary>Fix do eco: tudo do sistema MENOS o playback do proprio Primicord.</summary>
    public static ProcessLoopbackCapture ExcludingSelf()
        => new((uint)Environment.ProcessId, Mode.ExcludeProcessTree);

    /// <summary>Ativa e inicializa ja (lanca aqui se indisponivel -> chamador faz fallback).</summary>
    public void Prepare()
    {
        if (_client != null) return;
        if (!IsSupported) throw new PlatformNotSupportedException("requer Windows build 20348+");
        _client = Activate(_targetPid, _mode);
        // 2_000_000 = 200 ms em unidades de 100 ns; flags LOOPBACK|EVENTCALLBACK.
        _client.Initialize(AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
            2_000_000, 0, WaveFormat, Guid.Empty);
        _frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        _client.SetEventHandle(_frameEvent.SafeWaitHandle.DangerousGetHandle());
        _capture = _client.AudioCaptureClient; // GetService: SO depois do Initialize
    }

    public void StartRecording()
    {
        if (_thread != null) throw new InvalidOperationException("captura ja iniciada");
        Prepare();
        _client!.Start();
        _stop = false;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Primicord-ProcLoopback" };
        _thread.Start();
    }

    public void StopRecording()
    {
        _stop = true;
        try { _frameEvent?.Set(); } catch { }
        var t = _thread; _thread = null;
        if (t != null && t.IsAlive && t != Thread.CurrentThread) t.Join(1000);
        try { _client?.Stop(); } catch { }
    }

    public void Dispose()
    {
        StopRecording();
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
            while (!_stop)
            {
                // TIMEOUT obrigatorio: sistema em silencio => NENHUM evento/pacote
                // (process loopback nao gera pacotes de silencio).
                _frameEvent!.WaitOne(100);
                if (_stop) break;
                while (!_stop && cap.GetNextPacketSize() > 0)
                {
                    IntPtr p = cap.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    int bytes = frames * blockAlign;
                    if (buf.Length < bytes) buf = new byte[bytes];
                    if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(buf, 0, bytes);
                    else Marshal.Copy(p, buf, 0, bytes);
                    cap.ReleaseBuffer(frames);
                    // Contrato identico ao WasapiLoopbackCapture: buffer REUTILIZADO,
                    // o handler copia sincronamente (OnLoopbackData ja copia).
                    DataAvailable?.Invoke(this, new WaveInEventArgs(buf, bytes));
                }
            }
        }
        catch (Exception ex) { err = ex; }
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
        bool done = handler.Completion.Wait(ActivateTimeout);
        GC.KeepAlive(op); // op+handler vivos ate o callback (senao GC mata o CCW)
        if (!done)
        {
            // NAO liberar o blob agora (ativacao ainda em voo -> use-after-free). Quando o
            // callback finalmente completar, liberamos o blob E, se ele produziu uma
            // interface tardia, soltamos o COM object p/ nao vazar handle nativo.
            handler.Completion.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result != IntPtr.Zero)
                    try { Marshal.Release(t.Result); } catch { }
                Marshal.FreeHGlobal(pPars);
            }, TaskScheduler.Default);
            throw new TimeoutException("ActivateAudioInterfaceAsync nao completou");
        }
        try
        {
            IntPtr itf = handler.Completion.GetAwaiter().GetResult(); // relanca erro do callback
            try
            {
                // NAudio 2.3 declara GetActivateResult como "out object". Nessa
                // interface virtual o marshaler criava um RCW sem IAudioClient e o
                // cast falhava com E_NOINTERFACE, apesar de a ativacao ter dado OK.
                // Receber o IUnknown cru preserva exatamente o ponteiro devolvido
                // pelo Windows; so depois criamos o wrapper tipado do NAudio.
                var typed = (IAudioClient)Marshal.GetTypedObjectForIUnknown(itf, typeof(IAudioClient));
                return new AudioClient(typed);
            }
            finally { Marshal.Release(itf); }
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
        IActivateAudioInterfaceCompletionHandlerRaw completionHandler,
        out IActivateAudioInterfaceAsyncOperationRaw activationOperation);

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperationRaw
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult, out IntPtr activatedInterface);
    }

    [ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandlerRaw
    {
        [PreserveSig]
        int ActivateCompleted(IActivateAudioInterfaceAsyncOperationRaw operation);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComVisible(true)]
    private sealed class Handler : IActivateAudioInterfaceCompletionHandlerRaw, IAgileObject
    {
        private readonly TaskCompletionSource<IntPtr> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IntPtr> Completion => _tcs.Task;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperationRaw op)
        {
            try
            {
                int callHr = op.GetActivateResult(out int hr, out IntPtr itf);
                if (callHr < 0) hr = callHr;
                if (hr < 0) _tcs.TrySetException(
                    Marshal.GetExceptionForHR(hr) ?? new COMException("GetActivateResult", hr));
                else if (itf == IntPtr.Zero) _tcs.TrySetException(
                    new COMException("GetActivateResult devolveu uma interface nula"));
                else _tcs.TrySetResult(itf);
            }
            catch (Exception ex) { _tcs.TrySetException(ex); }
            return 0;
        }
    }
}
