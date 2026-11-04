using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace LetMeSleep;

public sealed record SleepRequest(string Category, string Kind, string Name, string Reason)
{
    public string Caller => Kind == "PROCESS" ? Name[(Name.LastIndexOf('\\') + 1)..] : Name;
    public bool CanOverride => Category is "SYSTEM" or "DISPLAY" or "AWAYMODE";
}

public sealed record RequestReport(IReadOnlyList<SleepRequest> Requests, bool Recognized);
public sealed record RequestOverride(string Kind, string Caller, string[] Categories);

public sealed class RequestGroup
{
    private RequestGroup(IReadOnlyList<SleepRequest> requests) => Requests = requests;

    public IReadOnlyList<SleepRequest> Requests { get; }
    public string Kind => Requests[0].Kind;
    public string Caller => Requests[0].Caller;
    public string[] Categories => Requests.Select(r => r.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public string[] OverrideCategories => Requests.Where(r => r.CanOverride).Select(r => r.Category)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public bool HasPendingOverride(IReadOnlyList<RequestOverride> overrides) =>
        OverrideCategories.Any(category => !overrides.Any(entry =>
            PowercfgParser.SameCaller(entry, Kind, Caller) &&
            entry.Categories.Contains(category, StringComparer.OrdinalIgnoreCase)));

    public bool IsOverridden(IReadOnlyList<RequestOverride> overrides) =>
        OverrideCategories.Length > 0 && !HasPendingOverride(overrides);

    public string DescribeOverrides(IReadOnlyList<RequestOverride> overrides) => string.Join("\r\n",
        Categories.Select(category => $"{category}: " +
            (overrides.Any(entry => PowercfgParser.SameCaller(entry, Kind, Caller) &&
                entry.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
                ? "Overridden"
                : OverrideCategories.Contains(category) ? "Not overridden" : "Diagnostic only")));

    public string Describe() => string.Join(" | ",
        Requests.GroupBy(r => r.Category).Select(group =>
        {
            var reasons = group.Select(r => r.Reason).Where(reason => reason.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return $"{group.Key}{(!group.First().CanOverride ? " [Diagnostic only]" : "")}: " +
                string.Join("; ", reasons);
        }));

    public static IReadOnlyList<RequestGroup> FromRequests(IEnumerable<SleepRequest> requests) =>
        requests.GroupBy(r => r.Kind + "\0" + r.Caller, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RequestGroup(group.ToArray())).ToArray();
}

public static class PowercfgParser
{
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "DISPLAY", "SYSTEM", "AWAYMODE", "EXECUTION", "PERFBOOST", "ACTIVELOCKSCREEN"
    };

    public static RequestReport ParseRequests(string text)
    {
        var requests = new List<SleepRequest>();
        string? category = null;
        var headings = new HashSet<string>(StringComparer.Ordinal);
        var recognized = true;
        var canAppendReason = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.EndsWith(':'))
            {
                var heading = line[..^1];
                if (Categories.Contains(heading))
                {
                    category = heading;
                    if (!headings.Add(heading)) recognized = false;
                    canAppendReason = false;
                    continue;
                }
                // Reasons can contain colons; reserve uppercase lines for section headings.
                if (line == line.ToUpperInvariant() && !line.StartsWith('['))
                {
                    recognized = false;
                    category = null;
                }
            }
            var match = Regex.Match(line, @"^\[(PROCESS|SERVICE|DRIVER)\]\s+(.+)$");
            if (match.Success && category is not null)
            {
                requests.Add(new SleepRequest(category, match.Groups[1].Value, match.Groups[2].Value, ""));
                canAppendReason = true;
            }
            else if (line.StartsWith('['))
            {
                recognized = false;
            }
            else if (line == "None." && category is not null && !canAppendReason)
            {
                continue;
            }
            else
            {
                if (canAppendReason && category is not null && requests.Count > 0 && requests[^1].Category == category)
                    requests[^1] = requests[^1] with
                    {
                        Reason = (requests[^1].Reason + " " + line).Trim()
                    };
                else
                    recognized = false;
            }
        }
        return new RequestReport(requests, recognized && Categories.IsSubsetOf(headings));
    }

    public static IReadOnlyList<RequestOverride> ParseOverrides(string text)
    {
        var entries = new List<RequestOverride>();
        var headings = new HashSet<string>(StringComparer.Ordinal);
        string? kind = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line is "[PROCESS]" or "[SERVICE]" or "[DRIVER]")
            {
                kind = line[1..^1];
                headings.Add(kind);
                continue;
            }
            var match = Regex.Match(line,
                @"^(.+?)\s+((?:(?:DISPLAY|SYSTEM|AWAYMODE)(?:\s+|$))+)$");
            if (kind is null || !match.Success)
                throw new InvalidOperationException(
                    "Windows returned an unrecognized override format. No settings were changed. See Raw diagnostics.");
            entries.Add(new RequestOverride(kind, match.Groups[1].Value.Trim(),
                match.Groups[2].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        }
        if (headings.Count != 3)
            throw new InvalidOperationException(
                "The override report is incomplete or localized. No settings were changed. See Raw diagnostics.");
        return entries;
    }

    public static bool SameCaller(RequestOverride entry, string kind, string caller) =>
        entry.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) &&
        entry.Caller.Equals(caller, StringComparison.OrdinalIgnoreCase);
}

public interface IPowercfg
{
    Task<string> RunAsync(params string[] arguments);
}

public sealed class Powercfg : IPowercfg
{
    public async Task<string> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Windows could not start powercfg.exe.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("powercfg did not respond within 20 seconds. Refresh to try again.");
        }
        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"powercfg {string.Join(' ', arguments)} failed (exit {process.ExitCode}).\r\n" +
                $"{stdout}\r\n{stderr}".Trim());
        return stdout;
    }

    private static Encoding ConsoleEncoding
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}
