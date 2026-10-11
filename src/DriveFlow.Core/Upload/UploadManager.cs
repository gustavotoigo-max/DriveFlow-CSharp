using System.Diagnostics;
using DriveFlow.Core.Auth;
using DriveFlow.Core.Drive;
using DriveFlow.Core.Storage;

namespace DriveFlow.Core.Upload;

/// <summary>Conta conectada vista pelo gerenciador; GoogleAuth implementa.</summary>
public interface IAccount
{
    string AccountId { get; }
    bool ReauthRequired { get; }
    HttpClient CreateClient();
}

/// <summary>
/// Inicia, pausa e acompanha os envios da fila. A interface chama Tick a cada meio segundo.
/// Port de upload.Manager.
/// </summary>
public sealed class UploadManager(Database db, IAccount auth)
{
    sealed record Job(Task Task, CancellationTokenSource Stop, string? Desired);

    readonly object gate = new();
    readonly Dictionary<string, Job> running = [];
    readonly Dictionary<string, (Stopwatch Clock, double Previous)> clocks = [];
    readonly HashSet<string> pendingRemovals = [];
    public bool Closed { get; set; }
    /// <summary>Limite de upload em bytes/s (0 = livre); a interface liga durante a compactação.</summary>
    public double RateLimit { get; set; }
    /// <summary>Espera entre tentativas; os testes trocam por uma sem atraso.</summary>
    public Func<int, CancellationToken, Task> Wait { get; init; } = UploadEngine.DefaultWait;

    public bool IsRunning(string id)
    {
        lock (gate) return running.ContainsKey(id);
    }

    public int RunningCount
    {
        get { lock (gate) return running.Count; }
    }

    public void Start(string id)
    {
        var item = db.Get(id);
        lock (gate)
        {
            if (item is null || pendingRemovals.Contains(id) || !item.Enabled || running.ContainsKey(id)
                || item.Status is Status.Done or Status.Canceled or Status.FileChanged or Status.SessionExpired)
                return;
        }
        if (auth.AccountId == "" || item.Account != auth.AccountId)
            throw new InvalidOperationException("Conecte a conta Google usada ao adicionar este arquivo.");
        db.Update(id, new() { Status = Status.Waiting, Error = "" });
    }

    public void Tick()
    {
        lock (gate)
        {
            foreach (var (id, job) in running.ToList())
            {
                if (clocks.TryGetValue(id, out var clock))
                    db.Update(id, new() { Elapsed = clock.Previous + clock.Clock.Elapsed.TotalSeconds });
                if (!job.Task.IsCompleted) continue;
                if (job.Task.Exception?.InnerException is { } exc)
                {
                    db.Update(id, new()
                    {
                        Status = Status.Error, Speed = 0,
                        Error = exc is AuthException ? exc.Message : "Falha na conexão ou no processamento. Verifique a rede e tente continuar.",
                    });
                    var item = db.Get(id);
                    db.Event(id, $"WORKER_ERROR type={exc.GetType().Name} offset={item?.Offset} last_confirmed={(item?.LastConfirmed is null or "" ? "unknown" : item.LastConfirmed)}");
                }
                if (job.Desired is not null && db.Get(id)?.Status != Status.Done)
                    db.Update(id, new() { Status = job.Desired, Speed = 0 });
                running.Remove(id);
                clocks.Remove(id);
                job.Stop.Dispose();
                if (pendingRemovals.Remove(id)) db.Remove(id);
            }
            if (Closed || auth.AccountId == "" || auth.ReauthRequired) return;
            var limit = db.Setting("concurrency", 1);
            foreach (var item in db.All())
            {
                if (running.Count >= limit) break;
                if (item.Enabled && item.Status == Status.Waiting && !running.ContainsKey(item.Id) && item.Account == auth.AccountId)
                {
                    var stop = new CancellationTokenSource();
                    clocks[item.Id] = (Stopwatch.StartNew(), item.Elapsed);
                    var id = item.Id;
                    running[id] = new Job(Task.Run(() => Run(id, stop.Token)), stop, null);
                }
            }
        }
    }

    public void Remove(string id)
    {
        lock (gate)
        {
            if (running.ContainsKey(id))
            {
                pendingRemovals.Add(id);
                Pause(id);
                return;
            }
        }
        db.Remove(id);
    }

    async Task Run(string id, CancellationToken stop)
    {
        var retries = db.Setting("retries", 10);
        using var http = auth.CreateClient();
        var engine = new UploadEngine(db, new DriveClient(http), stop, db.Setting("chunk_mib", 8) * 1024 * 1024, retries,
                                      () => RateLimit / Math.Max(1, RunningCount)) { Wait = Wait };
        await engine.Run(id).ConfigureAwait(false);
    }

    public void Pause(string id, bool cancel = false)
    {
        lock (gate)
        {
            // Pausar também tira da fila o que estava esperando: liberar uma vaga
            // não pode começar o próximo arquivo sem o usuário pedir.
            if (!cancel)
                foreach (var queued in db.All())
                    if (queued.Status == Status.Waiting && !running.ContainsKey(queued.Id))
                        db.Update(queued.Id, new() { Status = Status.Paused, Speed = 0 });
            var desired = cancel ? Status.Canceled : Status.Paused;
            if (running.TryGetValue(id, out var job))
            {
                running[id] = job with { Desired = desired };
                job.Stop.Cancel();
            }
            else if (db.Get(id) is { } item && item.Status is not (Status.Done or Status.Canceled)
                     && (cancel || item.Status is not (Status.FileChanged or Status.SessionExpired)))
            {
                db.Update(id, new() { Status = desired, Speed = 0 });
            }
        }
        db.Event(id, cancel ? "CANCEL_REQUESTED" : "PAUSE_REQUESTED");
    }

    public void PauseAll()
    {
        foreach (var item in db.All())
            if (Status.Active.Contains(item.Status) || IsRunning(item.Id))
                Pause(item.Id);
    }
}
