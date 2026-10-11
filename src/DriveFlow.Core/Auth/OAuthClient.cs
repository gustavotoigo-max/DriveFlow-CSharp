using System.Text.Json;

namespace DriveFlow.Core.Auth;

/// <summary>Cliente OAuth do tipo "Aplicativo para computador" (o JSON baixado do Google Cloud).</summary>
public sealed record OAuthClient(string ClientId, string ClientSecret, string AuthUri, string TokenUri)
{
    public const string DefaultAuthUri = "https://accounts.google.com/o/oauth2/auth";
    public const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    public static OAuthClient Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("installed", out var c))
                throw new AuthException("Importe uma credencial OAuth do tipo Aplicativo para computador. [AUTH_CLIENT_TYPE]");
            string Get(string key, string fallback = "") =>
                c.TryGetProperty(key, out var v) && v.GetString() is { Length: > 0 } s ? s : fallback;
            var client = new OAuthClient(Get("client_id"), Get("client_secret"), Get("auth_uri", DefaultAuthUri), Get("token_uri", DefaultTokenUri));
            if (client.ClientId == "" || client.ClientSecret == "")
                throw new AuthException("A credencial OAuth está incompleta. [AUTH_CLIENT_INCOMPLETE]");
            return client;
        }
        catch (JsonException)
        {
            throw new AuthException("O arquivo de credencial não é um JSON válido. [AUTH_CLIENT_JSON]");
        }
    }

    /// <summary>
    /// Cliente embutido no build (recurso oauth_client.json, fora do Git), ou nulo para pedir um JSON.
    /// O Google trata o segredo de apps instalados como não confidencial.
    /// </summary>
    public static OAuthClient? Bundled()
    {
        var assembly = typeof(OAuthClient).Assembly;
        using var stream = assembly.GetManifestResourceStream("DriveFlow.oauth_client.json");
        if (stream is null) return null;
        try { return Parse(new StreamReader(stream).ReadToEnd()); }
        catch (AuthException) { return null; }
    }
}
