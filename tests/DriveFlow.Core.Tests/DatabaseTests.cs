using DriveFlow.Core.Storage;
using Microsoft.Data.Sqlite;

namespace DriveFlow.Core.Tests;

public sealed class DatabaseTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("driveflow-test-").FullName;

    public void Dispose() => Directory.Delete(dir, true);

    [Fact]
    public void Opens_an_old_python_queue_and_marks_running_items_interrupted()
    {
        var path = Path.Combine(dir, "queue.sqlite3");
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Esquema da primeira versão Python, sem as colunas adicionadas depois.
            cmd.CommandText = """
                CREATE TABLE uploads (id TEXT PRIMARY KEY, path TEXT, name TEXT, size INTEGER, mtime TEXT,
                    folder_id TEXT, folder_name TEXT, account TEXT, status TEXT, offset INTEGER DEFAULT 0,
                    session TEXT DEFAULT '', remote_id TEXT DEFAULT '', error TEXT DEFAULT '', created TEXT,
                    updated TEXT, speed REAL DEFAULT 0);
                INSERT INTO uploads (id,path,name,size,mtime,folder_id,folder_name,account,status,created,updated)
                VALUES ('1','C:\\a.bin','a.bin',10,'1','f','Meu Drive / Obra','acc','enviando','2026-01-01','2026-01-01');
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = new Database(new PlainVault(), path);

        var item = db.Get("1")!;
        Assert.Equal(Status.Interrupted, item.Status);
        Assert.Equal("Meu Drive/Obra", item.FolderName);
        Assert.True(item.Enabled);
        Assert.Equal("", item.NotifyName);
    }

    [Fact]
    public void Rejects_a_second_item_with_the_same_name_for_the_same_folder()
    {
        using var db = new Database(new PlainVault(), Path.Combine(dir, "q.sqlite3"));
        var file = Path.Combine(dir, "x.txt");
        File.WriteAllText(file, "x");
        db.Add(file, "f", "f", "acc");

        Assert.Throws<ArgumentException>(() => db.Add(file, "f", "f", "acc"));
        db.Add(file, "outra", "outra", "acc");
    }

    [Fact]
    public void Session_is_stored_sealed_and_settings_round_trip()
    {
        using var db = new Database(new PlainVault(), Path.Combine(dir, "q.sqlite3"));
        var file = Path.Combine(dir, "x.txt");
        File.WriteAllText(file, "x");
        var id = db.Add(file, "f", "f", "acc");
        db.Update(id, new() { Session = "https://www.googleapis.com/upload?id=1" });

        var item = db.Get(id)!;
        Assert.DoesNotContain("googleapis", item.Session);
        Assert.Equal("https://www.googleapis.com/upload?id=1", db.SessionOf(item));

        Assert.Equal(1, db.Setting("concurrency", 1));
        db.SaveSetting("concurrency", 3);
        Assert.Equal(3, db.Setting("concurrency", 1));
    }

    [Fact]
    public void Mtime_matches_python_st_mtime_ns()
    {
        var file = new FileInfo(Path.Combine(dir, "t.txt"));
        File.WriteAllText(file.FullName, "x");
        File.SetLastWriteTimeUtc(file.FullName, new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567));
        file.Refresh();
        Assert.Equal("1791720000123456700", Database.MtimeOf(file));
    }
}
