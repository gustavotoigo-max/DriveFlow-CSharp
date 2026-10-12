using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DriveFlow.Core.Compress;

/// <summary>Compactação pelo WinRAR instalado: os volumes entram na fila assim que ficam prontos (winrar.py).</summary>
public static partial class WinRar
{
    // Mesmos nomes da janela "Archive name and parameters" do WinRAR (-m0 a -m5).
    public static readonly string[] Methods = ["Store", "Fastest", "Fast", "Normal", "Good", "Best"];
    public static readonly string[] Splits = ["20 GB", "25 GB", "30 GB"];
    // Códigos de saída do WinRAR; 0 e 1 (aviso) mantêm o arquivo válido.
    static readonly Dictionary<int, string> ExitCodes = new()
    {
        [2] = "erro fatal", [3] = "falha de verificação (CRC)", [4] = "arquivo bloqueado", [5] = "erro de gravação",
        [6] = "erro ao abrir um arquivo", [7] = "parâmetro inválido", [8] = "memória insuficiente",
        [9] = "erro ao criar o arquivo", [10] = "nenhum arquivo para compactar", [11] = "senha incorreta", [255] = "cancelado",
    };

    [GeneratedRegex(@"^\s*(\d+(?:[.,]\d+)?)\s*(MB|GB)\s*$", RegexOptions.IgnoreCase)] private static partial Regex SplitText();
    [GeneratedRegex(@"[<>:""/\\|?*]")] private static partial Regex BadName();
    [GeneratedRegex(@"\.z(\d{2,})$", RegexOptions.IgnoreCase)] private static partial Regex ZipPart();
    [GeneratedRegex(@"\.part(\d+)\.rar$", RegexOptions.IgnoreCase)] private static partial Regex RarPart();
    [GeneratedRegex(@"^\.(z\d{2,}|zip)$", RegexOptions.IgnoreCase)] private static partial Regex ZipSuffix();
    [GeneratedRegex(@"^(\.part\d+)?\.rar$", RegexOptions.IgnoreCase)] private static partial Regex RarSuffix();

    public static string ExitText(int code) => ExitCodes.TryGetValue(code, out var text) ? text : $"código {code}";

    /// <summary>"20 GB" ou "1,5 GB" em bytes (múltiplos de 1024, como no WinRAR).</summary>
    public static long SplitBytes(string text)
    {
        var match = SplitText().Match(text);
        if (!match.Success) throw new ArgumentException("Tamanho de divisão inválido.");
        var number = double.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var value = number * Math.Pow(1024, match.Groups[2].Value.Equals("GB", StringComparison.OrdinalIgnoreCase) ? 3 : 2);
        if (value < 1024 * 1024) throw new ArgumentException("O tamanho de divisão mínimo é 1 MB.");
        return (long)value;
    }

    public static string CleanName(string name)
    {
        name = name.Trim().TrimEnd('.', ' ');
        if (name == "" || BadName().IsMatch(name)) throw new ArgumentException("Nome inválido. Não use < > : \" / \\ | ? *");
        return name;
    }

    /// <summary>Argumentos do WinRAR em segundo plano, sem janela.</summary>
    public static List<string> Arguments(string source, string archive, string format, string method, long split, string password)
    {
        var args = new List<string> { "a", "-ibck", "-y", "-r", "-ep1", $"-m{Array.IndexOf(Methods, method)}", $"-v{split}b" };
        if (format == "ZIP") args.Add("-afzip");
        else args.AddRange(["-afrar", "-ma5", "-md4m"]);
        if (password != "") args.Add($"-p{password}");
        args.AddRange(["--", archive, source]);
        return args;
    }

