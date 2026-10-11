namespace DriveFlow.Core.Storage;

public static class Status
{
    public const string Waiting = "aguardando";
    public const string Starting = "iniciando";
    public const string Sending = "enviando";
    public const string Resuming = "retomando";
    public const string Paused = "pausado";
    public const string Interrupted = "interrompido";
    public const string Done = "concluído";
    public const string Canceled = "cancelado";
    public const string Error = "erro";
    public const string FileChanged = "arquivo alterado";
    public const string FileUnavailable = "arquivo indisponível";
    public const string SessionExpired = "sessão expirada";
    public const string DuplicateName = "nome duplicado";

    /// <summary>Ainda vai mandar bytes neste lote.</summary>
    public static readonly IReadOnlySet<string> Active = new HashSet<string> { Waiting, Starting, Sending, Resuming };
}

/// <summary>Uma linha da fila, no mesmo formato da tabela uploads da versão Python.</summary>
public sealed record UploadItem
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public long Size { get; init; }
    public string Mtime { get; init; } = "";
    public string FolderId { get; init; } = "";
    public string FolderName { get; init; } = "";
    public string Account { get; init; } = "";
    public string Status { get; init; } = Storage.Status.Paused;
    public long Offset { get; init; }
    /// <summary>Endereço da sessão retomável, ainda cifrado.</summary>
    public string Session { get; init; } = "";
    public string RemoteId { get; init; } = "";
    public string Error { get; init; } = "";
    public string Created { get; init; } = "";
    public string Updated { get; init; } = "";
    public double Speed { get; init; }
    public bool Enabled { get; init; } = true;
    public double Elapsed { get; init; }
    public string LastConfirmed { get; init; } = "";
    public bool Compressed { get; init; }
    public string NotifyName { get; init; } = "";
}
