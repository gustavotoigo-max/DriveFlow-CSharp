using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DriveFlow.Core.Auth;
using DriveFlow.Core.Storage;
using DriveFlow.Core.Upload;

namespace DriveFlow.App;

public partial class App : Application
{
    Mutex? single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? smokeOutput = null;
        string? smokeDir = null;
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-test")
        {
            // Teste rápido do pacote: abre a janela com dados temporários, sem rede nem credenciais.
            smokeOutput = Path.GetFullPath(e.Args[1]);
            smokeDir = Directory.CreateTempSubdirectory("driveflow-smoke-").FullName;
            Environment.SetEnvironmentVariable("DRIVEFLOW_DATA_DIR", smokeDir);
        }
        else
        {
            single = new Mutex(true, @"Local\DriveFlowNet.Desktop", out var first);
            if (!first)
            {
                Dialogs.Message(null, "O DriveFlow já está aberto neste usuário do Windows.");
                Shutdown(1);
                return;
            }
        }
        DispatcherUnhandledException += (_, args) =>
        {
            Dialogs.Message(MainWindow, $"Erro inesperado: {args.Exception.Message}");
            args.Handled = true;
        };
        var vault = new DpapiVault();
        var db = new Database(vault);
        ThemeManager.Apply(db.Setting("theme", "Automático"));
        var auth = new GoogleAuth(vault);
        var window = new MainWindow(db, auth, new UploadManager(db, auth));
        MainWindow = window;
        window.Show();
        db.Event("", "APPLICATION_STARTED");
        if (smokeOutput is not null)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                File.WriteAllText(smokeOutput, JsonSerializer.Serialize(new
                {
                    ok = true, version = global::DriveFlow.App.MainWindow.Version, title = window.Title, rows = db.All().Count,
                }));
                window.ForceClose();
                db.Dispose();
                try { Directory.Delete(smokeDir!, true); } catch (IOException) { }
            };
            timer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        single?.Dispose();
        base.OnExit(e);
    }
}
