using System.Diagnostics;
using System.Globalization;
using Lux.Library;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>
/// Export and animation checks on real captures through the library's job path (env-gated: LUX_M4_PROOF = a folder of linked captures, e.g.
/// with wide/L16_00493.lri, stacked/L16_00414.lri, lumen/L16_00066.lri + .lris). Every export the app's panel can ask for
/// (a matrix of options) is queued as a library job and compared with `lux-light convert` given the equivalent options (byte for byte except
/// the time stamps in DNG / JPG); the collision policies (add a number, skip, replace); a canceled export leaves no file; an animation job
/// equals `lux-light animate`. Results: &lt;LUX_M4_OUT&gt;/m4-proof.tsv (default: a folder in the workspace).
/// </summary>
public sealed class M4ProofTests(ITestOutputHelper output)
{
    static readonly string LuxLight = Paths.LuxLight;

    record Case(string Name, string Capture, ExportOptions Options, string ConvertArgs);

    [Fact]
    public async Task Library_exports_equal_convert()
    {
        var lib0 = Environment.GetEnvironmentVariable("LUX_M4_PROOF");
        if (string.IsNullOrEmpty(lib0)) return;
        string folder = Path.GetFullPath(lib0);
        string outRoot = Environment.GetEnvironmentVariable("LUX_M4_OUT") ?? Path.Combine(Path.GetDirectoryName(Paths.UiScratch)!, "..", "m6", "proof", "m4");
        outRoot = Path.GetFullPath(outRoot);
        if (Directory.Exists(Path.Combine(outRoot, "app"))) Directory.Delete(Path.Combine(outRoot, "app"), true);
        if (Directory.Exists(Path.Combine(outRoot, "cli"))) Directory.Delete(Path.Combine(outRoot, "cli"), true);
        Directory.CreateDirectory(outRoot);
        string db = Path.Combine(outRoot, "appdata", "library.luxlib");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        var log = new List<string>();
        void Say(string s) { output.WriteLine(s); log.Add(s); File.WriteAllLines(Path.Combine(outRoot, "m4-proof.log"), log); }
        await using var lib = LuxLibrary.Open(new LibraryOpenOptions { DatabasePath = db, MakeThumbnailsAfterScan = false, WatchFolders = false });
        if (!lib.GetFolders().Any(f => f.Path.TrimEnd('/') == folder.TrimEnd('/'))) await lib.AddFolderAsync(folder);
        for (int i = 0; i < 600 && lib.GetFolders().Any(f => f.Scanning); i++) await Task.Delay(200);
        var items = lib.Query(new CaptureQuery { Limit = 100 }).Items;
        CaptureItem Item(string name) => items.First(i => i.Name == name);
        Say($"library: {items.Count} captures: {string.Join(", ", items.Select(i => i.Name))}");
        // the imported Lumen photo (its session's settings: tone, the depth effect …, Lumen's newer tone style)
        var lumen = items.FirstOrDefault(i => i.HasLumenSession);
        if (lumen != null && lumen.Edits.Source != EditSource.Lumen)
        {
            foreach (var id in lib.ImportLumenEdits(new[] { lumen.Id }, newerToneStyle: true)) Say($"lumen import: {(await lib.Jobs.WaitAsync(id)).State}");
            items = lib.Query(new CaptureQuery { Limit = 100 }).Items;
            lumen = items.First(i => i.Id == lumen.Id);
        }
        string TS = "--jpeg-modify 2026-01-01T00:00:00";
        var cases = new List<Case>();
        void Add(string name, string cap, ExportOptions o, string args) => cases.Add(new Case(name, cap, o, args));
        // wide 00493
        Add("wide-full-dng-jpg", "L16_00493", new ExportOptions { Formats = new[] { "dng", "jpg" }, Level = 0 }, "--formats dng,jpg");
        Add("wide-13mp-jpg", "L16_00493", new ExportOptions { Formats = new[] { "jpg" }, Level = 1 }, "--level 1 --formats jpg");
        Add("wide-l2-ppm-hdr", "L16_00493", new ExportOptions { Formats = new[] { "ppm", "hdr", "jpg" }, Level = 2 }, "--level 2 --formats ppm,hdr,jpg");
        Add("wide-custom-1600", "L16_00493", new ExportOptions { Formats = new[] { "jpg" }, LongEdge = 1600 }, "CUSTOM 1600 --formats jpg");
        Add("wide-l2-f28", "L16_00493", new ExportOptions { Formats = new[] { "jpg", "dng" }, Level = 2, DepthEffectFNumber = 2.8f }, "--level 2 --f-number 2.8 --formats jpg,dng");
        Add("wide-l3-depth", "L16_00493", new ExportOptions { Formats = new[] { "jpg+depth", "depth" }, Level = 3 }, "--level 3 --formats jpg+depth,depth");
        // stacked 00414
        Add("stacked-l2-jpg-dng", "L16_00414", new ExportOptions { Formats = new[] { "jpg", "dng" }, Level = 2 }, "--level 2 --formats jpg,dng");
        Add("stacked-custom-1000", "L16_00414", new ExportOptions { Formats = new[] { "jpg" }, LongEdge = 1000 }, "CUSTOM 1000 --formats jpg");
        Add("stacked-l2-f2", "L16_00414", new ExportOptions { Formats = new[] { "jpg" }, Level = 2, DepthEffectFNumber = 2f }, "--level 2 --f-number 2 --formats jpg");
        if (lumen != null)
        {
            string n = lumen.Name;
            Add("lumen-as-saved-l1", n, new ExportOptions { Formats = new[] { "dng", "jpg" }, Level = 1 }, "--level 1 --formats dng,jpg");
            Add("lumen-clean-l2", n, new ExportOptions { Formats = new[] { "jpg" }, Level = 2, UseSavedEdits = false }, "--level 2 --no-edits --formats jpg");
            Add("lumen-depth-off-l2", n, new ExportOptions { Formats = new[] { "jpg" }, Level = 2, DepthEffectOff = true }, "DEPTHOFF --level 2 --formats jpg");
            Add("lumen-newer-tone-l2", n, new ExportOptions { Formats = new[] { "jpg", "dng" }, Level = 2, NewerToneStyle = true }, "--level 2 --formats jpg,dng");
            Add("lumen-custom-1200", n, new ExportOptions { Formats = new[] { "jpg" }, LongEdge = 1200 }, "CUSTOM 1200 --formats jpg");
        }
        var filter = Environment.GetEnvironmentVariable("LUX_M4_CASES");
        if (!string.IsNullOrEmpty(filter)) cases = cases.Where(c => filter.Split(',').Any(f => c.Name.Contains(f, StringComparison.Ordinal))).ToList();
        var rows = new List<string> { "case\tfile\tapp bytes\tconvert bytes\tresult\tapp s\tconvert s" };
        int failures = 0;
        foreach (var c in cases)
        {
            var item = Item(c.Capture);
            string appDir = Path.Combine(outRoot, "app", c.Name), cliDir = Path.Combine(outRoot, "cli", c.Name);
            Directory.CreateDirectory(appDir); Directory.CreateDirectory(cliDir);
            var sw = Stopwatch.StartNew();
            var id = lib.Export(new[] { item.Id }, c.Options with { OutputFolder = appDir }).Single();
            var job = await lib.Jobs.WaitAsync(id);
            double appS = sw.Elapsed.TotalSeconds;
            if (job.State != JobState.Done) { Say($"{c.Name}: job {job.State}: {job.Error}"); failures++; continue; }
            // the equivalent convert options
            string args = c.ConvertArgs;
            if (args.StartsWith("CUSTOM ", StringComparison.Ordinal))
            {
                // the library's rule (ExportJobs.CustomSize): the photo's full size (after its crop) scaled to the long edge, from the smallest
                // level at least that size
                int le = int.Parse(args.Split(' ')[1], CultureInfo.InvariantCulture);
                var full = c.Options.UseSavedEdits && item.Width > 0 ? (item.Width, item.Height) : (item.LevelSizes[0].Width, item.LevelSizes[0].Height);
                double k = (double)le / Math.Max(full.Item1, full.Item2);
                int w = (int)Math.Round(full.Item1 * k), h = (int)Math.Round(full.Item2 * k);
                int level = 0; for (int L = 4; L >= 0; L--) if (Math.Max(item.LevelSizes[L].Width, item.LevelSizes[L].Height) >= le) { level = L; break; }
                args = $"--level {level} --size {w},{h} " + string.Join(' ', args.Split(' ').Skip(2));
            }
            if (args.StartsWith("DEPTHOFF ", StringComparison.Ordinal)) args = $"--f-number {item.AllInFocusFNumber.ToString("R", CultureInfo.InvariantCulture)} " + args[9..];
            if (c.Options.NewerToneStyle == true) args += " --jpeg-v2 --dng-tone light_v2";
            sw.Restart();
            int rc = RunConvert(item.Path, $"{args} {(args.Contains("jpg") ? TS : "")} -o \"{cliDir}\"", out string clog);
            double cliS = sw.Elapsed.TotalSeconds;
            if (rc != 0) { Say($"{c.Name}: convert rc={rc}: {clog}"); failures++; continue; }
            foreach (var f in job.ResultFiles)
            {
                string other = Path.Combine(cliDir, Path.GetFileName(f));
                string res = !File.Exists(other) ? "missing in convert" : Compare(f, other);
                if (!res.StartsWith("identical", StringComparison.Ordinal)) failures++;
                rows.Add($"{c.Name}\t{Path.GetFileName(f)}\t{new FileInfo(f).Length}\t{(File.Exists(other) ? new FileInfo(other).Length : 0)}\t{res}\t{appS:0.0}\t{cliS:0.0}");
                Say(rows[^1]);
            }
            File.WriteAllLines(Path.Combine(outRoot, "m4-proof.tsv"), rows);
        }

        // ---- collision policies and cancel, on a small export of the wide capture
        if (string.IsNullOrEmpty(filter) || filter.Contains("collide", StringComparison.Ordinal))
        {
            var it = Item("L16_00493");
            string dir = Path.Combine(outRoot, "app", "collide");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var o = new ExportOptions { Formats = new[] { "jpg" }, Level = 4, OutputFolder = dir };
            var j1 = await lib.Jobs.WaitAsync(lib.Export(new[] { it.Id }, o).Single());
            var j2 = await lib.Jobs.WaitAsync(lib.Export(new[] { it.Id }, o with { FileNamePattern = "{name}" }).Single());
            var t1 = File.GetLastWriteTimeUtc(j1.ResultPath!);
            var j3 = await lib.Jobs.WaitAsync(lib.Export(new[] { it.Id }, o with { IfExists = ExistingFilePolicy.Skip }).Single());
            var j4 = await lib.Jobs.WaitAsync(lib.Export(new[] { it.Id }, o with { IfExists = ExistingFilePolicy.Overwrite }).Single());
            var names = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x => x).ToList();
            bool ok = Path.GetFileName(j1.ResultPath) == "L16_00493.jpg" && Path.GetFileName(j2.ResultPath) == "L16_00493-2.jpg"
                      && j3.ResultPath is null && j3.Notes.Any(n => n.StartsWith("Skipped", StringComparison.Ordinal))
                      && Path.GetFileName(j4.ResultPath) == "L16_00493.jpg" && File.GetLastWriteTimeUtc(j4.ResultPath!) > t1
                      && names.SequenceEqual(new[] { "L16_00493-2.jpg", "L16_00493.jpg" });
            Say($"collision: add a number → {Path.GetFileName(j2.ResultPath)}; skip → {(j3.ResultPath ?? "no file")} ({string.Join("; ", j3.Notes)}); replace → {Path.GetFileName(j4.ResultPath)} rewritten; folder: {string.Join(", ", names)} → {(ok ? "PASS" : "FAIL")}");
            if (!ok) failures++;

            // cancel a full-size export part way: nothing of it remains
            string cdir = Path.Combine(outRoot, "app", "cancel");
            if (Directory.Exists(cdir)) Directory.Delete(cdir, true);
            var cid = lib.Export(new[] { it.Id }, new ExportOptions { Formats = new[] { "dng", "jpg" }, Level = 0, OutputFolder = cdir }).Single();
            for (int i = 0; i < 1200; i++)
            {
                var info = lib.Jobs.Get(cid)!;
                if (info.State == JobState.Running && info.Progress >= 0.5) break;
                await Task.Delay(100);
            }
            double at = lib.Jobs.Get(cid)!.Progress;
            lib.Jobs.Cancel(cid);
            var cj = await lib.Jobs.WaitAsync(cid);
            var left = Directory.Exists(cdir) ? Directory.GetFiles(cdir).Select(Path.GetFileName).ToList() : new List<string?>();
            bool cok = cj.State == JobState.Cancelled && left.Count == 0;
            Say($"cancel at {at:P0} ({cj.Status}): {cj.State}, files left: {(left.Count == 0 ? "none" : string.Join(", ", left))} → {(cok ? "PASS" : "FAIL")}");
            if (!cok) failures++;
        }

