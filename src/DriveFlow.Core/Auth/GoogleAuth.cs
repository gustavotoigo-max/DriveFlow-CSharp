using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DriveFlow.Core.Drive;
using DriveFlow.Core.Storage;

namespace DriveFlow.Core.Auth;

/// <summary>
/// Login Google (loopback + PKCE), renovação do token e conta conectada. Port de auth.Auth.
/// A credencial fica em account.bin cifrada, no formato do google-auth usado pela versão Python.
/// </summary>
public sealed class GoogleAuth : DriveFlow.Core.Upload.IAccount
{
    public static readonly string[] Limited = ["https://www.googleapis.com/auth/drive.file"];
    public static readonly string[] Full = ["https://www.googleapis.com/auth/drive"];

    sealed class Credentials
    {
        public string Token = "";
        public string RefreshToken = "";
        public string TokenUri = OAuthClient.DefaultTokenUri;
        public string ClientId = "";
        public string ClientSecret = "";
        public string[] Scopes = [];
        public DateTime? Expiry;
        /// <summary>Renova um pouco antes de vencer, como o google-auth.</summary>
        public bool Valid => Token != "" && (Expiry is null || Expiry > DateTime.UtcNow.AddMinutes(4));
    }

    readonly string path;
    readonly IVault vault;
    readonly HttpMessageHandler transport;
    readonly SemaphoreSlim gate = new(1, 1);
    Credentials? credentials;
    bool savePending;

