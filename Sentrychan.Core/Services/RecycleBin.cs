using Microsoft.VisualBasic.FileIO;

namespace Sentrychan.Core.Services;

/// <summary>
/// Removes a file the app decided it doesn't need (a damaged or duplicate episode)
/// without destroying it: it goes to the Recycle Bin, where the user can still get it back.
/// </summary>
public static class RecycleBin
{
    /// <summary>True if the file is gone from its folder. Never falls back to a hard delete.</summary>
    public static bool Send(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
