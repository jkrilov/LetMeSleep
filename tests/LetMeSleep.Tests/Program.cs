using LetMeSleep;

internal static class Program
{
    private const string EmptyOverrides = "[SERVICE]\r\n\r\n[PROCESS]\r\n\r\n[DRIVER]\r\n";
    private const string Requests = """
        DISPLAY:
        None.

        SYSTEM:
        [PROCESS] \Device\HarddiskVolume3\Apps\Music Player.exe
        Playing audio.
        [DRIVER] High Definition Audio Device
        An audio stream is currently in use.

        AWAYMODE:
        None.
        EXECUTION:
        [SERVICE] Backup service
        Backup in progress.
        PERFBOOST:
        None.
        ACTIVELOCKSCREEN:
        None.
        """;
    private const string GroupedRequests = """
        DISPLAY:
        [PROCESS] \Device\HarddiskVolume3\Apps\msedge.exe
        Playing video.
        [PROCESS] \Device\HarddiskVolume3\Apps\MSEDGE.EXE
        Playing video.
        SYSTEM:
        [PROCESS] C:\Apps\msedge.exe
        Audio stream.
        [PROCESS] C:\Apps\other.exe
        Downloading.
        [DRIVER] msedge.exe
        Hardware request.
        AWAYMODE:
        [PROCESS] C:\Apps\msedge.exe
        Background media.
        EXECUTION:
        [PROCESS] C:\Apps\msedge.exe
        Browser task.
        [SERVICE] Backup service
        Backup in progress.
        PERFBOOST:
        None.
        ACTIVELOCKSCREEN:
        None.
        """;
    private static int assertions;