    public string Email { get; private set; } = "";
    public string AccountId { get; private set; } = "";
    public bool ReauthRequired { get; private set; }
    public bool Connected => credentials is not null;
    /// <summary>Abre o navegador no endereço de login; os testes trocam.</summary>
    public Action<string> OpenBrowser { get; init; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public GoogleAuth(IVault vault, string? path = null, HttpMessageHandler? transport = null)
    {
        this.vault = vault;
        this.path = path ?? Path.Combine(Database.DataDir(), "account.bin");
        this.transport = transport ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
    }

    HttpClient Plain() => new(transport, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };

    void Save()
    {
        var c = credentials!;
        var payload = new JsonObject
        {
            ["token"] = c.Token,
            ["refresh_token"] = c.RefreshToken,
            ["token_uri"] = c.TokenUri,
            ["client_id"] = c.ClientId,
            ["client_secret"] = c.ClientSecret,
            ["scopes"] = new JsonArray(c.Scopes.Select(s => (JsonNode?)s).ToArray()),
            ["universe_domain"] = "googleapis.com",
            ["account"] = "",
            ["driveflow_account"] = new JsonObject { ["email"] = Email, ["id"] = AccountId },
        };
        if (c.Expiry is { } expiry) payload["expiry"] = expiry.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        var temp = Path.ChangeExtension(path, ".tmp");
        File.WriteAllText(temp, vault.Seal(payload.ToJsonString()), Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Recupera a conta salva. Falso quando nunca houve login.</summary>
    public async Task<bool> Restore(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return false;
        credentials = null;
        try
        {
            var payload = JsonNode.Parse(vault.Open(File.ReadAllText(path, Encoding.UTF8)))!.AsObject();
            string S(string key) => payload[key]?.GetValue<string>() ?? "";
            credentials = new Credentials
            {
                Token = S("token"), RefreshToken = S("refresh_token"), ClientId = S("client_id"), ClientSecret = S("client_secret"),
                TokenUri = S("token_uri") is { Length: > 0 } uri ? uri : OAuthClient.DefaultTokenUri,
                Scopes = payload["scopes"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? [],
                Expiry = S("expiry") is { Length: > 0 } e ? DateTime.Parse(e, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : null,
            };
            var account = payload["driveflow_account"];
            Email = account?["email"]?.GetValue<string>() ?? "";
            AccountId = account?["id"]?.GetValue<string>() ?? "";
            if (Email == "" || AccountId == "") await Identify(ct).ConfigureAwait(false);
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            if (credentials is null) ReauthRequired = true;
            Email = AccountId = "";
            throw SafeError("Restaurar conexão", exc);
        }
        return true;
    }

    public static AuthException SafeError(string stage, Exception exc)
    {
        if (exc is AuthException auth) return auth;
        var detail = exc switch
        {
            HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } =>
                "Falha ao validar o certificado HTTPS. Verifique data/hora do Windows e eventual inspeção HTTPS do antivírus ou proxy.",
            HttpRequestException or TaskCanceledException or TemporaryException =>
                "Não foi possível alcançar o Google. Verifique a conexão e tente novamente.",
            IOException or UnauthorizedAccessException or SocketException or CryptographicException =>
                "O Windows não conseguiu acessar o arquivo, abrir a conexão local ou salvar a credencial.",
            OAuthError { Code: "access_denied" } => "A autorização foi recusada. Confira a conta e os usuários de teste no projeto Google Cloud.",
            OAuthError { Code: "invalid_grant" } => "A autorização expirou ou já foi utilizada. Inicie uma nova conexão.",
            OAuthError { Code: "invalid_client" } => "O Google recusou o cliente OAuth. Importe o JSON correto de Aplicativo para computador.",
            OAuthError { Code: "redirect_uri_mismatch" } => "A credencial não aceita o retorno local. Use um cliente OAuth de Aplicativo para computador.",
            _ => "A operação não foi concluída. Informe o código abaixo para diagnosticar.",
        };
        return new AuthException($"{stage}: {detail} [AUTH_{exc.GetType().Name}]");
    }

    /// <summary>Erro devolvido pelo servidor OAuth (campo "error").</summary>
    public sealed class OAuthError(string code, bool retryable) : Exception(code)
    {
        public string Code { get; } = code;
        public bool Retryable { get; } = retryable;
    }

    async Task<JsonObject> TokenRequest(string tokenUri, Dictionary<string, string> form, CancellationToken ct)
    {
        using var http = Plain();
        using var response = await http.PostAsync(tokenUri, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonObject? json = null;
        try { json = JsonNode.Parse(body) as JsonObject; } catch (JsonException) { }
        if (!response.IsSuccessStatusCode || json?["access_token"] is null)
        {
            var code = json?["error"]?.GetValue<string>() ?? $"http_{(int)response.StatusCode}";
            throw new OAuthError(code, (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests);
        }
        return json;
    }

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Abre o navegador, espera o retorno em 127.0.0.1 e troca o código pelo token.</summary>
    public async Task Login(OAuthClient client, bool full = true, CancellationToken ct = default)
    {
        var stage = "Obter autorização do Google";
        var previous = (credentials, Email, AccountId, ReauthRequired, savePending);
        var scopes = full ? Full : Limited;
        try
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = Base64Url(RandomNumberGenerator.GetBytes(24));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            var url = client.AuthUri + "?" + string.Join("&", new Dictionary<string, string>
            {
                ["response_type"] = "code", ["client_id"] = client.ClientId, ["redirect_uri"] = redirect,
                ["scope"] = string.Join(' ', scopes), ["state"] = state, ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256", ["access_type"] = "offline", ["prompt"] = "consent",
            }.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));
            OpenBrowser(url);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(180));
            var query = await LoopbackRedirect.Receive(listener, state, timeout.Token).ConfigureAwait(false);
            if (query.TryGetValue("error", out var error)) throw new OAuthError(error, false);
            if (!query.TryGetValue("state", out var returned) || returned != state)
                throw new AuthException("A resposta do Google não corresponde a este login. Tente de novo. [AUTH_STATE]");
            var token = await TokenRequest(client.TokenUri, new()
            {
                ["code"] = query.GetValueOrDefault("code", ""), ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret,
                ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier,
            }, timeout.Token).ConfigureAwait(false);
            // Aceita um conjunto maior de permissões, nunca um menor.
            var granted = (token["scope"]?.GetValue<string>() ?? string.Join(' ', scopes)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!scopes.All(granted.Contains))
                throw new AuthException("O Google não concedeu todas as permissões solicitadas. Conecte novamente e autorize o acesso ao Drive. [AUTH_SCOPE_MISSING]");
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                credentials = new Credentials
                {
                    Token = token["access_token"]!.GetValue<string>(), RefreshToken = token["refresh_token"]?.GetValue<string>() ?? "",
                    TokenUri = client.TokenUri, ClientId = client.ClientId, ClientSecret = client.ClientSecret, Scopes = scopes,
                    Expiry = DateTime.UtcNow.AddSeconds(token["expires_in"]?.GetValue<double>() ?? 3600),
                };
                Email = AccountId = "";
                ReauthRequired = savePending = false;
            }
            finally { gate.Release(); }
            stage = "Consultar conta e salvar conexão";
            await Identify(ct).ConfigureAwait(false);
        }
        catch (Exception exc) when (exc is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            (credentials, Email, AccountId, ReauthRequired, savePending) = previous;
            throw SafeError(stage, exc is OperationCanceledException ? new OAuthError("timeout", true) : exc);
        }
    }

    /// <summary>Token de acesso válido, renovando se preciso. rejected: token que o Google acabou de recusar.</summary>
    public async Task<string> AccessToken(string? rejected = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var c = credentials ?? throw new AuthException("Conecte sua conta Google. [AUTH_NOT_CONNECTED]");
            if (ReauthRequired) throw new AuthException("Reconecte a conta: autorização recusada pelo Google. [AUTH_REAUTH_REQUIRED]");
            if (!c.Valid || (rejected is not null && c.Token == rejected))
            {
                JsonObject token;
                try
                {
                    token = await TokenRequest(c.TokenUri, new()
                    {
                        ["refresh_token"] = c.RefreshToken, ["client_id"] = c.ClientId, ["client_secret"] = c.ClientSecret,
                        ["grant_type"] = "refresh_token",
                    }, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException) { throw new TemporaryException("Falha temporária ao renovar a conexão Google. [AUTH_TRANSPORT]"); }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new TemporaryException("Falha temporária ao renovar a conexão Google. [AUTH_TRANSPORT]"); }
                catch (OAuthError exc) when (exc.Retryable) { throw new TemporaryException("Google temporariamente indisponível para renovar a conexão. [AUTH_REFRESH_RETRYABLE]"); }
                catch (OAuthError exc)
                {
                    ReauthRequired = true;
                    throw new AuthException(exc.Code == "invalid_grant"
                        ? "O Google expirou ou revogou a autorização. Reconecte a conta. Projetos OAuth externos em modo Teste podem expirar em 7 dias. A credencial local e a fila foram preservadas. [AUTH_REAUTH_REQUIRED]"
                        : "O Google recusou a renovação da autorização. Confira a credencial OAuth e reconecte a conta. [AUTH_REFRESH_REJECTED]");
                }
                c.Token = token["access_token"]!.GetValue<string>();
                c.Expiry = DateTime.UtcNow.AddSeconds(token["expires_in"]?.GetValue<double>() ?? 3600);
                if (token["refresh_token"]?.GetValue<string>() is { Length: > 0 } refresh) c.RefreshToken = refresh;
                savePending = true;
            }
            if (savePending)
            {
                try { Save(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    throw new TemporaryException("Não foi possível salvar a credencial renovada. [AUTH_SAVE_RETRY]");
                }
                savePending = false;
            }
            return c.Token;
        }
        finally { gate.Release(); }
    }

    /// <summary>HttpClient que assina cada pedido e tenta de novo uma vez após um 401.</summary>
    public HttpClient CreateClient() => new(new AuthorizingHandler(this) { InnerHandler = transport }, disposeHandler: false)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    sealed class AuthorizingHandler(GoogleAuth owner) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var token = await owner.AccessToken(ct: ct).ConfigureAwait(false);
            // Os corpos usados (bytes, JSON e ThrottledContent) podem ser serializados de novo após um 401.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
            response.Dispose();
            token = await owner.AccessToken(token, ct).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            response = await base.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
            response.Dispose();
            owner.ReauthRequired = true;
            throw new AuthException("O Google recusou a autorização após renovação. Reconecte a conta. [AUTH_HTTP_401]");
        }
    }

