using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Lux.App.Services;

/// <summary>Folder picker through the window's storage provider; "show file" through the system's file manager.</summary>
public sealed class WindowPlatformServices : IPlatformServices
{
    public Window? Window { get; set; }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Window?.StorageProvider is not { CanPickFolder: true } sp) return null;
        var result = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            else
            {
                var dir = File.Exists(path) ? Path.GetDirectoryName(path)! : Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
                if (!Directory.Exists(dir)) return;
                Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", $"\"{dir}\"") { UseShellExecute = false });
            }
        }
        catch (Exception) { /* no file manager: nothing to show */ }
    }
}
