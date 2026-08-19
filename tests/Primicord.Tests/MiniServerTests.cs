using System.Net;
using System.Text.Json.Nodes;
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
}
