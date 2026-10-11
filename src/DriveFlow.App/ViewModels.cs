using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DriveFlow.Core;
using DriveFlow.Core.Drive;
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
    bool enabled, isActive, canAct = true;

    public string Name { get => name; private set => Set(ref name, value); }
    public string Destination { get => destination; private set => Set(ref destination, value); }
    public string BytesText { get => bytesText; private set => Set(ref bytesText, value); }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public string TimeText { get => timeText; private set => Set(ref timeText, value); }
    public string Tip { get => tip; private set => Set(ref tip, value); }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    /// <summary>Enviando ou retomando: status em destaque e barra animada.</summary>
    public bool IsActive { get => isActive; private set => Set(ref isActive, value); }
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
        IsActive = item.Status is Status.Sending or Status.Resuming;
        Progress = item.Size > 0 ? (double)item.Offset / item.Size : 0;
        BytesText = $"{Format.Size(item.Offset)} / {Format.Size(item.Size)}";
        StatusText = removing ? "Removendo…" : char.ToUpper(item.Status[0]) + item.Status[1..];
        var remaining = item.Speed > 0 ? Format.Duration(Math.Max(0, item.Size - item.Offset) / item.Speed) : "—";
        TimeText = $"{Format.Size(item.Speed)}/s\nDecorrido {Format.Duration(item.Elapsed)}\nRestante {remaining}";
        Tip = item.Error != "" ? item.Error : item.Path;
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
    public string Tip => item.Path;
}

public sealed record PhoneRow(string Phone, string ApiKey)
{
    public override string ToString() => $"{Phone}    apikey ••••{(ApiKey.Length >= 2 ? ApiKey[^2..] : ApiKey)}";
}

/// <summary>Recuo das árvores: 16 px por nível, como no Qt.</summary>
public sealed class IndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var depth = 0;
        for (var parent = ItemsControl.ItemsControlFromItemContainer(value as DependencyObject);
             parent is TreeViewItem; parent = ItemsControl.ItemsControlFromItemContainer(parent))
            depth++;
        return new Thickness(4 + depth * 16, 0, 8, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Arquivo, pasta ou unidade no navegador de arquivos locais.</summary>
public sealed class LocalNode : Observable
{
    readonly Action<LocalNode>? load;
    readonly Action<LocalNode, bool>? checkedChanged;
    bool isExpanded, isSelected, isChecked, loaded;

    public LocalNode(string path, string name, bool isDir, long size, Action<LocalNode>? load, Action<LocalNode, bool>? checkedChanged, bool isChecked)
    {
        (Path, Name, IsDir, this.load, this.checkedChanged, this.isChecked) = (path, name, isDir, load, checkedChanged, isChecked);
        IsDrive = isDir && System.IO.Path.GetPathRoot(path) == path;
        SizeText = isDir ? "" : Format.Size(size);
        Icon = IsDrive ? ShellIcons.ForPath(path) : isDir ? ShellIcons.Folder() : ShellIcons.ForName(name);
        if (isDir && load is not null) Children.Add(Placeholder());
    }

    static LocalNode Placeholder() => new("", "", false, 0, null, null, false) { IsPlaceholder = true };

    public string Path { get; }
    public string Name { get; }
    public bool IsDir { get; }
    /// <summary>Unidades inteiras (C:\) não podem ser marcadas.</summary>
    public bool IsDrive { get; }
    public bool IsPlaceholder { get; private init; }
    public bool IsCheckable => !IsDrive && !IsPlaceholder;
    public string SizeText { get; }
    public ImageSource? Icon { get; }
    public ObservableCollection<LocalNode> Children { get; } = [];
    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }

    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (isChecked == value) return;
            Set(ref isChecked, value);
            checkedChanged?.Invoke(this, value);
        }
    }

    /// <summary>Atualiza a marca sem avisar o navegador (Limpar seleção).</summary>
    public void ShowChecked(bool value)
    {
        isChecked = !value;
        Set(ref isChecked, value, nameof(IsChecked));
    }

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            Set(ref isExpanded, value);
            if (value && !loaded && load is not null)
            {
                loaded = true;
                load(this);
            }
        }
    }

    public IEnumerable<LocalNode> Descendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.Descendants())
                yield return node;
    }
}

/// <summary>Pasta ou arquivo do Drive na árvore; os filhos carregam ao expandir.</summary>
public sealed class DriveNode : Observable
{
    readonly Action<DriveNode>? load;
    bool isExpanded, isSelected, isDestination, loaded;
    string name = "", sizeText = "", tip = "";

    public DriveNode(string id, string name, string? mimeType, string? size, DriveNode? parent, Action<DriveNode>? load)
    {
        (Id, Parent, this.load) = (id, parent, load);
        Update(name, mimeType, size);
        if (IsFolder && load is not null) Children.Add(new DriveNode("", "Carregando…", "", null, this, null));
    }

    public static DriveNode From(DriveFile file, DriveNode parent, Action<DriveNode> load) =>
        new(file.Id, file.Name, file.MimeType, file.Size, parent, load);

    public void Update(string newName, string? mimeType, string? size)
    {
        MimeType = mimeType ?? DriveFile.FolderType;
        Name = newName;
        Icon = Id == "" ? null : IsFolder ? ShellIcons.Folder() : ShellIcons.ForName(newName);
        SizeText = IsFolder || Id == "" ? "" : long.TryParse(size, out var bytes) ? Format.Size(bytes) : "Google Docs";
    }

    public string Id { get; }
    public string MimeType { get; private set; } = DriveFile.FolderType;
    public bool IsFolder => MimeType == DriveFile.FolderType;
    public string Name { get => name; private set => Set(ref name, value); }
    public string SizeText { get => sizeText; private set => Set(ref sizeText, value); }
    public string Tip { get => tip; set => Set(ref tip, value); }
    public ImageSource? Icon { get; private set; }
    public DriveNode? Parent { get; }
    public ObservableCollection<DriveNode> Children { get; } = [];
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
                load(this);
            }
        }
    }

    /// <summary>Falhou ao carregar: tenta de novo na próxima vez que abrir.</summary>
    public void ForgetLoaded() => loaded = false;

    public IEnumerable<DriveNode> PathFromRoot()
    {
        var chain = new List<DriveNode>();
        for (var node = this; node is not null; node = node.Parent) chain.Add(node);
        chain.Reverse();
        return chain;
    }

    public string FullName => string.Join("/", PathFromRoot().Select(n => n.Name));

    public IEnumerable<DriveNode> Descendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.Descendants())
                yield return node;
    }
}
