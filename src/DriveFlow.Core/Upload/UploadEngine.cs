using System.Diagnostics;
using DriveFlow.Core.Drive;
using DriveFlow.Core.Storage;

namespace DriveFlow.Core.Upload;

/// <summary>Envia um item da fila até concluir, pausar ou falhar de vez. Port de upload.Engine.</summary>
public sealed class UploadEngine
{
    public const int Quantum = 256 * 1024;
    readonly Database db;
    readonly DriveClient drive;
    readonly CancellationToken stop;
    readonly int chunkSize;
    readonly int retries;
    readonly Func<double> limit;
    /// <summary>Espera entre tentativas; os testes trocam por uma sem atraso.</summary>
    public Func<int, CancellationToken, Task> Wait { get; init; } = DefaultWait;

    public UploadEngine(Database db, DriveClient drive, CancellationToken stop, int chunkSize = 8 * 1024 * 1024,
                        int retries = 10, Func<double>? limit = null)
    {
        if (chunkSize < Quantum || chunkSize % Quantum != 0)
            throw new ArgumentException("O bloco deve ser múltiplo de 256 KiB.");
        (this.db, this.drive, this.stop, this.chunkSize, this.retries, this.limit) = (db, drive, stop, chunkSize, retries, limit ?? (() => 0));
    }

