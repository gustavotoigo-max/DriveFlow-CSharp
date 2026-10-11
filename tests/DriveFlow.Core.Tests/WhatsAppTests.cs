using System.Net;
using DriveFlow.Core.Notify;
using DriveFlow.Core.Storage;

namespace DriveFlow.Core.Tests;

public sealed class WhatsAppTests
{
    static UploadItem Item(string status, string folder = "Meu Drive/Obra 12", string notify = "", string account = "acc") =>
        new() { Id = Guid.NewGuid().ToString(), Path = "p", Name = "n", Status = status, FolderName = folder, NotifyName = notify, Account = account };

    sealed class Reply(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public Uri? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public void Message_uses_the_selected_folder_name()
    {
        Assert.Equal("Obra 12 UPLOAD FINALIZADO", WhatsApp.Message(WhatsApp.BatchName(Item(Status.Done))));
        Assert.Equal("Fotos", WhatsApp.BatchName(Item(Status.Done, notify: "Fotos")));
        Assert.Equal("Meu Drive", WhatsApp.FolderLabel(""));
        Assert.Equal("554899494404", WhatsApp.Normalize("+55 (48) 9949-4404"));
    }

    [Fact]
    public void Notifies_only_when_the_last_active_file_of_the_folder_finishes()
    {
        var done = Item(Status.Done);
        var stillSending = Item(Status.Sending);
        var paused = Item(Status.Paused);

        Assert.Empty(WhatsApp.FinishedFolders([done, stillSending], [done]));
        Assert.Equal(["Obra 12"], WhatsApp.FinishedFolders([done, paused], [done]));
        Assert.Equal(["Obra 12"], WhatsApp.FinishedFolders([done, Item(Status.Done)], [done, Item(Status.Done)]));
    }

    [Fact]
    public async Task Send_accepts_queued_and_reports_callmebot_errors()
    {
        var ok = new Reply(HttpStatusCode.OK, "<p>Message queued. You will receive it in a few seconds.</p>");
        await WhatsApp.Send(new HttpClient(ok), "554899494404", "123", "Obra UPLOAD FINALIZADO");
        Assert.Contains("phone=554899494404", ok.Last!.Query);
        Assert.Contains("text=Obra%20UPLOAD%20FINALIZADO", ok.Last.Query);

        var bad = new Reply((HttpStatusCode)203, "<b>APIKey is invalid.</b>");
        var error = await Assert.ThrowsAsync<WhatsAppException>(() => WhatsApp.Send(new HttpClient(bad), "1", "2", "t"));
        Assert.Contains("HTTP 203", error.Message);
        Assert.Contains("APIKey is invalid.", error.Message);
    }

    [Fact]
    public async Task Notifier_reads_the_python_config_and_logs_only_last_digits()
    {
        var dir = Directory.CreateTempSubdirectory("driveflow-test-").FullName;
        try
        {
            using var db = new Database(new PlainVault(), Path.Combine(dir, "q.sqlite3"));
            var reply = new Reply(HttpStatusCode.OK, "Message queued");
            var notifier = new Notifier(db, new HttpClient(reply));
            notifier.Save(true, [("+55 48 9949-4404", " 777 ")]);

            Assert.True(notifier.Config().Enabled);
            Assert.Equal([("554899494404", "777")], notifier.Recipients());
            Assert.Empty(await notifier.Notify("teste"));
            Assert.Contains(db.Events(), e => e.Event == "WHATSAPP_SENT phone=…4404");
            Assert.DoesNotContain(db.Events(), e => e.Event.Contains("5548"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
