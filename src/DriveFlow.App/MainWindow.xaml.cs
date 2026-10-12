using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DriveFlow.Core;
using DriveFlow.Core.Auth;
using DriveFlow.Core.Drive;
using DriveFlow.Core.Notify;
using DriveFlow.Core.Storage;
using DriveFlow.Core.Upload;
using Microsoft.Win32;

namespace DriveFlow.App;

/// <summary>Ícones da versão Python (pasta Assets), pintados pelo tema na tela.</summary>
public static class Icons
{
    public static readonly ImageSource Transfers = TintedIcon.Asset("transferencias_24px.png");
    public static readonly ImageSource Charts = TintedIcon.Asset("graficos.png");
    public static readonly ImageSource History = TintedIcon.Asset("historico_24px.png");
    public static readonly ImageSource Settings = TintedIcon.Asset("config_24px.png");
    public static readonly ImageSource Activity = TintedIcon.Asset("lista_24px.png");
    public static readonly ImageSource Back = TintedIcon.Asset("voltar.png");
    public static readonly ImageSource Refresh = TintedIcon.Asset("atualizar.png");
    public static readonly ImageSource NewFolder = TintedIcon.Asset("nova_pasta.png");
    public static readonly ImageSource Play = TintedIcon.Asset("play_24px.png");
    public static readonly ImageSource Pause = TintedIcon.Asset("pausa_24px.png");
    public static readonly ImageSource Stop = TintedIcon.Asset("stop.png");
    public static readonly ImageSource Remove = TintedIcon.Asset("remover.png");
}

/// <summary>Configuração do monitoramento remoto, mesmo formato da versão Python.</summary>
public sealed record RemoteMonitoring(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("computer_id")] string ComputerId,
    [property: JsonPropertyName("computer_name")] string ComputerName,
    [property: JsonPropertyName("update_interval_seconds")] int UpdateIntervalSeconds,
    [property: JsonPropertyName("firebase_credentials")] string FirebaseCredentials)
{
    public static RemoteMonitoring Default() =>
        new(false, Guid.NewGuid().ToString("N"), Environment.MachineName, 5, "config/firebase-service-account.json");
}

public partial class MainWindow : Window
{
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
    public const string AdvancedTip = "Para HD: compactação e upload nunca rodam juntos. Quando um volume fica pronto, "
        + "o WinRAR pausa, o volume sobe em velocidade total e a compactação continua. "
        + "O disco lê ou grava uma coisa por vez, com menos movimento da cabeça de leitura.";
    const string DefaultStatus = "Fila salva neste computador";
    const string SafeError = "A operação falhou. Verifique a conexão e a conta.";

    readonly Database db;
    readonly GoogleAuth auth;
    readonly UploadManager manager;
    readonly Notifier notifier;
    readonly HttpClient plain = new();
    readonly ObservableCollection<QueueRow> queue = [];
    readonly ObservableCollection<HistoryRow> history = [];
    readonly ObservableCollection<PhoneRow> phones = [];
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer folderTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    HashSet<string> completedIds;
    HashSet<string> duplicateWarned;
    DriveClient? drive;
    bool authBusy, closing, forceClose, loadingSettings;
    int statusToken;