        // ---- an animation job = `lux-light animate` with the same options (same frames, same writer: the same bytes)
        if (string.IsNullOrEmpty(filter) || filter.Contains("anim", StringComparison.Ordinal))
        {
            var it = Item("L16_00493");
            string adir = Path.Combine(outRoot, "app", "anim"), cdir = Path.Combine(outRoot, "cli", "anim");
            Directory.CreateDirectory(adir); Directory.CreateDirectory(cdir);
            foreach (var (o, a) in new[]
            {
                (new AnimationOptions { Effect = Lux.Engine.Session.AnimationEffect.RackFocus, Frames = 8, LongEdge = 520, Format = AnimationFormat.Gif, OutputFolder = adir },
                 "--anim-effect rack-focus --anim-frames 8 --anim-size 520 --anim-container gif"),
                (new AnimationOptions { Effect = Lux.Engine.Session.AnimationEffect.Wigglegram, LongEdge = 640, Format = AnimationFormat.Apng, OutputFolder = adir },
                 "--anim-effect wigglegram --anim-size 640 --anim-container apng"),
            })
            {
                var sw = Stopwatch.StartNew();
                var aj = await lib.Jobs.WaitAsync(lib.Animate(new[] { it.Id }, o).Single());
                double appS = sw.Elapsed.TotalSeconds; sw.Restart();
                int rc = RunCli($"animate \"{it.Path}\" {a} -o \"{cdir}\"", out string alog);
                double cliS = sw.Elapsed.TotalSeconds;
                string other = Path.Combine(cdir, Path.GetFileName(aj.ResultPath ?? "none"));
                bool same = aj.State == JobState.Done && rc == 0 && File.Exists(other) && File.ReadAllBytes(aj.ResultPath!).SequenceEqual(File.ReadAllBytes(other));
                rows.Add($"anim-{Path.GetFileName(aj.ResultPath)}\t{Path.GetFileName(aj.ResultPath)}\t{(aj.ResultPath is { } rp && File.Exists(rp) ? new FileInfo(rp).Length : 0)}\t{(File.Exists(other) ? new FileInfo(other).Length : 0)}\t{(same ? "identical (whole file)" : "DIFFERENT " + aj.State + " " + aj.Error + " rc=" + rc)}\t{appS:0.0}\t{cliS:0.0}");
                Say(rows[^1]);
                if (!same) failures++;
            }
            File.WriteAllLines(Path.Combine(outRoot, "m4-proof.tsv"), rows);
        }
        Assert.Equal(0, failures);
    }

    static int RunConvert(string lri, string args, out string log) => RunCli($"convert \"{lri}\" {args}", out log);

    static int RunCli(string args, out string log)
    {
        var psi = new ProcessStartInfo(LuxLight, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        log = o + err.Result;
        return p.ExitCode;
    }

    /// <summary>Byte comparison where only time stamps may differ: every differing byte must lie inside an Exif / XMP date-time string
    /// ("YYYY:MM:DD HH:MM:SS") or a time-zone offset ("+01:00") at the same place in both files.</summary>
    internal static string Compare(string a, string b)
    {
        var x = File.ReadAllBytes(a); var y = File.ReadAllBytes(b);
        if (x.Length != y.Length) return $"DIFFERENT length {x.Length} vs {y.Length}";
        int diff = 0, outside = 0, first = -1;
        for (int i = 0; i < x.Length; i++)
        {
            if (x[i] == y[i]) continue;
            diff++;
            if (!InDate(x, i) || !InDate(y, i)) { outside++; if (first < 0) first = i; }
        }
        return diff == 0 ? "identical" : outside == 0 ? $"identical except time stamps ({diff} bytes)" : $"DIFFERENT {outside} bytes outside time stamps (first at {first})";
    }

    static bool InDate(byte[] d, int i)
    {
        // a 19-byte "YYYY:MM:DD HH:MM:SS" (or "YYYY-MM-DDTHH:MM:SS") window containing i, or a 6-byte "+HH:MM" / "-HH:MM"
        for (int s = Math.Max(0, i - 18); s <= i && s + 19 <= d.Length; s++)
        {
            bool ok = true;
            for (int k = 0; k < 19 && ok; k++)
            {
                byte c = d[s + k];
                ok = k switch { 4 or 7 => c is (byte)':' or (byte)'-', 10 => c is (byte)' ' or (byte)'T', 13 or 16 => c == ':', _ => c >= '0' && c <= '9' };
            }
            if (ok) return true;
        }
        for (int s = Math.Max(0, i - 5); s <= i && s + 6 <= d.Length; s++)
            if (d[s] is (byte)'+' or (byte)'-' && char.IsDigit((char)d[s + 1]) && char.IsDigit((char)d[s + 2]) && d[s + 3] == ':' && char.IsDigit((char)d[s + 4]) && char.IsDigit((char)d[s + 5])) return true;
        return false;
    }
}
