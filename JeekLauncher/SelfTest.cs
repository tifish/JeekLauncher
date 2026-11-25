using System.Diagnostics;
using System.Text;
using System.Threading;
using Jeek.Avalonia.Localization;
using JeekLauncher.Services;
using JeekTools;

namespace JeekLauncher;

/// <summary>
/// Headless diagnostics (JeekLauncher.exe --selftest [all|search|watch|autostart|lang|quickwords]).
/// Exercises the real services without starting the GUI and writes a report to
/// <c>%TEMP%\jeek-selftest.txt</c>.
/// </summary>
internal static class SelfTest
{
    public static void Run(string mode)
    {
        var report = new StringBuilder();
        bool all = mode == "all";

        if (all || mode == "search")
            Section(report, "SEARCH", BuildSearchReport);
        if (all || mode == "watch")
            Section(report, "WATCH", BuildWatchReport);
        if (all || mode == "autostart")
            Section(report, "AUTOSTART", BuildAutoStartReport);
        if (all || mode == "lang")
            Section(report, "LANG", BuildLangReport);
        if (all || mode == "quickwords")
            Section(report, "QUICKWORDS", BuildQuickWordsReport);

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "jeek-selftest.txt"), report.ToString());
    }

    private static void Section(StringBuilder report, string title, Func<string> build)
    {
        report.AppendLine($"################  {title}  ################");
        report.AppendLine();
        report.AppendLine(build());
        report.AppendLine();
    }

    // ---- Scan + search -----------------------------------------------------

    private static string BuildSearchReport()
    {
        var report = new StringBuilder();
        void Line(string s = "") => report.AppendLine(s);

        var local = LocalSettings.Load();

        var watch = Stopwatch.StartNew();
        var entries = ShortcutScanner.Scan(local.ScanDirectories);
        watch.Stop();

        Line($"Scanned {entries.Count} shortcuts in {watch.ElapsedMilliseconds} ms");
        Line();

        Line("== Chinese-named entries  (name -> initials) ==");
        foreach (var e in entries.Where(e => InitialsHelper.HasCjk(e.Name)).Take(20))
            Line($"   {e.Name}  ->  [{e.Initials}]");
        Line();

        var usage = new UsageStore();
        var quickWords = new QuickWordStore();

        void Query(string q)
        {
            var hits = SearchEngine.Search(q, entries, usage, quickWords, 6);
            Line($"== Search \"{q}\"  ->  {hits.Count} hits ==");
            foreach (var r in hits)
                Line($"     {r.Name}   [{r.Initials}]");
            Line();
        }

        Query("vsc");      // exact initials
        Query("bl");       // partial initials -> 哔哩哔哩
        Query("code");     // letters inside a word
        Query("bangong");  // partial pinyin
        Query("codex");    // packaged AppsFolder app (no shortcut file)
        Query("claude");   // packaged AppsFolder app

        return report.ToString().TrimEnd();
    }

    // ---- Directory watcher -------------------------------------------------

    private static string BuildWatchReport()
    {
        var report = new StringBuilder();

        string dir = Path.Combine(Path.GetTempPath(), "jeek-watchtest");
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(dir))
            File.Delete(f);

        int fired = 0;
        using (var watcher = new DirectoryWatcher(() => Interlocked.Increment(ref fired)))
        {
            watcher.Watch(new[] { dir });
            Thread.Sleep(400); // let the watcher spin up

            string lnk = Path.Combine(dir, "ZzWatchTest.lnk");
            File.WriteAllText(lnk, "dummy");
            Thread.Sleep(1500);
            int afterCreate = fired;

            File.Delete(lnk);
            Thread.Sleep(1500);
            int afterDelete = fired;

            File.WriteAllText(Path.Combine(dir, "ignore.tmp"), "x");
            Thread.Sleep(1200);
            int afterIrrelevant = fired;

            report.AppendLine($"fired after .lnk create : {afterCreate}  (expect >= 1)");
            report.AppendLine($"fired after .lnk delete : {afterDelete}  (expect > create)");
            report.AppendLine($"fired after .tmp create : {afterIrrelevant}  (expect == delete, .tmp ignored)");
            report.AppendLine();
            report.AppendLine(afterCreate >= 1 && afterDelete > afterCreate && afterIrrelevant == afterDelete
                ? "RESULT: PASS"
                : "RESULT: FAIL");
        }

        try { Directory.Delete(dir, true); } catch { /* ignore */ }
        return report.ToString().TrimEnd();
    }

    // ---- Autostart ---------------------------------------------------------

    private static string BuildAutoStartReport()
    {
        var report = new StringBuilder();
        bool liveEnabled = AutoStart.IsEnabled();
        string testTask = $"JeekLauncher-SelfTest-{Environment.ProcessId}";
        string exe = Environment.ProcessPath ?? "";

        bool created = false;
        bool afterOn = false;
        bool afterOff = false;
        try
        {
            created = !string.IsNullOrEmpty(exe) && AutoStart.RegisterTask(exe, testTask);
            afterOn = AutoStart.TaskExists(testTask);
            AutoStart.DeleteTask(testTask);
            afterOff = AutoStart.TaskExists(testTask);
        }
        finally
        {
            if (AutoStart.TaskExists(testTask))
                AutoStart.DeleteTask(testTask);
        }

        report.AppendLine($"live autostart : {liveEnabled}  (untouched)");
        report.AppendLine($"test task      : {testTask}");
        report.AppendLine($"register ok    : {created}   (expect True)");
        report.AppendLine($"after on       : {afterOn}   (expect True)");
        report.AppendLine($"after off      : {afterOff}  (expect False)");
        report.AppendLine();
        report.AppendLine(created && afterOn && !afterOff ? "RESULT: PASS" : "RESULT: FAIL");

        return report.ToString().TrimEnd();
    }

    // ---- Localization ------------------------------------------------------

    private static string BuildLangReport()
    {
        var report = new StringBuilder();

        Localizer.SetLocalizer(new TabLocalizer(
            Path.Combine(AppPaths.ProgramDir, "Data", "Languages.tab")));

        report.AppendLine("languages: " + string.Join(", ", Localizer.Languages));
        report.AppendLine();

        string[] keys = { "AppName", "TrayShow", "Settings", "SearchWatermark", "StatusNoMatches", "StatusExists", "StatusMissing" };
        foreach (string lang in Localizer.Languages)
        {
            Localizer.Language = lang;
            report.AppendLine($"== {lang} ==");
            foreach (string k in keys)
                report.AppendLine($"   {k} = {Localizer.Get(k)}");
            report.AppendLine($"   StatusResults = {string.Format(Localizer.Get("StatusResults"), 3)}");
            report.AppendLine();
        }

        bool pass = Localizer.Languages.Contains("en")
            && Localizer.Languages.Contains("zh")
            && Localizer.Get("TrayShow") != "TrayShow"; // key resolved, not echoed back
        report.AppendLine(pass ? "RESULT: PASS" : "RESULT: FAIL");

        return report.ToString().TrimEnd();
    }

    // ---- QuickWords multi-machine merge ------------------------------------

    private static string BuildQuickWordsReport()
    {
        var report = new StringBuilder();
        var failures = new List<string>();
        void Check(bool ok, string label)
        {
            report.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {label}");
            if (!ok)
                failures.Add(label);
        }

        string root = Path.Combine(Path.GetTempPath(), $"jeek-qwtest-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            // 1. Legacy QuickWords.json fallback + override by shard
            File.WriteAllText(
                Path.Combine(root, "QuickWords.json"),
                """{"Words":{"legacy1":"C:\\Old\\One.lnk","shared":"C:\\Old\\Shared.lnk"}}""");

            var pcA = QuickWordStore.LoadFrom(root, "PC-A");
            var pcB = QuickWordStore.LoadFrom(root, "PC-B");
            Check(pcA.GetTarget("legacy1") == @"C:\Old\One.lnk", "Legacy fallback loaded");

            // 2. Concurrent disjoint edits across PC-A and PC-B -> zero conflicts, both preserved
            Check(pcA.Set("vs", @"C:\Apps\Code.lnk"), "PC-A sets 'vs'");
            pcA.SaveTo(root);
            Check(pcB.Set("gh", @"C:\Apps\GitHub.lnk"), "PC-B sets 'gh'");
            pcB.SaveTo(root);

            string qwDir = Path.Combine(root, "QuickWords");
            Check(File.Exists(Path.Combine(qwDir, "PC-A.json")), "PC-A shard exists under QuickWords/");
            Check(File.Exists(Path.Combine(qwDir, "PC-B.json")), "PC-B shard exists under QuickWords/");

            pcA.ReloadFrom(root);
            pcB.ReloadFrom(root);
            Check(pcA.GetTarget("vs") == @"C:\Apps\Code.lnk" && pcA.GetTarget("gh") == @"C:\Apps\GitHub.lnk",
                "PC-A merged both 'vs' and 'gh'");
            Check(pcB.GetTarget("vs") == @"C:\Apps\Code.lnk" && pcB.GetTarget("gh") == @"C:\Apps\GitHub.lnk",
                "PC-B merged both 'vs' and 'gh'");

            // 3. Same-word concurrent edit + HLC clock skew resilience (remote shard 10 min ahead)
            long futureTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000;
            File.WriteAllText(
                Path.Combine(qwDir, "PC-C.json"),
                $$$$"""{"Machine":"PC-C","Entries":{"shared":{"Target":"C:\\Future\\C.lnk","UpdatedAtUtcMs":{{{{futureTs}}}},"UpdatedBy":"PC-C"}}}""");

            pcA.ReloadFrom(root);
            Check(pcA.GetTarget("shared") == @"C:\Future\C.lnk", "Remote future shard overrides legacy 'shared'");
            Check(pcA.Set("shared", @"C:\Local\A.lnk"), "PC-A edits 'shared' after observing future shard");
            pcA.SaveTo(root);
            pcB.ReloadFrom(root);
            Check(pcB.GetTarget("shared") == @"C:\Local\A.lnk" && pcB.Entries["shared"].UpdatedAtUtcMs > futureTs,
                "HLC local edit strictly beats skewed remote clock");

            // 4. Delete tombstone beats older active entry (no zombie resurrection), re-add beats tombstone
            Check(pcB.Remove("vs"), "PC-B deletes 'vs'");
            pcB.SaveTo(root);
            pcA.ReloadFrom(root);
            Check(pcA.GetTarget("vs") is null && pcA.Entries["vs"].Deleted,
                "Tombstone suppresses 'vs' on PC-A without resurrection");

            Check(pcA.Set("vs", @"C:\Apps\CodeInsiders.lnk"), "PC-A re-adds 'vs'");
            pcA.SaveTo(root);
            pcB.ReloadFrom(root);
            Check(pcB.GetTarget("vs") == @"C:\Apps\CodeInsiders.lnk", "Re-add beats older tombstone");

            // 5. Rename tombstones old key and creates new key across machines
            Check(pcA.Rename("gh", "git"), "PC-A renames 'gh' -> 'git'");
            pcA.SaveTo(root);
            pcB.ReloadFrom(root);
            Check(pcB.GetTarget("gh") is null && pcB.GetTarget("git") == @"C:\Apps\GitHub.lnk",
                "Rename propagated cleanly to PC-B");

            // 6. Tombstone GC safety: expired tombstone kept while another shard still has active entry, pruned once gone
            long oldTs = 1_000_000;
            long laterTs = oldTs + 100;
            long afterRetention = laterTs + QuickWordStore.TombstoneRetentionMs + 1_000;
            string gcRoot = Path.Combine(root, "gc");
            string gcQwDir = Path.Combine(gcRoot, "QuickWords");
            Directory.CreateDirectory(gcQwDir);
            File.WriteAllText(
                Path.Combine(gcQwDir, "OLD-PC.json"),
                $$$$"""{"Machine":"OLD-PC","Entries":{"dead":{"Target":"C:\\Old.lnk","UpdatedAtUtcMs":{{{{oldTs}}}},"UpdatedBy":"OLD-PC"}}}""");
            File.WriteAllText(
                Path.Combine(gcQwDir, "PC-A.json"),
                $$$$"""{"Machine":"PC-A","Entries":{"dead":{"Target":"","UpdatedAtUtcMs":{{{{laterTs}}}},"UpdatedBy":"PC-A","Deleted":true}}}""");

            var gcStore = QuickWordStore.LoadFrom(gcRoot, "PC-A");
            gcStore.SaveTo(gcRoot, forceWrite: true, nowUtcMsOverride: afterRetention);
            Check(gcStore.Entries.ContainsKey("dead") && gcStore.Entries["dead"].Deleted,
                "Expired tombstone retained while OLD-PC shard still has active entry");

            File.Delete(Path.Combine(gcQwDir, "OLD-PC.json"));
            gcStore.SaveTo(gcRoot, forceWrite: true, nowUtcMsOverride: afterRetention);
            Check(!gcStore.Entries.ContainsKey("dead"),
                "Expired tombstone pruned once no other shard advertises active entry");

            // 7. ConfigWatcher detects remote shard in QuickWords/ subdir and ignores .tmp
            string watchRoot = Path.Combine(root, "watch");
            Directory.CreateDirectory(watchRoot);
            int qwChanges = 0;
            using (var watcher = new ConfigWatcher(kind =>
            {
                if (kind.HasFlag(ConfigChangeKind.QuickWords))
                    Interlocked.Increment(ref qwChanges);
            }, quietPeriodMs: 300))
            {
                watcher.Watch(watchRoot);
                Thread.Sleep(250);

                string watchQwDir = Path.Combine(watchRoot, "QuickWords");
                Directory.CreateDirectory(watchQwDir);
                File.WriteAllText(Path.Combine(watchQwDir, "REMOTE-PC.json"), """{"Machine":"REMOTE-PC","Entries":{}}""");
                Thread.Sleep(800);
                int afterRemote = Volatile.Read(ref qwChanges);

                File.WriteAllText(Path.Combine(watchQwDir, "REMOTE-PC.json.123.tmp"), "tmp");
                Thread.Sleep(700);
                int afterTmp = Volatile.Read(ref qwChanges);

                Check(afterRemote >= 1, $"ConfigWatcher fired on remote shard in QuickWords/ ({afterRemote})");
                Check(afterTmp == afterRemote, $"ConfigWatcher ignored .tmp in QuickWords/ ({afterTmp} == {afterRemote})");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }

        report.AppendLine();
        report.AppendLine(failures.Count == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures.Count} failures)");
        return report.ToString().TrimEnd();
    }
}