    public MainWindow(Database db, GoogleAuth auth, UploadManager manager)
    {
        InitializeComponent();
        (this.db, this.auth, this.manager) = (db, auth, manager);
        notifier = new Notifier(db, plain);
        var rows = db.All();
        completedIds = rows.Where(x => x.Status == Status.Done).Select(x => x.Id).ToHashSet();
        duplicateWarned = rows.Where(x => x.Status == Status.DuplicateName).Select(x => x.Id).ToHashSet();
        QueueList.ItemsSource = queue;
        HistoryList.ItemsSource = history;
        PhoneList.ItemsSource = phones;
        LocalTree.ItemsSource = localRoots;
        FolderTree.ItemsSource = driveRoots;
        VersionText.Text = $"v{Version}";
        ConnectButton.Content = LoginText;
        DataDirText.Text = $"Dados protegidos pelo Windows:\n{Database.DataDir()}";
        ClientNote.Visibility = OAuthClient.Bundled() is null ? Visibility.Visible : Visibility.Collapsed;
        UnitsIcon.Source = ShellIcons.ForPath((Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\");
        LoadSettings();
        UpdateCompressButton();
        ShowStatus(DefaultStatus);
        StateChanged += (_, _) => WindowStateChanged();
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        folderTimer.Tick += (_, _) => RefreshWatchedFolders();
        folderTimer.Start();
        NavigateLocal("");
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

    static bool Expected(Exception exc) =>
        exc is AuthException or DriveException or TemporaryException or IOException or InvalidOperationException
            or ArgumentException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException;

    /// <summary>Mensagem que pode ir para a tela: erros de conta e de validação; o resto vira um aviso genérico.</summary>
    static string Friendly(Exception exc) => exc is AuthException or ArgumentException or InvalidOperationException ? exc.Message : SafeError;

    /// <summary>Roda uma operação de rede sem travar a janela e mostra o erro, se houver.</summary>
    async Task<T?> Try<T>(Func<Task<T>> work, bool silent = false)
    {
        try
        {
            return await work();
        }
        catch (Exception exc) when (Expected(exc))
        {
            db.Event("", exc is AuthException ? exc.Message : $"BACKGROUND_ERROR type={exc.GetType().Name}");
            if (!silent) Notice(Friendly(exc));
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
            UpdateConnectionDot();
        }
    }

    // ---- Janela

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    /// <summary>
    /// Sem moldura, a janela maximizada passa da tela pela borda de redimensionar e cortaria os botões
    /// de minimizar, maximizar e fechar; a margem compensa. O botão vira Restaurar.
    /// </summary>
    void WindowStateChanged()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restaurar" : "Maximizar";
        if (!maximized)
        {
            Root.Margin = new Thickness(0);
            return;
        }
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var padded = GetSystemMetrics(92) / scale; // SM_CXPADDEDBORDER
        var border = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = new Thickness(border.Left + padded, border.Top + padded, border.Right + padded, border.Bottom + padded);
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (TransfersPage is null || db is null) return;
        static Visibility Show(RadioButton nav) => nav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TransfersPage.Visibility = Show(NavTransfers);
        ChartsPage.Visibility = Show(NavCharts);
        HistoryPage.Visibility = Show(NavHistory);
        SettingsPage.Visibility = Show(NavSettings);
        ActivityPage.Visibility = Show(NavActivity);
        Refresh();
    }

    /// <summary>Teste rápido do pacote: abre cada página em cada tema para os modelos de tela serem montados.</summary>
    public void SmokeTour()
    {
        foreach (var theme in ThemeManager.Themes.Reverse())
        {
            ThemeManager.Apply(theme);
            foreach (var nav in new[] { NavCharts, NavHistory, NavSettings, NavActivity, NavTransfers })
            {
                nav.IsChecked = true;
                UpdateLayout();
            }
        }
        ThemeManager.Apply(db.Setting("theme", "Automático"));
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
            folderTimer.Stop();
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
        folderTimer.Stop();
        db.Event("", "APPLICATION_CLOSED");
    }

    // ---- Conta

    void UpdateConnectionDot()
    {
        var connected = auth.AccountId != "";
        ConnectionDot.Fill = new SolidColorBrush(connected ? Color.FromRgb(0x22, 0xC5, 0x5E) : Color.FromRgb(0xDF, 0x66, 0x66));
        ConnectionDot.ToolTip = connected ? "Conta autenticada" : "Conta desconectada";
    }

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
            ClearDriveTree();
            DrivePath.Text = "Meu Drive";
            DestinationText.Text = "Conecte a conta.";
            DestinationText.ClearValue(TextBlock.ForegroundProperty);
            DestinationText.ClearValue(TextBlock.FontWeightProperty);
            AccountText.Text = "Desconectado";
            AccountText.ToolTip = null;
            UpdateConnectionDot();
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
        DestinationText.ClearValue(TextBlock.ForegroundProperty);
        DestinationText.ClearValue(TextBlock.FontWeightProperty);
        AccountText.Text = "Conectado";
        AccountText.ToolTip = auth.Email;
        UpdateConnectionDot();
        ConnectButton.Content = "Desconectar";
        db.SaveAccount(auth.AccountId, auth.Email);
        db.Event("", "ACCOUNT_CONNECTED");
        ShowStatus(DefaultStatus);
        ClearDriveTree();
        ResetDriveTree();
    }

    // ---- Arquivos locais (FileBrowser da versão Python)

    readonly ObservableCollection<LocalNode> localRoots = [];
    readonly HashSet<string> checkedPaths = new(StringComparer.OrdinalIgnoreCase);
    string localRoot = "";

    sealed record LocalEntry(string Path, string Name, bool IsDir, long Size);

    static List<LocalEntry> ListLocal(string path)
    {
        if (path == "")
        {
            var drives = new List<LocalEntry>();
            foreach (var unit in DriveInfo.GetDrives())
            {
                var letter = unit.Name.TrimEnd('\\');
                string label;
                try { label = unit.IsReady && unit.VolumeLabel != "" ? $"{unit.VolumeLabel} ({letter})" : letter; }
                catch (IOException) { label = letter; }
                catch (UnauthorizedAccessException) { label = letter; }
                drives.Add(new LocalEntry(unit.Name, label, true, 0));
            }
            return drives;
        }
        var entries = new List<LocalEntry>();
        try
        {
            foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                // Como o Explorer sem "itens ocultos": arquivos ocultos e do sistema ficam de fora.
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                var dir = (info.Attributes & FileAttributes.Directory) != 0;
                entries.Add(new LocalEntry(info.FullName, info.Name, dir, dir ? 0 : ((FileInfo)info).Length));
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        return entries.OrderBy(x => !x.IsDir).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    LocalNode MakeLocal(LocalEntry entry) =>
        new(entry.Path, entry.Name, entry.IsDir, entry.Size, LoadLocalChildren, LocalCheckChanged, checkedPaths.Contains(entry.Path));

    async void LoadLocalChildren(LocalNode node)
    {
        var entries = await Task.Run(() => ListLocal(node.Path));
        node.Children.Clear();
        foreach (var entry in entries) node.Children.Add(MakeLocal(entry));
    }

    async void NavigateLocal(string path)
    {
        if (path != "" && !Directory.Exists(path))
        {
            LocalPath.ToolTip = "Pasta não encontrada.";
            return;
        }
        LocalPath.ToolTip = null;
        localRoot = path;
        LocalPath.Text = path;
        var entries = await Task.Run(() => ListLocal(path));
        if (localRoot != path) return;
        localRoots.Clear();
        foreach (var entry in entries) localRoots.Add(MakeLocal(entry));
    }

    void Units_Click(object sender, RoutedEventArgs e) => NavigateLocal("");

    void LocalUp_Click(object sender, RoutedEventArgs e)
    {
        var path = LocalPath.Text.Trim();
        var parent = path == "" ? null : Path.GetDirectoryName(path.TrimEnd('\\') + (path.EndsWith(":\\") || path.EndsWith(':') ? "\\" : ""));
        NavigateLocal(parent ?? "");
    }

    void LocalPath_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) NavigateLocal(LocalPath.Text.Trim());
    }

    void LocalCheckChanged(LocalNode node, bool value)
    {
        if (value) checkedPaths.Add(node.Path);
        else checkedPaths.Remove(node.Path);
        UpdateLocalSummary();
    }

    IEnumerable<LocalNode> VisibleLocal() => localRoots.SelectMany(n => n.Descendants());

    void SetChecked(string path, bool value)
    {
        if (value) checkedPaths.Add(path);
        else checkedPaths.Remove(path);
        foreach (var node in VisibleLocal().Where(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)))
            node.ShowChecked(value);
        UpdateLocalSummary();
    }

