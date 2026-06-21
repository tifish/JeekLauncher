using System.Diagnostics;
using System.Text;
using System.Threading;
using Jeek.Avalonia.Localization;
using JeekLauncher.Services;

namespace JeekLauncher;

/// <summary>
/// Headless diagnostics (JeekLauncher.exe --selftest [all|search|watch|autostart]).
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
        foreach (var e in entries.Where(e => HasCjk(e.Name)).Take(20))
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

        bool before = AutoStart.IsEnabled();
        AutoStart.SetEnabled(true);
        bool afterOn = AutoStart.IsEnabled();
        AutoStart.SetEnabled(false);
        bool afterOff = AutoStart.IsEnabled();

        report.AppendLine($"initial   : {before}");
        report.AppendLine($"after on  : {afterOn}   (expect True)");
        report.AppendLine($"after off : {afterOff}  (expect False)");
        report.AppendLine();
        report.AppendLine(afterOn && !afterOff ? "RESULT: PASS" : "RESULT: FAIL");

        return report.ToString().TrimEnd();
    }

    // ---- Localization ------------------------------------------------------

    private static string BuildLangReport()
    {
        var report = new StringBuilder();

        Localizer.SetLocalizer(new TabLocalizer(
            Path.Combine(AppContext.BaseDirectory, "Data", "Languages.tab")));

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

    private static bool HasCjk(string s)
    {
        foreach (char c in s)
        {
            if (c is >= '一' and <= '鿿')
                return true;
        }
        return false;
    }
}
