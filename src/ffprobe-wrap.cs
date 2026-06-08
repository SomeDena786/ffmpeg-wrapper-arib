// Passthrough wrapper for ffprobe.
//
// Jellyfin derives the ffprobe path from the configured ffmpeg path (same folder),
// so when ffmpeg points at our wrapper folder, ffprobe must live here too. This
// just forwards every argument, verbatim, to the real bundled ffprobe with stdio
// inherited (Jellyfin reads ffprobe's stdout JSON). Nothing is modified.

using System;
using System.Diagnostics;

class P
{
    const string REAL_FFPROBE = @"C:\Program Files\Jellyfin\Server\ffprobe.exe";

    static int Main()
    {
        string args = StripExe(Environment.CommandLine);
        var psi = new ProcessStartInfo
        {
            FileName = REAL_FFPROBE,
            Arguments = args,
            UseShellExecute = false
        };
        var proc = Process.Start(psi);
        proc.WaitForExit();
        return proc.ExitCode;
    }

    static string StripExe(string cmd)
    {
        cmd = cmd.TrimStart();
        if (cmd.StartsWith("\""))
        {
            int e = cmd.IndexOf('"', 1);
            return e < 0 ? "" : cmd.Substring(e + 1).TrimStart();
        }
        int sp = cmd.IndexOf(' ');
        return sp < 0 ? "" : cmd.Substring(sp + 1).TrimStart();
    }
}