    List<string> CheckedFolders() => checkedPaths.Where(Directory.Exists).Order(StringComparer.OrdinalIgnoreCase).ToList();

    void UpdateLocalSummary()
    {
        var folders = CheckedFolders().Count;
        var files = checkedPaths.Count - folders;
        var parts = new List<string>();
        if (files > 0) parts.Add($"{files} arquivo(s)");
        if (folders > 0) parts.Add($"{folders} pasta(s)");
        LocalSummary.Text = parts.Count > 0 ? string.Join(" e ", parts) + " selecionado(s)" : "Nada selecionado";
    }

    void PickFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Selecionar arquivos", Multiselect = true, Filter = "Todos os arquivos (*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames) SetChecked(path, true);
    }

    void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        checkedPaths.Clear();
        foreach (var node in VisibleLocal()) node.ShowChecked(false);
        UpdateLocalSummary();
    }

    // ---- Árvore do Drive (drive_tree.py)

    readonly ObservableCollection<DriveNode> driveRoots = [];
    readonly Dictionary<string, DriveNode> driveNodes = [];
    readonly HashSet<string> driveLoaded = [], driveLoading = [], reloadPending = [];
    (string Id, string Name)? destination;
    int generation;

    void ClearDriveTree()
    {
        generation++;
        driveRoots.Clear();
        driveNodes.Clear();
        driveLoaded.Clear();
        driveLoading.Clear();
        reloadPending.Clear();
    }

    void ResetDriveTree()
    {
        ClearDriveTree();
        if (drive is null) return;
        var root = new DriveNode("root", "Meu Drive", DriveFile.FolderType, null, null, n => _ = LoadDrive(n));
        driveRoots.Add(root);
        driveNodes["root"] = root;
        MarkDestination();
        root.IsSelected = true;
        root.IsExpanded = true;
    }

    async Task LoadDrive(DriveNode node, bool force = false, bool silent = false)
    {
        if (!node.IsFolder || drive is null) return;
        var (id, gen, client) = (node.Id, generation, drive);
        if (driveLoading.Contains(id))
        {
            if (force) reloadPending.Add(id);
            return;
        }
        if (driveLoaded.Contains(id) && !force) return;
        driveLoading.Add(id);
        node.Tip = "Carregando…";
        List<DriveFile>? rows = null;
        Exception? error = null;
        try
        {
            rows = await client.Children(id);
        }
        catch (Exception exc) when (Expected(exc))
        {
            error = exc;
        }
        if (gen != generation || !driveNodes.TryGetValue(id, out var current) || current != node) return;
        driveLoading.Remove(id);
        if (rows is null)
        {
            db.Event("", error is AuthException ? error.Message : $"BACKGROUND_ERROR type={error?.GetType().Name}");
            node.Tip = "Falha ao carregar. Clique em Atualizar.";
            node.ForgetLoaded();
            if (!silent) Notice(Friendly(error!));
            // Um pedido de atualização chegou durante a falha: tenta uma vez, sem laço.
            if (reloadPending.Remove(id)) await LoadDrive(node, force: true);
            return;
        }
        driveLoaded.Add(id);
        node.Tip = "";
        // Reconcilia pelo ID para manter pastas abertas e a seleção.
        foreach (var stale in node.Children.Where(c => c.Id == "" || !rows.Any(r => r.Id == c.Id)).ToList())
        {
            ForgetDrive(stale);
            node.Children.Remove(stale);
        }
        for (var position = 0; position < rows.Count; position++)
        {
            var row = rows[position];
            var child = node.Children.FirstOrDefault(c => c.Id == row.Id);
            if (child is null)
            {
                child = DriveNode.From(row, node, n => _ = LoadDrive(n));
                node.Children.Insert(Math.Min(position, node.Children.Count), child);
            }
            else
            {
                child.Update(row.Name, row.MimeType, row.Size);
                var index = node.Children.IndexOf(child);
                if (index != position && position < node.Children.Count) node.Children.Move(index, position);
            }
            driveNodes[row.Id] = child;
        }
        MarkDestination();
        FolderSelected();
        if (reloadPending.Remove(id)) await LoadDrive(node, force: true);
    }

    void ForgetDrive(DriveNode node)
    {
        foreach (var child in node.Children) ForgetDrive(child);
        if (node.Id == "") return;
        driveNodes.Remove(node.Id);
        driveLoaded.Remove(node.Id);
    }

    void MarkDestination()
    {
        foreach (var node in driveNodes.Values) node.IsDestination = node.Id == destination?.Id;
    }

    /// <summary>A pasta aberta: a selecionada ou, num arquivo, a pasta dele.</summary>
    DriveNode? CurrentFolder()
    {
        var node = FolderTree.SelectedItem as DriveNode;
        return node is { IsFolder: false } ? node.Parent : node;
    }

    void FolderSelected() => DrivePath.Text = CurrentFolder()?.FullName ?? "Meu Drive";

    void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => FolderSelected();

    void FolderTree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Duplo clique numa pasta também a escolhe como destino.
        if (FolderTree.SelectedItem is DriveNode { IsFolder: true, Id.Length: > 0 }) ChooseFolder_Click(sender, e);
    }

    void DriveBack_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentFolder()?.Parent is { } parent) parent.IsSelected = true;
    }

    void DriveRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (auth.AccountId == "" || drive is null) return;
        if (driveNodes.Count == 0)
        {
            ResetDriveTree();
            return;
        }
        // Atualiza todas as pastas já abertas, não só a selecionada.
        foreach (var id in driveLoaded.Append("root").Distinct().ToList())
            if (driveNodes.TryGetValue(id, out var node)) _ = LoadDrive(node, force: true);
    }

    void RefreshFolder(string folderId)
    {
        if (driveNodes.TryGetValue(folderId, out var node)) _ = LoadDrive(node, force: true);
    }

    /// <summary>A cada 30 s: só a pasta aberta e o destino, sem empilhar pedidos.</summary>
    void RefreshWatchedFolders()
    {
        UpdateCompressButton();
        if (auth.AccountId == "" || closing || authBusy) return;
        var ids = new HashSet<string>();
        if (destination is { } target) ids.Add(target.Id);
        if (CurrentFolder() is { } current) ids.Add(current.Id);
        foreach (var id in ids)
            if (driveNodes.TryGetValue(id, out var node) && !driveLoading.Contains(id))
                _ = LoadDrive(node, force: true, silent: true);
    }

    void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (auth.AccountId == "" || drive is null)
        {
            Notice("Conecte sua conta Google primeiro.");
            return;
        }
        var folder = CurrentFolder() ?? driveRoots.FirstOrDefault();
        if (folder is null) return;
        destination = (folder.Id, folder.FullName);
        DestinationText.Text = "✓  Destino: " + folder.FullName;
        DestinationText.Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
        DestinationText.FontWeight = FontWeights.SemiBold;
        MarkDestination();
    }

    async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (auth.AccountId == "" || drive is null)
        {
            Notice("Conecte sua conta Google primeiro.");
            return;
        }
        var name = Dialogs.GetText(this, "Nova pasta no Drive", "Nome da pasta:")?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var parent = CurrentFolder()?.Id ?? "root";
        var client = drive;
        if (await Try(() => client.CreateFolder(parent, name)) is not null) RefreshFolder(parent);
    }

    // ---- Compactar (WinRAR): a compactação chega na próxima etapa da versão C#.

    static string? FindWinRar()
    {
        var candidates = new List<string>();
        void Read(RegistryKey hive, string key, string value)
        {
            try
            {
                using var handle = hive.OpenSubKey(key);
                if (handle?.GetValue(value) is string path) candidates.Add(path);
            }
            catch (Exception exc) when (exc is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        Read(Registry.LocalMachine, @"SOFTWARE\WinRAR", "exe64");
        Read(Registry.LocalMachine, @"SOFTWARE\WinRAR", "exe32");
        Read(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\WinRAR", "exe32");
        Read(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WinRAR.exe", "");
        Read(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WinRAR.exe", "");
        foreach (var variable in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } folder)
                candidates.Add(Path.Combine(folder, "WinRAR", "WinRAR.exe"));
        return candidates.Select(c => c.Trim('"'))
            .FirstOrDefault(c => Path.GetFileName(c).Equals("WinRAR.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(c));
    }

    void UpdateCompressButton()
    {
        var winrar = FindWinRar();
        CompressIcon.Source = winrar is null ? null : ShellIcons.ForPath(winrar);
        CompressIcon.Visibility = CompressIcon.Source is null ? Visibility.Collapsed : Visibility.Visible;
        CompressButton.IsEnabled = false;
        CompressButton.ToolTip = winrar is null ? "WinRAR não encontrado neste computador" : "Compactar com o WinRAR: em breve na versão C#";
        ToolTipService.SetShowOnDisabled(CompressButton, true);
    }

    void Compress_Click(object sender, RoutedEventArgs e) { }

    // ---- Fila

    async Task<bool> Enqueue()
    {
        if (destination is not { } target || auth.AccountId == "" || drive is null)
        {
            Notice("Escolha a pasta de destino e clique em Usar esta pasta.");
            return false;
        }
        if (checkedPaths.Count == 0)
        {
            Notice("Marque ao menos um arquivo ou pasta.");
            return false;
        }
        var folders = CheckedFolders();
        if (folders.Count > 0) EnqueueFolders(folders, target);
        var errors = new List<string>();
        var added = new List<string>();
        foreach (var path in checkedPaths.Except(folders, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList())
        {
            try
            {
                var id = db.Add(path, target.Id, target.Name, auth.AccountId);
                added.Add(path);
                db.Event(id, "ADDED_TO_QUEUE");
            }
            catch (Exception exc) when (exc is IOException or ArgumentException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(path)}: {exc.Message}");
            }
        }
        foreach (var path in added) SetChecked(path, false);
        if (errors.Count > 0) Notice(string.Join("\n", errors));
        await Task.CompletedTask;
        return true;
    }

    /// <summary>Pastas marcadas sobem com a mesma estrutura de subpastas no Drive.</summary>
    async void EnqueueFolders(List<string> folders, (string Id, string Name) target)
    {
        var (account, client) = (auth.AccountId, drive!);
        foreach (var folder in folders) SetChecked(folder, false);
        ShowStatus("Criando as pastas no Drive…", 8000);
        var plan = await Try(() => FolderPlan.Build(client, folders, target.Id, target.Name));
        if (plan is null) return;
        var errors = new List<string>();
        foreach (var file in plan)
        {
            try
            {
                var id = db.Add(file.Path, file.FolderId, file.FolderName, account, notifyName: file.NotifyName);
                db.Event(id, "ADDED_TO_QUEUE_FROM_FOLDER");
                manager.Start(id);
            }
            catch (Exception exc) when (exc is IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
            {
                errors.Add($"{Path.GetFileName(file.Path)}: {exc.Message}");
            }
        }
        RefreshFolder(target.Id);
        Refresh();
        if (errors.Count > 0) Notice(string.Join("\n", errors));
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (auth.AccountId == "")
        {
            Notice("Conecte sua conta Google primeiro.");
            return;
        }
        if (checkedPaths.Count > 0 && !await Enqueue()) return;
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
        if (RowId(sender) is not { } id || db.Get(id) is not { } item || manager.IsRemoving(id)) return;
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
        if (RowId(sender) is { } id) manager.Pause(id, cancel: true);
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

    /// <summary>Botão direito seleciona a linha antes de abrir o menu.</summary>
    void QueueList_RightButton(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is ListBoxItem item)
            {
                item.IsSelected = true;
                return;
            }
        e.Handled = true;
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

    /// <summary>Continuar só quando há envio já começado e parado; senão Iniciar.</summary>
    string ContinueText(List<UploadItem> pending) =>
        pending.Any(x => x.Status is not (Status.Done or Status.Canceled) && !manager.IsRunning(x.Id)
                         && (x.Offset > 0 || x.RemoteId != "" || x.Elapsed > 0)) ? "Continuar" : "Iniciar";

    void WarnDuplicates(List<UploadItem> pending)
    {
        var current = pending.Where(x => x.Status == Status.DuplicateName).ToList();
        var fresh = current.Where(x => !duplicateWarned.Contains(x.Id)).ToList();
        // Renomeado e de novo duplicado avisa outra vez.
        duplicateWarned = current.Select(x => x.Id).ToHashSet();
        if (fresh.Count == 0) return;
        var names = string.Join("\n", fresh.Select(x => $"• {x.Name}  ({x.FolderName})"));
        // Fora do Refresh: o aviso é modal e o timer continua rodando.
        Dispatcher.BeginInvoke(() => Notice(
            $"Já existe um arquivo com o mesmo nome na pasta de destino do Drive:\n{names}\n\n"
            + "Renomeie o item na fila (botão direito) ou remova o arquivo do Drive e clique em Continuar."));
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
        StartButton.Content = ContinueText(pending);
        WarnDuplicates(pending);
        Sync(pending);
        if (history.Count != done.Count || history.FirstOrDefault()?.Id != done.LastOrDefault()?.Id)
        {
            var selected = (HistoryList.SelectedItem as HistoryRow)?.Id;
            history.Clear();
            foreach (var item in Enumerable.Reverse(done)) history.Add(new HistoryRow(item));
            HistoryList.SelectedItem = history.FirstOrDefault(h => h.Id == selected);
        }
        SampleCharts(rows);
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
            row.Load(item, manager.IsRemoving(item.Id));
        }
    }

    // ---- Gráficos (charts.py)

    readonly Dictionary<string, UploadPlot> plots = [];
    readonly Stopwatch chartClock = Stopwatch.StartNew();
    HashSet<string> lastActive = [];

    void SampleCharts(IReadOnlyList<UploadItem> rows)
    {
        var byId = rows.ToDictionary(x => x.Id);
        var active = rows.Where(x => manager.IsRunning(x.Id)).Select(x => x.Id).ToHashSet();
        // Os últimos gráficos ficam até começar um novo lote.
        var keep = active.Count > 0 ? active : lastActive;
        if (active.Count > 0) lastActive = active;
        foreach (var id in plots.Keys.ToList())
            if (!keep.Contains(id) || !byId.ContainsKey(id))
            {
                ChartsGrid.Children.Remove(plots[id]);
                plots.Remove(id);
            }
        foreach (var id in keep.Order())
        {
            if (!byId.TryGetValue(id, out var item)) continue;
            if (!plots.TryGetValue(id, out var plot))
            {
                plots[id] = plot = new UploadPlot();
                ChartsGrid.Children.Add(plot);
            }
            plot.Sample(chartClock.Elapsed.TotalSeconds, item);
        }
        ChartsEmpty.Visibility = plots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        Refresh();
    }

    // ---- Configurações

    static string Number(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    RemoteMonitoring Remote()
    {
        try { return db.Setting<RemoteMonitoring?>("remote_monitoring", null) ?? RemoteMonitoring.Default(); }
        catch (JsonException) { return RemoteMonitoring.Default(); }
    }

    void LoadSettings()
    {
        loadingSettings = true;
        ThemeBox.ItemsSource = ThemeManager.Themes;
        ThemeBox.SelectedItem = ThemeManager.Normalize(db.Setting("theme", "Automático"));
        try { StartWindowsBox.IsChecked = Startup.IsEnabled(); }
        catch (Exception exc) when (exc is System.Security.SecurityException or UnauthorizedAccessException or IOException) { StartWindowsBox.IsChecked = false; }
        ConcurrencyBox.Text = Math.Clamp(db.Setting("concurrency", 1), 1, 3).ToString();
        ChunkBox.ItemsSource = new[] { "1", "4", "8", "16", "32", "64" };
        ChunkBox.SelectedItem = db.Setting("chunk_mib", 8).ToString();
        RetriesBox.Text = db.Setting("retries", 10).ToString();
        CompressLimitBox.Text = Number(db.Setting("compress_upload_limit", 1.0)) + " MB/s";
        AdvancedBox.IsChecked = db.Setting("advanced_pipeline", false);
        FullScopeBox.IsChecked = db.Setting("full_scope", true);
        var remote = Remote();
        RemoteEnabledBox.IsChecked = remote.Enabled;
        RemoteIdBox.Text = remote.ComputerId;
        RemoteNameBox.Text = remote.ComputerName;
        RemoteIntervalBox.Text = remote.UpdateIntervalSeconds.ToString();
        RemoteCredentialsBox.Text = remote.FirebaseCredentials;
        UpdateStatus.Text = $"Versão {Version}";
        WhatsAppEnabled.IsChecked = notifier.Config().Enabled;
        phones.Clear();
        foreach (var (phone, key) in notifier.Recipients()) phones.Add(new PhoneRow(phone, key));
        loadingSettings = false;
    }

    void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Como na versão Python: trocar o tema salva as configurações.
        if (!loadingSettings && IsLoaded) SaveSettings();
    }

    void SaveSettings_Click(object sender, RoutedEventArgs e) => SaveSettings();

    static bool ParseNumber(string text, out double value) =>
        double.TryParse(text.Replace("MB/s", "", StringComparison.OrdinalIgnoreCase).Trim().Replace(',', '.'),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    void SaveSettings()
    {
        if (!int.TryParse(ConcurrencyBox.Text.Trim(), out var concurrency) || concurrency is < 1 or > 3)
        {
            Notice("Uploads simultâneos: use um número de 1 a 3.");
            return;
        }
        if (!int.TryParse(RetriesBox.Text.Trim(), out var retries) || retries is < 0 or > 50)
        {
            Notice("Tentativas por falha: use um número de 0 a 50.");
            return;
        }
        if (!ParseNumber(CompressLimitBox.Text, out var limit) || limit is < 0.1 or > 1000)
        {
            Notice("Upload durante a compactação: use um valor de 0,1 a 1000 MB/s.");
            return;
        }
        if (!int.TryParse(RemoteIntervalBox.Text.Trim(), out var interval) || interval is < 1 or > 3600)
        {
            Notice("Intervalo: use um número de 1 a 3600 segundos.");
            return;
        }
        var remoteId = RemoteIdBox.Text.Trim();
        if (RemoteEnabledBox.IsChecked == true && (!Regex.IsMatch(remoteId, "^[A-Za-z0-9_-]{1,128}$")
                || RemoteNameBox.Text.Trim() == "" || RemoteCredentialsBox.Text.Trim() == ""))
        {
            Notice("Preencha ID (letras, números, _ ou -), nome e credencial Firebase.");
            return;
        }
        db.SaveSetting("remote_monitoring", new RemoteMonitoring(RemoteEnabledBox.IsChecked == true, remoteId,
            RemoteNameBox.Text.Trim(), interval, RemoteCredentialsBox.Text.Trim()));
        var startup = StartWindowsBox.IsChecked == true;
        try
        {
            if (startup != Startup.IsEnabled()) Startup.SetEnabled(startup);
            db.SaveSetting("start_with_windows", startup);
        }
        catch (Exception exc) when (exc is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Notice("O Windows bloqueou a inicialização automática.");
            StartWindowsBox.IsChecked = !startup;
            return;
        }
        var theme = ThemeBox.SelectedItem as string ?? "Automático";
        db.SaveSetting("theme", theme);
        db.SaveSetting("concurrency", concurrency);
        db.SaveSetting("chunk_mib", int.Parse(ChunkBox.SelectedItem as string ?? "8"));
        db.SaveSetting("retries", retries);
        db.SaveSetting("full_scope", FullScopeBox.IsChecked == true);
        db.SaveSetting("compress_upload_limit", Math.Round(limit, 1));
        db.SaveSetting("advanced_pipeline", AdvancedBox.IsChecked == true);
        CompressLimitBox.Text = Number(Math.Round(limit, 1)) + " MB/s";
        ThemeManager.Apply(theme);
        foreach (var plot in plots.Values) plot.InvalidateVisual();
        ShowStatus("Configurações salvas.", 6000);
    }

    void SelectFirebase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Credencial de serviço Firebase", Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true) RemoteCredentialsBox.Text = dialog.FileName;
    }

    // O monitoramento pelo celular (Firebase) chega numa próxima etapa da versão C#.
    void PairMobile_Click(object sender, RoutedEventArgs e) =>
        Notice(Remote().Enabled ? "Conectar celular ainda não está disponível na versão C#. Use a versão Python por enquanto." : "Ative e salve o Firebase antes.");

    void MobileReaders_Click(object sender, RoutedEventArgs e) => PairMobile_Click(sender, e);

    void CheckUpdate_Click(object sender, RoutedEventArgs e) =>
        UpdateStatus.Text = $"Versão {Version}\nA atualização automática da versão C# ainda não está disponível.";

    // ---- WhatsApp

    bool AddPhone()
    {
        var phone = WhatsApp.Normalize(PhoneBox.Text);
        var key = ApiKeyBox.Text.Trim();
        if (phone.Length < 10 || key == "")
        {
            Notice("Informe o número com DDI e DDD (ex.: 5511999998888) e a apikey.");
            return false;
        }
        foreach (var old in phones.Where(p => p.Phone == phone).ToList()) phones.Remove(old);
        phones.Add(new PhoneRow(phone, key));
        PhoneBox.Clear();
        ApiKeyBox.Clear();
        ShowStatus("Número adicionado. Clique em Salvar notificações.", 6000);
        return true;
    }

    void AddPhone_Click(object sender, RoutedEventArgs e) => AddPhone();

    void ApiKey_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddPhone();
    }

    void RemovePhone_Click(object sender, RoutedEventArgs e)
    {
        if (PhoneList.SelectedItem is PhoneRow row) phones.Remove(row);
    }

    /// <summary>Número digitado e ainda não adicionado também conta, para nada se perder.</summary>
    bool TypedPhoneAdded() => (PhoneBox.Text.Trim() == "" && ApiKeyBox.Text.Trim() == "") || AddPhone();

    void SaveWhatsApp_Click(object sender, RoutedEventArgs e)
    {
        if (!TypedPhoneAdded()) return;
        if (WhatsAppEnabled.IsChecked == true && phones.Count == 0)
        {
            Notice("Adicione ao menos um número para ativar as notificações.");
            return;
        }
        try
        {
            notifier.Save(WhatsAppEnabled.IsChecked == true, phones.Select(p => (p.Phone, p.ApiKey)));
        }
        catch (Exception exc) when (exc is System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
        {
            Notice("Não foi possível proteger a apikey neste computador.");
            return;
        }
        ShowStatus("Notificações salvas.", 6000);
    }

    async void TestWhatsApp_Click(object sender, RoutedEventArgs e)
    {
        if (!TypedPhoneAdded()) return;
        if (phones.Count == 0)
        {
            Notice("Informe o número e a apikey.");
            return;
        }
        ShowStatus("Enviando teste…");
        var errors = await notifier.Notify(WhatsApp.Message("DriveFlow TESTE"), phones.Select(p => (p.Phone, p.ApiKey)).ToList());
        ShowStatus(DefaultStatus);
        Notice(errors.Count == 0 ? "Teste enviado. Confira o WhatsApp." : string.Join("\n", errors));
    }

    void OpenDataDir_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Database.DataDir()) { UseShellExecute = true });

    // ---- Atividade

    void ExportEvents_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Exportar eventos", FileName = "driveflow-eventos.log", Filter = "Log (*.log)|*.log" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, ActivityText.Text); }
        catch (IOException) { Notice("Não foi possível salvar o arquivo."); }
    }
}
