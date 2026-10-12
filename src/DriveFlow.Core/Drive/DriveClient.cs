using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DriveFlow.Core.Storage;

namespace DriveFlow.Core.Drive;

public sealed record DriveFile(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mimeType")] string? MimeType = null,
    [property: JsonPropertyName("size")] string? Size = null,
    [property: JsonPropertyName("trashed")] bool Trashed = false,
    [property: JsonPropertyName("webViewLink")] string? WebViewLink = null)
{
    public const string FolderType = "application/vnd.google-apps.folder";
    public bool IsFolder => MimeType == FolderType;
}

/// <summary>Chamadas da Drive API v3 usadas pelo DriveFlow. O HttpClient já vem autenticado.</summary>
public sealed partial class DriveClient(HttpClient http)
{
    public const string Api = "https://www.googleapis.com/drive/v3";
    public const string UploadApi = "https://www.googleapis.com/upload/drive/v3/files";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    static string Query(IEnumerable<(string Key, string Value)> args) =>
        string.Join("&", args.Select(a => Uri.EscapeDataString(a.Key) + "=" + Uri.EscapeDataString(a.Value)));

    async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new TemporaryException("Conexão interrompida ou tempo limite excedido.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TemporaryException("Conexão interrompida ou tempo limite excedido.");
        }
        var code = (int)response.StatusCode;
        if (code == 429 || code >= 500)
        {
            response.Dispose();
            throw new TemporaryException($"Google temporariamente indisponível (HTTP {code}).");
        }
        if (code == 403)
        {
            var reasons = await ErrorReasons(response, ct).ConfigureAwait(false);
            if (reasons.Overlaps(["rateLimitExceeded", "userRateLimitExceeded", "backendError"]))
            {
                response.Dispose();
                throw new TemporaryException("Limite temporário do Google. Aguardando nova tentativa.");
            }
        }
        return response;
    }

    public static async Task<HashSet<string>> ErrorReasons(HttpResponseMessage response, CancellationToken ct)
    {
        var reasons = new HashSet<string>();
        try
        {
            await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var error = doc.RootElement.GetProperty("error");
            foreach (var list in new[] { "errors", "details" })
                if (error.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var item in items.EnumerateArray())
                        if (item.TryGetProperty("reason", out var reason) && reason.GetString() is { } r) reasons.Add(r);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
        return reasons;
    }

    static async Task<T> Checked<T>(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new DriveException($"Google retornou HTTP {(int)response.StatusCode}. Verifique acesso, espaço e limites da conta.");
            return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false)
                ?? throw new DriveException("Resposta vazia do Google.");
        }
    }

    sealed record FileList(
        [property: JsonPropertyName("files")] List<DriveFile>? Files,
        [property: JsonPropertyName("nextPageToken")] string? NextPageToken);

    public async Task<List<DriveFile>> ListFiles(string query, CancellationToken ct = default)
    {
        var files = new List<DriveFile>();
        string? token = null;
        do
        {
            var args = new List<(string, string)>
            {
                ("q", query), ("fields", "nextPageToken,files(id,name,size,mimeType)"), ("pageSize", "1000"),
                ("supportsAllDrives", "true"), ("includeItemsFromAllDrives", "true"),
            };
            if (token is not null) args.Add(("pageToken", token));
            var page = await Checked<FileList>(await Send(new(HttpMethod.Get, $"{Api}/files?{Query(args)}"), ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            files.AddRange(page.Files ?? []);
            token = page.NextPageToken;
        } while (!string.IsNullOrEmpty(token));
        return files.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task<List<DriveFile>> Folders(string parent, CancellationToken ct = default) =>
        ListFiles($"'{Escape(parent)}' in parents and trashed=false and mimeType='{DriveFile.FolderType}'", ct);

    public async Task<List<DriveFile>> Children(string parent, CancellationToken ct = default) =>
        (await ListFiles($"'{Escape(parent)}' in parents and trashed=false", ct).ConfigureAwait(false))
            .OrderBy(f => !f.IsFolder).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Nomes na pasta que contêm o prefixo (volumes que já existem no destino).</summary>
    public async Task<List<string>> NamesStarting(string parent, string prefix, CancellationToken ct = default) =>
        (await ListFiles($"'{Escape(parent)}' in parents and trashed=false and name contains '{Escape(prefix)}'", ct).ConfigureAwait(false))
            .Select(f => f.Name).ToList();

    public Task<List<DriveFile>> Duplicates(string parent, string name, CancellationToken ct = default) =>
        ListFiles($"'{Escape(parent)}' in parents and trashed=false and name='{Escape(name)}'", ct);

    public async Task<DriveFile> CreateFolder(string parent, string name, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/files?fields=id,name&supportsAllDrives=true")
        {
            Content = JsonContent.Create(new { name, parents = new[] { parent }, mimeType = DriveFile.FolderType }),
        };
        return await Checked<DriveFile>(await Send(request, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    /// <summary>Pasta existente com esse nome no destino, ou uma nova.</summary>
    public async Task<string> Folder(string parent, string name, CancellationToken ct = default)
    {
        foreach (var row in await Duplicates(parent, name, ct).ConfigureAwait(false))
            if (row.IsFolder) return row.Id;
        return (await CreateFolder(parent, name, ct).ConfigureAwait(false)).Id;
    }

    sealed record Ids([property: JsonPropertyName("ids")] List<string> Values);

    public async Task<string> GenerateId(CancellationToken ct = default) =>
        (await Checked<Ids>(await Send(new(HttpMethod.Get, $"{Api}/files/generateIds?count=1&space=drive&type=files"), ct).ConfigureAwait(false), ct).ConfigureAwait(false)).Values[0];

    public async Task<DriveFile?> Metadata(string id, CancellationToken ct = default)
    {
        var response = await Send(new(HttpMethod.Get, $"{Api}/files/{Uri.EscapeDataString(id)}?fields=id,size,name,trashed,webViewLink&supportsAllDrives=true"), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        return await Checked<DriveFile>(response, ct).ConfigureAwait(false);
    }

    /// <summary>Abre a sessão retomável e devolve o endereço dela.</summary>
    public async Task<string> Start(UploadItem item, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{UploadApi}?uploadType=resumable&fields=id,size&supportsAllDrives=true")
        {
            Content = JsonContent.Create(new { id = item.RemoteId, name = item.Name, parents = new[] { item.FolderId } }),
        };
        request.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
        request.Headers.Add("X-Upload-Content-Length", item.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await Send(request, ct).ConfigureAwait(false);
        var code = (int)response.StatusCode;
        if (code is not (200 or 201))
            throw new DriveException($"Google retornou HTTP {code}. Verifique acesso, espaço e limites da conta.");
        var uri = response.Headers.Location?.ToString() ?? "";
        ValidateUri(uri);
        return uri;
    }

    /// <summary>Só aceita sessões no próprio Google, para o token nunca ir a outro endereço.</summary>
    public static void ValidateUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != "https"
            || parsed.Host != "www.googleapis.com" || parsed.Port != 443 || parsed.UserInfo.Length > 0)
            throw new DriveException("Endereço de sessão inválido.");
    }

    [GeneratedRegex(@"^bytes=0-(\d+)$")]
    private static partial Regex RangePattern();

    /// <summary>
    /// Sem conteúdo, pergunta quantos bytes o Google já tem. Com conteúdo, envia o bloco a partir de offset.
    /// Devolve os bytes confirmados e se o arquivo terminou.
    /// </summary>
    public async Task<(long Received, bool Complete)> Transfer(string uri, long size, long? offset = null, HttpContent? chunk = null, CancellationToken ct = default)
    {
        ValidateUri(uri);
        var length = chunk?.Headers.ContentLength ?? 0;
        var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = chunk ?? new ByteArrayContent([]) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = length;
        request.Content.Headers.ContentRange = offset is null || length == 0
            ? new ContentRangeHeaderValue(size) { Unit = "bytes" }
            : new ContentRangeHeaderValue(offset.Value, offset.Value + length - 1, size) { Unit = "bytes" };
        using var response = await Send(request, ct).ConfigureAwait(false);
        var code = (int)response.StatusCode;
        if (code is 404 or 410) throw new ExpiredSessionException();
        if (code == 308)
        {
            long received = 0;
            if (response.Headers.TryGetValues("Range", out var values))
            {
                var match = RangePattern().Match(values.First());
                if (!match.Success) throw new DriveException("Resposta de progresso inválida do Google.");
                received = long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 1;
            }
            if (received > size) throw new DriveException("Progresso remoto maior que o arquivo.");
            return (received, false);
        }
        if (!response.IsSuccessStatusCode)
            throw new DriveException($"Google retornou HTTP {code}. Verifique acesso, espaço e limites da conta.");
        return (size, true);
    }
}
