using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DriveFlow.Core;
using DriveFlow.Core.Auth;
using DriveFlow.Core.Drive;
using DriveFlow.Core.Notify;
using DriveFlow.Core.Storage;
using DriveFlow.Core.Upload;
using Microsoft.Win32;

namespace DriveFlow.App;

public partial class MainWindow : Window
{
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
    const string DefaultStatus = "Fila salva neste computador";

    readonly Database db;
    readonly GoogleAuth auth;
    readonly UploadManager manager;
    readonly Notifier notifier;
    readonly HttpClient plain = new();
    readonly ObservableCollection<QueueRow> queue = [];
    readonly ObservableCollection<HistoryRow> history = [];
    readonly ObservableCollection<SourceRow> sources = [];
    readonly ObservableCollection<FolderNode> roots = [];
    readonly ObservableCollection<string> phones = [];
    readonly List<(string Phone, string ApiKey)> recipients = [];
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    HashSet<string> completedIds;
    DriveClient? drive;
    FolderNode? destination;
    bool authBusy, closing, forceClose;
    int statusToken;

    public MainWindow(Database db, GoogleAuth auth, UploadManager manager)
    {
        InitializeComponent();
        (this.db, this.auth, this.manager) = (db, auth, manager);
        notifier = new Notifier(db, plain);
        completedIds = db.All().Where(x => x.Status == Status.Done).Select(x => x.Id).ToHashSet();
        QueueList.ItemsSource = queue;
        HistoryList.ItemsSource = history;
        SourceList.ItemsSource = sources;
        FolderTree.ItemsSource = roots;
        PhoneList.ItemsSource = phones;
        VersionText.Text = $"v{Version}";
        ConnectButton.Content = LoginText;
        DataDirText.Text = $"Dados protegidos pelo Windows:\n{Database.DataDir()}";
        ClientNote.Visibility = OAuthClient.Bundled() is null ? Visibility.Visible : Visibility.Collapsed;
        LoadSettings();
        ShowStatus(DefaultStatus);
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Refresh();
        if (File.Exists(Path.Combine(Database.DataDir(), "account.bin")))
            _ = AuthTask(() => auth.Restore());
    }

    static string LoginText => OAuthClient.Bundled() is null ? "Conectar conta" : "Entrar com Google";

    void Notice(string text) => Dialogs.Message(this, text);

