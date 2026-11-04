using System;
using System.Collections.Generic;
using Urt2;

// 1.2.2: finished texts with filled-in [tokens] (quest letters kept in the save) and single-word grammar rules
public static class TemplatesTests
{
    static int pass, fail;

    static void Eq(string what, string got, string want)
    {
        if (got == want) { pass++; return; }
        fail++;
        Console.WriteLine($"FAIL templates {what}: got [{got}] expected [{want}]");
    }

    static void True(string what, bool b)
    {
        if (b) { pass++; return; }
        fail++;
        Console.WriteLine($"FAIL templates {what}");
    }

    public static int Run()
    {
        var t = new Templates();
        True("letter template added", t.Add("Intercepted communications indicate the abandoned lab has been scavenged by others. The quest [resolvedQuestName] has expired.",
            "Zachycená komunikace naznačuje, že opuštěnou laboratoř vykradli jiní. Úkol [resolvedQuestName] vypršel."));
        True("label template added", t.Add("Quest expired: [resolvedQuestName]", "Úkol vypršel: [resolvedQuestName]"));
        True("two tokens", t.Add("The caravan of [faction] arrived at [settlement] today.", "Karavana frakce [faction] dnes dorazila do [settlement]."));
        True("rule not a template", !t.Add("questName->[journey] the [adjectiveLab] [ruins]", "x [journey] [ruins]"));
        True("lost token not a template", !t.Add("Something long happened to [PAWN_nameDef] today.", "Něco se dnes stalo."));
        True("starting with a token not a template", !t.Add("[PAWN_nameDef] has finished building something nice.", "[PAWN_nameDef] dokončil stavbu."));

        var parts = new Dictionary<string, string> { { "Exploration of the Dilapidated lab", "Průzkum zchátralé laboratoře" } };
        Func<string, string> part = s => parts.TryGetValue(s, out string r) ? r : null;

        Eq("letter with English name", t.Fill("Intercepted communications indicate the abandoned lab has been scavenged by others. The quest Exploration of the Dilapidated lab has expired.", part),
            "Zachycená komunikace naznačuje, že opuštěnou laboratoř vykradli jiní. Úkol Průzkum zchátralé laboratoře vypršel.");
        Eq("letter with half-Czech name kept", t.Fill("Intercepted communications indicate the abandoned lab has been scavenged by others. The quest Průzkum Dilapidated laboratoř has expired.", part),
            "Zachycená komunikace naznačuje, že opuštěnou laboratoř vykradli jiní. Úkol Průzkum Dilapidated laboratoř vypršel.");
        Eq("label", t.Fill("Quest expired: Exploration of the Dilapidated lab", part), "Úkol vypršel: Průzkum zchátralé laboratoře");
        Eq("two tokens", t.Fill("The caravan of Red Hawks arrived at Northfort today.", part), "Karavana frakce Red Hawks dnes dorazila do Northfort.");
        Eq("no match", t.Fill("Intercepted communications indicate something else entirely.", part), null);
        Eq("short text", t.Fill("Quest", part), null);

        // grammar rules are judged by their text, not by the keyword
        True("rule single word is text", !Protect.LooksLikeNonText("adjectiveLab->Dilapidated"));
        True("rule underscore keyword is text", !Protect.LooksLikeNonText("r_name->Bandit"));
        True("rule with tokens only is not text", Protect.LooksLikeNonText("questName->[journey] [ruins]"));
        True("def name still not text", Protect.LooksLikeNonText("Some_DefName"));
        True("CamelCase still not text", Protect.LooksLikeNonText("AdjectiveLab"));
        True("rule with sentence is text", !Protect.LooksLikeNonText("questName(priority=1)->[journey] the [adjectiveLab] [ruins]"));

        Console.WriteLine($"URT template tests: {pass} passed, {fail} failed");
        return fail;
    }
}
