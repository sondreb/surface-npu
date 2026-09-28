namespace SurfaceAIStudio;

internal static class StudioLog
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SurfaceAIStudio");

    public static string LogPath => Path.Combine(DirectoryPath, "studio.log");

    public static string SelfTestFlag => Path.Combine(DirectoryPath, "run-selftest");

    public static string SelfTestReport => Path.Combine(DirectoryPath, "selftest.txt");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must not take down the editor.
        }
    }
}
