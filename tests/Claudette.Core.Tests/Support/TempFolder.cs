namespace Claudette.Core.Tests.Support;

/// <summary>A temporary folder that's deleted when disposed.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder(string prefix = "claudette")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>A path inside the folder (not created).</summary>
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Writes a file inside the folder, creating its folders, and returns its path.</summary>
    public string Write(string relativePath, string content)
    {
        var path = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Creates a folder inside the folder and returns its path.</summary>
    public string CreateFolder(string relativePath) => Directory.CreateDirectory(Combine(relativePath.Split('/'))).FullName;

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
