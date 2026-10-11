using System.Net.Sockets;
using System.Text;

namespace DriveFlow.Core.Auth;

/// <summary>
/// Recebe o retorno do Google em http://127.0.0.1:porta/ com um TcpListener simples,
/// que não precisa de permissão de administrador como o HttpListener.
/// </summary>
public static class LoopbackRedirect
{
    const string Page = "<!doctype html><meta charset=utf-8><title>DriveFlow</title>" +
        "<p style=\"font-family:Segoe UI,sans-serif\">Resposta recebida do Google. Volte ao DriveFlow para conferir se a conexão foi concluída. Você pode fechar esta aba.</p>";

    public static async Task<Dictionary<string, string>> Receive(TcpListener listener, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var stream = client.GetStream();
            var line = await ReadRequestLine(stream, ct).ConfigureAwait(false);
            // "GET /?code=...&state=... HTTP/1.1"
            var parts = line.Split(' ');
            var target = parts.Length >= 2 && parts[0] == "GET" ? parts[1] : "";
            var query = Parse(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "");
            var body = Encoding.UTF8.GetBytes(Page);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, ct).ConfigureAwait(false);
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
            // Ignora pedidos do navegador que não são o retorno deste login (ex.: favicon).
            if (query.ContainsKey("state") && query["state"] == state) return query;
            if (query.ContainsKey("error")) return query;
        }
    }

    static async Task<string> ReadRequestLine(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < 8192 && await stream.ReadAsync(one, ct).ConfigureAwait(false) == 1)
        {
            if (one[0] == '\n') break;
            if (one[0] != '\r') buffer.Add(one[0]);
        }
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    public static Dictionary<string, string> Parse(string query)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((i < 0 ? pair : pair[..i]).Replace('+', ' '));
            result[key] = i < 0 ? "" : Uri.UnescapeDataString(pair[(i + 1)..].Replace('+', ' '));
        }
        return result;
    }
}
