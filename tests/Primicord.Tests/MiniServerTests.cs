using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Primicord.Server;
using Xunit;

namespace Primicord.Tests;

[Collection("rede")]
public sealed class MiniServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "primicord-server-test-" + Guid.NewGuid().ToString("N"));

    public MiniServerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Sqlite_faz_crud_merge_lista_query_e_limpeza_em_cascata()
    {
        var db = new SqliteDocumentStore(Path.Combine(_dir, "test.db"));
        await db.InitializeAsync();

        await db.SetAsync("pc_rooms/sala-1", new JsonObject
        {
            ["name"] = "SALA 01",
            ["createdBy"] = "bane",
        }, merge: false);
        await db.SetAsync("pc_rooms/sala-1/peers/bane-1", new JsonObject
        {
            ["nick"] = "bane",
            ["lastSeen"] = 200L,
        }, merge: false);
        await db.SetAsync("pc_rooms/sala-1", new JsonObject { ["status"] = "ao vivo" }, merge: true);

        JsonObject? room = await db.GetAsync("pc_rooms/sala-1");
        Assert.NotNull(room);
        Assert.Equal("SALA 01", room["name"]!.GetValue<string>());
        Assert.Equal("ao vivo", room["status"]!.GetValue<string>());

        var rooms = await db.ListAsync("pc_rooms", 100, null);
        Assert.Single(rooms);
        Assert.Equal("sala-1", rooms[0].Id);

        var peers = await db.QueryAsync(new StoreQuery
        {
            ParentPath = "pc_rooms/sala-1",
            CollectionId = "peers",
            OrderField = "lastSeen",
            SinceInclusive = 150,
            Limit = 10,
        });
        Assert.Single(peers);
        Assert.Equal("bane-1", peers[0].Id);

        await db.DeleteAsync("pc_rooms/sala-1");
        Assert.Null(await db.GetAsync("pc_rooms/sala-1"));
        Assert.Empty(await db.ListAsync("pc_rooms/sala-1/peers", 100, null));
    }

    [Fact]
    public async Task Snapshot_replica_dados_e_tombstone_impede_ressurreicao()
    {
        var first = new SqliteDocumentStore(Path.Combine(_dir, "first.db"));
        var second = new SqliteDocumentStore(Path.Combine(_dir, "second.db"));
        await first.InitializeAsync();
        await second.InitializeAsync();

        await first.SetAsync("pc_rooms/sala-1", new JsonObject { ["name"] = "Sala 1" }, false);
        List<SnapshotDocument> stale = await first.ExportAsync();
        await second.ImportAsync(stale);
        Assert.NotNull(await second.GetAsync("pc_rooms/sala-1"));

        await first.DeleteAsync("pc_rooms/sala-1");
        await second.ImportAsync(await first.ExportAsync());
        Assert.Null(await second.GetAsync("pc_rooms/sala-1"));

        await second.ImportAsync(stale);
        Assert.Null(await second.GetAsync("pc_rooms/sala-1"));
        Assert.Contains(await second.ExportAsync(), row => row.Path == "pc_rooms/sala-1" && row.Deleted);
    }

    [Fact]
    public async Task Atualizacao_adiciona_tombstones_ao_banco_antigo_sem_perder_salas()
    {
        string path = Path.Combine(_dir, "legacy.db");
        await using (var connection = new SqliteConnection("Data Source=" + path))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE documents (
                    path TEXT PRIMARY KEY NOT NULL,
                    fields_json TEXT NOT NULL,
                    updated_at INTEGER NOT NULL
                );
                INSERT INTO documents(path, fields_json, updated_at)
                VALUES('pc_rooms/legada', '{"name":"Legada"}', 1);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var upgraded = new SqliteDocumentStore(path);
        await upgraded.InitializeAsync();

        JsonObject? room = await upgraded.GetAsync("pc_rooms/legada");
        Assert.Equal("Legada", room!["name"]!.GetValue<string>());
        await upgraded.DeleteAsync("pc_rooms/legada");
        Assert.Null(await upgraded.GetAsync("pc_rooms/legada"));
    }

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("192.168.0.10", false)]
    public void Servidor_restringe_acesso_a_tailnet(string text, bool expected)
        => Assert.Equal(expected, TailnetAddress.IsAllowed(IPAddress.Parse(text)));

    [Fact]
    public void Servidor_recusa_conexao_sem_endereco_identificado()
        => Assert.False(TailnetAddress.IsAllowed(null));

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.0.1", false)]
    public void Cliente_reconhece_endereco_do_tailscale(string text, bool expected)
        => Assert.Equal(expected, TailscaleIntegration.IsTailnetAddress(IPAddress.Parse(text)));

    [Fact]
    public void Descoberta_inclui_self_e_somente_peers_online_em_ordem_de_ip()
    {
        string json = """
        {
          "BackendState": "Running",
          "TailscaleIPs": ["fd7a:115c:a1e0::1", "100.100.8.10"],
          "Self": { "DNSName": "vitinho.tail.test." },
          "Peer": {
            "node-bane": {
              "Online": true,
              "TailscaleIPs": ["100.64.2.73"],
              "DNSName": "pc-bane.tail.test."
            },
            "node-offline": {
              "Online": false,
              "TailscaleIPs": ["100.70.0.1"],
              "DNSName": "offline.tail.test."
            }
          }
        }
        """;

        List<TailnetNode> nodes = TailscaleIntegration.ParseOnlineNodes(json);

        Assert.Equal(2, nodes.Count);
        Assert.Equal("100.64.2.73", nodes[0].Address);
        Assert.False(nodes[0].IsSelf);
        Assert.Equal("100.100.8.10", nodes[1].Address);
        Assert.True(nodes[1].IsSelf);
    }
}
