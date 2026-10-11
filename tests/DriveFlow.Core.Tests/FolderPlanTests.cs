using DriveFlow.Core.Upload;

namespace DriveFlow.Core.Tests;

public sealed class FolderPlanTests
{
    [Fact]
    public async Task Mirrors_subfolders_and_names_the_batch_after_the_marked_folder()
    {
        var dir = Directory.CreateTempSubdirectory("driveflow-test-").FullName;
        try
        {
            var obra = Directory.CreateDirectory(Path.Combine(dir, "Obra 12"));
            File.WriteAllText(Path.Combine(obra.FullName, "b.txt"), "b");
            File.WriteAllText(Path.Combine(obra.FullName, "a.txt"), "a");
            var fotos = Directory.CreateDirectory(Path.Combine(obra.FullName, "Fotos"));
            File.WriteAllText(Path.Combine(fotos.FullName, "1.jpg"), "1");
            var created = new List<string>();

            var plan = await FolderPlan.Build((parent, name) =>
            {
                created.Add($"{parent}>{name}");
                return Task.FromResult($"id-{name}");
            }, [obra.FullName], "root", "Meu Drive/Clientes");

            Assert.Equal(["root>Obra 12", "id-Obra 12>Fotos"], created);
            Assert.Equal(["a.txt", "b.txt", "1.jpg"], plan.Select(p => Path.GetFileName(p.Path)));
            Assert.Equal("Meu Drive/Clientes/Obra 12/Fotos", plan[2].FolderName);
            Assert.Equal("id-Fotos", plan[2].FolderId);
            Assert.All(plan, p => Assert.Equal("Obra 12", p.NotifyName));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Formats_sizes_and_durations_in_portuguese()
    {
        Assert.Equal("512 B", Format.Size(512));
        Assert.Equal("1,5 MiB", Format.Size(1.5 * 1024 * 1024));
        Assert.Equal("01:01:05", Format.Duration(3665));
    }
}
