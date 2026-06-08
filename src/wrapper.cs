// Jellyfin ffmpeg wrapper for Japanese BS4K/ARIB .ts recordings.
//
// Problem: these broadcasts switch the AAC audio mode mid-stream (2.0 <-> 5.1)
// within a single elementary stream. Jellyfin remuxes audio with
//   -codec:a:0 copy -bsf:a aac_adtstoasc
// which freezes the AudioSpecificConfig (channel layout) at the first frame, so
// playback breaks on Web / FireTV the moment the mode changes.
//
// Fix: when (and only when) Jellyfin would copy audio out of a .ts/.m2ts file,
// re-encode audio to a CONSTANT layout instead (video stays copy = fast/lossless).
// The layout is decided by the audio mode at the 8-second mark of the recording
// (the user records with ~8s pre-roll): 5.1 at 8s -> whole file 5.1, else stereo.
//
// Everything else (version checks, trickplay, non-ts transcodes, already-encoded
// audio) is passed through to the real ffmpeg unchanged.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

class W
{
    const string REAL_FFMPEG = @"C:\Program Files\Jellyfin\Server\ffmpeg.exe";
    const string FFPROBE     = @"C:\Program Files\Jellyfin\Server\ffprobe.exe";
    const string LOG         = @"C:\ProgramData\Jellyfin\Server\log\ffmpeg-wrap.log";

    static int Main()
    {
        string args = StripExe(Environment.CommandLine);
        try { args = Transform(args); }
        catch (Exception ex) { Log("transform-error: " + ex.Message); }
        return Run(REAL_FFMPEG, args);
    }

    static string Transform(string args)
    {
        if (args.IndexOf("-codec:a:0 copy", StringComparison.Ordinal) < 0)
            return args;

        string input = ExtractInput(args);
        if (input == null) { Log("no-input; passthrough"); return args; }

        string ext = Path.GetExtension(input).ToLowerInvariant();
        if (ext != ".ts" && ext != ".m2ts") { Log("non-ts (" + ext + "); passthrough"); return args; }

        int ch = ProbeChannelsAt8s(input);
        int ac = ch >= 6 ? 6 : (ch == 1 ? 1 : 2);     // 5.1 -> 6, mono -> 1, else stereo
        string br = ac >= 6 ? "384k" : (ac == 1 ? "128k" : "256k");
        Log("input=" + input + " ch@8s=" + ch + " -> -ac " + ac + " -b:a " + br);

        args = args.Replace("-bsf:a aac_adtstoasc ", "");
        args = args.Replace("-bsf:a aac_adtstoasc", "");
        args = args.Replace("-codec:a:0 copy",
                            "-codec:a:0 aac -ac " + ac + " -b:a " + br);
        return args;
    }

    // Pull the source path out of `-i file:"..."` (Jellyfin's form) or `-i "..."`.
    static string ExtractInput(string args)
    {
        int i = args.IndexOf("file:\"", StringComparison.Ordinal);
        if (i >= 0)
        {
            int s = i + 6;
            int e = args.IndexOf('"', s);
            if (e > s) return args.Substring(s, e - s);
        }
        int j = args.IndexOf("-i \"", StringComparison.Ordinal);
        if (j >= 0)
        {
            int s = j + 4;
            int e = args.IndexOf('"', s);
            if (e > s)
            {
                string p = args.Substring(s, e - s);
                if (p.StartsWith("file:", StringComparison.Ordinal)) p = p.Substring(5);
                return p;
            }
        }
        return null;
    }

    // Read ~9s of audio frames from the head and return the channel count of the
    // frame nearest 8.0s past the first frame. TS PTS does not start at 0, so we
    // measure offset from the first observed frame rather than seeking by abs time.
    static int ProbeChannelsAt8s(string input)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FFPROBE,
                Arguments = "-v error -select_streams a:0 -read_intervals \"%+#430\" " +
                            "-show_entries frame=pts_time,channels -of csv=p=0 " +
                            "-i file:\"" + input + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var p = Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(20000);

            double baseT = double.NaN;
            int lastCh = 0;
            foreach (var raw in outp.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var parts = line.Split(',');
                if (parts.Length < 2) continue;
                double t; int c;
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out t)) continue;
                if (!int.TryParse(parts[1].Trim(), out c)) continue;
                if (double.IsNaN(baseT)) baseT = t;
                lastCh = c;
                if (t - baseT >= 8.0) return c;   // first frame at/after the 8s mark
            }
            return lastCh;   // file shorter than 8s: use last seen
        }
        catch (Exception ex) { Log("probe-error: " + ex.Message); return 0; }
    }

    static void Log(string m)
    {
        try { File.AppendAllText(LOG, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + m + Environment.NewLine); }
        catch { }
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

    // Launch the real ffmpeg inside a job object flagged KILL_ON_JOB_CLOSE, so when
    // Jellyfin kills this wrapper (stop/seek), the child ffmpeg dies too instead of
    // orphaning. stdio is inherited (not redirected) so Jellyfin's progress/log
    // capture works exactly as before.
    static int Run(string exe, string args)
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job != IntPtr.Zero)
        {
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int len = Marshal.SizeOf(info);
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, buf, false);
                SetInformationJobObject(job, 9, buf, (uint)len); // JobObjectExtendedLimitInformation
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        var psi = new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = false };
        var proc = Process.Start(psi);
        if (job != IntPtr.Zero)
        {
            try { AssignProcessToJobObject(job, proc.Handle); } catch { }
        }
        proc.WaitForExit();
        return proc.ExitCode;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr a, string name);
    [DllImport("kernel32.dll")]
    static extern bool SetInformationJobObject(IntPtr job, int infoType, IntPtr info, uint length);
    [DllImport("kernel32.dll")]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
}