    [STAThread]
    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LetMeSleep.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestParsers();
            // Keep the WinForms smoke test on the STA thread.
            TestForm(Path.Combine(directory, "ui.json"));
            TestChangesAsync(Path.Combine(directory, "journal.json")).GetAwaiter().GetResult();
            TestBulkAsync(Path.Combine(directory, "bulk.json")).GetAwaiter().GetResult();
            TestRealRunnerAsync().GetAwaiter().GetResult();
            Console.WriteLine($"PASS: {assertions} assertions. No real power settings were changed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static void TestParsers()
    {
        var parsed = PowercfgParser.ParseRequests(Requests);
        Check(parsed.Recognized && parsed.Requests.Count == 3, "Parse all callers and categories");
        Check(parsed.Requests[0].Caller == "Music Player.exe", "Use process basename, preserving spaces");
        Check(parsed.Requests[1].Reason == "An audio stream is currently in use.", "Keep driver reason");
        Check(!parsed.Requests[2].CanOverride, "Execution requests cannot be overridden");
        Check(!PowercfgParser.ParseRequests("SYSTEM:\n[UNKNOWN] foo").Recognized, "Reject unknown caller");
        Check(!PowercfgParser.ParseRequests("SYSTÈME:\nAucun.").Recognized, "Localized output is not an all-clear");
        Check(!PowercfgParser.ParseRequests("").Recognized, "Empty output is not an all-clear");
        var empty = string.Join("\n", new[] { "DISPLAY", "SYSTEM", "AWAYMODE", "EXECUTION", "PERFBOOST", "ACTIVELOCKSCREEN" }
            .Select(c => c + ":\nNone."));
        Check(PowercfgParser.ParseRequests(empty) is { Recognized: true, Requests.Count: 0 }, "Complete empty request list");
        Check(!PowercfgParser.ParseRequests("DISPLAY:\nNone.\nSYSTEM:\nNone.").Recognized, "Incomplete report is not an all-clear");
        Check(!PowercfgParser.ParseRequests("Unexpected error\n" + empty).Recognized, "Unexpected report preamble is not an all-clear");
        Check(PowercfgParser.ParseOverrides(EmptyOverrides).Count == 0, "Empty override list");
        var entries = PowercfgParser.ParseOverrides(EmptyOverrides + "My audio driver SYSTEM DISPLAY\n");
        Check(entries[0].Caller == "My audio driver" && entries[0].Categories.Length == 2, "Override names with spaces");
        Throws<InvalidOperationException>(() => PowercfgParser.ParseOverrides("[PROCESS]\nfoo SYSTEM"), "Require complete report");
        Throws<InvalidOperationException>(() => PowercfgParser.ParseOverrides(EmptyOverrides + "foo EXECUTION"), "Reject unknown override types");
        var groups = RequestGroup.FromRequests(PowercfgParser.ParseRequests(GroupedRequests).Requests);
        Check(groups.Count == 4, "Group repeated callers without merging processes, services or drivers");
        var edge = groups[0];
        Check(edge.Caller == "msedge.exe" && edge.Requests.Count == 5, "Case-insensitive process grouping across paths and categories");
        Check(edge.Categories.Length == 4 && edge.OverrideCategories.Length == 3, "Preserve every category; override only supported ones");
        var description = edge.Describe();
        Check(description.Contains("Playing video.") && description.Contains("Audio stream.") &&
            description.Contains("Background media.") && description.Contains("Browser task."), "Keep all distinct reasons");
        Check(description.Split("Playing video.").Length == 2, "Deduplicate repeated reasons");
        Check(description.Contains("EXECUTION [Diagnostic only]"), "Label unsupported categories in grouped rows");
        var existing = new[] { new RequestOverride("PROCESS", "MSEDGE.EXE", ["DISPLAY", "SYSTEM", "AWAYMODE"]) };
        Check(!edge.HasPendingOverride(existing) && !groups[3].HasPendingOverride(existing), "Skip already covered and diagnostic-only groups");
        Check(edge.DescribeOverrides(existing).Contains("SYSTEM: Overridden"), "Show category-level coverage in tooltip");
        Check(!description.Contains("overridden", StringComparison.OrdinalIgnoreCase), "Keep override status out of explanations");
        Check(edge.IsOverridden(existing), "Mark a group when all supported categories are covered");
        Check(!edge.IsOverridden([]) && !groups[3].IsOverridden([]), "Do not mark uncovered or diagnostic-only groups");
        var partial = new[] { new RequestOverride("PROCESS", "msedge.exe", ["DISPLAY"]) };
        Check(!edge.IsOverridden(partial) && edge.DescribeOverrides(partial).Contains("SYSTEM: Not overridden"),
            "Partially covered group remains active and explains pending categories");
        Check(!groups[2].IsOverridden(existing), "Process override does not mark a same-name driver");
    }

    private static async Task TestChangesAsync(string path)
    {
        var fake = new FakePowercfg();
        var journal = new OverrideJournal(path);
        var service = new OverrideService(fake, journal);
        var request = PowercfgParser.ParseRequests(Requests).Requests[0];
        await ExpectOutcomeAsync(service, [request], OverrideOutcome.Updated, "Apply a selected source");
        Check(fake.Mutations == 1 && journal.Load().Count == 1, "Apply persists an undo entry");
        Check(fake.LastMutation!.SequenceEqual(new[] { "/requestsoverride", "PROCESS", "Music Player.exe", "SYSTEM" }),
            "Pass caller as one argument without shell quoting");
        await ExpectOutcomeAsync(service, [request], OverrideOutcome.AlreadyAllowed, "Already-covered request is a no-op");
        Check(fake.Mutations == 1, "No mutation on refusal");
        await new OverrideService(fake, new OverrideJournal(path)).UndoAsync(journal.Load()[0]);
        Check(fake.Mutations == 2 && journal.Load().Count == 0 && fake.Current.Count == 0, "Undo survives app restart");

        fake.Current.Add(new RequestOverride("PROCESS", request.Caller, ["DISPLAY"]));
        await ExpectOutcomeAsync(service, [request], OverrideOutcome.Updated, "Extend existing rules without losing categories");
        Check(fake.Current[0].Categories.Contains("DISPLAY") && journal.Load()[0].OriginalCategories!.SequenceEqual(["DISPLAY"]),
            "Save pre-existing rule for restoration");
        fake.Current[0] = fake.Current[0] with { Categories = ["SYSTEM", "DISPLAY", "AWAYMODE"] };
        var mutations = fake.Mutations;
        await FailsAsync(() => service.UndoAsync(journal.Load()[0]), "Refuse undo if another tool changed the rule");
        Check(fake.Mutations == mutations && journal.Load().Count == 1, "Conflict preserves both settings and receipt");
        fake.Current[0] = fake.Current[0] with { Categories = ["SYSTEM", "DISPLAY"] };
        await service.UndoAsync(journal.Load()[0]);
        Check(journal.Load().Count == 0 && fake.Current[0].Categories.SequenceEqual(["DISPLAY"]), "Undo restores pre-existing categories");
        fake.Current.Clear();

        fake.FailMutation = true;
        await ExpectOutcomeAsync(service, [request], OverrideOutcome.Failed, "Surface command failure");
        Check(journal.Load().Count == 1, "Failed apply retains write-ahead receipt");
        fake.FailMutation = false;
        await service.UndoAsync(journal.Load()[0]);
        fake.NoOpMutation = true;
        await ExpectOutcomeAsync(service, [request], OverrideOutcome.Failed, "Detect success exit without a matching Windows rule");
        Check(journal.Load().Count == 1, "Unverified apply retains receipt");
        fake.NoOpMutation = false;
        await service.UndoAsync(journal.Load()[0]);
        await ExpectOutcomeAsync(service, [request], OverrideOutcome.Updated, "Apply before undo verification");
        fake.NoOpMutation = true;
        await FailsAsync(() => service.UndoAsync(journal.Load()[0]), "Detect success exit without removing the Windows rule");
        Check(journal.Load().Count == 1 && fake.Current.Count == 1, "Unverified undo retains receipt");
        fake.NoOpMutation = false;
        await service.UndoAsync(journal.Load()[0]);

        using (var lockedJournal = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            mutations = fake.Mutations;
            await ExpectOutcomeAsync(service, [request], OverrideOutcome.Failed, "Unwritable journal must prevent a change");
            Check(fake.Mutations == mutations, "Journal must be durable before Windows settings change");
        }
        fake.RequestOutput = Requests.Replace(@"\Device\HarddiskVolume3\Apps\Music Player.exe", @"C:\Different.exe");
        mutations = fake.Mutations;
        await ExpectOutcomeAsync(service, [request], OverrideOutcome.NoLongerActive, "Skip stale requests");
        Check(fake.Mutations == mutations && journal.Load().Count == 0, "Stale request causes no change");
        fake.RequestOutput = Requests;
        await ExpectOutcomeAsync(service, [new SleepRequest("EXECUTION", "SERVICE", "Backup", "")],
            OverrideOutcome.Unsupported, "Unsupported category causes no change");
        fake.OverrideOutput = "unexpected output";
        await ExpectOutcomeAsync(service, [request], OverrideOutcome.Failed, "Unknown output causes no change");
        Check(journal.Load().Count == 0, "Unknown output does not create receipt");
        File.WriteAllText(path, "not json");
        Throws<System.Text.Json.JsonException>(() => journal.Load(), "Corrupted journal surfaces an error");
        File.WriteAllText(path, """[{"Kind":"PROCESS","Caller":"foo","Categories":["EXECUTION"]}]""");
        Throws<InvalidDataException>(() => journal.Load(), "Reject invalid journal categories");
        File.WriteAllText(path, "[null]");
        Throws<InvalidDataException>(() => journal.Load(), "Reject null journal entries");
        File.WriteAllText(path, """[{"Kind":"PROCESS","Caller":"foo","Categories":["SYSTEM"],"OriginalCategories":["DISPLAY"]}]""");
        Throws<InvalidDataException>(() => journal.Load(), "Reject a baseline that is not preserved by the saved rule");
        File.WriteAllText(path, """[{"Kind":"PROCESS","Caller":"foo","Categories":["SYSTEM"]},{"Kind":"PROCESS","Caller":"FOO","Categories":["DISPLAY"]}]""");
        Throws<InvalidDataException>(() => journal.Load(), "Reject duplicate caller receipts");
    }

    private static async Task TestBulkAsync(string path)
    {
        var fake = new FakePowercfg { RequestOutput = GroupedRequests };
        var journal = new OverrideJournal(path);
        var service = new OverrideService(fake, journal);
        var groups = RequestGroup.FromRequests(PowercfgParser.ParseRequests(GroupedRequests).Requests);
        fake.Current.Add(new RequestOverride("PROCESS", "msedge.exe", ["DISPLAY"]));
        var result = await service.ApplyAllAsync(groups.Concat(groups));
        Check(result.Results.Count == 4 && result.Results.Count(r => r.Outcome == OverrideOutcome.Updated) == 3,
            "Bulk applies one rule per unique supported caller");
        Check(result.Results[3].Outcome == OverrideOutcome.Unsupported && result.Summary.Contains("diagnostic only"),
            "Bulk explicitly reports unsupported sources");
        Check(fake.Mutations == 3 && journal.Load().Count == 3, "Bulk avoids repeated writes for duplicate requests");
        var edge = fake.Current.Single(e => e.Kind == "PROCESS" && e.Caller == "msedge.exe");
        Check(edge.Categories.ToHashSet().SetEquals(["DISPLAY", "SYSTEM", "AWAYMODE"]), "One rule covers every supported category");
        var mutations = fake.Mutations;
        result = await service.ApplyAllAsync(groups);
        Check(result.Results.Count(r => r.Outcome == OverrideOutcome.AlreadyAllowed) == 3 && fake.Mutations == mutations,
            "Repeated bulk action is idempotent");
        foreach (var receipt in journal.Load()) await service.UndoAsync(receipt);
        Check(fake.Current.Count == 1 && fake.Current[0].Categories.SequenceEqual(["DISPLAY"]) && journal.Load().Count == 0,
            "Bulk undo restores pre-existing rules and removes only app additions");
        fake.Current.Clear();

        fake.FailMutationCaller = "other.exe";
        result = await service.ApplyAllAsync(groups);
        Check(result.Results.Count(r => r.Outcome == OverrideOutcome.Updated) == 2 &&
            result.Results.Single(r => r.Outcome == OverrideOutcome.Failed).Caller == "other.exe",
            "Bulk continues after a failed source and identifies partial failure");
        Check(result.Results.Single(r => r.Outcome == OverrideOutcome.Failed).Error!.Contains("access denied") &&
            result.Summary.Contains("failed"), "Bulk surfaces the failure reason and count");
        Check(journal.Load().Count == 3 && fake.Current.Count == 2, "Successful and pending changes retain undo entries");
        fake.FailMutationCaller = null;
        foreach (var receipt in journal.Load()) await service.UndoAsync(receipt);
        Check(journal.Load().Count == 0 && fake.Current.Count == 0, "Undo recovers a partially failed batch");

        File.WriteAllText(path, """[{"Kind":"PROCESS","Caller":"msedge.exe","Categories":["SYSTEM"]}]""");
        fake.Current.Add(new RequestOverride("PROCESS", "msedge.exe", ["SYSTEM"]));
        result = await service.ApplyAllAsync([groups[0]]);
        Check(result.Results.Single().Outcome == OverrideOutcome.Updated && journal.Load()[0].Categories.Length == 3,
            "Extend a legacy single-category receipt to all active categories");
        await new OverrideService(fake, new OverrideJournal(path)).UndoAsync(journal.Load()[0]);
        Check(fake.Current.Count == 0, "Legacy receipt extension remains undoable after restart");

        var systemOnly = groups[0].Requests.Where(r => r.Category == "SYSTEM").ToArray();
        await ExpectOutcomeAsync(service, systemOnly, OverrideOutcome.Updated, "Apply initial single category");
        fake.NoOpMutation = true;
        await ExpectOutcomeAsync(service, groups[0].Requests, OverrideOutcome.Failed, "Failed extension retains previous app-owned state");
        Check(journal.Load()[0].PreviousCategories!.SequenceEqual(["SYSTEM"]), "Write-ahead receipt records the prior app rule");
        fake.NoOpMutation = false;
        await service.UndoAsync(journal.Load()[0]);
        Check(fake.Current.Count == 0 && journal.Load().Count == 0, "Undo recovers an interrupted multi-category extension");

        fake.ThrowAfterMutation = true;
        await ExpectOutcomeAsync(service, groups[0].Requests, OverrideOutcome.Failed, "Surface failure after a rule was written");
        Check(fake.Current.Count == 1 && journal.Load().Count == 1, "Written rule remains tracked even if command reports failure");
        fake.ThrowAfterMutation = false;
        await service.UndoAsync(journal.Load()[0]);
        Check(fake.Current.Count == 0, "Undo recovers a write that failed after mutation");

        mutations = fake.Mutations;
        fake.RequestOutput = "localized or incomplete";
        await FailsAsync(() => service.ApplyAllAsync(groups), "Incomplete live report aborts the whole batch");
        Check(fake.Mutations == mutations && journal.Load().Count == 0, "Unsafe preflight performs no writes");
        fake.RequestOutput = GroupedRequests;
        File.WriteAllText(path, "broken json");
        await ThrowsAsync<System.Text.Json.JsonException>(() => service.ApplyAllAsync(groups), "Corrupt journal aborts the whole batch");
        Check(fake.Mutations == mutations, "Corrupt journal causes no Windows changes");
    }

    private static async Task TestRealRunnerAsync()
    {
        var runner = new Powercfg();
        var report = await runner.RunAsync("/requestsoverride");
        Check(PowercfgParser.ParseOverrides(report) is not null, "Read real Windows override report without mutations");
        await FailsAsync(() => runner.RunAsync("/LetMeSleepInvalidOption"), "Surface actual powercfg nonzero exit code");
    }

    private static void TestForm(string path)
    {
        ApplicationConfiguration.Initialize();
        var fake = new FakePowercfg { RequestOutput = GroupedRequests };
        using var form = new MainForm(fake, new OverrideJournal(path));
        using var timer = new System.Windows.Forms.Timer { Interval = 200 };
        Exception? error = null;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var tabs = Descendants(form).OfType<TabControl>().Single();
                using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("LetMeSleep.AppIcon"))
                {
                    Check(stream is not null, "Sleeping symbol icon is embedded in the app");
                    using var expected = new Icon(stream!);
                    using var expectedBitmap = expected.ToBitmap();
                    using var actualBitmap = form.Icon!.ToBitmap();
                    Check(expectedBitmap.Size == actualBitmap.Size &&
                        Enumerable.Range(0, expectedBitmap.Width).All(x =>
                            Enumerable.Range(0, expectedBitmap.Height).All(y =>
                                expectedBitmap.GetPixel(x, y) == actualBitmap.GetPixel(x, y))),
                        "Window and taskbar use the embedded sleeping symbol icon");
                }
                Check(tabs.TabPages.Count == 3, "UI exposes blockers, undo, and diagnostics");
                Check(Descendants(form).OfType<ListView>().First().Items.Count == 4, "UI loads deduplicated source rows");
                var text = Descendants(form).OfType<TextBox>().Single().Text;
                Check(text.Contains("Last wake source") && text.Contains("Available sleep states"), "UI includes wake and sleep diagnostics");
                Check(Descendants(form).OfType<Button>().Single(b => b.Text == "Refresh").Enabled, "Refresh completes without blocking UI");
                var ignore = Descendants(form).OfType<Button>().Single(b => b.Text == "Allow sleep for selected");
                Check(!ignore.Enabled, "Ignore requires a selection");
                var list = Descendants(form).OfType<ListView>().First();
                Check(list.Columns[4].Text == "Overridden" && list.Columns[4].TextAlign == HorizontalAlignment.Center &&
                    list.Columns[4].Width <= 100 && list.Columns[5].Text == "Windows explanation",
                    "Override status has a compact centered column separate from explanations");
                Check(list.Items[0].SubItems[4].Text == "" && list.Items[0].ForeColor == list.ForeColor,
                    "Active unoverridden row is unmarked and uses normal text");
                Check(list.Items[0].SubItems[2].Text == "msedge.exe" && list.Items[0].SubItems[3].Text == "5",
                    "UI shows Edge once with an explicit request count");
                Check(list.Items[0].SubItems[0].Text.Contains("SYSTEM") && list.Items[0].SubItems[0].Text.Contains("DISPLAY"),
                    "Grouped UI retains all blocker categories");
                Check(list.Items[0].ToolTipText.Contains(@"C:\Apps\msedge.exe"), "Original paths remain available in tooltips");
                var all = Descendants(form).OfType<Button>().Single(b => b.Text == "Allow sleep for all");
                Check(all.Enabled, "Bulk action is available without selecting a row");
                list.Items[0].Selected = true;
                Check(ignore.Enabled, "Supported blocker enables ignore");
                list.Items[0].Selected = false;
                list.Items[3].Selected = true;
                Check(!ignore.Enabled, "Unsupported blocker cannot be ignored");
                fake.Current.Add(new RequestOverride("PROCESS", "msedge.exe", ["DISPLAY"]));
                Descendants(form).OfType<Button>().Single(b => b.Text == "Refresh").PerformClick();
                Check(list.Items[0].SubItems[4].Text == "" && list.Items[0].ForeColor == list.ForeColor,
                    "Partially overridden row stays unmarked and undimmed");
                Check(list.Items[0].ToolTipText.Contains("DISPLAY: Overridden") &&
                    list.Items[0].ToolTipText.Contains("SYSTEM: Not overridden"), "Tooltip distinguishes partial category coverage");
                Check(!list.Items[0].SubItems[5].Text.Contains("overridden", StringComparison.OrdinalIgnoreCase),
                    "Partially overridden explanation stays free of override badges");
                fake.Current.Clear();
                foreach (var group in RequestGroup.FromRequests(PowercfgParser.ParseRequests(GroupedRequests).Requests)
                             .Where(group => group.OverrideCategories.Length > 0))
                    fake.Current.Add(new RequestOverride(group.Kind, group.Caller, group.OverrideCategories));
                Descendants(form).OfType<Button>().Single(b => b.Text == "Refresh").PerformClick();
                Check(!all.Enabled, "Bulk action disables when all supported requests are already covered");
                Check(list.Items.Cast<ListViewItem>().Take(3).All(item =>
                    item.SubItems[4].Text == "\u2713" && item.ForeColor == SystemColors.GrayText && item.UseItemStyleForSubItems),
                    "Fully overridden rows have a check mark and grey text across all columns");
                Check(list.Items[3].SubItems[4].Text == "" && list.Items[3].ForeColor == list.ForeColor,
                    "Diagnostic-only row is not incorrectly checked or dimmed");
                Check(list.Items[0].SubItems[5].Text.Contains("Audio stream.") &&
                    !list.Items[0].SubItems[5].Text.Contains("overridden", StringComparison.OrdinalIgnoreCase),
                    "Overridden row retains its explanation without inline override status");
                list.Items[0].Selected = true;
                Check(!ignore.Enabled, "Covered group cannot be reapplied");
                fake.Current.Clear();
                Descendants(form).OfType<Button>().Single(b => b.Text == "Refresh").PerformClick();
                Check(list.Items.Cast<ListViewItem>().All(item =>
                    item.SubItems[4].Text == "" && item.ForeColor == list.ForeColor),
                    "Removing overrides restores unmarked, normal-color rows");
                fake.OverrideOutput = "unknown format";
                Descendants(form).OfType<Button>().Single(b => b.Text == "Refresh").PerformClick();
                Check(!all.Enabled && !ignore.Enabled, "Unrecognized diagnostics disable selected and bulk actions");
                Check(list.Items[0].SubItems[4].Text == "" && list.Items[0].ForeColor == list.ForeColor &&
                    list.Items[0].ToolTipText.Contains("Override status unavailable."),
                    "Unreadable overrides never produce a misleading check mark or dimmed row");
                Check(fake.Mutations == 0, "Grouping, selection and refresh never modify settings");
            }
            catch (Exception ex) { error = ex; }
            form.Close();
        };
        form.Shown += (_, _) => timer.Start();
        Application.Run(form);
        if (error is not null) throw error;
    }

    private static IEnumerable<Control> Descendants(Control root) =>
        root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        assertions++;
    }

    private static void Throws<T>(Action action, string description) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new Exception("FAIL: " + description);
    }

    private static async Task FailsAsync(Func<Task> action, string description)
    {
        try { await action(); }
        catch (InvalidOperationException) { assertions++; return; }
        throw new Exception("FAIL: " + description);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string description) where T : Exception
    {
        try { await action(); }
        catch (T) { assertions++; return; }
        throw new Exception("FAIL: " + description);
    }

    private static async Task ExpectOutcomeAsync(OverrideService service, IEnumerable<SleepRequest> requests,
        OverrideOutcome expected, string description)
    {
        var result = await service.ApplyAllAsync(RequestGroup.FromRequests(requests));
        Check(result.Results.Count == 1 && result.Results[0].Outcome == expected, description);
        if (expected == OverrideOutcome.Failed)
            Check(!string.IsNullOrWhiteSpace(result.Results[0].Error), description + " includes an error");
    }

    private sealed class FakePowercfg : IPowercfg
    {
        public readonly List<RequestOverride> Current = [];
        public int Mutations;
        public string[]? LastMutation;
        public string RequestOutput = Requests;
        public string? OverrideOutput;
        public bool FailMutation;
        public bool NoOpMutation;
        public string? FailMutationCaller;
        public bool ThrowAfterMutation;

        public Task<string> RunAsync(params string[] arguments)
        {
            if (arguments[0] == "/requests") return Task.FromResult(RequestOutput);
            if (arguments[0] != "/requestsoverride") return Task.FromResult("Diagnostic result.");
            if (arguments.Length == 1)
            {
                if (OverrideOutput is not null) return Task.FromResult(OverrideOutput);
                var text = "";
                foreach (var kind in new[] { "SERVICE", "PROCESS", "DRIVER" })
                {
                    text += $"[{kind}]\r\n";
                    foreach (var entry in Current.Where(e => e.Kind == kind))
                        text += entry.Caller + " " + string.Join(' ', entry.Categories) + "\r\n";
                }
                return Task.FromResult(text);
            }
            Mutations++;
            LastMutation = arguments;
            if (FailMutation || arguments[2] == FailMutationCaller) throw new InvalidOperationException("Simulated access denied.");
            if (NoOpMutation) return Task.FromResult("");
            Current.RemoveAll(e => PowercfgParser.SameCaller(e, arguments[1], arguments[2]));
            if (arguments.Length > 3) Current.Add(new RequestOverride(arguments[1], arguments[2], arguments[3..]));
            if (ThrowAfterMutation) throw new InvalidOperationException("Simulated failure after mutation.");
            return Task.FromResult("");
        }
    }
}
