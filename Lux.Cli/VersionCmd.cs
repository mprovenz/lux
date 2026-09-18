using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Lux.Cli;

/// <summary>`lux-light version [--offline]`: this build's version, and unless `--offline`, the latest release published on the
/// GitHub repository (`releases/latest` — drafts and pre-releases excluded). Exit 0 when up to date (or no release exists),
/// 1 when a newer release is available, 2 when the check could not be made (no network, API error).</summary>
public static class VersionCmd
{
    public const string Repository = "mprovenz/lux";
    public static readonly string ReleasesPage = $"https://github.com/{Repository}/releases";
    static readonly string LatestApi = $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>The build's version: `AssemblyInformationalVersion` (the csproj `Version`, minus any `+commit` suffix) or the assembly version.</summary>
    public static string Local()
    {
        var asm = typeof(VersionCmd).Assembly;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) { int plus = info.IndexOf('+'); return plus > 0 ? info[..plus] : info; }
        var v = asm.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>Numeric comparison of a release tag (`v1.2.0`, `1.2`) with a build version: −1 tag older, 0 same, 1 tag newer;
    /// null when either does not parse as a dotted version.</summary>
    public static int? Compare(string local, string tag)
    {
        static Version? P(string s)
        {
            s = s.Trim(); if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
            int dash = s.IndexOfAny(new[] { '-', '+' }); if (dash > 0) s = s[..dash];   // 1.3.0-rc1 → 1.3.0
            if (!s.Contains('.')) s += ".0";
            return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0)) : null;
        }
        var a = P(local); var b = P(tag);
        if (a is null || b is null) return null;
        return b.CompareTo(a) switch { < 0 => -1, 0 => 0, _ => 1 };
    }

    public static int Run(Options o)
    {
        string local = Local();
        Console.WriteLine($"lux-light {local}");
        if (o.Offline) return 0;

        string tag, url, date;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("lux-light", local));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var resp = http.GetAsync(LatestApi).GetAwaiter().GetResult();
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Console.WriteLine($"no release published yet — {ReleasesPage}");
                return 0;
            }
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"release check failed: GitHub answered HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}) — {ReleasesPage}");
                return 2;
            }
            using var doc = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = doc.RootElement;
            tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
            date = root.TryGetProperty("published_at", out var d) && d.GetString() is { Length: >= 10 } ds ? ds[..10] : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            Console.Error.WriteLine($"release check failed: {ex.Message} — {ReleasesPage}");
            return 2;
        }

        string when = date.Length > 0 ? $" ({date})" : "";
        switch (Compare(local, tag))
        {
            case 1:
                Console.WriteLine($"a newer release is available: {tag}{when}");
                Console.WriteLine($"  {url}");
                return 1;
            case 0:
                Console.WriteLine($"up to date (latest release {tag}{when})");
                return 0;
            case -1:
                Console.WriteLine($"this build is newer than the latest release {tag}{when}");
                return 0;
            default:
                Console.WriteLine($"latest release: {tag}{when} — {url}");
                return 0;
        }
    }
}