    /// <summary>Posição do volume pelo nome; null quando o arquivo não é volume deste pacote.</summary>
    public static int? VolumeOrder(string name, string format)
    {
        if (format == "ZIP")
        {
            var zip = ZipPart().Match(name);
            if (zip.Success) return int.Parse(zip.Groups[1].Value, CultureInfo.InvariantCulture);
            return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? 1_000_000 : null;
        }
        var rar = RarPart().Match(name);
        if (rar.Success) return int.Parse(rar.Groups[1].Value, CultureInfo.InvariantCulture);
        return name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) ? 0 : null;
    }

    public static bool IsVolume(string name, string baseName, string format)
    {
        if (!name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = name[baseName.Length..];
        return format == "ZIP" ? ZipSuffix().IsMatch(suffix) : RarSuffix().IsMatch(suffix);
    }

    public static List<string> Volumes(string folder, string baseName, string format)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(path => IsVolume(Path.GetFileName(path), baseName, format))
                .OrderBy(path => VolumeOrder(Path.GetFileName(path), format)).ToList();
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static bool SameDisk(string first, string second) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(first)), Path.GetPathRoot(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    static string Normalized(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>A pasta dos volumes nunca é a pasta de origem nem fica dentro dela.</summary>
    public static void CheckOutput(string source, string output)
    {
        if (!Directory.Exists(output)) throw new ArgumentException("Escolha a pasta onde salvar os volumes.");
        var (from, to) = (Normalized(source), Normalized(output));
        if (to.Equals(from, StringComparison.OrdinalIgnoreCase)
            || to.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Salve os volumes fora da pasta de origem.");
    }

    public static List<string> ExistingOutput(string folder, string baseName) =>
        new[] { "ZIP", "RAR" }.SelectMany(format => Volumes(folder, baseName, format)).Select(path => Path.GetFileName(path)).ToList();

    public static long FolderSize(string path)
    {
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                try { total += file.Length; }
                catch (IOException) { }
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
        }
        return total;
    }

    /// <summary>WinRAR.exe pelo registro ou pelas pastas padrão; null quando não instalado.</summary>
    public static string? Find(IEnumerable<string?> registryValues)
    {
        var candidates = registryValues.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
        foreach (var variable in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } folder)
                candidates.Add(Path.Combine(folder, "WinRAR", "WinRAR.exe"));
        return candidates.Select(c => c.Trim().Trim('"'))
            .FirstOrDefault(c => Path.GetFileName(c).Equals("WinRAR.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(c));
    }
}

/// <summary>O processo do WinRAR, trocado por um falso nos testes.</summary>
public interface IArchiverProcess
{
    /// <summary>Código de saída; null enquanto roda.</summary>
    int? ExitCode { get; }
    /// <summary>Bytes que o WinRAR já leu do disco; null quando não dá para saber.</summary>
    long? BytesRead { get; }
    void Kill();
    void Suspend();
    void Resume();
}

public sealed class WinRarProcess : IArchiverProcess
{
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr process);

    readonly Process process;

    public WinRarProcess(string exe, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        process = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível abrir o WinRAR.");
    }

    public int? ExitCode => process.HasExited ? process.ExitCode : null;

    public long? BytesRead => OperatingSystem.IsWindows() && !process.HasExited && GetProcessIoCounters(process.Handle, out var counters)
        ? (long)counters.ReadBytes : null;

    public void Kill()
    {
        try { process.Kill(); }
        catch (InvalidOperationException) { }
    }

    public void Suspend()
    {
        if (OperatingSystem.IsWindows() && !process.HasExited) NtSuspendProcess(process.Handle);
    }

    public void Resume()
    {
        if (OperatingSystem.IsWindows() && !process.HasExited) NtResumeProcess(process.Handle);
    }
}

/// <summary>
/// Acompanha um WinRAR em execução. Poll() devolve os volumes que ficaram prontos: um volume está pronto
/// quando o WinRAR já começou o seguinte, ou quando terminou.
/// </summary>
public sealed class Compression
{
    readonly IArchiverProcess process;
    readonly List<string> delivered = [];
    readonly Stopwatch clock = Stopwatch.StartNew();
    long? total;

    public Compression(string exe, string source, string output, string name, string format, string method, long split, string password,
                       Func<string, List<string>, IArchiverProcess>? launch = null)
    {
        (Source, Format, Split) = (Path.GetFullPath(source), format, split);
        Name = WinRar.CleanName(name);
        Folder = Path.GetFullPath(output);
        if (!Directory.Exists(Source)) throw new ArgumentException("Selecione uma pasta.");
        WinRar.CheckOutput(Source, Folder);
        if (WinRar.ExistingOutput(Folder, Name) is [var clash, ..])
            throw new ArgumentException($"Já existe {clash} em {Folder}. Escolha outro nome.");
        Archive = Path.Combine(Folder, Name + (format == "ZIP" ? ".zip" : ".rar"));
        _ = Task.Run(() => total = WinRar.FolderSize(Source));
        var arguments = WinRar.Arguments(Source, Archive, format, method, split, password);
        process = (launch ?? ((file, args) => new WinRarProcess(file, args)))(exe, arguments);
    }

    public string Source { get; }
    public string Folder { get; }
    public string Name { get; }
    public string Format { get; }
    public long Split { get; }
    public string Archive { get; }
    public int? Result { get; private set; }
    public bool Cancelled { get; private set; }
    public bool Suspended { get; private set; }
    public bool Running => Result is null;
    public double Elapsed => clock.Elapsed.TotalSeconds;
    /// <summary>Pasta de destino no Drive e conta, definidas por quem começou a compactação.</summary>
    public (string Id, string Name) Destination { get; init; }
    public string Account { get; init; } = "";

    public List<string> Poll()
    {
        var code = process.ExitCode;
        var found = WinRar.Volumes(Folder, Name, Format);
        var ready = code is not null ? found : found.Take(Math.Max(0, found.Count - 1)).ToList();
        var fresh = ready.Where(path => !delivered.Contains(path)).ToList();
        if (code is not null && Result is null) Result = code;
        if (Cancelled || code > 1) return [];
        delivered.AddRange(fresh);
        return fresh;
    }

    public string Error => Cancelled ? "Compactação cancelada."
        : Result is null or <= 1 ? "" : $"O WinRAR parou: {WinRar.ExitText(Result.Value)}.";

    static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
    }

    /// <summary>Volume em gravação e sua fração estimada (0 a 1).</summary>
    public (string? Path, double Fraction) Current()
    {
        var found = WinRar.Volumes(Folder, Name, Format).Where(p => !delivered.Contains(p)).ToList();
        if (found.Count == 0 || !Running) return (null, Running ? 0 : 1);
        var path = found[^1];
        var size = SizeOf(path);
        double expected = Split;
        if (process.BytesRead is { } read && read > 0 && total is { } all && all > 0)
        {
            // Tamanho final estimado pela proporção já lida da pasta.
            var fraction = Math.Min(1.0, (double)read / all);
            var done = delivered.Sum(SizeOf);
            if (fraction > .02) expected = Math.Min(Split, Math.Max(size, (done + size) / fraction - done));
        }
        return (path, expected > 0 ? Math.Min(.99, size / expected) : 0);
    }

    public static long Size(string? path) => path is null ? 0 : SizeOf(path);

    public void Cancel()
    {
        if (!Running) return;
        Cancelled = true;
        Resume();
        process.Kill();
    }

    /// <summary>Congela o WinRAR (o disco fica livre para o upload) sem perder o progresso.</summary>
    public void Suspend()
    {
        if (!Running || Suspended) return;
        process.Suspend();
        Suspended = true;
    }

    public void Resume()
    {
        if (!Suspended) return;
        process.Resume();
        Suspended = false;
    }
}
