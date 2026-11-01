using System;
using System.Collections.Generic;
using Urt2;

// Tests of the URT quality check (Validate.Check) in many target languages.
// Every language gets: a good translation (must pass), a cut-off one, an untranslated one,
// one in a wrong writing system, a lost {token} (all must be rejected), and a short label that
// keeps an English mod name (must pass). Bolek98 cannot read most of these languages, so this is
// how a check that only works for Czech (like the length check before 1.1.1) gets caught early.
public static class ValidateTests
{
    const string Long = "Allows your colonists to store items on neural racks which keep them fresh for a long time.";
    const string Tok = "{0} has finished building {1}.";
    const string Label = "Neural Racks storage shelf";

    // iso, long translation, token translation, label translation (keeps the English name)
    static readonly string[][] Langs =
    {
        new[] { "cs", "Umožňuje kolonistům ukládat věci na neurální regály, které je dlouho udrží čerstvé.", "{0} dokončil stavbu {1}.", "Úložná police Neural Racks" },
        new[] { "de", "Ermöglicht deinen Kolonisten, Gegenstände in neuronalen Regalen zu lagern, die sie lange frisch halten.", "{0} hat den Bau von {1} abgeschlossen.", "Lagerregal von Neural Racks" },
        new[] { "fr", "Permet à vos colons de ranger des objets sur des étagères neuronales qui les gardent frais longtemps.", "{0} a terminé la construction de {1}.", "Étagère de stockage Neural Racks" },
        new[] { "es", "Permite a tus colonos guardar objetos en estantes neuronales que los mantienen frescos durante mucho tiempo.", "{0} ha terminado de construir {1}.", "Estante de almacenaje Neural Racks" },
        new[] { "it", "Permette ai tuoi coloni di riporre oggetti su scaffali neurali che li mantengono freschi a lungo.", "{0} ha finito di costruire {1}.", "Scaffale di deposito Neural Racks" },
        new[] { "pl", "Pozwala twoim kolonistom przechowywać przedmioty na regałach neuronowych, które długo utrzymują je w świeżości.", "{0} ukończył budowę {1}.", "Regał magazynowy Neural Racks" },
        new[] { "pt-BR", "Permite que seus colonos guardem itens em prateleiras neurais que os mantêm frescos por muito tempo.", "{0} terminou de construir {1}.", "Prateleira de armazenagem Neural Racks" },
        new[] { "tr", "Kolonistlerinin eşyaları uzun süre taze tutan nöral raflarda saklamasını sağlar.", "{0}, {1} inşasını tamamladı.", "Neural Racks depolama rafı" },
        new[] { "ru", "Позволяет колонистам хранить предметы на нейронных стеллажах, которые долго сохраняют их свежими.", "{0} завершил строительство {1}.", "Стеллаж хранения Neural Racks" },
        new[] { "uk", "Дозволяє колоністам зберігати предмети на нейронних стелажах, які довго зберігають їх свіжими.", "{0} завершив будівництво {1}.", "Стелаж зберігання Neural Racks" },
        new[] { "zh-Hans", "允许你的殖民者将物品存放在神经架上，可长时间保鲜。", "{0} 已完成建造 {1}。", "Neural Racks 储物架" },
        new[] { "zh-Hant", "允許你的殖民者將物品存放在神經架上，可長時間保鮮。", "{0} 已完成建造 {1}。", "Neural Racks 儲物架" },
        new[] { "ja", "入植者がアイテムをニューラルラックに保管でき、長期間新鮮に保てます。", "{0}が{1}の建設を完了しました。", "Neural Racks 保管棚" },
        new[] { "ko", "정착민이 신경 선반에 물건을 보관할 수 있으며, 오랫동안 신선하게 유지됩니다.", "{0}이(가) {1} 건설을 완료했습니다.", "Neural Racks 보관 선반" },
        new[] { "ar", "يسمح لمستعمريك بتخزين الأغراض على رفوف عصبية تحافظ عليها طازجة لفترة طويلة.", "أنهى {0} بناء {1}.", "رف تخزين Neural Racks" },
        new[] { "th", "ช่วยให้ชาวอาณานิคมเก็บของไว้บนชั้นวางประสาทซึ่งรักษาความสดได้นาน", "{0} สร้าง {1} เสร็จแล้ว", "ชั้นเก็บของ Neural Racks" },
    };

    static int pass, fail;

    static void Expect(string iso, string what, string src, string tr, Func<Verdict, bool> ok, string want)
    {
        Verdict v;
        try { v = Validate.Check(src, tr, iso); }
        catch (Exception e) { fail++; Console.WriteLine($"FAIL {iso,-8} {what}: crashed – {e.GetType().Name}: {e.Message}"); return; }
        if (ok(v)) { pass++; return; }
        fail++;
        Console.WriteLine($"FAIL {iso,-8} {what}: got {v}, expected {want}   [{tr}]");
    }

    // a text in a writing system that is wrong for this target
    static string Foreign(string iso)
    {
        Lang.Script s = Lang.ScriptOf(iso);
        if (s == Lang.Script.Cyrillic) return "Umožňuje kolonistům ukládat věci na neurální regály, které je dlouho udrží čerstvé.";
        return "Позволяет колонистам хранить предметы на нейронных стеллажах, которые долго сохраняют их свежими.";
    }

    // the first fifth of the text – what a cut-off answer looks like
    static string Cut(string s)
    {
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        var parts = new List<string>();
        while (e.MoveNext()) parts.Add((string)e.Current);
        return string.Concat(parts.GetRange(0, Math.Max(1, parts.Count / 5)));
    }

    public static int Main()
    {
        foreach (string[] l in Langs)
        {
            string iso = l[0];
            Expect(iso, "good translation", Long, l[1], v => v == Verdict.Ok, "Ok");
            Expect(iso, "good with {tokens}", Tok, l[2], v => v == Verdict.Ok, "Ok");
            Expect(iso, "label keeping an English name", Label, l[3], v => v == Verdict.Ok, "Ok");
            Expect(iso, "cut off", Long, Cut(l[1]), v => v != Verdict.Ok, "rejected");
            Expect(iso, "left in English", Long, Long, v => v != Verdict.Ok, "rejected");
            Expect(iso, "English with one target letter", Long, Long + " " + Cut(l[3].Replace("Neural Racks", "").Trim()), v => v != Verdict.Ok, "rejected");
            Expect(iso, "wrong writing system", Long, Foreign(iso), v => v != Verdict.Ok, "rejected");
            Expect(iso, "lost {1}", Tok, l[2].Replace("{1}", ""), v => v == Verdict.BrokenTokens, "BrokenTokens");
            Expect(iso, "service error text", Long, "Error 429: Too Many Requests", v => v != Verdict.Ok, "rejected");
            Expect(iso, "empty", Long, "  ", v => v == Verdict.Empty, "Empty");
        }
        Console.WriteLine($"URT check tests: {pass} passed, {fail} failed ({Langs.Length} languages)");
        return fail == 0 ? 0 : 1;
    }
}
