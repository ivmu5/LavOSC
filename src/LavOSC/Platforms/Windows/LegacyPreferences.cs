using System.Diagnostics;
using Microsoft.Maui.Storage;

namespace Laveyana.LavOSC;

internal static class LegacyPreferences
{
    public static void Import()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string previous = Path.Combine(local, "Wheel.App", "Wheel.App", "Settings", "preferences.dat");
        if (!File.Exists(previous))
            return;

        try
        {
            var directory = Path.GetFullPath(Path.Combine(FileSystem.AppDataDirectory, "..", "Settings"));
            CopyIfMissing(previous, directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not import previous LavOSC preferences: {0}", exception.Message);
        }
    }

    internal static void CopyIfMissing(string previous, string directory)
    {
        string destination = Path.Combine(directory, "preferences.dat");
        if (File.Exists(destination))
            return;

        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $"preferences-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(previous, temporary);
            File.Move(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
