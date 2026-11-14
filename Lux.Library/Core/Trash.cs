using System.Globalization;
using System.Text;

namespace Lux.Library.Core;

/// <summary>
/// Removing a file: to the platform's trash (Linux: the freedesktop.org trash in <c>$XDG_DATA_HOME/Trash</c>; Windows: the recycle bin;
/// macOS: <c>~/.Trash</c>), or for good. A link is moved or deleted itself — never the file it points to.
/// </summary>
internal static class Trash
{
    public static void Remove(string path, bool toTrash, string? trashOverride)
    {
        if (!toTrash) { File.Delete(path); return; }
        if (trashOverride is not null) { MoveInto(path, trashOverride, null); return; }
        if (OperatingSystem.IsWindows())
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            MoveInto(path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash"), null);
            return;
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string root = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        string trash = Path.Combine(root, "Trash");
        MoveInto(path, Path.Combine(trash, "files"), Path.Combine(trash, "info"));
    }

    static void MoveInto(string path, string filesDir, string? infoDir)
    {
        Directory.CreateDirectory(filesDir);
        if (infoDir is not null) Directory.CreateDirectory(infoDir);
        string name = Path.GetFileName(path), stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = i == 1 ? name : $"{stem}.{i}{ext}";
            string dst = Path.Combine(filesDir, candidate);
            if (File.Exists(dst) || Directory.Exists(dst)) continue;
            if (infoDir is not null)
            {
                string info = Path.Combine(infoDir, candidate + ".trashinfo");
                try
                {
                    using var fs = new FileStream(info, FileMode.CreateNew, FileAccess.Write);
                    var text = "[Trash Info]\nPath=" + Uri.EscapeDataString(Path.GetFullPath(path)).Replace("%2F", "/") + "\nDeletionDate="
                             + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "\n";
                    fs.Write(Encoding.UTF8.GetBytes(text));
                }
                catch (IOException) when (File.Exists(info)) { continue; }
                try { File.Move(path, dst); }
                catch { try { File.Delete(info); } catch (IOException) { } throw; }
                return;
            }
            File.Move(path, dst);
            return;
        }
    }
}
