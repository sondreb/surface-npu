using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace SurfaceAIStudio;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            StudioLog.Write(e.Exception?.ToString() ?? e.Message);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (RelaunchWithPackageIdentity())
        {
            Environment.Exit(0);
            return;
        }

        var window = new MainWindow();
        window.Activate();
    }

    static bool RelaunchWithPackageIdentity()
    {
        try
        {
            _ = Package.Current.Id.FamilyName;
            return false;
        }
        catch (InvalidOperationException)
        {
        }

        var family = RegisteredFamilyName();
        if (family is null)
        {
            StudioLog.Write("No package identity, and Surface AI Studio is not registered.");
            return false;
        }

        StudioLog.Write("Relaunching through the installed app so the NPU models are available.");
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"shell:AppsFolder\\{family}!App",
            UseShellExecute = true,
        });
        return true;
    }

    static string? RegisteredFamilyName()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        var fullName = key?.GetSubKeyNames()
            .FirstOrDefault(name => name.StartsWith("SurfaceAIStudio_", StringComparison.Ordinal));
        if (fullName is null)
        {
            return null;
        }

        var parts = fullName.Split("__", 2, StringSplitOptions.None);
        if (parts.Length != 2)
        {
            return null;
        }

        var packageName = parts[0].Split('_')[0];
        return packageName + "_" + parts[1];
    }
}
