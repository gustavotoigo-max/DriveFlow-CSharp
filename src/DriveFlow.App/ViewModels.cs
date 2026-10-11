using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DriveFlow.Core;
using DriveFlow.Core.Storage;

namespace DriveFlow.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Uma linha da fila na tela, atualizada no lugar a cada meio segundo.</summary>
public sealed class QueueRow(string id, Action<QueueRow, bool> enabledChanged) : Observable
{
    public string Id { get; } = id;
    string name = "", destination = "", bytesText = "", statusText = "", timeText = "", tip = "";
    double progress;
    bool enabled, isActive, isDone, canAct = true;

    public string Name { get => name; private set => Set(ref name, value); }
    public string Destination { get => destination; private set => Set(ref destination, value); }
    public string BytesText { get => bytesText; private set => Set(ref bytesText, value); }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public string TimeText { get => timeText; private set => Set(ref timeText, value); }
    public string Tip { get => tip; private set => Set(ref tip, value); }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public bool IsActive { get => isActive; private set => Set(ref isActive, value); }
    public bool IsDone { get => isDone; private set => Set(ref isDone, value); }
    public bool CanAct { get => canAct; private set => Set(ref canAct, value); }

    /// <summary>Marcado = entra no Iniciar. Desmarcar pausa o envio.</summary>
    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            Set(ref enabled, value);
            if (!loading) enabledChanged(this, value);
        }
    }

    bool loading;

    public void Load(UploadItem item, bool removing)
    {
        loading = true;
        Name = item.Name;
        Destination = item.FolderName.Replace(" / ", "/");
        Progress = item.Size > 0 ? (double)item.Offset / item.Size : 0;
        BytesText = $"{Format.Size(item.Offset)} / {Format.Size(item.Size)}";
        StatusText = removing ? "Removendo…" : char.ToUpper(item.Status[0]) + item.Status[1..];
        var remaining = item.Speed > 0 ? Format.Duration(Math.Max(0, item.Size - item.Offset) / item.Speed) : "—";
        TimeText = $"{Format.Size(item.Speed)}/s\nDecorrido {Format.Duration(item.Elapsed)}\nRestante {remaining}";
        Tip = item.Error != "" ? item.Error : item.Path;
        IsActive = item.Status is Status.Sending or Status.Resuming;
        IsDone = item.Status == Status.Done;
        CanAct = !removing;
        Enabled = item.Enabled;
        loading = false;
    }
}

public sealed class HistoryRow(UploadItem item)
{
    public string Id => item.Id;
    public string RemoteId => item.RemoteId;
    public string Name => item.Name;
    public string SizeText => Format.Size(item.Size);
    public string Destination => item.FolderName.Replace(" / ", "/");
    public string When => (item.Updated.Length >= 19 ? item.Updated[..19].Replace('T', ' ') : item.Updated) + " UTC";
}

public sealed class SourceRow(string path, bool folder)
{
    public string Path { get; } = path;
    public bool IsFolder { get; } = folder;
    public string Icon => IsFolder ? "" : "";
}

/// <summary>Pasta do Drive na árvore; os filhos carregam ao expandir.</summary>
public sealed class FolderNode : Observable
{
    readonly Func<FolderNode, Task>? load;
    bool isExpanded, isSelected, isDestination, loaded;

    public FolderNode(string id, string name, FolderNode? parent, Func<FolderNode, Task>? load)
    {
        (Id, Name, Parent, this.load) = (id, name, parent, load);
        if (load is not null) Children.Add(new FolderNode("", "Carregando…", null, null));
    }

    public string Id { get; }
    public string Name { get; }
    public FolderNode? Parent { get; }
    public ObservableCollection<FolderNode> Children { get; } = [];
    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
    public bool IsDestination { get => isDestination; set => Set(ref isDestination, value); }

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            Set(ref isExpanded, value);
            if (value && !loaded && load is not null)
            {
                loaded = true;
                _ = load(this);
            }
        }
    }

    /// <summary>Recarrega na próxima expansão (ou já, se estiver aberta).</summary>
    public void Invalidate()
    {
        if (load is null) return;
        loaded = false;
        if (isExpanded)
        {
            loaded = true;
            _ = load(this);
        }
    }

    public IEnumerable<FolderNode> PathFromRoot()
    {
        var chain = new List<FolderNode>();
        for (var node = this; node is not null; node = node.Parent) chain.Add(node);
        chain.Reverse();
        return chain;
    }

    public string FullName => string.Join("/", PathFromRoot().Select(n => n.Name));

    public IEnumerable<FolderNode> Descendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.Descendants())
                yield return node;
    }
}
