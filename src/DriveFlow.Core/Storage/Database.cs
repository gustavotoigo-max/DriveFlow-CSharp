using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DriveFlow.Core.Storage;

/// <summary>Campos que o envio pode alterar numa linha. Nulo = não mexe.</summary>
public sealed record UploadChange
{
    public string? Status { get; init; }
    public long? Offset { get; init; }
    /// <summary>Endereço em texto claro; é cifrado antes de gravar.</summary>
    public string? Session { get; init; }
    public string? RemoteId { get; init; }
    public string? Error { get; init; }
    public double? Speed { get; init; }
    public string? Name { get; init; }
    public bool? Enabled { get; init; }
    public double? Elapsed { get; init; }
    public string? LastConfirmed { get; init; }
}

public sealed record EventRow(string Time, string UploadId, string Event);

/// <summary>
/// Fila em SQLite com o mesmo esquema da versão Python, para que a fila e as
/// configurações possam ser reaproveitadas.
/// </summary>
public sealed class Database : IDisposable
{
    readonly object gate = new();
    readonly SqliteConnection conn;
    public IVault Vault { get; }
    /// <summary>Chamado com a fila inteira quando status, progresso ou nome mudam.</summary>
    public Action<IReadOnlyList<UploadItem>>? OnChange { get; set; }

