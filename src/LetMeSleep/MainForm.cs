using System.Diagnostics;

namespace LetMeSleep;

public sealed class MainForm : Form
{
    private readonly IPowercfg powercfg;
    private readonly OverrideJournal journal;
    private readonly OverrideService overrides;
    private readonly ListView requests = MakeList("Keeps awake", "Source", "Name", "Requests", "Overridden", "Windows explanation");
    private readonly ListView changes = MakeList("Source", "Name", "Ignored request");
    private readonly TextBox raw = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 10)
    };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(1000, 0) };
    private readonly Button refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Button ignore = new() { Text = "Allow sleep for selected", AutoSize = true, Enabled = false };
    private readonly Button ignoreAll = new() { Text = "Allow sleep for all", AutoSize = true, Enabled = false };
    private readonly Button undo = new() { Text = "Undo selected change", AutoSize = true, Enabled = false };
    private readonly Button sleep = new() { Text = "Sleep now...", AutoSize = true };
    private readonly Button settings = new() { Text = "Power settings", AutoSize = true };
    private bool busy;
    private bool recognized;
    private bool closing;
    private IReadOnlyList<RequestGroup> activeGroups = [];
    private IReadOnlyList<RequestOverride> currentOverrides = [];

    public MainForm(IPowercfg powercfg, OverrideJournal journal)
    {
        this.powercfg = powercfg;
        this.journal = journal;
        overrides = new OverrideService(powercfg, journal);
        Text = "LetMeSleep";
        using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("LetMeSleep.AppIcon")
            ?? throw new InvalidOperationException("The application icon resource is missing."))
        using (var icon = new Icon(stream))
            Icon = (Icon)icon.Clone();
        Disposed += (_, _) => Icon?.Dispose();
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(1100, 720);
        MinimumSize = new Size(850, 580);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = "Let your PC rest", Font = new Font("Segoe UI", 23, FontStyle.Bold),
            AutoSize = true, Margin = new Padding(0, 0, 0, 8)
        });
        layout.Controls.Add(new Label
        {
            Text = "See active sleep blockers, then choose what Windows may ignore. No apps are killed.",
            AutoSize = true, Margin = new Padding(0, 0, 0, 14)
        });
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
        actions.Controls.AddRange([refresh, ignore, ignoreAll, undo, sleep, settings]);
        layout.Controls.Add(actions);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        AddTab(tabs, "Active blockers", requests);
        AddTab(tabs, "Saved changes (undo)", changes);
        AddTab(tabs, "Raw diagnostics", raw);
        layout.Controls.Add(tabs);
        status.Text = "Ready to scan.";
        status.Margin = new Padding(0, 12, 0, 0);
        layout.Controls.Add(status);
        Controls.Add(layout);
        layout.SizeChanged += (_, _) => status.MaximumSize = new Size(Math.Max(100, layout.ClientSize.Width - 40), 0);

        refresh.Click += async (_, _) => await RunAsync(RefreshAsync);
        ignore.Click += async (_, _) =>
        {
            if (requests.SelectedItems.Count != 1 || requests.SelectedItems[0].Tag is not RequestGroup group) return;
            await ConfirmAndApplyAsync([group], all: false);
        };
        ignoreAll.Click += async (_, _) => await ConfirmAndApplyAsync(activeGroups, all: true);
        undo.Click += async (_, _) =>
        {
            if (changes.SelectedItems.Count != 1 || changes.SelectedItems[0].Tag is not SavedOverride entry) return;
            await RunAsync(async () =>
            {
                await overrides.UndoAsync(entry);
                await RefreshAsync();
                status.Text = "Change undone. This source's previous override settings were restored. " + status.Text;
            });
        };
        sleep.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Put this PC to sleep now? Save your work first.\r\n\r\n" +
                "Wake timers and devices can still wake it again. No applications will be forcibly closed.",
                "Sleep now", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (!Application.SetSuspendState(PowerState.Suspend, force: false, disableWakeEvent: false))
                MessageBox.Show(this, "Windows refused the sleep request. Check available sleep states in Raw diagnostics.",
                    "Could not sleep", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        settings.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                MessageBox.Show(this, ex.Message, "Could not open power settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        requests.SelectedIndexChanged += (_, _) => UpdateActions();
        changes.SelectedIndexChanged += (_, _) => UpdateActions();
        Shown += async (_, _) => await RunAsync(RefreshAsync);
        FormClosing += (_, e) =>
        {
            if (!busy) { closing = true; return; }
            e.Cancel = true;
            status.Text = "Please wait for the current operation to finish before closing.";
        };
    }

    private async Task ConfirmAndApplyAsync(IReadOnlyList<RequestGroup> groups, bool all)
    {
        if (busy || !recognized || !groups.Any(group => group.HasPendingOverride(currentOverrides))) return;
        var supported = groups.Where(group => group.HasPendingOverride(currentOverrides)).ToArray();
        var preview = string.Join("\r\n", supported.Take(8).Select(group =>
            $"{group.Caller} ({string.Join(", ", group.OverrideCategories)})"));
        if (supported.Length > 8) preview += $"\r\n...and {supported.Length - 8} more source(s).";
        if (MessageBox.Show(this,
            $"Allow sleep for {(all ? "all active supported blockers" : "this source")}?\r\n\r\n{preview}\r\n\r\n" +
            "All active DISPLAY, SYSTEM and AWAYMODE categories for these sources will be ignored. " +
            "Other categories cannot be overridden. Existing rules are preserved; already-allowed requests are skipped.\r\n\r\n" +
            "These are persistent, system-wide rules for caller names, not just running instances. " +
            "Sleep may interrupt playback, downloads, backups or other work. Closing or pausing the app is safer.\r\n\r\n" +
            "Undo in Saved changes to restore previous rules. This does not force sleep or change your sleep timer.",
            all ? "Allow sleep for all?" : "Allow sleep for this source?",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await RunAsync(async () =>
        {
            var result = await overrides.ApplyAllAsync(groups);
            await RefreshAsync();
            status.Text = result.Summary + " " + status.Text;
            var failures = result.Results.Where(r => r.Outcome == OverrideOutcome.Failed).ToArray();
            if (failures.Length == 0) return;
            var detail = string.Join("\r\n\r\n", failures.Select(r => $"{r.Kind} {r.Caller}: {r.Error}"));
            raw.AppendText("\r\n=== Override failures ===\r\n" + detail + "\r\n");
            MessageBox.Show(this,
                result.Summary + "\r\n\r\n" +
                string.Join("\r\n\r\n", failures.Take(5).Select(r => $"{r.Caller}: {r.Error}")) +
                "\r\n\r\nSuccessful changes and pending undo entries are in Saved changes. Full errors are in Raw diagnostics.",
                "Some blockers could not be allowed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        UpdateActions();
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or
                                   UnauthorizedAccessException or TimeoutException or
                                   System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            status.Text = "Operation failed. " + ex.Message;
            MessageBox.Show(this, ex.Message + "\r\n\r\nAny saved undo entries are retained. Refresh to inspect the current state.",
                "LetMeSleep", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            busy = false;
            if (!closing) UpdateActions();
        }
    }

    public async Task RefreshAsync()
    {
        status.Text = "Checking Windows power requests and wake diagnostics...";
        recognized = false;
        activeGroups = [];
        currentOverrides = [];
        requests.Items.Clear();
        changes.Items.Clear();
        raw.Clear();
        var errors = new List<string>();
        var reports = new Dictionary<string, string>();
        foreach (var (title, args) in new (string, string[])[]
        {
            ("Active requests", ["/requests"]),
            ("Current overrides", ["/requestsoverride"]),
            ("Last wake source", ["/lastwake"]),
            ("Wake timers", ["/waketimers"]),
            ("Devices allowed to wake the PC (not necessarily sleep blockers)", ["/devicequery", "wake_armed"]),
            ("Available sleep states", ["/a"]),
            ("Active power plan and sleep timeouts (AC / DC values are seconds, in hexadecimal)", ["/query", "SCHEME_CURRENT", "SUB_SLEEP"])
        })
        {
            try
            {
                reports[title] = await powercfg.RunAsync(args);
                raw.AppendText($"=== {title} ===\r\n{reports[title].Trim()}\r\n\r\n");
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                errors.Add(title);
                raw.AppendText($"=== {title} - ERROR ===\r\n{ex.Message}\r\n\r\n");
            }
        }
        IReadOnlyList<RequestOverride> current = [];
        var overrideReadable = false;
        if (reports.TryGetValue("Current overrides", out var overrideText))
        {
            try
            {
                current = PowercfgParser.ParseOverrides(overrideText);
                currentOverrides = current;
                overrideReadable = true;
            }
            catch (InvalidOperationException ex)
            {
                errors.Add("Override parsing");
                raw.AppendText(ex.Message + "\r\n\r\n");
            }
        }
        var count = 0;
        if (reports.TryGetValue("Active requests", out var requestText))
        {
            var report = PowercfgParser.ParseRequests(requestText);
            recognized = report.Recognized && overrideReadable;
            count = report.Requests.Count;
            activeGroups = RequestGroup.FromRequests(report.Requests);
            foreach (var group in activeGroups)
            {
                var overridden = overrideReadable && group.IsOverridden(current);
                var item = new ListViewItem([string.Join(", ", group.Categories), group.Kind, group.Caller,
                    group.Requests.Count.ToString(), overridden ? "\u2713" : "", group.Describe()])
                {
                    Tag = group,
                    ForeColor = overridden ? SystemColors.GrayText : requests.ForeColor,
                    ToolTipText = (overrideReadable ? group.DescribeOverrides(current) : "Override status unavailable.") +
                        "\r\n\r\n" + string.Join("\r\n", group.Requests.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase))
                };
                requests.Items.Add(item);
            }
            if (!report.Recognized) errors.Add("Request parsing (unsupported or localized output)");
        }
        foreach (var entry in journal.Load())
            changes.Items.Add(new ListViewItem([entry.Kind, entry.Caller, string.Join(", ", entry.Categories)]) { Tag = entry });
        status.Text = errors.Count > 0
            ? "Some diagnostics could not be read: " + string.Join(", ", errors) + ". See Raw diagnostics; no all-clear is implied."
            : count == 0
                ? "No active power requests. Check wake sources, sleep availability and timeouts in Raw diagnostics if sleep still fails."
                : $"{count} active request(s) from {activeGroups.Count} source(s), grouped by name. " +
                  "DISPLAY keeps the screen on; SYSTEM / AWAYMODE affect sleep. Other categories are diagnostic only.";
    }

    private void UpdateActions()
    {
        refresh.Enabled = sleep.Enabled = settings.Enabled = !busy;
        ignore.Enabled = !busy && recognized && requests.SelectedItems.Count == 1 &&
            requests.SelectedItems[0].Tag is RequestGroup group && group.HasPendingOverride(currentOverrides);
        ignoreAll.Enabled = !busy && recognized && activeGroups.Any(group => group.HasPendingOverride(currentOverrides));
        undo.Enabled = !busy && changes.SelectedItems.Count == 1;
    }

    private static ListView MakeList(params string[] columns)
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            MultiSelect = false, HideSelection = false, ShowItemToolTips = true
        };
        for (var i = 0; i < columns.Length; i++)
        {
            var width = columns[i] switch
            {
                "Keeps awake" => 180,
                "Source" => 90,
                "Name" => 180,
                "Requests" => 70,
                "Overridden" => 90,
                _ => 400
            };
            list.Columns.Add(columns[i], width,
                columns[i] == "Overridden" ? HorizontalAlignment.Center : HorizontalAlignment.Left);
        }
        return list;
    }

    private static void AddTab(TabControl tabs, string name, Control content)
    {
        var page = new TabPage(name) { Padding = new Padding(8) };
        page.Controls.Add(content);
        tabs.TabPages.Add(page);
    }
}
