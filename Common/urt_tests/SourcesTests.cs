using System;
using System.IO;
using Urt2;

// 1.2.0: the game-free parts of "translations from other mods": language folder names, LoadFolders.xml,
// URL → zip, which zip entries are taken and the zip-slip check.
public static class SourcesTests
{
    static int pass, fail;
    static void Ok(bool c, string what) { if (c) pass++; else { fail++; Console.WriteLine("FAIL sources: " + what); } }

    public static int Run()
    {
        Ok(SourcesLogic.LangDirMatches("ChineseSimplified", "ChineseSimplified", null), "same name");
        Ok(SourcesLogic.LangDirMatches("ChineseSimplified (简体中文)", "ChineseSimplified", null), "name with native part");
        Ok(SourcesLogic.LangDirMatches("chinesesimplified", "ChineseSimplified (简体中文)", null), "case and native part on the game side");
        Ok(SourcesLogic.LangDirMatches("Czech (Čeština)", "Czech", "Czech (Čeština)"), "legacy name");
        Ok(!SourcesLogic.LangDirMatches("ChineseTraditional", "ChineseSimplified", null), "other Chinese");
        Ok(!SourcesLogic.LangDirMatches("English", "Czech", null), "other language");

        string lf = "<loadFolders><v1.6><li>/</li><li>1.6</li></v1.6><v1.7><li>/</li><li>1.7</li><li IfModActive=\"a.b\">Mods/AB</li><li IfModNotActive=\"c.d\">NoCD</li></v1.7></loadFolders>";
        var f = SourcesLogic.LoadFolders(lf, "1.7", new[] { "1.6", "1.7" }, id => id == "a.b" || id == "c.d");
        Ok(f.Count == 3 && f[0] == "" && f[1] == "1.7" && f[2] == "Mods/AB", "LoadFolders 1.7 with conditions: " + string.Join("|", f));
        var g = SourcesLogic.LoadFolders(lf, "1.8", new string[0], id => false);
        Ok(g.Contains("1.7") && !g.Contains("Mods/AB") && g.Contains("NoCD"), "LoadFolders newest older version");
        var h = SourcesLogic.LoadFolders(null, "1.7", new[] { "1.5", "1.6", "Common" }, null);
        Ok(h.Count == 3 && h[1] == "Common" && h[2] == "1.6", "default folders fall back to 1.6: " + string.Join("|", h));
        var bad = SourcesLogic.LoadFolders("<not xml", "1.7", new[] { "1.7" }, null);
        Ok(bad.Contains("1.7"), "broken LoadFolders.xml");

        string n, e;
        Ok(SourcesLogic.ToDownloadUrl("https://github.com/Owner/Repo", out n, out e) == "https://api.github.com/repos/Owner/Repo/zipball" && n == "Owner_Repo", "GitHub repo");
        Ok(SourcesLogic.ToDownloadUrl("https://github.com/o/r.git", out n, out e) == "https://api.github.com/repos/o/r/zipball", "GitHub .git");
        Ok(SourcesLogic.ToDownloadUrl("https://github.com/o/r/tree/dev", out n, out e) == "https://codeload.github.com/o/r/zip/refs/heads/dev", "GitHub branch");
        Ok(SourcesLogic.ToDownloadUrl("https://example.com/packs/cn.zip", out n, out e) == "https://example.com/packs/cn.zip" && n == "example.com_cn", "plain zip");
        Ok(SourcesLogic.ToDownloadUrl("https://steamcommunity.com/sharedfiles/filedetails/?id=1", out n, out e) == null, "Steam refused");
        Ok(SourcesLogic.ToDownloadUrl("ftp://x/y.zip", out n, out e) == null, "ftp refused");
        Ok(SourcesLogic.ToDownloadUrl("", out n, out e) == null, "empty refused");

        Ok(SourcesLogic.WantedEntry("Languages/ChineseSimplified/Keyed/a.xml"), "keyed file");
        Ok(SourcesLogic.WantedEntry("1.6/Languages/ChineseSimplified/DefInjected/ThingDef/a.xml"), "versioned def file");
        Ok(SourcesLogic.WantedEntry("About/About.xml"), "About");
        Ok(!SourcesLogic.WantedEntry("Assemblies/evil.dll"), "no dll");
        Ok(!SourcesLogic.WantedEntry("Languages/x/run.bat"), "no scripts");
        Ok(!SourcesLogic.WantedEntry("Defs/a.xml"), "no defs");

        string root = Path.Combine(Path.GetTempPath(), "urtlib");
        Ok(SourcesLogic.SafeTarget(root, "Languages/a.xml") != null, "normal target");
        Ok(SourcesLogic.SafeTarget(root, "../x.xml") == null, "zip slip ..");
        Ok(SourcesLogic.SafeTarget(root, "Languages/../../x.xml") == null, "zip slip inside");
        Ok(SourcesLogic.SafeTarget(root, "/etc/passwd") == null, "absolute");
        Ok(SourcesLogic.SafeTarget(root, "C:/Windows/x.xml") == null, "drive letter");

        Ok(SourcesLogic.CommonTop(new[] { "repo-main/About/About.xml", "repo-main/Languages/x.xml" }) == "repo-main/", "common top");
        Ok(SourcesLogic.CommonTop(new[] { "About/About.xml", "Languages/x.xml" }) == "", "no common top");
        Ok(SourcesLogic.CommonTop(new[] { "file.xml" }) == "", "root file");

        Console.WriteLine($"URT sources tests: {pass} passed, {fail} failed");
        return fail;
    }
}