    public static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);

    public static string DataDir()
    {
        var path = Environment.GetEnvironmentVariable("DRIVEFLOW_DATA_DIR");
        if (string.IsNullOrEmpty(path))
        {
            // Pasta própria enquanto as duas versões convivem; a migração copia a fila depois.
            path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveFlowNet");
        }
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Data de modificação em nanossegundos desde 1970, como o st_mtime_ns do Python.</summary>
    public static string MtimeOf(FileInfo file) =>
        ((file.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100).ToString(CultureInfo.InvariantCulture);

    public Database(IVault vault, string? path = null)
    {
        Vault = vault;
        path ??= System.IO.Path.Combine(DataDir(), "queue.sqlite3");
        conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS uploads (
                id TEXT PRIMARY KEY, path TEXT, name TEXT, size INTEGER, mtime TEXT,
                folder_id TEXT, folder_name TEXT, account TEXT, status TEXT,
                offset INTEGER DEFAULT 0, session TEXT DEFAULT '', remote_id TEXT DEFAULT '',
                error TEXT DEFAULT '', created TEXT, updated TEXT, speed REAL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS accounts (id TEXT PRIMARY KEY, email TEXT);
            CREATE TABLE IF NOT EXISTS events (time TEXT, upload_id TEXT, event TEXT);
            """);
        var columns = new HashSet<string>();
        using (var cmd = Command("PRAGMA table_info(uploads)"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(1));
        using var tx = conn.BeginTransaction();
        void AddColumn(string name, string definition)
        {
            if (!columns.Contains(name)) Exec($"ALTER TABLE uploads ADD COLUMN {name} {definition}", tx);
        }
        AddColumn("enabled", "INTEGER NOT NULL DEFAULT 1");
        AddColumn("elapsed", "REAL NOT NULL DEFAULT 0");
        AddColumn("last_confirmed", "TEXT NOT NULL DEFAULT ''");
        AddColumn("compressed", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("notify_name", "TEXT NOT NULL DEFAULT ''");
        Exec("UPDATE uploads SET folder_name=replace(folder_name, ' / ', '/')", tx);
        // Envios que estavam rodando quando o programa fechou.
        Exec("UPDATE uploads SET status='interrompido', speed=0 WHERE status IN ('enviando','iniciando','retomando','aguardando')", tx);
        tx.Commit();
    }

    SqliteCommand Command(string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    void Exec(string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = Command(sql, tx, args);
        cmd.ExecuteNonQuery();
    }

    static UploadItem Read(SqliteDataReader r)
    {
        string S(string col) => r[col] is string s ? s : "";
        long L(string col) => r[col] is DBNull ? 0 : Convert.ToInt64(r[col], CultureInfo.InvariantCulture);
        double D(string col) => r[col] is DBNull ? 0 : Convert.ToDouble(r[col], CultureInfo.InvariantCulture);
        return new UploadItem
        {
            Id = S("id"), Path = S("path"), Name = S("name"), Size = L("size"), Mtime = S("mtime"),
            FolderId = S("folder_id"), FolderName = S("folder_name"), Account = S("account"), Status = S("status"),
            Offset = L("offset"), Session = S("session"), RemoteId = S("remote_id"), Error = S("error"),
            Created = S("created"), Updated = S("updated"), Speed = D("speed"), Enabled = L("enabled") != 0,
            Elapsed = D("elapsed"), LastConfirmed = S("last_confirmed"), Compressed = L("compressed") != 0,
            NotifyName = S("notify_name"),
        };
    }

    public IReadOnlyList<UploadItem> All()
    {
        lock (gate)
        {
            using var cmd = Command("SELECT * FROM uploads ORDER BY created");
            using var r = cmd.ExecuteReader();
            var rows = new List<UploadItem>();
            while (r.Read()) rows.Add(Read(r));
            return rows;
        }
    }

    public UploadItem? Get(string id)
    {
        lock (gate)
        {
            using var cmd = Command("SELECT * FROM uploads WHERE id=$id", null, ("$id", id));
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
    }

    void Notify()
    {
        if (OnChange is null) return;
        try { OnChange(All()); }
        catch
        {
            try { Event("", "MONITOR_OBSERVER_ERROR"); } catch { }
        }
    }

    public string Add(string path, string folderId, string folderName, string account, string? name = null,
                      bool compressed = false, string notifyName = "")
    {
        var file = new FileInfo(System.IO.Path.GetFullPath(path));
        if (!file.Exists) throw new ArgumentException("Selecione um arquivo.");
        name ??= file.Name;
        lock (gate)
        {
            using var tx = conn.BeginTransaction();
            using (var dup = Command("SELECT id FROM uploads WHERE (path=$p OR name=$n) AND folder_id=$f AND account=$a AND status NOT IN ('concluído','cancelado')",
                                     tx, ("$p", file.FullName), ("$n", name), ("$f", folderId), ("$a", account)))
            {
                if (dup.ExecuteScalar() is not null)
                    throw new ArgumentException("Este arquivo ou outro com o mesmo nome já está na fila para esse destino. Renomeie o item existente antes de adicionar outro com esse nome.");
            }
            var id = Guid.NewGuid().ToString("N");
            var now = Now();
            Exec("INSERT INTO uploads (id,path,name,size,mtime,folder_id,folder_name,account,status,created,updated,compressed,notify_name) VALUES ($id,$p,$n,$s,$m,$f,$fn,$a,'pausado',$c,$c,$z,$nn)",
                 tx, ("$id", id), ("$p", file.FullName), ("$n", name), ("$s", file.Length), ("$m", MtimeOf(file)), ("$f", folderId),
                 ("$fn", folderName), ("$a", account), ("$c", now), ("$z", compressed ? 1 : 0), ("$nn", notifyName));
            tx.Commit();
            Notify();
            return id;
        }
    }

    public void Update(string id, UploadChange change)
    {
        var values = new List<(string Column, object? Value)>();
        if (change.Status is not null) values.Add(("status", change.Status));
        if (change.Offset is not null) values.Add(("offset", change.Offset));
        if (change.Session is not null) values.Add(("session", Vault.Seal(change.Session)));
        if (change.RemoteId is not null) values.Add(("remote_id", change.RemoteId));
        if (change.Error is not null) values.Add(("error", change.Error));
        if (change.Speed is not null) values.Add(("speed", change.Speed));
        if (change.Name is not null) values.Add(("name", change.Name));
        if (change.Enabled is not null) values.Add(("enabled", change.Enabled.Value ? 1 : 0));
        if (change.Elapsed is not null) values.Add(("elapsed", change.Elapsed));
        if (change.LastConfirmed is not null) values.Add(("last_confirmed", change.LastConfirmed));
        values.Add(("updated", Now()));
        var args = values.Select((v, i) => ($"$v{i}", v.Value)).Append(("$id", (object?)id)).ToArray();
        var sql = "UPDATE uploads SET " + string.Join(",", values.Select((v, i) => $"{v.Column}=$v{i}")) + " WHERE id=$id";
        lock (gate)
        {
            Exec(sql, null, args);
            if (change.Status is not null || change.Offset is not null || change.Speed is not null || change.Name is not null || change.Enabled is not null)
                Notify();
        }
    }

    /// <summary>Endereço da sessão retomável em texto claro.</summary>
    public string SessionOf(UploadItem item) => Vault.Open(item.Session);

    public T Setting<T>(string key, T fallback)
    {
        lock (gate)
        {
            using var cmd = Command("SELECT value FROM settings WHERE key=$k", null, ("$k", key));
            return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json) ?? fallback : fallback;
        }
    }

    public void SaveSetting<T>(string key, T value)
    {
        lock (gate) Exec("INSERT OR REPLACE INTO settings VALUES ($k,$v)", null, ("$k", key), ("$v", JsonSerializer.Serialize(value)));
    }

    public void Event(string uploadId, string text)
    {
        lock (gate) Exec("INSERT INTO events VALUES ($t,$u,$e)", null, ("$t", Now()), ("$u", uploadId), ("$e", text));
    }

    public void SaveAccount(string id, string email)
    {
        lock (gate) Exec("INSERT OR REPLACE INTO accounts VALUES ($i,$e)", null, ("$i", id), ("$e", email));
    }

    public IReadOnlyList<EventRow> Events()
    {
        lock (gate)
        {
            using var cmd = Command("SELECT time, upload_id, event FROM events ORDER BY rowid DESC LIMIT 500");
            using var r = cmd.ExecuteReader();
            var rows = new List<EventRow>();
            while (r.Read()) rows.Add(new EventRow(r.GetString(0), r.GetString(1), r.GetString(2)));
            return rows;
        }
    }

    public void Remove(string id)
    {
        lock (gate)
        {
            Exec("DELETE FROM uploads WHERE id=$id", null, ("$id", id));
            Notify();
        }
    }

    public void Dispose()
    {
        conn.Dispose();
    }
}
