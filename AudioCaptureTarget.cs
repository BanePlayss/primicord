using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Primicord;

public enum AudioCaptureKind { Silent, Application, System }

/// <summary>
/// An explicit audio selection. Application capture includes the process and its
/// children (all tabs of a browser), never the other applications on the desktop.
/// </summary>
public sealed record AudioCaptureTarget
{
    public AudioCaptureKind Kind { get; }
    public uint ProcessId { get; }
    public string Name { get; }
    public string ProcessName { get; }
    public IntPtr Window { get; }
    private long ProcessStartedUtcTicks { get; }

    private AudioCaptureTarget(AudioCaptureKind kind, string name, uint processId = 0,
        string processName = "", long processStartedUtcTicks = 0, IntPtr window = default)
    {
        Kind = kind;
        Name = name;
        ProcessId = processId;
        ProcessName = processName;
        ProcessStartedUtcTicks = processStartedUtcTicks;
        Window = window;
    }

    public static AudioCaptureTarget Silent { get; } = new(AudioCaptureKind.Silent, "Sem audio");
    public static AudioCaptureTarget System { get; } =
        new(AudioCaptureKind.System, "Todo o PC (exceto as vozes do Primicord)");

    public string Description => Kind switch
    {
        AudioCaptureKind.Application => "Audio deste aplicativo e seus processos filhos. Navegadores incluem todas as abas.",
        AudioCaptureKind.System => "Transmite todos os aplicativos, exceto o Primicord. Selecione esta opcao somente se quiser compartilhar todo o PC.",
        _ => "A transmissao continua sem audio do PC."
    };

    public static AudioCaptureTarget ForWindow(CaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsWindow || target.ProcessId == 0)
            throw new ArgumentException("Escolha uma janela para capturar o audio do aplicativo.", nameof(target));
        GetWindowThreadProcessId(target.Window, out uint actualPid);
        if (actualPid != target.ProcessId)
            throw new InvalidOperationException("A janela selecionada foi fechada. Escolha novamente.");
        var source = ForProcess(target.ProcessId,
            string.IsNullOrWhiteSpace(target.WindowTitle) ? target.Name : target.WindowTitle);
        return new AudioCaptureTarget(source.Kind, source.Name, source.ProcessId,
            source.ProcessName, source.ProcessStartedUtcTicks, target.Window);
    }

    public static AudioCaptureTarget ForProcess(uint processId, string? displayName = null)
    {
        if (processId == 0 || processId > int.MaxValue || processId == Environment.ProcessId)
            throw new ArgumentOutOfRangeException(nameof(processId), "Escolha outro aplicativo como fonte de audio.");
        using var process = Process.GetProcessById((int)processId);
        if (process.HasExited) throw new InvalidOperationException("O aplicativo selecionado foi fechado.");
        string name = string.IsNullOrWhiteSpace(displayName) ? process.ProcessName : displayName.Trim();
        return new AudioCaptureTarget(AudioCaptureKind.Application, name, processId,
            process.ProcessName, process.StartTime.ToUniversalTime().Ticks);
    }

    /// <summary>Includes minimized applications, useful as explicit screen-audio sources.</summary>
    public static List<AudioCaptureTarget> ListApplications()
    {
        var result = new List<AudioCaptureTarget>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero) continue;
                    string title = process.MainWindowTitle.Trim();
                    if (title.Length == 0) continue;
                    result.Add(ForProcess((uint)process.Id, $"{process.ProcessName} — {title}"));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                    or global::System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        return result.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal void ValidateActive()
    {
        if (Kind == AudioCaptureKind.Silent)
            throw new InvalidOperationException("Nenhuma fonte de audio selecionada.");
        if (!ProcessLoopbackCapture.IsSupported)
            throw new PlatformNotSupportedException(ProcessLoopbackCapture.SupportMessage);
        if (Kind != AudioCaptureKind.Application) return;
        try
        {
            using var process = Process.GetProcessById((int)ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != ProcessStartedUtcTicks)
                throw new InvalidOperationException("O aplicativo selecionado foi fechado ou reiniciado. Escolha a fonte novamente.");
            if (Window != IntPtr.Zero)
            {
                GetWindowThreadProcessId(Window, out uint actualPid);
                if (actualPid != ProcessId)
                    throw new InvalidOperationException("A janela selecionada foi fechada. Escolha a fonte novamente.");
            }
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("O aplicativo selecionado foi fechado. Escolha a fonte novamente.", ex);
        }
    }

    public override string ToString() => Name;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
