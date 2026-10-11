using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DriveFlow.Core.Storage;

namespace DriveFlow.Core.Tests;

/// <summary>Cofre de teste: só Base64, sem DPAPI.</summary>
sealed class PlainVault : IVault
{
    public string Seal(string value) => value == "" ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes("sealed:" + value));
    public string Open(string value) => value == "" ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(value))["sealed:".Length..];
}

/// <summary>Imitação da Drive API v3 com sessões retomáveis em memória.</summary>
sealed class FakeDrive : HttpMessageHandler
{
    public sealed class Upload
    {
        public required string Id;
        public required string Name;
        public required string Parent;
        public long Size;
        public MemoryStream Data = new();
    }

    public readonly Dictionary<string, Upload> Sessions = [];
    public readonly Dictionary<string, Upload> Files = [];
    public readonly List<(string Parent, string Name, string MimeType)> Existing = [];
    public readonly List<string> Log = [];
    /// <summary>Respostas forçadas para os próximos pedidos que casarem com o prefixo "METHOD path".</summary>
    public readonly List<(string Match, HttpStatusCode Code)> Failures = [];
    /// <summary>Ao receber um bloco, aceita só esta quantidade de bytes (simula confirmação parcial).</summary>
    public long? AcceptOnly;
    int ids;

    static HttpResponseMessage Json(JsonNode body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var key = $"{request.Method} {uri.AbsolutePath}";
        Log.Add(key);
        var failure = Failures.FindIndex(f => key.StartsWith(f.Match));
        if (failure >= 0)
        {
            var code = Failures[failure].Code;
            Failures.RemoveAt(failure);
            return new HttpResponseMessage(code) { Content = new StringContent("{}") };
        }
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (key == "GET /drive/v3/files/generateIds")
            return Json(new JsonObject { ["ids"] = new JsonArray($"gen{++ids}") });
        if (key == "GET /drive/v3/files")
        {
            var q = query["q"]!;
            var files = new JsonArray();
            foreach (var (parent, name, mime) in Existing)
                if (q.Contains($"'{parent}' in parents") && q.Contains($"name='{name}'"))
                    files.Add(new JsonObject { ["id"] = "x", ["name"] = name, ["mimeType"] = mime });
            return Json(new JsonObject { ["files"] = files });
        }
        if (request.Method == HttpMethod.Get && uri.AbsolutePath.StartsWith("/drive/v3/files/"))
        {
            var id = uri.AbsolutePath["/drive/v3/files/".Length..];
            if (!Files.TryGetValue(id, out var f)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            return Json(new JsonObject { ["id"] = id, ["name"] = f.Name, ["size"] = f.Size.ToString(), ["trashed"] = false });
        }
        if (key == "POST /upload/drive/v3/files")
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var session = $"s{Sessions.Count + 1}";
            Sessions[session] = new Upload
            {
                Id = body["id"]!.GetValue<string>(), Name = body["name"]!.GetValue<string>(),
                Parent = body["parents"]![0]!.GetValue<string>(),
                Size = long.Parse(request.Headers.GetValues("X-Upload-Content-Length").First()),
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Location = new Uri($"https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id={session}");
            return response;
        }
        if (request.Method == HttpMethod.Put && uri.AbsolutePath == "/upload/drive/v3/files")
        {
            if (!Sessions.TryGetValue(query["upload_id"]!, out var upload)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var range = request.Content!.Headers.ContentRange!;
            var bytes = await request.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 0)
            {
                Assert.Equal(upload.Data.Length, range.From);
                var take = AcceptOnly is { } only ? (int)Math.Min(only, bytes.Length) : bytes.Length;
                upload.Data.Write(bytes, 0, take);
            }
            if (upload.Data.Length == upload.Size)
            {
                Files[upload.Id] = upload;
                return Json(new JsonObject { ["id"] = upload.Id, ["size"] = upload.Size.ToString() });
            }
            var partial = new HttpResponseMessage((HttpStatusCode)308);
            if (upload.Data.Length > 0) partial.Headers.TryAddWithoutValidation("Range", $"bytes=0-{upload.Data.Length - 1}");
            return partial;
        }
        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }
}
