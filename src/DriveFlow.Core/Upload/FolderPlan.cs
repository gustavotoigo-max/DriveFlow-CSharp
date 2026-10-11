using DriveFlow.Core.Drive;

namespace DriveFlow.Core.Upload;

/// <summary>Um arquivo de uma pasta marcada, já com a subpasta correspondente criada no Drive.</summary>
public sealed record PlannedFile(string Path, string FolderId, string FolderName, string NotifyName);

public static class FolderPlan
{
    /// <summary>
    /// Pastas marcadas sobem com a mesma estrutura de subpastas no Drive.
    /// Reaproveita pastas que já existem com o mesmo nome.
    /// </summary>
    public static async Task<List<PlannedFile>> Build(Func<string, string, Task<string>> folder, IEnumerable<string> folders,
                                                      string parentId, string parentName)
    {
        var files = new List<PlannedFile>();
        foreach (var top in folders)
        {
            var root = new DirectoryInfo(top);
            async Task Walk(DirectoryInfo dir, string driveParent, string relative)
            {
                var id = await folder(driveParent, dir.Name).ConfigureAwait(false);
                foreach (var file in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.Ordinal))
                    files.Add(new PlannedFile(file.FullName, id, $"{parentName}/{relative}", root.Name));
                foreach (var sub in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
                    await Walk(sub, id, $"{relative}/{sub.Name}").ConfigureAwait(false);
            }
            await Walk(root, parentId, root.Name).ConfigureAwait(false);
        }
        return files;
    }

    public static Task<List<PlannedFile>> Build(DriveClient drive, IEnumerable<string> folders, string parentId, string parentName,
                                                CancellationToken ct = default) =>
        Build((parent, name) => drive.Folder(parent, name, ct), folders, parentId, parentName);
}