    public static async Task DefaultWait(int failure, CancellationToken stop)
    {
        var delay = Math.Min(60, Math.Pow(2, Math.Min(failure, 6))) + Random.Shared.NextDouble();
        try { await Task.Delay(TimeSpan.FromSeconds(delay), stop).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    /// <summary>Registra a falha e espera. Falso quando é para parar de tentar.</summary>
    public static async Task<bool> Retry(Database db, string id, CancellationToken stop, int failure, int limit, string stage, Exception exc,
                                         Func<int, CancellationToken, Task> wait)
    {
        var item = db.Get(id)!;
        var diagnostic = $"stage={stage} type={exc.GetType().Name} offset={item.Offset} last_confirmed={(item.LastConfirmed is "" ? "unknown" : item.LastConfirmed)}";
        if (stop.IsCancellationRequested) return false;
        if (failure > limit)
        {
            db.Update(id, new() { Status = Status.Interrupted, Error = "Limite de tentativas atingido. Você pode continuar mais tarde.", Speed = 0 });
            db.Event(id, $"RETRY_LIMIT_REACHED {diagnostic}");
            return false;
        }
        db.Update(id, new() { Status = Status.Resuming, Speed = 0, Error = $"{exc.Message} Tentativa {failure}/{limit}." });
        db.Event(id, $"NETWORK_RETRY attempt={failure} {diagnostic} reason={exc.Message}");
        await wait(failure, stop).ConfigureAwait(false);
        return true;
    }

    static void Validate(UploadItem item)
    {
        var file = new FileInfo(item.Path);
        if (!file.Exists) throw new FileNotFoundException(item.Path);
        if (file.Length != item.Size || Database.MtimeOf(file) != item.Mtime)
            throw new LocalFileChangedException("Arquivo alterado desde a seleção. Remova o item e adicione o arquivo novamente.");
    }

    async Task<bool> VerifyCompleted(UploadItem item)
    {
        var meta = await drive.Metadata(item.RemoteId, stop).ConfigureAwait(false);
        if (meta is null) return false;
        if (meta.Trashed || !long.TryParse(meta.Size, out var size) || size != item.Size)
            throw new DriveException("Tamanho remoto divergente ou arquivo na lixeira. Verifique o arquivo no Drive.");
        db.Update(item.Id, new() { Status = Status.Done, Offset = item.Size, Error = "", Speed = 0, Session = "" });
        db.Event(item.Id, "UPLOAD_COMPLETED_SIZE_VERIFIED");
        return true;
    }

    public async Task Run(string id)
    {
        var failures = 0;
        var expired = false;
        var stage = "validate";
        while (!stop.IsCancellationRequested)
        {
            var item = db.Get(id)!;
            try
            {
                Validate(item);
                if (expired)
                {
                    stage = "expired_session_check";
                    if (await VerifyCompleted(item).ConfigureAwait(false)) return;
                    db.Update(id, new() { Status = Status.SessionExpired, Error = "Sessão expirada. Use Reiniciar sessão para reenviar este arquivo.", Speed = 0 });
                    db.Event(id, "SESSION_EXPIRED_RESTART_REQUIRES_USER");
                    return;
                }
                stage = "metadata";
                // Um ID reservado no Drive torna recuperável a perda da resposta final ou um travamento.
                if (item.RemoteId != "" && await VerifyCompleted(item).ConfigureAwait(false)) return;
                if (item.RemoteId == "")
                {
                    stage = "prepare";
                    if ((await drive.Duplicates(item.FolderId, item.Name, stop).ConfigureAwait(false)).Count > 0)
                        throw new DuplicateNameException("Já existe um arquivo com esse nome no destino. Renomeie o item antes de continuar.");
                    db.Update(id, new() { RemoteId = await drive.GenerateId(stop).ConfigureAwait(false) });
                    item = db.Get(id)!;
                }
                var uri = db.SessionOf(item);
                if (uri == "")
                {
                    stage = "create_session";
                    db.Update(id, new() { Status = Status.Starting });
                    uri = await drive.Start(item, stop).ConfigureAwait(false);
                    // Grava a sessão antes de mandar qualquer byte.
                    db.Update(id, new() { Session = uri, Offset = 0 });
                    db.Event(id, "SESSION_CREATED");
                }
                db.Update(id, new() { Status = Status.Resuming, Speed = 0 });
                stage = "query_session";
                var (offset, complete) = await drive.Transfer(uri, item.Size, ct: stop).ConfigureAwait(false);
                db.Update(id, new() { Offset = offset });
                db.Event(id, $"SESSION_QUERIED offset={offset}");
                using (var source = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    while (!complete && !stop.IsCancellationRequested)
                    {
                        Validate(item);
                        var size = (int)Math.Min(chunkSize, item.Size - offset);
                        var chunk = new byte[size];
                        source.Seek(offset, SeekOrigin.Begin);
                        await source.ReadExactlyAsync(chunk, stop).ConfigureAwait(false);
                        db.Update(id, new() { Status = Status.Sending, Error = "" });
                        var clock = Stopwatch.StartNew();
                        stage = "send_chunk";
                        HttpContent body = limit() > 0 ? new ThrottledContent(chunk, limit, stop) : new ByteArrayContent(chunk);
                        var (next, done) = await drive.Transfer(uri, item.Size, offset, body, stop).ConfigureAwait(false);
                        complete = done;
                        if (next <= offset && !complete) throw new TemporaryException("O servidor ainda não confirmou novos bytes.");
                        var speed = Math.Max(0, next - offset) / Math.Max(.001, clock.Elapsed.TotalSeconds);
                        offset = next;
                        db.Update(id, new() { Offset = offset, Speed = speed, LastConfirmed = Database.Now() });
                        failures = 0;
                    }
                }
                if (complete)
                {
                    stage = "verify_completed";
                    Validate(item);
                    if (!await VerifyCompleted(item).ConfigureAwait(false))
                        throw new TemporaryException("Aguardando confirmação dos metadados do arquivo.");
                    return;
                }
            }
            catch (ExpiredSessionException)
            {
                // Confere na próxima volta, para que falhas de rede ali entrem no caminho normal de nova tentativa.
                expired = true;
            }
            catch (TemporaryException exc)
            {
                failures++;
                if (!await Retry(db, id, stop, failures, retries, stage, exc, Wait).ConfigureAwait(false))
                {
                    if (stop.IsCancellationRequested) break;
                    return;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (AuthException exc)
            {
                db.Update(id, new() { Status = Status.Error, Error = exc.Message, Speed = 0 });
                db.Event(id, $"AUTH_ERROR stage={stage} offset={db.Get(id)!.Offset} reason={exc.Message}");
                return;
            }
            catch (LocalFileChangedException exc)
            {
                db.Update(id, new() { Status = Status.FileChanged, Error = exc.Message, Speed = 0 });
                db.Event(id, "LOCAL_FILE_CHANGED");
                return;
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                db.Update(id, new() { Status = Status.FileUnavailable, Error = "Verifique se o disco está conectado e o arquivo pode ser lido. Depois clique em Continuar.", Speed = 0 });
                db.Event(id, "LOCAL_FILE_UNAVAILABLE");
                return;
            }
            catch (DuplicateNameException exc)
            {
                db.Update(id, new() { Status = Status.DuplicateName, Error = exc.Message, Speed = 0 });
                return;
            }
            catch (DriveException exc)
            {
                db.Update(id, new() { Status = Status.Error, Error = exc.Message, Speed = 0 });
                db.Event(id, "DRIVE_ERROR");
                return;
            }
            catch (Exception exc)
            {
                var checkpoint = db.Get(id)!;
                db.Event(id, $"WORKER_EXCEPTION stage={stage} type={exc.GetType().Name} offset={checkpoint.Offset} last_confirmed={(checkpoint.LastConfirmed is "" ? "unknown" : checkpoint.LastConfirmed)}");
                throw;
            }
        }
        db.Update(id, new() { Status = Status.Paused, Speed = 0 });
        db.Event(id, "UPLOAD_PAUSED");
    }
}
