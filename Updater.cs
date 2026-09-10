using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Primicord;

public sealed class UpdateInfoView
{
    public string Version = "";
    public long Size;
    public string Notes = "";
    public bool IsDelta;
    public string SizeLabel => Size <= 0 ? "" : $"{Size / (1024.0 * 1024.0):0.0} MB";
}

/// <summary>Verificação e instalação do canal público do Primicord.</summary>
public static class Updater
{
    public const string DefaultRepo = "BanePlayss/primicord-releases";

    public static string CurrentVersion
    {
        get
        {
            try
            {
                var asm = typeof(Updater).Assembly;
                string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrEmpty(v))
                {
                    int plus = v.IndexOf('+');
                    return plus > 0 ? v[..plus] : v;
                }
                return asm.GetName().Version?.ToString(3) ?? "0.0.0";
            }
            catch { return "0.0.0"; }
        }
    }

    private static UpdateManager Manager(string repo)
        => new(new GithubSource($"https://github.com/{repo}", null, false));

    public static bool CanSelfUpdate
    {
        get
        {
            try { return Manager(DefaultRepo).IsInstalled; }
            catch { return false; }
        }
    }

    private static UpdateInfo? _pending;

    public static async Task<UpdateInfoView?> CheckAsync(CancellationToken ct = default)
    {
        var manager = Manager(DefaultRepo);
        if (!manager.IsInstalled)
            throw new InvalidOperationException(
                "Esta cópia não foi instalada pelo setup. Use o instalador uma vez para ativar atualizações automáticas.");

        var info = await manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        if (info == null)
        {
            _pending = null;
            Log.Write($"update: {CurrentVersion} já é a mais recente");
            return null;
        }

        _pending = info;
        bool delta = info.DeltasToTarget is { Length: > 0 };
        long size = delta ? info.DeltasToTarget.Sum(d => d.Size) : info.TargetFullRelease.Size;
        return new UpdateInfoView
        {
            Version = info.TargetFullRelease.Version.ToString(),
            Size = size,
            Notes = info.TargetFullRelease.NotesMarkdown ?? "",
            IsDelta = delta,
        };
    }

    public static async Task DownloadAndApplyAsync(IProgress<int>? progress, CancellationToken ct = default)
    {
        var info = _pending ?? throw new InvalidOperationException("Procure uma atualização antes de instalar.");
        var manager = Manager(DefaultRepo);
        await manager.DownloadUpdatesAsync(info, p => progress?.Report(p), ct).ConfigureAwait(false);
        Log.Write("update: baixado, reiniciando na versão nova");
        manager.ApplyUpdatesAndRestart(info.TargetFullRelease, new[] { "--updated" });
    }
}
