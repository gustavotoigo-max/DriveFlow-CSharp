using DriveFlow.Core.Compress;

namespace DriveFlow.Core.Tests;

public sealed class WinRarTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("driveflow-rar").FullName;

    public void Dispose() => Directory.Delete(root, true);

    sealed class FakeProcess : IArchiverProcess
    {
        public int? ExitCode { get; set; }
        public long? BytesRead => null;
        public bool Killed, Paused;
        public void Kill() { Killed = true; ExitCode ??= 255; }
        public void Suspend() => Paused = true;
        public void Resume() => Paused = false;
    }

    (Compression Job, FakeProcess Process, List<string> Args, string Output) Start(string format = "ZIP", string password = "")
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "Obra 12")).FullName;
        File.WriteAllText(Path.Combine(source, "a.txt"), "abc");
        var output = Directory.CreateDirectory(Path.Combine(root, "volumes")).FullName;
        var fake = new FakeProcess();
        var args = new List<string>();
        var job = new Compression("WinRAR.exe", source, output, "Obra 12", format, "Normal", 20L << 30, password,
                                  (_, a) => { args.AddRange(a); return fake; });
        return (job, fake, args, output);
    }

    [Fact]
    public void Split_sizes_use_binary_units_and_reject_tiny_values()
    {
        Assert.Equal(20L << 30, WinRar.SplitBytes("20 GB"));
        Assert.Equal((long)(1.5 * (1L << 30)), WinRar.SplitBytes("1,5 GB"));
        Assert.Equal(100L << 20, WinRar.SplitBytes("100 mb"));
        Assert.Throws<ArgumentException>(() => WinRar.SplitBytes("0.5 MB"));
        Assert.Throws<ArgumentException>(() => WinRar.SplitBytes("vinte"));
    }

    [Fact]
    public void Names_with_forbidden_characters_are_refused()
    {
        Assert.Equal("Obra 12", WinRar.CleanName("  Obra 12. "));
        Assert.Throws<ArgumentException>(() => WinRar.CleanName("a/b"));
        Assert.Throws<ArgumentException>(() => WinRar.CleanName("   "));
    }

    [Fact]
    public void Arguments_match_the_python_version()
    {
        Assert.Equal(["a", "-ibck", "-y", "-r", "-ep1", "-m3", "-v1024b", "-afzip", "--", "out.zip", "src"],
                     WinRar.Arguments("src", "out.zip", "ZIP", "Normal", 1024, ""));
        Assert.Equal(["a", "-ibck", "-y", "-r", "-ep1", "-m5", "-v1024b", "-afrar", "-ma5", "-md4m", "-psegredo", "--", "out.rar", "src"],
                     WinRar.Arguments("src", "out.rar", "RAR", "Best", 1024, "segredo"));
    }

    [Fact]
    public void Volumes_are_ordered_like_winrar_writes_them()
    {
        Assert.True(WinRar.IsVolume("Obra.z01", "Obra", "ZIP"));
        Assert.True(WinRar.IsVolume("Obra.zip", "Obra", "ZIP"));
        Assert.False(WinRar.IsVolume("Obra 2.zip", "Obra", "ZIP"));
        Assert.True(WinRar.IsVolume("Obra.part3.rar", "Obra", "RAR"));
        Assert.False(WinRar.IsVolume("Obra.part3.rar", "Obra", "ZIP"));
        foreach (var name in new[] { "Obra.zip", "Obra.z02", "Obra.z01", "Outro.z01" })
            File.WriteAllText(Path.Combine(root, name), "");
        Assert.Equal(["Obra.z01", "Obra.z02", "Obra.zip"], WinRar.Volumes(root, "Obra", "ZIP").Select(Path.GetFileName));
    }

    [Fact]
    public void Output_inside_the_source_is_refused()
    {
        var inner = Directory.CreateDirectory(Path.Combine(root, "src", "out")).FullName;
        Assert.Throws<ArgumentException>(() => WinRar.CheckOutput(Path.Combine(root, "src"), inner));
        Assert.Throws<ArgumentException>(() => WinRar.CheckOutput(Path.Combine(root, "src"), Path.Combine(root, "src")));
        Assert.Throws<ArgumentException>(() => WinRar.CheckOutput(root, Path.Combine(root, "nada")));
        // "src2" começa com "src" mas não fica dentro dela.
        WinRar.CheckOutput(Path.Combine(root, "src"), Directory.CreateDirectory(Path.Combine(root, "src2")).FullName);
    }

    [Fact]
    public void Volume_is_delivered_once_the_next_one_starts_and_the_last_on_exit()
    {
        var (job, process, args, output) = Start();
        Assert.Equal(Path.Combine(output, "Obra 12.zip"), args[^2]);
        File.WriteAllText(Path.Combine(output, "Obra 12.z01"), "x");
        Assert.Empty(job.Poll());
        File.WriteAllText(Path.Combine(output, "Obra 12.z02"), "x");
        Assert.Equal(["Obra 12.z01"], job.Poll().Select(Path.GetFileName));
        Assert.Empty(job.Poll());
        File.WriteAllText(Path.Combine(output, "Obra 12.zip"), "x");
        process.ExitCode = 0;
        Assert.Equal(["Obra 12.z02", "Obra 12.zip"], job.Poll().Select(Path.GetFileName));
        Assert.False(job.Running);
        Assert.Equal("", job.Error);
    }

    [Fact]
    public void Failure_or_cancel_delivers_nothing_more()
    {
        var (job, process, _, output) = Start("RAR");
        File.WriteAllText(Path.Combine(output, "Obra 12.part1.rar"), "x");
        job.Suspend();
        Assert.True(process.Paused && job.Suspended);
        job.Cancel();
        Assert.True(process.Killed);
        Assert.False(process.Paused);
        Assert.Empty(job.Poll());
        Assert.Equal("Compactação cancelada.", job.Error);

        var other = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
        var failing = new FakeProcess { ExitCode = 9 };
        var broken = new Compression("WinRAR.exe", Path.Combine(root, "Obra 12"), other, "X", "ZIP", "Normal", 1 << 20, "", (_, _) => failing);
        File.WriteAllText(Path.Combine(other, "X.zip"), "x");
        Assert.Empty(broken.Poll());
        Assert.Equal("O WinRAR parou: erro ao criar o arquivo.", broken.Error);
    }

    [Fact]
    public void Existing_volumes_with_the_same_name_block_the_start()
    {
        var output = Directory.CreateDirectory(Path.Combine(root, "volumes")).FullName;
        File.WriteAllText(Path.Combine(output, "Obra 12.part1.rar"), "");
        var exc = Assert.Throws<ArgumentException>(() => Start());
        Assert.Contains("Obra 12.part1.rar", exc.Message);
    }
}