    void ShowStatus(string text, int timeoutMs = 0)
    {
        var token = ++statusToken;
        StatusText.Text = text;
        if (timeoutMs > 0)
        {
            var reset = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(timeoutMs) };
            reset.Tick += (_, _) =>
            {
                reset.Stop();
                if (token == statusToken) ShowStatus(DefaultStatus);
            };
            reset.Start();
        }
    }

    /// <summary>Roda uma operação de rede sem travar a janela e mostra o erro, se houver.</summary>
    async Task<T?> Try<T>(Func<Task<T>> work, bool silent = false)
    {
        try
        {
            return await work();
        }
        catch (Exception exc) when (exc is AuthException or DriveException or TemporaryException or IOException or InvalidOperationException or ArgumentException)
        {
            if (!silent) Notice(exc.Message);
            return default;
        }
    }

    async Task AuthTask(Func<Task> work)
    {
        if (authBusy) return;
        authBusy = true;
        ConnectButton.IsEnabled = false;
        ShowStatus("Conectando ao Google…");
        try
        {
            await work();
            Connected();
        }
        catch (AuthException exc)
        {
            Notice(exc.Message);
            ShowStatus(DefaultStatus);
        }
        finally
        {
            authBusy = false;
            ConnectButton.IsEnabled = true;
        }
    }

    // ---- Janela

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (TransfersPage is null || db is null) return;
        TransfersPage.Visibility = NavTransfers.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = NavHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = NavActivity.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    public void ForceClose()
    {
        forceClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (forceClose)
        {
            manager.Closed = true;
            manager.PauseAll();
            timer.Stop();
            return;
        }
        if (manager.RunningCount > 0)
        {
            if (!closing && !Dialogs.Ask(this, "Fechar DriveFlow", "Há uploads ativos. Pausar e fechar? Eles continuam ao reabrir."))
            {
                e.Cancel = true;
                return;
            }
            // Espera os envios gravarem o progresso; Refresh fecha quando não houver mais nenhum.
            closing = true;
            manager.Closed = true;
            manager.PauseAll();
            ShowStatus("Salvando antes de fechar…");
            e.Cancel = true;
            return;
        }
        manager.Closed = true;
        manager.PauseAll();
        timer.Stop();
        db.Event("", "APPLICATION_CLOSED");
    }

    // ---- Conta

    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (authBusy) return;
        if (manager.RunningCount > 0)
        {
            Notice("Pause os uploads antes de trocar a conta.");
            return;
        }
        if (auth.AccountId != "")
        {
            manager.PauseAll();
            auth.Logout();
            drive = null;
            destination = null;
            roots.Clear();
            DrivePath.Text = "Meu Drive";
            DestinationText.Text = "Conecte a conta.";
            AccountText.Text = "Desconectado";
            AccountText.ToolTip = null;
            ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "OnNavyMuted");
            ConnectButton.Content = LoginText;
            db.Event("", "ACCOUNT_DISCONNECTED_LOCALLY");
            return;
        }
        if (File.Exists(Path.Combine(Database.DataDir(), "account.bin")) && !auth.ReauthRequired)
        {
            await AuthTask(() => auth.Restore());
            return;
        }
        var client = OAuthClient.Bundled();
        if (client is null)
        {
            ImportClient_Click(sender, e);
            return;
        }
        await AuthTask(() => auth.Login(client, db.Setting("full_scope", true)));
    }

    async void ImportClient_Click(object sender, RoutedEventArgs e)
    {
        if (authBusy) return;
        if (auth.AccountId != "")
        {
            Notice("Desconecte a conta antes.");
            return;
        }
        var dialog = new OpenFileDialog { Title = "Importar cliente OAuth Desktop", Filter = "Credenciais Google (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        await AuthTask(async () =>
        {
            OAuthClient client;
            try { client = OAuthClient.Parse(await File.ReadAllTextAsync(dialog.FileName)); }
            catch (IOException) { throw new AuthException("Não foi possível ler o arquivo de credencial."); }
            await auth.Login(client, db.Setting("full_scope", true));
        });
    }

    void Connected()
    {
        drive = new DriveClient(auth.CreateClient());
        destination = null;
        DestinationText.Text = "Escolha a pasta de destino.";
        AccountText.Text = "Conectado";
        AccountText.ToolTip = auth.Email;
        ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Success");
        ConnectButton.Content = "Desconectar";
        db.SaveAccount(auth.AccountId, auth.Email);
        db.Event("", "ACCOUNT_CONNECTED");
        ShowStatus(DefaultStatus);
        ResetTree();
    }

    // ---- Árvore do Drive

    void ResetTree()
    {
        roots.Clear();
        if (drive is null) return;
        var root = new FolderNode("root", "Meu Drive", null, LoadChildren);
        roots.Add(root);
        root.IsExpanded = true;
        root.IsSelected = true;
    }

    async Task LoadChildren(FolderNode node)
    {
        if (drive is null) return;
        var folders = await Try(() => drive.Folders(node.Id), silent: true);
        node.Children.Clear();
        if (folders is null)
        {
            ShowStatus("Não foi possível listar as pastas do Drive. Clique em Atualizar.", 8000);
            return;
        }
        foreach (var folder in folders)
            node.Children.Add(new FolderNode(folder.Id, folder.Name, node, LoadChildren) { IsDestination = folder.Id == destination?.Id });
    }

    FolderNode? SelectedFolder => FolderTree.SelectedItem as FolderNode;

    void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        DrivePath.Text = SelectedFolder?.FullName ?? "Meu Drive";

    void FolderTree_DoubleClick(object sender, MouseButtonEventArgs e) => ChooseFolder_Click(sender, e);

    void DriveBack_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFolder?.Parent is { } parent) parent.IsSelected = true;
    }

    void DriveRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (drive is null) return;
        if (roots.Count == 0) ResetTree();
        else (SelectedFolder ?? roots[0]).Invalidate();
    }

    void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (drive is null)
        {
            Notice("Conecte sua conta Google primeiro.");
            return;
        }
        if (SelectedFolder is not { } folder) return;
        foreach (var node in roots.SelectMany(r => r.Descendants())) node.IsDestination = node.Id == folder.Id;
        destination = folder;
        DestinationText.Text = "✓  Destino: " + folder.FullName;
        DestinationText.SetResourceReference(TextBlock.ForegroundProperty, "Success");
    }

    async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (drive is null)
        {
            Notice("Conecte sua conta Google primeiro.");
            return;
        }
        var parent = SelectedFolder ?? roots.FirstOrDefault();
        if (parent is null) return;
        var name = Dialogs.GetText(this, "Nova pasta no Drive", "Nome da pasta:")?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (await Try(() => drive.CreateFolder(parent.Id, name)) is not null)
        {
            parent.IsExpanded = true;
            parent.Invalidate();
        }
    }

    void RefreshFolder(string folderId)
    {
        foreach (var node in roots.SelectMany(r => r.Descendants()).ToList())
            if (node.Id == folderId && node.IsExpanded) node.Invalidate();
    }

    // ---- Origem

    void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Arquivos para enviar", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames)
            if (sources.All(s => s.Path != path)) sources.Add(new SourceRow(path, false));
    }

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Pasta para enviar (com subpastas)", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FolderNames)
            if (sources.All(s => s.Path != path)) sources.Add(new SourceRow(path, true));
    }

    void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        var selected = SourceList.SelectedItems.Cast<SourceRow>().ToList();
        foreach (var row in selected.Count > 0 ? selected : sources.ToList()) sources.Remove(row);
    }

    /// <summary>Põe os arquivos e pastas escolhidos na fila. Falso quando falta destino.</summary>
    async Task<bool> Enqueue()
    {
        if (destination is null || drive is null)
        {
            Notice("Escolha a pasta de destino e clique em Usar esta pasta.");
            return false;
        }
        var errors = new List<string>();
        var account = auth.AccountId;
        var (parentId, parentName) = (destination.Id, destination.FullName);
        foreach (var row in sources.Where(s => !s.IsFolder).ToList())
        {
            try
            {
                var id = db.Add(row.Path, parentId, parentName, account);
                db.Event(id, "ADDED_TO_QUEUE");
                sources.Remove(row);
            }
            catch (Exception exc) when (exc is IOException or ArgumentException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(row.Path)}: {exc.Message}");
            }
        }
        var folders = sources.Where(s => s.IsFolder).ToList();
        if (folders.Count > 0)
        {
            foreach (var row in folders) sources.Remove(row);
            ShowStatus("Criando as pastas no Drive…", 8000);
            var plan = await Try(() => FolderPlan.Build(drive, folders.Select(f => f.Path), parentId, parentName));
            foreach (var file in plan ?? [])
            {
                try
                {
                    var id = db.Add(file.Path, file.FolderId, file.FolderName, account, notifyName: file.NotifyName);
                    db.Event(id, "ADDED_TO_QUEUE_FROM_FOLDER");
                }
                catch (Exception exc) when (exc is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    errors.Add($"{Path.GetFileName(file.Path)}: {exc.Message}");
                }
            }
            RefreshFolder(parentId);
        }
        if (errors.Count > 0) Notice(string.Join("\n", errors));
        return true;
    }

    // ---- Fila

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (auth.AccountId == "")
        {
            Notice("Conecte sua conta Google primeiro.");
            return;
        }
        if (sources.Count > 0 && !await Enqueue()) return;
        foreach (var item in db.All())
            if (item.Enabled && item.Account == auth.AccountId)
            {
                try { manager.Start(item.Id); }
                catch (InvalidOperationException) { }
            }
        Refresh();
    }

    void PauseAll_Click(object sender, RoutedEventArgs e) => manager.PauseAll();

    static string? RowId(object sender) => ((FrameworkElement)sender).DataContext is QueueRow row ? row.Id : null;

    void RowPlay_Click(object sender, RoutedEventArgs e)
    {
        if (RowId(sender) is not { } id || db.Get(id) is not { } item) return;
        if (item.Status == Status.Canceled && !manager.IsRunning(id)) db.Update(id, new() { Status = Status.Paused });
        db.Update(id, new() { Enabled = true });
        try { manager.Start(id); }
        catch (InvalidOperationException exc) { Notice(exc.Message); }
        Refresh();
    }

    void RowPause_Click(object sender, RoutedEventArgs e)
    {
        if (RowId(sender) is { } id) manager.Pause(id);
    }

    void RowStop_Click(object sender, RoutedEventArgs e)
    {
        if (RowId(sender) is { } id && Dialogs.Ask(this, "Cancelar upload", "Cancelar este envio?")) manager.Pause(id, cancel: true);
    }

    void RowRemove_Click(object sender, RoutedEventArgs e)
    {
        if (RowId(sender) is { } id && Dialogs.Ask(this, "Remover da fila", "Remover da lista? Nenhum arquivo é apagado."))
        {
            manager.Remove(id);
            Refresh();
        }
    }

    void QueueEnabledChanged(QueueRow row, bool enabled)
    {
        db.Update(row.Id, new() { Enabled = enabled });
        if (!enabled) manager.Pause(row.Id);
    }

    UploadItem? SelectedQueueItem => QueueList.SelectedItem is QueueRow row ? db.Get(row.Id) : null;

    void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedQueueItem is { Error.Length: > 0 } item) ShowStatus(item.Error, 6000);
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQueueItem is not { } item) return;
        if (item.RemoteId != "" || manager.IsRunning(item.Id))
        {
            Notice("Só é possível renomear antes do envio começar.");
            return;
        }
        var name = Dialogs.GetText(this, "Nome no Google Drive", "Novo nome no Drive:", item.Name)?.Trim();
        if (!string.IsNullOrEmpty(name)) db.Update(item.Id, new() { Name = name, Status = Status.Paused, Error = "" });
    }

    void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQueueItem is not { Status: Status.SessionExpired } item)
        {
            Notice("Disponível só para sessões expiradas.");
            return;
        }
        if (!Dialogs.Ask(this, "Reiniciar sessão expirada", "A sessão expirou. Reenviar do início?")) return;
        db.Event(item.Id, $"USER_RESTART_EXPIRED previous_offset={item.Offset}");
        db.Update(item.Id, new() { Session = "", Offset = 0, Status = Status.Paused, Error = "" });
        try { manager.Start(item.Id); }
        catch (InvalidOperationException exc) { Notice(exc.Message); }
    }

    void Refresh()
    {
        manager.Tick();
        var rows = db.All();
        var pending = rows.Where(x => x.Status != Status.Done).ToList();
        var done = rows.Where(x => x.Status == Status.Done).ToList();
        StatQueued.Text = pending.Count(x => x.Status != Status.Canceled).ToString();
        StatConfirmed.Text = Format.Size(rows.Sum(x => (double)x.Offset));
        StatSpeed.Text = Format.Size(rows.Sum(x => x.Speed)) + "/s";
        StatDone.Text = done.Count.ToString();
        Sync(pending);
        if (HistoryPage.Visibility == Visibility.Visible && (history.Count != done.Count || history.FirstOrDefault()?.Id != done.LastOrDefault()?.Id))
        {
            history.Clear();
            foreach (var item in Enumerable.Reverse(done)) history.Add(new HistoryRow(item));
        }
        var newlyDone = done.Where(x => !completedIds.Contains(x.Id)).ToList();
        completedIds = done.Select(x => x.Id).ToHashSet();
        notifier.UploadsCompleted(rows, newlyDone);
        if (auth.AccountId != "")
            foreach (var folderId in newlyDone.Where(x => x.Account == auth.AccountId).Select(x => x.FolderId).Distinct())
                RefreshFolder(folderId);
        if (ActivityPage.Visibility == Visibility.Visible)
        {
            var text = string.Join("\n", db.Events().Select(r => $"{r.Time}   {(r.UploadId.Length > 0 ? r.UploadId[..Math.Min(8, r.UploadId.Length)] : "APP")}   {r.Event}"));
            if (ActivityText.Text != text) ActivityText.Text = text;
        }
        if (closing && manager.RunningCount == 0) Close();
    }

    /// <summary>Atualiza as linhas no lugar, para não perder a seleção nem piscar.</summary>
    void Sync(List<UploadItem> items)
    {
        var ids = items.Select(x => x.Id).ToHashSet();
        foreach (var stale in queue.Where(r => !ids.Contains(r.Id)).ToList()) queue.Remove(stale);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var index = -1;
            for (var j = i; j < queue.Count; j++)
                if (queue[j].Id == item.Id) { index = j; break; }
            QueueRow row;
            if (index < 0)
            {
                row = new QueueRow(item.Id, QueueEnabledChanged);
                queue.Insert(i, row);
            }
            else
            {
                row = queue[index];
                if (index != i) queue.Move(index, i);
            }
            row.Load(item, removing: false);
        }
    }

    // ---- Histórico

    HistoryRow? SelectedHistory => HistoryList.SelectedItem as HistoryRow;
    static string Link(HistoryRow row) => $"https://drive.google.com/file/d/{row.RemoteId}/view";

    void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHistory is not { } row) return;
        if (Copy(Link(row))) ShowStatus("Link copiado.", 5000);
    }

    void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHistory is { } row) Process.Start(new ProcessStartInfo(Link(row)) { UseShellExecute = true });
    }

    void CopyId_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHistory is { } row) Copy(row.RemoteId);
    }

    /// <summary>A área de transferência pode estar ocupada por outro programa.</summary>
    bool Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            ShowStatus("Não foi possível copiar. Tente de novo.", 5000);
            return false;
        }
    }

    void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Ask(this, "Limpar histórico", "Limpar o histórico? Os arquivos no Drive continuam.")) return;
        foreach (var item in db.All().Where(x => x.Status == Status.Done)) db.Remove(item.Id);
        history.Clear();
    }

    // ---- Configurações

    void LoadSettings()
    {
        ThemeBox.ItemsSource = ThemeManager.Themes;
        ThemeBox.SelectedItem = ThemeManager.Normalize(db.Setting("theme", "Automático"));
        ConcurrencyBox.ItemsSource = new[] { 1, 2, 3 };
        ConcurrencyBox.SelectedItem = Math.Clamp(db.Setting("concurrency", 1), 1, 3);
        ChunkBox.ItemsSource = new[] { 1, 4, 8, 16, 32, 64 };
        ChunkBox.SelectedItem = db.Setting("chunk_mib", 8);
        RetriesBox.Text = db.Setting("retries", 10).ToString();
        FullScopeBox.IsChecked = db.Setting("full_scope", true);
        var config = notifier.Config();
        WhatsAppEnabled.IsChecked = config.Enabled;
        recipients.Clear();
        recipients.AddRange(notifier.Recipients());
        ShowPhones();
    }

    void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is string theme && IsLoaded)
        {
            db.SaveSetting("theme", theme);
            ThemeManager.Apply(theme);
        }
    }

    void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RetriesBox.Text, out var retries) || retries is < 0 or > 50)
        {
            Notice("Tentativas por falha: use um número de 0 a 50.");
            return;
        }
        db.SaveSetting("concurrency", (int)(ConcurrencyBox.SelectedItem ?? 1));
        db.SaveSetting("chunk_mib", (int)(ChunkBox.SelectedItem ?? 8));
        db.SaveSetting("retries", retries);
        db.SaveSetting("full_scope", FullScopeBox.IsChecked == true);
        ShowStatus("Configurações salvas.", 5000);
    }

    void ShowPhones()
    {
        phones.Clear();
        foreach (var (phone, _) in recipients) phones.Add(phone);
    }

    void AddPhone_Click(object sender, RoutedEventArgs e)
    {
        var phone = WhatsApp.Normalize(PhoneBox.Text);
        var key = ApiKeyBox.Text.Trim();
        if (phone.Length < 10 || key == "")
        {
            Notice("Informe o número com DDI e DDD e a apikey recebida do CallMeBot.");
            return;
        }
        recipients.RemoveAll(r => r.Phone == phone);
        recipients.Add((phone, key));
        PhoneBox.Clear();
        ApiKeyBox.Clear();
        ShowPhones();
    }

    void RemovePhone_Click(object sender, RoutedEventArgs e)
    {
        if (PhoneList.SelectedItem is string phone)
        {
            recipients.RemoveAll(r => r.Phone == phone);
            ShowPhones();
        }
    }

    void SaveWhatsApp_Click(object sender, RoutedEventArgs e)
    {
        notifier.Save(WhatsAppEnabled.IsChecked == true, recipients);
        ShowStatus("Notificações salvas.", 5000);
    }

    async void TestWhatsApp_Click(object sender, RoutedEventArgs e)
    {
        if (recipients.Count == 0)
        {
            Notice("Adicione ao menos um número.");
            return;
        }
        ShowStatus("Enviando teste…");
        var errors = await notifier.Notify(WhatsApp.Message("DriveFlow TESTE"), recipients.ToList());
        ShowStatus(DefaultStatus);
        Notice(errors.Count == 0 ? "Teste enviado. Confira o WhatsApp." : string.Join("\n", errors));
    }

    void OpenDataDir_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Database.DataDir()) { UseShellExecute = true });

    // ---- Atividade

    void ExportEvents_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "driveflow-eventos.log", Filter = "Log (*.log)|*.log" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, ActivityText.Text); }
        catch (IOException) { Notice("Não foi possível salvar o arquivo."); }
    }
}
