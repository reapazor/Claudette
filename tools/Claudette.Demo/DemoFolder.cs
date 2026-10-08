namespace Claudette.Demo;

/// <summary>
/// A folder the demo makes, marked as its own, so a later run can replace it without risking one that holds anything
/// else.
/// </summary>
internal static class DemoFolder
{
    private const string Marker = ".claudette-demo";

    /// <summary>Empties <paramref name="path"/>, or makes it, for the demo to fill.</summary>
    /// <exception cref="IOException">The folder holds something the demo didn't make.</exception>
    public static void Replace(string path)
    {
        var folder = new DirectoryInfo(path);
        if (folder.Exists)
        {
            if (!File.Exists(Path.Combine(path, Marker)) && folder.EnumerateFileSystemInfos().Any())
            {
                throw new IOException($"{path} isn't empty, and claudette-demo didn't make it: choose another folder.");
            }
            // Git keeps its objects read-only, which stops a delete on Windows.
            foreach (var file in folder.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                file.Attributes = FileAttributes.Normal;
            }
            folder.Delete(recursive: true);
        }
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, Marker), "Made by tools/Claudette.Demo, which replaces this folder each time it runs.\n");
    }
}
