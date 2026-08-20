using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Primicord;
using Xunit;

namespace Primicord.Tests;

[Collection("rede")]
public sealed class ClusterFailoverIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "PrimicordClusterHttp-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = new();

    public ClusterFailoverIntegrationTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Sala_e_escritas_continuam_quando_primeiro_servidor_cai()
    {
        int firstPort = FreePort();
        int secondPort = FreePort();
        Process firstProcess = StartServer("first", firstPort);
        Process secondProcess = StartServer("second", secondPort);
        var first = new MiniServerStore($"http://127.0.0.1:{firstPort}");
        var second = new MiniServerStore($"http://127.0.0.1:{secondPort}");
        await WaitUntilReady(first, firstProcess);
        await WaitUntilReady(second, secondProcess);

        var replicas = new Dictionary<string, IClusterReplica>
        {
            ["first"] = first,
            ["second"] = second,
        };
        var cluster = new ClusterDocumentStore(
            new FixedEndpoints(new[]
            {
                new ClusterEndpoint("first", first.BaseUrl),
                new ClusterEndpoint("second", second.BaseUrl),
            }),
            new FailingFallback(), endpoint => replicas[endpoint.Identity]);

        await cluster.SetAsync("pc_rooms/sala-ao-vivo",
            new Dictionary<string, object?> { ["name"] = "Sala ao vivo" });
        Assert.NotNull(await second.GetAsync("pc_rooms/sala-ao-vivo"));

        firstProcess.Kill(entireProcessTree: true);
        Assert.True(firstProcess.WaitForExit(5_000));

        Dictionary<string, object?>? room = await cluster.GetAsync("pc_rooms/sala-ao-vivo");
        Assert.Equal("Sala ao vivo", room!["name"]);
        await cluster.SetAsync("pc_rooms/sala-ao-vivo/peers/vitinho",
            new Dictionary<string, object?> { ["nick"] = "vitinho", ["lastSeen"] = 123L });
        Assert.NotNull(await second.GetAsync("pc_rooms/sala-ao-vivo/peers/vitinho"));
    }

    [Fact]
    public async Task Voz_opus_atravessa_relay_sem_porta_udp_no_cliente()
    {
        int port = FreePort();
        Process server = StartServer("voice-relay", port);
        var store = new MiniServerStore($"http://127.0.0.1:{port}");
        await WaitUntilReady(store, server);
        int deadPort = FreePort();
        var endpoints = new FixedEndpoints(new[]
        {
            new ClusterEndpoint("dead", $"http://127.0.0.1:{deadPort}"),
            new ClusterEndpoint("relay", store.BaseUrl),
        });

        using var a = new RelayVoiceTransport(endpoints, "war-room", "bane-aaa", "bane");
        using var b = new RelayVoiceTransport(endpoints, "war-room", "kentaroz-bbb", "kentaroz");
        int frames = 0;
        uint sender = 0;
        b.VoiceReceived += (id, _, _, count) =>
        {
            sender = id;
            if (count > 0) Interlocked.Increment(ref frames);
        };
        a.Start();
        b.Start();

        Assert.True(await Wait.UntilAsync(
            () => a.HasPeer(RoomSession.HashId("kentaroz-bbb"))
               && b.HasPeer(RoomSession.HashId("bane-aaa")), 8_000));

        // Regressao 0.6.24: RequestAborted encerrava o socket logo apos o
        // upgrade e os clientes reconectavam centenas de vezes por minuto.
        await Task.Delay(1_500);
        Assert.True(a.Connected);
        Assert.True(b.Connected);
        Assert.True(a.HasPeer(RoomSession.HashId("kentaroz-bbb")));
        Assert.True(b.HasPeer(RoomSession.HashId("bane-aaa")));

        var pcm = new byte[VoiceEngine.FrameBytes];
        double phase = 0;
        for (int frame = 0; frame < 60; frame++)
        {
            for (int i = 0; i < pcm.Length / 2; i++, phase++)
            {
                short sample = (short)(Math.Sin(phase * 2 * Math.PI * 440 / 48000) * 9000);
                pcm[i * 2] = (byte)sample;
                pcm[i * 2 + 1] = (byte)(sample >> 8);
            }
            a.SendVoice(pcm, 0, pcm.Length);
            await Task.Delay(10);
        }

        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref frames) >= 10, 5_000));
        Assert.Equal(RoomSession.HashId("bane-aaa"), sender);
    }

    private Process StartServer(string name, int port)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "Primicord.Server.dll");
        Assert.True(File.Exists(dll), "Primicord.Server.dll nao foi copiado para os testes.");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(dll);
        start.Environment["PRIMICORD_SERVER_DATA_DIR"] = Path.Combine(_dir, name);
        start.Environment["PRIMICORD_SERVER_URLS"] = $"http://127.0.0.1:{port}";
        Process process = Process.Start(start)!;
        _processes.Add(process);
        return process;
    }

    private static async Task WaitUntilReady(MiniServerStore store, Process process)
    {
        for (int i = 0; i < 40; i++)
        {
            if (process.HasExited)
            {
                string error = await process.StandardError.ReadToEndAsync();
                string output = await process.StandardOutput.ReadToEndAsync();
                throw new InvalidOperationException("Mini servidor saiu ao iniciar: " + error + output);
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            if (await store.IsAvailableAsync(timeout.Token)) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Mini servidor de teste nao iniciou.");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        foreach (Process process in _processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FixedEndpoints(IReadOnlyList<ClusterEndpoint> endpoints)
        : IClusterEndpointProvider
    {
        public Task<IReadOnlyList<ClusterEndpoint>> GetEndpointsAsync(CancellationToken ct = default)
            => Task.FromResult(endpoints);
    }

    private sealed class FailingFallback : IDocumentStore
    {
        private static InvalidOperationException Used() => new("Firestore nao deveria ser usado.");
        public Task<List<(string Id, Dictionary<string, object?> Fields)>> ListAsync(
            string path, int pageSize = 100, CancellationToken ct = default, string? orderBy = null)
            => Task.FromException<List<(string, Dictionary<string, object?>)>>(Used());
        public Task<Dictionary<string, object?>?> GetAsync(string path, CancellationToken ct = default)
            => Task.FromException<Dictionary<string, object?>?>(Used());
        public Task SetAsync(string path, Dictionary<string, object?> fields,
                             bool mergeFields = false, CancellationToken ct = default)
            => Task.FromException(Used());
        public Task<List<(string Id, Dictionary<string, object?> Fields)>> QueryAsync(
            string parent, string collection, string order, bool desc, int limit,
            CancellationToken ct = default)
            => Task.FromException<List<(string, Dictionary<string, object?>)>>(Used());
        public Task<List<(string Id, Dictionary<string, object?> Fields)>> QuerySinceAsync(
            string parent, string collection, string order, long since, int limit,
            CancellationToken ct = default)
            => Task.FromException<List<(string, Dictionary<string, object?>)>>(Used());
        public Task DeleteAsync(string path, CancellationToken ct = default)
            => Task.FromException(Used());
    }
}
