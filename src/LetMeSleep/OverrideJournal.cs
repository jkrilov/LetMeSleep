using System.Text.Json;

namespace LetMeSleep;

public sealed record SavedOverride(string Kind, string Caller, string[] Categories,
    string[]? OriginalCategories = null, string[]? PreviousCategories = null);

public sealed class OverrideJournal(string path)
{
    public IReadOnlyList<SavedOverride> Load()
    {
        if (!File.Exists(path)) return [];
        var entries = JsonSerializer.Deserialize<List<SavedOverride>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The undo journal is empty or damaged.");
        var callers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry is null || entry.Kind is not ("PROCESS" or "SERVICE" or "DRIVER") ||
                string.IsNullOrWhiteSpace(entry.Caller) || entry.Caller.Contains('\0') ||
                !ValidCategories(entry.Categories, allowEmpty: false) ||
                !ValidCategories(entry.OriginalCategories ?? [], allowEmpty: true) ||
                (entry.PreviousCategories is not null && !ValidCategories(entry.PreviousCategories, allowEmpty: true)) ||
                !(entry.OriginalCategories ?? []).All(c => entry.Categories.Contains(c)) ||
                !(entry.PreviousCategories ?? []).All(c => entry.Categories.Contains(c)) ||
                !callers.Add(entry.Kind + "\0" + entry.Caller))
                throw new InvalidDataException("The undo journal contains an invalid entry. No settings were changed.");
        }
        return entries;
    }

    private static bool ValidCategories(string[]? categories, bool allowEmpty) =>
        categories is not null && categories.Length >= (allowEmpty ? 0 : 1) && categories.Length <= 3 &&
        categories.All(c => c is "SYSTEM" or "DISPLAY" or "AWAYMODE") &&
        categories.Distinct(StringComparer.OrdinalIgnoreCase).Count() == categories.Length;

    public void Save(IEnumerable<SavedOverride> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, entries, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}

public enum OverrideOutcome { Updated, AlreadyAllowed, NoLongerActive, Unsupported, Failed }

public sealed record OverrideResult(string Kind, string Caller, OverrideOutcome Outcome, string? Error = null);

public sealed record OverrideBatchResult(IReadOnlyList<OverrideResult> Results)
{
    public string Summary => string.Join("; ", Results.GroupBy(r => r.Outcome).Select(group =>
        group.Key switch
        {
            OverrideOutcome.Updated => $"{group.Count()} source(s) updated",
            OverrideOutcome.AlreadyAllowed => $"{group.Count()} source(s) already allowed",
            OverrideOutcome.NoLongerActive => $"{group.Count()} source(s) no longer active",
            OverrideOutcome.Unsupported => $"{group.Count()} source(s) diagnostic only (Windows cannot override these categories)",
            _ => $"{group.Count()} source(s) failed"
        })) + ". Undo saved changes to restore previous rules.";
}

public sealed class OverrideService(IPowercfg powercfg, OverrideJournal journal)
{
    public async Task<OverrideBatchResult> ApplyAllAsync(IEnumerable<RequestGroup> groups)
    {
        var targets = RequestGroup.FromRequests(groups.SelectMany(group => group.Requests));
        if (targets.Count == 0) throw new InvalidOperationException("No active blockers were selected.");
        journal.Load();
        var current = PowercfgParser.ParseRequests(await powercfg.RunAsync("/requests"));
        if (!current.Recognized)
            throw new InvalidOperationException("Active requests could not be verified. Refresh first. No settings were changed.");
        var results = new List<OverrideResult>();
        foreach (var group in targets)
        {
            try
            {
                var outcome = await ApplyGroupAsync(group, current.Requests);
                results.Add(new OverrideResult(group.Kind, group.Caller, outcome));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or
                                       UnauthorizedAccessException or TimeoutException or
                                       JsonException or System.ComponentModel.Win32Exception)
            {
                results.Add(new OverrideResult(group.Kind, group.Caller, OverrideOutcome.Failed, ex.Message));
            }
        }
        return new OverrideBatchResult(results);
    }

