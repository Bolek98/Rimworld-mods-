using System;
using System.Collections.Generic;
using Urt2;

// 1.2.1: texts already (partly) in the target language, names, and Chinese/Japanese brackets
public static class MixedTests
{
    static int pass, fail;

    static void Expect(string what, string src, string tr, string iso, Func<Verdict, bool> ok, string want)
    {
        Verdict v = Validate.Check(src, tr, iso);
        if (ok(v)) { pass++; return; }
        fail++;
        Console.WriteLine($"FAIL mixed {what}: got {v}, expected {want}   [{src}] -> [{tr}]");
    }

    // a small stand-in for the game's English words (names and Czech words are not in it)
    static readonly HashSet<string> English = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "replicate", "specialty", "food", "staff", "clothing", "race", "envenom", "tech", "weapons", "space", "steel", "wall" };

    static void All(bool withWords)
    {
        string tag = withWords ? " (game words known)" : " (no word list)";
        Expect("already Czech" + tag, "oblak toxického prachu", "oblak toxického prachu", "cs", v => v == Verdict.Ok, "Ok");
        Expect("already Czech, longer" + tag, "Umožňuje kolonistům ukládat věci na regály", "Umožňuje kolonistům ukládat věci na regály", "cs", v => v == Verdict.Ok, "Ok");
        Expect("mixed, English part translated" + tag, "Replicate Zdravotní péče", "Replikovat Zdravotní péče", "cs", v => v == Verdict.Ok, "Ok");
        Expect("mixed, 4 words, one translated" + tag, "Replicate Syrové erotické jídlo", "Replikujte Syrové erotické jídlo", "cs", v => v == Verdict.Ok, "Ok");
        Expect("English label translated" + tag, "Wolfein Specialty Food", "Speciální jídlo Wolfein", "cs", v => v == Verdict.Ok, "Ok");
        Expect("English label left in English" + tag, "Wolfein Specialty Food", "Wolfein Specialty Food", "cs", v => v != Verdict.Ok, "rejected");
        Expect("German already German" + tag, "Lagerregal für Waffen", "Lagerregal für Waffen", "de", v => v == Verdict.Ok, "Ok");
        Expect("brackets 【】 → [ ]" + tag, "【カルミデスB型】封入カプセル", "[Calmides typ B] Kapsle", "cs", v => v == Verdict.Ok, "Ok");
        Expect("brackets 【】 Chinese" + tag, "这是一场【试炼】", "Toto je [soud]", "cs", v => v == Verdict.Ok, "Ok");
        Expect("real token still checked" + tag, "{0} built [PAWN_nameDef]'s wall.", "{0} postavil zeď.", "cs", v => v == Verdict.BrokenTokens, "BrokenTokens");
        Expect("invented [token] without wide brackets" + tag, "Steel wall", "[Ocelová] zeď", "cs", v => v == Verdict.BrokenTokens, "BrokenTokens");
    }

    public static int Run()
    {
        Validate.EnglishWord = null;
        All(false);
        Validate.EnglishWord = w => English.Contains(w);
        All(true);
        // with the game's words a mixed text that still has its English word is sent again
        Expect("mixed, English word left (game words known)", "Replicate Zdravotní péče", "Replicate Zdravotní péče", "cs", v => v == Verdict.Untranslated, "Untranslated");
        Expect("name + Czech, nothing English (game words known)", "Chaoura: Zbraně (vesmírná)", "Chaoura: Zbraně (vesmírná)", "cs", v => v == Verdict.Ok, "Ok");
        Validate.EnglishWord = null;
        Console.WriteLine($"URT mixed-text tests: {pass} passed, {fail} failed");
        return fail;
    }
}
