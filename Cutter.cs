using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace VideoEditor;

public record Segment(TimeSpan Start, TimeSpan End)
{
    public override string ToString() => $@"{Start:hh\:mm\:ss\.f}  →  {End:hh\:mm\:ss\.f}";
}

/// <summary>Cuts and joins kept segments with ffmpeg, without re-encoding.</summary>
public static class Cutter
{
    /// <summary>Sorts segments, drops empty ones and merges any that overlap or touch.</summary>
    public static List<Segment> Normalize(IEnumerable<Segment> segments)
    {
        var result = new List<Segment>();
        foreach (var s in segments.Where(s => s.End > s.Start).OrderBy(s => s.Start))
        {
            if (result.Count > 0 && s.Start <= result[^1].End)
                result[^1] = result[^1] with { End = s.End > result[^1].End ? s.End : result[^1].End };
            else
                result.Add(s);
        }
        return result;
    }

    // ponytail: stream copy means each segment starts at the nearest keyframe at or before Start,
    // so cuts can be off by up to one keyframe interval. Upgrade path: re-encode (-c:v libx264 / h264_nvenc) for frame-accurate cuts.
    public static async Task ExportAsync(string input, IEnumerable<Segment> keep, string output)
    {
        var segments = Normalize(keep);
        if (segments.Count == 0) throw new InvalidOperationException("Nothing to export: add at least one segment.");
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Export would overwrite the source video. Pick a different file name.");

        var tmp = Directory.CreateTempSubdirectory("videoeditor_");
        try
        {
            var parts = new List<string>();
            foreach (var s in segments)
            {
                var part = Path.Combine(tmp.FullName, $"part{parts.Count}.mp4");
                await RunFfmpeg("-y", "-ss", Secs(s.Start), "-i", input, "-t", Secs(s.End - s.Start),
                                "-c", "copy", "-avoid_negative_ts", "make_zero", part);
                parts.Add(part);
            }

            if (parts.Count == 1)
            {
                File.Move(parts[0], output, overwrite: true);
                return;
            }

            var list = Path.Combine(tmp.FullName, "list.txt");
            await File.WriteAllLinesAsync(list, parts.Select(p => $"file '{p.Replace("'", @"'\''")}'"));
            await RunFfmpeg("-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", output);
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    static string Secs(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    static async Task RunFfmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process process;
        try { process = Process.Start(psi)!; }
        catch (Win32Exception)
        {
            throw new FileNotFoundException("ffmpeg was not found on PATH. Install it with: winget install Gyan.FFmpeg");
        }

        using (process)
        {
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                var lines = (await stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                throw new Exception("ffmpeg failed:\n" + string.Join('\n', lines.TakeLast(8)));
            }
        }
    }

    /// <summary>Run with: VideoEditor.exe --selfcheck  (exit code 0 = pass).</summary>
    public static void SelfCheck()
    {
        static Segment S(double a, double b) => new(TimeSpan.FromSeconds(a), TimeSpan.FromSeconds(b));
        static void Check(bool ok, string what) { if (!ok) throw new Exception("Self-check failed: " + what); }

        Check(Normalize([]).Count == 0, "empty input");
        Check(Normalize([S(5, 5), S(7, 3)]).Count == 0, "empty and reversed segments are dropped");
        Check(Normalize([S(10, 20), S(0, 5)]).SequenceEqual([S(0, 5), S(10, 20)]), "sorted by start");
        Check(Normalize([S(0, 10), S(5, 15)]).SequenceEqual([S(0, 15)]), "overlap merges");
        Check(Normalize([S(0, 10), S(10, 12)]).SequenceEqual([S(0, 12)]), "touching merges");
        Check(Normalize([S(0, 20), S(5, 10)]).SequenceEqual([S(0, 20)]), "contained segment absorbed");
        Check(Secs(TimeSpan.FromMilliseconds(1500)) == "1.5", "invariant-culture seconds");
    }
}
