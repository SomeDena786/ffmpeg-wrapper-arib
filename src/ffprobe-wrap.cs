// ffprobe wrapper for Jellyfin.
//
// Default: transparent passthrough to the real bundled ffprobe.
//
// Special case: when Jellyfin runs its *media-info* probe (`-show_streams` + JSON)
// on an ARIB `.ts`/`.m2ts` file whose audio is 5.1 at the 8-second mark, the real
// ffprobe reports the stream as "stereo" (it reads the first frames, which are 2.0
// pre-roll). That makes Jellyfin advertise stereo to every client, so surround
// either gets downmixed or — worse — a forced 5.1 output mismatches what a strict
// client (Fire TV) was told, producing silence.
//
// To fix that at the source, we rewrite the main audio stream in the probe JSON to
// channels=6 / channel_layout=5.1, so Jellyfin negotiates surround correctly per
// client (AAC 5.1 where supported, otherwise its own AC3/EAC3 5.1 transcode).
// Requires re-scanning the affected items so Jellyfin re-runs this probe.
//
// Everything else (keyframe/packet probes, non-ts, stereo files) passes through
// unchanged; on any error we emit the original probe output verbatim.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

class P
{
    const string REAL_FFPROBE = @"C:\Program Files\Jellyfin\Server\ffprobe.exe";
    const string LOG          = @"C:\ProgramData\Jellyfin\Server\log\ffmpeg-wrap.log";

    static int Main()
    {
        string args = StripExe(Environment.CommandLine);
        try
        {
            if (IsMediaInfoProbe(args))
            {
                string input = ExtractInput(args);
                if (input != null)
                {
                    string ext = Path.GetExtension(input).ToLowerInvariant();
                    if (ext == ".ts" || ext == ".m2ts")
                        return RunPatched(args, input);
                }
            }
        }
        catch (Exception ex) { Log("ffprobe-wrap error: " + ex.Message); }
        return RunPassthrough(args);
    }

    static bool IsMediaInfoProbe(string a)
    {
        return a.IndexOf("-show_streams", StringComparison.Ordinal) >= 0
            && (a.IndexOf("-print_format json", StringComparison.Ordinal) >= 0
                || a.IndexOf("-of json", StringComparison.Ordinal) >= 0);
    }

    // Run the real ffprobe, capture stdout JSON, optionally patch audio to 5.1.
    static int RunPatched(string args, string input)
    {
        int ch = ProbeChannelsAt8s(input);

        var psi = new ProcessStartInfo
        {
            FileName = REAL_FFPROBE,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
            // stderr is inherited so Jellyfin still sees ffprobe warnings
        };
        var p = Process.Start(psi);
        string json = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        if (ch >= 6)
        {
            try { json = PatchAudioTo51(json); Log("probe-patched 5.1: ch@8s=" + ch + " " + input); }
            catch (Exception ex) { Log("probe patch failed (" + ex.Message + "), verbatim: " + input); }
        }

        var stdout = Console.OpenStandardOutput();
        var bytes = new UTF8Encoding(false).GetBytes(json);
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
        return p.ExitCode;
    }

    // Rewrite the first real audio stream (0 < channels < 6) to 5.1.
    static string PatchAudioTo51(string json)
    {
        var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var root = ser.DeserializeObject(json) as Dictionary<string, object>;
        if (root == null) return json;
        var streams = (root.ContainsKey("streams") ? root["streams"] : null) as object[];
        if (streams == null) return json;

        foreach (var so in streams)
        {
            var st = so as Dictionary<string, object>;
            if (st == null) continue;
            object ct;
            if (!st.TryGetValue("codec_type", out ct) || (ct as string) != "audio") continue;

            int ch = 0;
            object cv;
            if (st.TryGetValue("channels", out cv))
                int.TryParse(Convert.ToString(cv, CultureInfo.InvariantCulture), out ch);

            if (ch > 0 && ch < 6)
            {
                st["channels"] = 6;
                st["channel_layout"] = "5.1";
                break;   // only the primary audio
            }
        }
        return ser.Serialize(root);
    }

    // Channel count of the audio frame nearest 8s past the first frame (TS PTS is
    // not 0-based, so we measure offset from the first observed frame).
    static int ProbeChannelsAt8s(string input)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = REAL_FFPROBE,
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
                if (t - baseT >= 8.0) return c;
            }
            return lastCh;
        }
        catch (Exception ex) { Log("probe-error: " + ex.Message); return 0; }
    }

    static int RunPassthrough(string args)
    {
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

    static void Log(string m)
    {
        try { File.AppendAllText(LOG, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [ffprobe] " + m + Environment.NewLine); }
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
}
