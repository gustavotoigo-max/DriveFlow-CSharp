using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DriveFlow.Core.Auth;

namespace DriveFlow.Core.Tests;

public sealed class AuthTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("driveflow-test-").FullName;
    readonly PlainVault vault = new();

    public void Dispose() => Directory.Delete(dir, true);

    /// <summary>Google de mentira: token, about e uma rota protegida que recusa o primeiro token.</summary>
    sealed class FakeGoogle : HttpMessageHandler
    {
        public int Refreshes;
        public string? RefreshError;
        public string Valid = "fresh";
        public readonly List<string> Seen = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/token")
            {
                var form = await request.Content!.ReadAsStringAsync(ct);
                if (RefreshError is not null)
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent($"{{\"error\":\"{RefreshError}\"}}") };
                Refreshes++;
                Assert.Contains(form.Contains("grant_type=refresh_token") ? "refresh_token=r1" : "code_verifier=", form);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"access_token\":\"{Valid}\",\"expires_in\":3600,\"refresh_token\":\"r1\",\"scope\":\"https://www.googleapis.com/auth/drive\"}}"),
                };
            }
            var token = request.Headers.Authorization?.Parameter;
            Seen.Add(token ?? "");
            if (token != Valid) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (path == "/drive/v3/about")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"user\":{\"emailAddress\":\"g@gmail.com\",\"permissionId\":\"perm1\"}}"),
                };
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    string SavePythonCredential(string token, DateTime expiry, bool withAccount = true)
    {
        // Formato de google.oauth2.credentials.Credentials.to_json() + conta do DriveFlow.
        var payload = new JsonObject
        {
            ["token"] = token, ["refresh_token"] = "r1", ["token_uri"] = "https://oauth2.googleapis.com/token",
            ["client_id"] = "cid", ["client_secret"] = "sec", ["scopes"] = new JsonArray("https://www.googleapis.com/auth/drive"),
            ["universe_domain"] = "googleapis.com", ["account"] = "", ["expiry"] = expiry.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'"),
        };
        if (withAccount) payload["driveflow_account"] = new JsonObject { ["email"] = "g@gmail.com", ["id"] = "perm1" };
        var path = Path.Combine(dir, "account.bin");
        File.WriteAllText(path, vault.Seal(payload.ToJsonString()));
        return path;
    }

    [Fact]
    public async Task Restores_the_python_credential_without_network()
    {
        var google = new FakeGoogle();
        var auth = new GoogleAuth(vault, SavePythonCredential("fresh", DateTime.UtcNow.AddHours(1)), google);

        Assert.True(await auth.Restore());

        Assert.Equal("perm1", auth.AccountId);
        Assert.Equal("g@gmail.com", auth.Email);
        Assert.Equal(0, google.Refreshes);
        Assert.Equal("fresh", await auth.AccessToken());
    }

    [Fact]
    public async Task Refreshes_after_a_401_and_saves_the_new_token()
    {
        var google = new FakeGoogle();
        var path = SavePythonCredential("old", DateTime.UtcNow.AddHours(1));
        var auth = new GoogleAuth(vault, path, google);
        await auth.Restore();

        using var http = auth.CreateClient();
        var response = await http.PostAsync("https://www.googleapis.com/drive/v3/files", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["old", "fresh"], google.Seen);
        Assert.Equal(1, google.Refreshes);
        Assert.Contains("\"token\":\"fresh\"", vault.Open(File.ReadAllText(path)));
    }

    [Fact]
    public async Task Revoked_refresh_token_requires_reconnecting()
    {
        var google = new FakeGoogle { RefreshError = "invalid_grant" };
        var auth = new GoogleAuth(vault, SavePythonCredential("old", DateTime.UtcNow.AddHours(-1)), google);
        await auth.Restore();

        var error = await Assert.ThrowsAsync<AuthException>(() => auth.AccessToken());

        Assert.Contains("AUTH_REAUTH_REQUIRED", error.Message);
        Assert.True(auth.ReauthRequired);
    }

    [Fact]
    public async Task Login_uses_pkce_and_the_loopback_redirect()
    {
        var google = new FakeGoogle();
        var path = Path.Combine(dir, "account.bin");
        string? opened = null;
        var auth = new GoogleAuth(vault, path, google)
        {
            OpenBrowser = url =>
            {
                opened = url;
                var query = LoopbackRedirect.Parse(new Uri(url).Query.TrimStart('?'));
                Assert.Equal("S256", query["code_challenge_method"]);
                Assert.Equal("https://www.googleapis.com/auth/drive", query["scope"]);
                // O "navegador" volta para o DriveFlow com o código.
                _ = Task.Run(async () =>
                {
                    using var browser = new HttpClient();
                    await browser.GetStringAsync($"{query["redirect_uri"]}favicon.ico");
                    await browser.GetStringAsync($"{query["redirect_uri"]}?code=abc&state={Uri.EscapeDataString(query["state"])}");
                });
            },
        };
        var client = new OAuthClient("cid", "sec", "https://accounts.google.com/o/oauth2/auth", "https://oauth2.googleapis.com/token");

        await auth.Login(client);

        Assert.NotNull(opened);
        Assert.StartsWith("http://127.0.0.1:", LoopbackRedirect.Parse(new Uri(opened!).Query.TrimStart('?'))["redirect_uri"]);
        Assert.Equal("perm1", auth.AccountId);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Parses_the_desktop_client_json()
    {
        var client = OAuthClient.Parse("{\"installed\":{\"client_id\":\"cid\",\"client_secret\":\"sec\"}}");
        Assert.Equal("https://oauth2.googleapis.com/token", client.TokenUri);
        Assert.Throws<AuthException>(() => OAuthClient.Parse("{\"web\":{}}"));
    }
}
