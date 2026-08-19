using System.Net;
using System.Text;
using Primicord.SetupShared;
using Xunit;

namespace Primicord.Tests;

public sealed class GroupActivationTests
{
    [Theory]
    [InlineData("  grupo primicord  ", "GRUPO-PRIMICORD")]
    [InlineData("Grupo_Primicord", "GRUPO-PRIMICORD")]
    [InlineData("GRUPO---PRIMICORD", "GRUPO-PRIMICORD")]
    public void NormalizaCodigoParaColarSemSurpresa(string input, string expected)
        => Assert.Equal(expected, GroupActivationClient.NormalizeCode(input));

    [Fact]
    public async Task TrocaCodigoPorCredencialSemPersistirSegredo()
    {
        string? received = null;
        using var http = new HttpClient(new StubHandler(async request =>
        {
            received = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK,
                """{"serverUrl":"http://100.64.2.73:8765","authKey":"tskey-auth-abc123456789012345678901234567890","activationId":"41114e2f-b231-4295-b8f0-c3c7b11af822"}""");
        })) { Timeout = TimeSpan.FromSeconds(2) };

        var activation = await GroupActivationClient.ActivateAsync(http,
            "https://activation.example", "grupo primicord",
            "2c9c70a3-3a08-49b9-a1cb-28f8ac1f6cf8");

        Assert.Equal("http://100.64.2.73:8765", activation.ServerUrl);
        Assert.StartsWith("tskey-auth-", activation.AuthKey);
        Assert.Contains("GRUPO-PRIMICORD", received);
        Assert.NotNull(received);
    }

    [Fact]
    public async Task TraduzCodigoRecusadoSemExporRespostaInterna()
    {
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(
            Json(HttpStatusCode.Unauthorized, """{"error":"invalid_code"}"""))));

        var error = await Assert.ThrowsAsync<GroupActivationException>(() =>
            GroupActivationClient.ActivateAsync(http, "https://activation.example", "errado",
                "2c9c70a3-3a08-49b9-a1cb-28f8ac1f6cf8"));

        Assert.Equal("invalid_code", error.ErrorCode);
        Assert.Equal("Codigo do grupo incorreto.", error.Message);
    }

    [Fact]
    public async Task RecusaAtivadorHttpForaDoProprioPc()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new Xunit.Sdk.XunitException(
            "nao deveria chamar a rede")));
        var error = await Assert.ThrowsAsync<GroupActivationException>(() =>
            GroupActivationClient.ActivateAsync(http, "http://activation.example", "grupo-certo",
                "2c9c70a3-3a08-49b9-a1cb-28f8ac1f6cf8"));
        Assert.Equal("invalid_endpoint", error.ErrorCode);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
