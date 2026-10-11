using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DriveFlow.Core.Storage;

namespace DriveFlow.Core.Notify;

public class WhatsAppException(string message) : Exception(message);

public sealed record Recipient(
    [property: JsonPropertyName("phone")] string Phone,
    /// <summary>Apikey cifrada pelo cofre.</summary>
    [property: JsonPropertyName("apikey")] string ApiKey);

public sealed record WhatsAppConfig(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("recipients")] List<Recipient> Recipients);

/// <summary>Aviso "PASTA UPLOAD FINALIZADO" via CallMeBot. Port de whatsapp.py, mesma configuração.</summary>
public static partial class WhatsApp
{
    public const string Api = "https://api.callmebot.com/whatsapp.php";

    [GeneratedRegex(@"\D")] private static partial Regex NonDigit();
    [GeneratedRegex("<[^>]+>")] private static partial Regex Tag();
    [GeneratedRegex(@"\s+")] private static partial Regex Space();

    public static string Normalize(string? phone) => NonDigit().Replace(phone ?? "", "");

    public static string FolderLabel(string? folderName)
    {
        var parts = (folderName ?? "").Replace(" / ", "/").Split('/').Where(x => x.Trim() != "").ToArray();
        return parts.Length > 0 ? parts[^1].Trim() : "Meu Drive";
    }

    /// <summary>Pasta escolhida para o envio: a pasta marcada, a origem da compactação ou o destino.</summary>
    public static string BatchName(UploadItem item) => item.NotifyName != "" ? item.NotifyName : FolderLabel(item.FolderName);

    public static string Message(string name) => $"{name} UPLOAD FINALIZADO";

    public static async Task Send(HttpClient http, string phone, string apikey, string text, CancellationToken ct = default)
    {
        var url = $"{Api}?phone={Uri.EscapeDataString(phone)}&text={Uri.EscapeDataString(text)}&apikey={Uri.EscapeDataString(apikey)}";
        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            response = await http.GetAsync(url, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new WhatsAppException("Sem conexão com o CallMeBot.");
        }
        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var body = Space().Replace(Tag().Replace(raw, " "), " ").Trim();
            var lower = body.ToLowerInvariant();
            if (lower.Contains("queued") || lower.Contains("message sent")) return;
            // O CallMeBot responde páginas HTML; o motivo vem no texto.
            if (response.StatusCode != HttpStatusCode.OK || new[] { "apikey", "invalid", "not activated", "error" }.Any(lower.Contains))
                throw new WhatsAppException($"CallMeBot recusou o envio (HTTP {(int)response.StatusCode}): {(body == "" ? "sem resposta" : body[..Math.Min(200, body.Length)])}");
        }
    }

    /// <summary>Lotes cujo último upload ativo acabou de terminar.</summary>
    public static List<string> FinishedFolders(IReadOnlyList<UploadItem> rows, IEnumerable<UploadItem> newlyDone)
    {
        var names = new List<string>();
        foreach (var item in newlyDone)
        {
            var name = BatchName(item);
            if (!names.Contains(name) && !rows.Any(x => x.Account == item.Account && BatchName(x) == name && Status.Active.Contains(x.Status)))
                names.Add(name);
        }
        return names;
    }
}

public sealed class Notifier(Database db, HttpClient http)
{
    public WhatsAppConfig Config() => db.Setting<WhatsAppConfig?>("whatsapp", null) ?? new WhatsAppConfig(false, []);

    /// <summary>Números com a apikey aberta. Chaves ilegíveis são puladas e registradas.</summary>
    public List<(string Phone, string ApiKey)> Recipients()
    {
        var result = new List<(string, string)>();
        foreach (var row in Config().Recipients)
        {
            try { result.Add((row.Phone, db.Vault.Open(row.ApiKey))); }
            catch { db.Event("", $"WHATSAPP_KEY_UNREADABLE phone=…{Last4(row.Phone)}"); }
        }
        return result;
    }

    static string Last4(string phone) => phone.Length <= 4 ? phone : phone[^4..];

    public void Save(bool enabled, IEnumerable<(string Phone, string ApiKey)> recipients) =>
        db.SaveSetting("whatsapp", new WhatsAppConfig(enabled,
            recipients.Select(r => new Recipient(WhatsApp.Normalize(r.Phone), db.Vault.Seal(r.ApiKey.Trim()))).ToList()));

    public void UploadsCompleted(IReadOnlyList<UploadItem> rows, IReadOnlyCollection<UploadItem> newlyDone)
    {
        if (newlyDone.Count == 0 || !Config().Enabled) return;
        foreach (var name in WhatsApp.FinishedFolders(rows, newlyDone))
            _ = Notify(WhatsApp.Message(name));
    }

    /// <summary>Envia para todos (ou para recipients) e devolve os erros, um por número.</summary>
    public async Task<List<string>> Notify(string text, IEnumerable<(string Phone, string ApiKey)>? recipients = null)
    {
        var errors = new List<string>();
        foreach (var (phone, apikey) in (recipients ?? Recipients()).ToList())
        {
            try
            {
                await WhatsApp.Send(http, phone, apikey, text).ConfigureAwait(false);
                db.Event("", $"WHATSAPP_SENT phone=…{Last4(phone)}");
            }
            catch (WhatsAppException exc)
            {
                errors.Add($"{phone}: {exc.Message}");
                db.Event("", $"WHATSAPP_ERROR phone=…{Last4(phone)} reason={exc.Message}");
            }
        }
        return errors;
    }
}