    private async Task<OverrideOutcome> ApplyGroupAsync(RequestGroup group, IReadOnlyList<SleepRequest> active)
    {
        if (group.OverrideCategories.Length == 0) return OverrideOutcome.Unsupported;
        var categories = group.OverrideCategories.Where(category => active.Any(request =>
            request.Category == category && request.Kind == group.Kind &&
            request.Caller.Equals(group.Caller, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (categories.Length == 0) return OverrideOutcome.NoLongerActive;
        var existing = PowercfgParser.ParseOverrides(await powercfg.RunAsync("/requestsoverride"));
        var before = CallerCategories(existing, group.Kind, group.Caller);
        var saved = journal.Load().ToList();
        var previous = saved.SingleOrDefault(e => SameCaller(e, group.Kind, group.Caller));
        if (previous is not null && !IsKnownState(previous, before))
            throw new InvalidOperationException("This override was changed outside LetMeSleep. No settings were changed for this source.");
        var desired = before.Concat(categories).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (SameCategories(before, desired)) return OverrideOutcome.AlreadyAllowed;
        var entry = new SavedOverride(group.Kind, group.Caller, desired,
            previous?.OriginalCategories ?? (previous is null ? before : []), before);
        if (previous is not null) saved.Remove(previous);
        saved.Add(entry);
        // Keep both possible states until the write is verified, so interrupted updates remain undoable.
        journal.Save(saved);
        await powercfg.RunAsync(["/requestsoverride", entry.Kind, entry.Caller, .. desired]);
        var after = PowercfgParser.ParseOverrides(await powercfg.RunAsync("/requestsoverride"));
        if (!SameCategories(CallerCategories(after, entry.Kind, entry.Caller), desired))
            throw new InvalidOperationException(
                "Windows did not confirm the override. The undo entry was retained; inspect Raw diagnostics.");
        saved[saved.IndexOf(entry)] = entry with { PreviousCategories = null };
        journal.Save(saved);
        return OverrideOutcome.Updated;
    }

    public async Task UndoAsync(SavedOverride entry)
    {
        var saved = journal.Load().ToList();
        var receipt = saved.SingleOrDefault(e => SameCaller(e, entry.Kind, entry.Caller) &&
            SameCategories(e.Categories, entry.Categories)) ??
            throw new InvalidOperationException("The saved change no longer exists. Refresh first.");
        var existing = PowercfgParser.ParseOverrides(await powercfg.RunAsync("/requestsoverride"));
        var before = CallerCategories(existing, entry.Kind, entry.Caller);
        var original = receipt.OriginalCategories ?? [];
        if (!IsKnownState(receipt, before))
            throw new InvalidOperationException(
                "This override was changed outside LetMeSleep. Undo was refused to preserve those settings.");
        if (!SameCategories(before, original))
        {
            await powercfg.RunAsync(["/requestsoverride", entry.Kind, entry.Caller, .. original]);
            var after = PowercfgParser.ParseOverrides(await powercfg.RunAsync("/requestsoverride"));
            if (!SameCategories(CallerCategories(after, entry.Kind, entry.Caller), original))
                throw new InvalidOperationException("Windows did not restore the previous override. The undo entry was retained.");
        }
        saved.Remove(receipt);
        journal.Save(saved);
    }

    private static string[] CallerCategories(IReadOnlyList<RequestOverride> overrides, string kind, string caller)
    {
        var matches = overrides.Where(e => PowercfgParser.SameCaller(e, kind, caller)).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException("Windows returned duplicate override rules for this source. No further changes were made.");
        return matches.SingleOrDefault()?.Categories ?? [];
    }

    private static bool SameCaller(SavedOverride entry, string kind, string caller) =>
        entry.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) &&
        entry.Caller.Equals(caller, StringComparison.OrdinalIgnoreCase);

    private static bool SameCategories(string[] left, string[] right) =>
        left.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right);

    private static bool IsKnownState(SavedOverride entry, string[] categories) =>
        SameCategories(entry.Categories, categories) ||
        SameCategories(entry.OriginalCategories ?? [], categories) ||
        (entry.PreviousCategories is not null && SameCategories(entry.PreviousCategories, categories));
}
