using DriveFlow.Core.Drive;
using DriveFlow.Core.Storage;
using DriveFlow.Core.Upload;

namespace DriveFlow.Core.Tests;

public sealed class EngineTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("driveflow-test-").FullName;
    readonly Database db;
    readonly FakeDrive fake = new();
    readonly DriveClient drive;

    public EngineTests()
    {
        db = new Database(new PlainVault(), Path.Combine(dir, "queue.sqlite3"));
        drive = new DriveClient(new HttpClient(fake));
    }

    public void Dispose()
    {
        db.Dispose();
        Directory.Delete(dir, true);
    }

    string File(string name, int size)
    {
        var path = Path.Combine(dir, name);
        var data = new byte[size];
        new Random(size).NextBytes(data);
        System.IO.File.WriteAllBytes(path, data);
        return path;
    }

    UploadEngine Engine(CancellationToken stop = default, int retries = 10) =>
        new(db, drive, stop, UploadEngine.Quantum, retries) { Wait = (_, _) => Task.CompletedTask };

    [Fact]
    public async Task Sends_file_in_chunks_and_verifies_size()
    {
        var path = File("video.mp4", UploadEngine.Quantum * 2 + 1000);
        var id = db.Add(path, "folder1", "Meu Drive/Obra", "acc");

        await Engine().Run(id);

        var item = db.Get(id)!;
        Assert.Equal(Status.Done, item.Status);
        Assert.Equal(item.Size, item.Offset);
        Assert.Equal("", db.SessionOf(item));
        var uploaded = fake.Files[item.RemoteId];
        Assert.Equal(System.IO.File.ReadAllBytes(path), uploaded.Data.ToArray());
        Assert.Equal("folder1", uploaded.Parent);
        // Uma consulta de progresso e três blocos.
        Assert.Equal(4, fake.Log.Count(x => x == "PUT /upload/drive/v3/files"));
        Assert.Contains(db.Events(), e => e.Event == "UPLOAD_COMPLETED_SIZE_VERIFIED");
    }

    [Fact]
    public async Task Resumes_from_the_offset_the_server_confirmed()
    {
        var path = File("a.bin", UploadEngine.Quantum * 3);
        var id = db.Add(path, "f", "f", "acc");
        // O Google confirma só parte de cada bloco: o motor deve reler a partir do que foi confirmado.
        fake.AcceptOnly = 1000;

        await Engine().Run(id);

        Assert.Equal(Status.Done, db.Get(id)!.Status);
        Assert.Equal(System.IO.File.ReadAllBytes(path), fake.Files[db.Get(id)!.RemoteId].Data.ToArray());
    }

    [Fact]
    public async Task Retries_temporary_errors_and_gives_up_after_the_limit()
    {
        var path = File("b.bin", 5000);
        var id = db.Add(path, "f", "f", "acc");
        for (var i = 0; i < 3; i++) fake.Failures.Add(("PUT /upload", System.Net.HttpStatusCode.ServiceUnavailable));

        await Engine(retries: 2).Run(id);

        var item = db.Get(id)!;
        Assert.Equal(Status.Interrupted, item.Status);
        Assert.Contains(db.Events(), e => e.Event.StartsWith("RETRY_LIMIT_REACHED stage=query_session"));

        // Continuar depois reaproveita a mesma sessão e o mesmo ID reservado.
        await Engine().Run(id);
        Assert.Equal(Status.Done, db.Get(id)!.Status);
        Assert.Single(fake.Sessions);
    }

    [Fact]
    public async Task Expired_session_asks_the_user_to_restart()
    {
        var path = File("c.bin", 5000);
        var id = db.Add(path, "f", "f", "acc");
        fake.Failures.Add(("PUT /upload", System.Net.HttpStatusCode.Gone));

        await Engine().Run(id);

        Assert.Equal(Status.SessionExpired, db.Get(id)!.Status);
    }

    [Fact]
    public async Task Duplicate_name_in_destination_stops_before_upload()
    {
        var path = File("d.bin", 5000);
        var id = db.Add(path, "f", "f", "acc");
        fake.Existing.Add(("f", "d.bin", "application/octet-stream"));

        await Engine().Run(id);

        Assert.Equal(Status.DuplicateName, db.Get(id)!.Status);
        Assert.Empty(fake.Sessions);
    }

    [Fact]
    public async Task Changed_file_is_not_uploaded()
    {
        var path = File("e.bin", 5000);
        var id = db.Add(path, "f", "f", "acc");
        System.IO.File.AppendAllText(path, "mais");

        await Engine().Run(id);

        Assert.Equal(Status.FileChanged, db.Get(id)!.Status);
    }

    [Fact]
    public async Task Missing_file_is_reported_as_unavailable()
    {
        var path = File("g.bin", 5000);
        var id = db.Add(path, "f", "f", "acc");
        System.IO.File.Delete(path);

        await Engine().Run(id);

        Assert.Equal(Status.FileUnavailable, db.Get(id)!.Status);
    }

    [Fact]
    public async Task Stopping_pauses_the_upload()
    {
        var path = File("h.bin", 5000);
        var id = db.Add(path, "f", "f", "acc");
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Engine(stop.Token).Run(id);

        Assert.Equal(Status.Paused, db.Get(id)!.Status);
    }

    [Fact]
    public async Task Lost_final_response_is_recovered_from_the_reserved_id()
    {
        var path = File("i.bin", 5000);
        var id = db.Add(path, "f", "f", "acc");
        await Engine().Run(id);
        // Simula o programa fechando antes de gravar a conclusão.
        db.Update(id, new() { Status = Status.Interrupted, Offset = 0 });
        fake.Log.Clear();

        await Engine().Run(id);

        Assert.Equal(Status.Done, db.Get(id)!.Status);
        Assert.DoesNotContain("POST /upload/drive/v3/files", fake.Log);
    }

    [Fact]
    public void Session_uri_must_be_google()
    {
        Assert.Throws<DriveException>(() => DriveClient.ValidateUri("https://evil.example/upload"));
        Assert.Throws<DriveException>(() => DriveClient.ValidateUri("http://www.googleapis.com/upload"));
        DriveClient.ValidateUri("https://www.googleapis.com/upload/drive/v3/files?upload_id=1");
    }
}
