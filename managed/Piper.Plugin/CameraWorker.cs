using System.Diagnostics;

namespace LuckyRobots.Piper.Plugin;

public static class CameraWorker
{
    public static ProcessStartInfo StartInfo(string packageDirectory)
    {
        string folder = Path.Combine(packageDirectory, "camera-runtime");
        string executable = Path.Combine(folder, OperatingSystem.IsWindows() ? "LuckyRobots.Piper.Camera.exe" : "LuckyRobots.Piper.Camera");
        if (!File.Exists(executable)) throw new FileNotFoundException("The bundled camera runtime is missing. Reinstall the Pipers package.", executable);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        // Separate stdin avoids sharing the engine's live plugin-protocol pipe.
        return new ProcessStartInfo(executable) { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    }
}
