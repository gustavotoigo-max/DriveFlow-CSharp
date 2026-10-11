using DriveFlow.Core.Storage;
using DriveFlow.Core.Upload;

namespace DriveFlow.Core.Tests;

public sealed class ManagerTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("driveflow-test-").FullName;
    readonly Database db;
    readonly FakeDrive fake = new();

    sealed class Account(FakeDrive fake) : IAccount
    {
        public string AccountId { get; set; } = "acc";
        public bool ReauthRequired => false;
        public HttpClient CreateClient() => new(fake, disposeHandler: false);
    }

    public ManagerTests() => db = new Database(new PlainVault(), Path.Combine(dir, "q.sqlite3"));

    public void Dispose()
    {
        db.Dispose();
        Directory.Delete(dir, true);
    }

    string Add(string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[1000]);
        return db.Add(path, "f", "f", "acc");
    }

    static async Task Until(Func<bool> condition, UploadManager manager)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            manager.Tick();
            await Task.Delay(20);
        }
        manager.Tick();
    }

    [Fact]
    public async Task Runs_waiting_items_one_at_a_time_by_default()
    {
        var a = Add("a.bin");
        var b = Add("b.bin");
        var manager = new UploadManager(db, new Account(fake));
        manager.Start(a);
        manager.Start(b);

        manager.Tick();
        Assert.Equal(1, manager.RunningCount);
        await Until(() => db.Get(b)!.Status == Status.Done, manager);

        Assert.Equal(Status.Done, db.Get(a)!.Status);
        Assert.Equal(0, manager.RunningCount);
    }

    [Fact]
    public void Pausing_withdraws_waiting_items_and_rejects_other_accounts()
    {
        var a = Add("a.bin");
        var b = Add("b.bin");
        var manager = new UploadManager(db, new Account(fake) { AccountId = "outra" });

        Assert.Throws<InvalidOperationException>(() => manager.Start(a));

        db.Update(a, new() { Status = Status.Waiting });
        db.Update(b, new() { Status = Status.Waiting });
        manager.Pause(a);
        Assert.Equal(Status.Paused, db.Get(a)!.Status);
        Assert.Equal(Status.Paused, db.Get(b)!.Status);
    }

    [Fact]
    public async Task Removing_a_running_item_waits_for_it_to_stop()
    {
        var a = Add("a.bin");
        var manager = new UploadManager(db, new Account(fake));
        manager.Start(a);
        manager.Tick();
        manager.Remove(a);

        await Until(() => db.Get(a) is null, manager);

        Assert.Null(db.Get(a));
    }
}
