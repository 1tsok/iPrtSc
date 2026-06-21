using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace iPrtSc;

/// <summary>
/// Checks the GitHub "latest release" endpoint and reports whether a newer
/// version than the running build is available. Network access is best-effort
/// and silent: any failure (offline, rate-limit, parse error) yields no update.
/// </summary>
public static class UpdateChecker
{
    public const string ReleasesUrl = "https://github.com/1tsok/iPrtSc/releases/latest";
    private const string ApiUrl = "https://api.github.com/repos/1tsok/iPrtSc/releases/latest";

    /// <summary>Only hit the network this often; otherwise reuse the cached result.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static readonly HttpClient Http = CreateClient();

    public readonly record struct Result(bool UpdateAvailable, string? LatestVersion);

    /// <summary>The version of the running assembly as Major.Minor.Build.</summary>
    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, v.Build);
        }
    }

    /// <summary>
    /// Resolves whether an update is available, throttled to one network call per
    /// <see cref="CheckInterval"/>. Updates <paramref name="settings"/> in place
    /// (caller persists). Never throws.
    /// </summary>
    public static async Task<Result> CheckAsync(AppSettings settings)
    {
        try
        {
            bool fresh = settings.LastUpdateCheckUtc is { } last
                && DateTime.UtcNow - last < CheckInterval;

            if (!fresh)
            {
                string? fetched = await FetchLatestAsync().ConfigureAwait(false);
                settings.LastUpdateCheckUtc = DateTime.UtcNow;
                if (fetched is not null)
                    settings.LatestVersionSeen = fetched;
            }

            return Evaluate(settings.LatestVersionSeen);
        }
        catch (Exception ex)
        {
            Logger.Log("UpdateChecker.CheckAsync", ex);
            return new Result(false, settings.LatestVersionSeen);
        }
    }

    private static Result Evaluate(string? latestVersion)
    {
        if (TryParse(latestVersion, out var latest) && latest > Current)
            return new Result(true, latest.ToString());
        return new Result(false, latestVersion);
    }

    private static async Task<string?> FetchLatestAsync()
    {
        using var resp = await Http.GetAsync(ApiUrl).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Logger.Log($"UpdateChecker: GitHub returned {(int)resp.StatusCode}.");
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

        if (doc.RootElement.TryGetProperty("tag_name", out var tag) &&
            tag.GetString() is { } raw && TryParse(raw, out var v))
        {
            Logger.Log($"UpdateChecker: latest tag={raw} (parsed {v}), current={Current}.");
            return v.ToString();
        }

        Logger.Log("UpdateChecker: no parseable tag_name in response.");
        return null;
    }

    /// <summary>Parses a tag like "v0.3.1" or "0.3.1" into a 3-part Version.</summary>
    private static bool TryParse(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;

        var s = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(s, out var parsed)) return false;

        // Normalise to Major.Minor.Build so comparisons ignore an absent Revision.
        version = new Version(parsed.Major, Math.Max(parsed.Minor, 0), Math.Max(parsed.Build, 0));
        return true;
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        // GitHub's API rejects requests without a User-Agent.
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("iPrtSc", Current.ToString()));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }
}