    async Task Identify(CancellationToken ct)
    {
        using var http = CreateClient();
        using var response = await http.GetAsync($"{DriveClient.Api}/about?fields=user(emailAddress,permissionId)", ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var reasons = await DriveClient.ErrorReasons(response, ct).ConfigureAwait(false);
            if (reasons.Overlaps(["accessNotConfigured", "SERVICE_DISABLED"]))
                throw new AuthException("A Google Drive API está desativada no projeto da credencial. Ative-a em Google Cloud → APIs e serviços → Biblioteca e tente conectar novamente. [AUTH_DRIVE_API_DISABLED]");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new AuthException("O Google autorizou o login, mas negou a consulta ao Drive. Confira as permissões concedidas e possíveis restrições da organização. [AUTH_DRIVE_HTTP_403]");
            throw new AuthException($"Não foi possível consultar a conta no Google Drive. Tente novamente. [AUTH_DRIVE_HTTP_{(int)response.StatusCode}]");
        }
        var user = (await response.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false))?["user"];
        var email = user?["emailAddress"]?.GetValue<string>() ?? "";
        var id = user?["permissionId"]?.GetValue<string>() ?? "";
        if (email == "" || id == "")
            throw new AuthException("A resposta do Drive não trouxe a identificação da conta. [AUTH_ACCOUNT_INCOMPLETE]");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            (Email, AccountId) = (email, id);
            Save();
        }
        finally { gate.Release(); }
    }

    public void Logout()
    {
        gate.Wait();
        try
        {
            File.Delete(path);
            File.Delete(Path.ChangeExtension(path, ".tmp"));
            credentials = null;
            Email = AccountId = "";
            ReauthRequired = savePending = false;
        }
        finally { gate.Release(); }
    }
}
