using System.Buffers;
using System.Net;
using System.Text.Json;

namespace Primicord.SetupShared;

public sealed record GroupActivation(string ServerUrl, string AuthKey, string ActivationId);

public static class GroupActivationDefaults
{
    // Endpoint publico. O segredo do codigo e as credenciais do Tailscale ficam
    // somente no Worker; este endereco pode estar no app e no Setup sem risco.
    public const string Endpoint =
        "https://primicord-activation.primicord-primitivos-bane.workers.dev";
    public const string ClientVersion = "0.6.24";
}

public sealed class GroupActivationException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public string ErrorCode { get; }

    public GroupActivationException(string errorCode, string message,
                                    HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }
}

/// <summary>
/// Troca o codigo curto por uma auth key no ativador publico. O servidor devolve
/// uma credencial de uso unico quando esta configurado com OAuth do Tailscale;
/// o Setup nunca carrega OAuth secret nem chave permanente.
/// </summary>
public static class GroupActivationClient
{
    public static string NormalizeCode(string value)
        => string.Join('-', value.Trim().ToUpperInvariant()
            .Split(new[] { ' ', '\t', '\r', '\n', '_', '-' },
                   StringSplitOptions.RemoveEmptyEntries));

    public static async Task<GroupActivation> ActivateAsync(
        HttpClient http, string endpoint, string code, string installId,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback))
            throw new GroupActivationException("invalid_endpoint",
                "O endereco do ativador do grupo nao e HTTPS.");
        if (!Guid.TryParse(installId, out _))
            throw new ArgumentException("O identificador da instalacao nao e valido.", nameof(installId));

        string normalized = NormalizeCode(code);
        if (normalized.Length is < 4 or > 96)
            throw new GroupActivationException("invalid_code", "O codigo do grupo nao e valido.");

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("code", normalized);
            writer.WriteString("installId", installId);
            writer.WriteString("version", GroupActivationDefaults.ClientVersion);
            writer.WriteEndObject();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(baseUri, "/v1/activate"));
        request.Headers.UserAgent.ParseAdd("Primicord-Setup/" + GroupActivationDefaults.ClientVersion);
        request.Content = new ByteArrayContent(buffer.WrittenSpan.ToArray());
        request.Content.Headers.ContentType = new("application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                                            cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GroupActivationException("timeout",
                "O ativador do grupo demorou demais para responder.");
        }
        catch (HttpRequestException ex)
        {
            throw new GroupActivationException("offline",
                "Nao foi possivel falar com o ativador do grupo. Verifique a internet.", null, ex);
        }

        using (response)
        {
            byte[] payload = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                                                   .ConfigureAwait(false);
            if (payload.Length > 32 * 1024)
                throw new GroupActivationException("invalid_response",
                    "O ativador devolveu uma resposta grande demais.", response.StatusCode);

            string? errorCode = null;
            JsonDocument? document = null;
            try { document = JsonDocument.Parse(payload); }
            catch (JsonException) { }
            using (document)
            {
                if (document?.RootElement.TryGetProperty("error", out var error) == true
                    && error.ValueKind == JsonValueKind.String)
                    errorCode = error.GetString();

                if (!response.IsSuccessStatusCode)
                    throw ErrorFor(errorCode, response.StatusCode);

                if (document == null || document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new GroupActivationException("invalid_response",
                        "O ativador devolveu uma resposta invalida.", response.StatusCode);

                string serverUrl = ReadString(document.RootElement, "serverUrl");
                string authKey = ReadString(document.RootElement, "authKey");
                string activationId = ReadString(document.RootElement, "activationId");
                if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server)
                    || server.Scheme != Uri.UriSchemeHttp || string.IsNullOrWhiteSpace(server.Host)
                    || server.Port != 8765)
                    throw new GroupActivationException("invalid_response",
                        "O ativador devolveu um mini servidor invalido.", response.StatusCode);
                if (!authKey.StartsWith("tskey-auth-", StringComparison.Ordinal)
                    || authKey.Length < 30)
                    throw new GroupActivationException("invalid_response",
                        "O ativador devolveu uma credencial invalida.", response.StatusCode);
                if (!Guid.TryParse(activationId, out _))
                    throw new GroupActivationException("invalid_response",
                        "O ativador devolveu uma identificacao invalida.", response.StatusCode);

                return new GroupActivation(serverUrl.TrimEnd('/'), authKey, activationId);
            }
        }
    }

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static GroupActivationException ErrorFor(string? code, HttpStatusCode status)
        => code switch
        {
            "invalid_code" => new(code, "Codigo do grupo incorreto.", status),
            "too_many_attempts" => new(code,
                "Muitas tentativas. Aguarde um minuto antes de tentar novamente.", status),
            "service_not_configured" => new(code,
                "O ativador do grupo ainda nao foi configurado pelo administrador.", status),
            "activation_temporarily_unavailable" => new(code,
                "O Tailscale nao liberou uma entrada agora. Tente novamente em instantes.", status),
            _ => new(code ?? "activation_failed",
                "O ativador do grupo recusou a instalacao.", status),
        };
}
