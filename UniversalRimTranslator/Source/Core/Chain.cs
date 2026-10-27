using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Urt2
{
    // One text to translate and its outcome.
    public sealed class Job
    {
        public string Source;            // original text (may contain several lines, tokens, a rule keyword)
        public string SourceLang;        // null = detect
        public string Result;            // finished translation (null = failed)
        public string Provider;          // who translated it
        public Verdict Verdict = Verdict.Empty;
        public string Error;
        public string Avoid;             // 2.4.1: a service not to use ("translate again" wants a different result)
        public string Hint;              // 2.5.2: short context for AI services (kind of text, mod, glossary words)
    }

    // Translates jobs with the chain of services: the first service that is configured, not
    // cooling down and supports the language pair gets the batch; what fails (or fails the check)
    // goes to the next one. Services that rate-limit or block pause themselves for a while.
    public sealed class Chain
    {
        public List<string> Order = new List<string> { "edge", "yandex", "google", "mymemory", "lingva" };
        public ProviderConfig Config = new ProviderConfig();
        public Action<string> Log = s => { };
        public string Notes;             // 2.5.2: extra instructions for AI services (the player's preferred words)

        private readonly Dictionary<string, DateTime> pausedUntil = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, int> strikes = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> lastCall = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, string> lastError = new Dictionary<string, string>();
        private readonly Dictionary<string, int> doneCount = new Dictionary<string, int>();
        // 2.6.0: a service that rejected its key (HTTP 401/403) is skipped for the rest of the
        // session – until its settings change (the value is a fingerprint of the settings)
        private readonly Dictionary<string, string> authFailed = new Dictionary<string, string>();
        private readonly object gate = new object();
        public volatile string LastProvider;    // 2.6.0: the service that answered last (for the progress line)

        private string Fingerprint()
        {
            ProviderConfig c = Config;
            if (c == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var f in typeof(ProviderConfig).GetFields())
                sb.Append(f.GetValue(c)).Append('\u0001');
            return sb.ToString();
        }

        public bool AuthSkipped(string id)
        {
            lock (gate) return authFailed.TryGetValue(id, out string fp) && fp == Fingerprint();
        }

        public sealed class Problem
        {
            public string Id, Name, Message;
            public bool KeyRejected;
            public int Seconds;
        }

        // 2.6.0: services that do not work right now (key rejected or paused) – for the diagnostics line
        public List<Problem> Problems()
        {
            var l = new List<Problem>();
            foreach (string id in Order)
            {
                Provider p = Providers.Get(id);
                if (p == null || !p.Configured(Config)) continue;
                lock (gate)
                {
                    lastError.TryGetValue(id, out string e);
                    if (authFailed.TryGetValue(id, out string fp) && fp == Fingerprint())
                        l.Add(new Problem { Id = id, Name = p.Name, Message = e, KeyRejected = true });
                    else if (pausedUntil.TryGetValue(id, out DateTime t) && t > DateTime.UtcNow)
                        l.Add(new Problem { Id = id, Name = p.Name, Message = e, Seconds = (int)Math.Ceiling((t - DateTime.UtcNow).TotalSeconds) });
                }
            }
            return l;
        }

        // 2.6.0: is there at least one service that can be asked now
        public bool AnyAvailable()
        {
            foreach (string id in Order)
            {
                Provider p = Providers.Get(id);
                if (p != null && p.Configured(Config) && !Paused(id) && !AuthSkipped(id)) return true;
            }
            return false;
        }

        public string Status(string id)
        {
            lock (gate)
            {
                if (authFailed.TryGetValue(id, out string afp) && afp == Fingerprint())
                    return "key rejected – skipped this session" + (lastError.TryGetValue(id, out string ae) ? " – " + ae : "");
                if (pausedUntil.TryGetValue(id, out DateTime t) && t > DateTime.UtcNow)
                    return "paused " + Math.Ceiling((t - DateTime.UtcNow).TotalSeconds) + " s" + (lastError.TryGetValue(id, out string e) ? " – " + e : "");
                return doneCount.TryGetValue(id, out int n) ? "ok, " + n + " texts" : "ready";
            }
        }

        public int Done(string id) { lock (gate) return doneCount.TryGetValue(id, out int n) ? n : 0; }

        private bool Paused(string id)
        {
            lock (gate) return pausedUntil.TryGetValue(id, out DateTime t) && t > DateTime.UtcNow;
        }

        private void Pause(string id, ProviderError e)
        {
            lock (gate)
            {
                strikes.TryGetValue(id, out int s);
                s++;
                strikes[id] = s;
                int sec;
                switch (e.Kind)
                {
                    case FailKind.Auth: sec = 3600; authFailed[id] = Fingerprint(); break;   // wrong key – skipped this session (2.6.0)
                    case FailKind.Blocked: sec = Math.Max(e.RetryAfterSeconds, 300 * s); break;
                    case FailKind.RateLimit: sec = Math.Max(e.RetryAfterSeconds, Math.Min(30 * (1 << Math.Min(s, 6)), 1800)); break;
                    case FailKind.Unsupported: sec = 0; break;
                    default: sec = Math.Min(10 * s, 300); break;
                }
                if (sec > 0) pausedUntil[id] = DateTime.UtcNow.AddSeconds(sec);
                lastError[id] = e.Message;
            }
        }

        private void Success(string id, int n)
        {
            lock (gate)
            {
                strikes[id] = 0;
                doneCount.TryGetValue(id, out int d);
                doneCount[id] = d + n;
            }
        }

        public void ClearPauses() { lock (gate) { pausedUntil.Clear(); strikes.Clear(); } }

        private void Throttle(Provider p)
        {
            DateTime last;
            lock (gate) lastCall.TryGetValue(p.Id, out last);
            double wait = p.MinIntervalSeconds - (DateTime.UtcNow - last).TotalSeconds;
            if (wait > 0) Thread.Sleep((int)(wait * 1000));
            lock (gate) lastCall[p.Id] = DateTime.UtcNow;
        }

        // Translates all jobs into the target language. Returns when every job has a result or
        // every service was tried.
        public void Run(List<Job> jobs, string targetIso, CancellationToken ct)
        {
            // split every job into lines; lines are the units sent to the services
            var units = new List<Unit>();
            foreach (Job j in jobs)
            {
                j.Result = null; j.Error = null; j.Provider = null;
                var lines = Protect.Lines(j.Source ?? "");
                var parts = new List<Unit>();
                foreach (string line in lines)
                {
                    var u = new Unit { Job = j, Masked = Protect.Mask(line), Original = line };
                    if (!string.IsNullOrEmpty(j.Avoid)) u.Tried.Add(j.Avoid);
                    if (u.Masked.Empty || Protect.LooksLikeNonText(line)) u.Done = line;   // nothing to translate in this line
                    parts.Add(u);
                    // gender/plural options are translated as their own little texts
                    foreach (var kv in u.Masked.Choices)
                        for (int k = 0; k < kv.Value.Length; k++)
                        {
                            var o = new Unit { Job = j, Masked = Protect.Mask(kv.Value[k]), Original = kv.Value[k], Owner = u, ChoiceToken = kv.Key, ChoiceIndex = k };
                            if (!string.IsNullOrEmpty(j.Avoid)) o.Tried.Add(j.Avoid);
                            if (o.Masked.Empty) o.Done = kv.Value[k];
                            u.Options.Add(o);
                            if (o.Done == null) units.Add(o);
                        }
                }
                j.SetUnits(parts);
                units.AddRange(parts.Where(x => x.Done == null));
            }

            foreach (string id in Order)
            {
                if (ct.IsCancellationRequested) break;
                Provider p = Providers.Get(id);
                if (p == null || !p.Configured(Config)) continue;
                if (AuthSkipped(id)) continue;     // 2.6.0: key rejected this session
                var todo = units.Where(u => u.Done == null && !u.Tried.Contains(id)).ToList();
                if (todo.Count == 0) break;
                foreach (var group in todo.GroupBy(u => u.Job.SourceLang ?? ""))
                {
                    string src = group.Key.Length == 0 ? null : group.Key;
                    foreach (List<Unit> batch in Batches(group.ToList(), p))
                    {
                        if (ct.IsCancellationRequested || Paused(id)) break;
                        foreach (Unit u in batch) u.Tried.Add(id);
                        List<string> got;
                        try
                        {
                            Throttle(p);
                            // 2.5.2: AI services get the context of every text (other services ignore it)
                            var ctx = p.IsAi ? new TranslateContext { Hints = batch.Select(u => u.Job.Hint).ToList(), Notes = Notes } : null;
                            got = p.Translate(batch.Select(u => u.Masked.Text).ToList(), src, targetIso, Config, ct, ctx);
                        }
                        catch (ProviderError e)
                        {
                            Pause(id, e);
                            Log(e.Message);
                            if (e.Kind != FailKind.Unsupported) break;
                            continue;
                        }
                        catch (OperationCanceledException) { return; }
                        catch (Exception e)
                        {
                            Pause(id, new ProviderError(FailKind.Network, p.Name + ": " + e.Message));
                            Log(p.Name + ": " + e);
                            break;
                        }
                        int ok = 0;
                        for (int i = 0; i < batch.Count; i++)
                        {
                            if (batch[i].Owner != null)
                            {
                                string opt = i < got.Count ? got[i] : null;
                                string optText = opt == null ? null : Protect.Unmask(batch[i].Masked, opt);
                                if (optText != null) { batch[i].Done = optText; ok++; }
                                continue;
                            }
                            // a line with gender options needs its options first
                            if (batch[i].Options.Any(o => o.Done == null)) { batch[i].Tried.Remove(id); batch[i].Pending = got != null && i < got.Count ? got[i] : null; batch[i].PendingBy = p.Id; continue; }
                            FillChoices(batch[i]);
                            string back = i < got.Count ? got[i] : null;
                            string text = back == null ? null : Protect.Unmask(batch[i].Masked, back);
                            if (text == null) { batch[i].LastError = p.Name + ": placeholders broken"; continue; }
                            if (Validate.Check(batch[i].Original, text, targetIso) == Verdict.ServiceError) { batch[i].LastError = p.Name + ": error text"; continue; }
                            batch[i].Done = text;
                            batch[i].By = p.Id;
                            ok++;
                        }
                        if (ok > 0) { Success(id, ok); LastProvider = p.Name; }
                    }
                }
            }

            FinishPending(units, targetIso);
            foreach (Job j in jobs) j.Finish(targetIso);
        }

        private static void FillChoices(Unit u)
        {
            foreach (var grp in u.Options.GroupBy(o => o.ChoiceToken))
                u.Masked.ChoicesDone[grp.Key] = grp.OrderBy(o => o.ChoiceIndex).Select(o => o.Done).ToArray();
        }

        // lines whose answer came before their gender options: finish them now
        private void FinishPending(List<Unit> units, string targetIso)
        {
            foreach (Unit u in units)
            {
                if (u.Done != null || u.Owner != null || u.Pending == null || u.Options.Any(o => o.Done == null)) continue;
                FillChoices(u);
                string text = Protect.Unmask(u.Masked, u.Pending);
                if (text != null) { u.Done = text; u.By = u.PendingBy; }
            }
        }

        private static IEnumerable<List<Unit>> Batches(List<Unit> units, Provider p)
        {
            var cur = new List<Unit>();
            int chars = 0;
            foreach (Unit u in units)
            {
                int len = u.Masked.Text.Length;
                if (len > p.MaxChars) { u.LastError = p.Name + ": text too long"; continue; }
                if (cur.Count > 0 && (cur.Count >= p.MaxItems || chars + len > p.MaxChars))
                {
                    yield return cur;
                    cur = new List<Unit>();
                    chars = 0;
                }
                cur.Add(u);
                chars += len;
            }
            if (cur.Count > 0) yield return cur;
        }
    }

    internal sealed class Unit
    {
        public Job Job;
        public Masked Masked;
        public string Original;
        public string Done;
        public string By;
        public string LastError;
        public readonly HashSet<string> Tried = new HashSet<string>();
        public Unit Owner;                 // set for a gender/plural option
        public int ChoiceToken, ChoiceIndex;
        public readonly List<Unit> Options = new List<Unit>();
        public string Pending, PendingBy;  // answer kept until the options are done
    }

    internal static class JobExt
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Job, List<Unit>> parts = new System.Runtime.CompilerServices.ConditionalWeakTable<Job, List<Unit>>();

        public static void SetUnits(this Job j, List<Unit> u)
        {
            parts.Remove(j);
            parts.Add(j, u);
        }

        public static void Finish(this Job j, string targetIso)
        {
            if (!parts.TryGetValue(j, out List<Unit> u)) return;
            if (u.Any(x => x.Done == null))
            {
                j.Error = u.Where(x => x.Done == null).Select(x => x.LastError).FirstOrDefault(e => e != null) ?? "no service could translate it";
                j.Verdict = Verdict.Empty;
                return;
            }
            j.Result = Protect.Join(u.Select(x => x.Done).ToList());
            j.Provider = u.Select(x => x.By).FirstOrDefault(b => b != null);
            j.Verdict = Validate.Check(j.Source, j.Result, targetIso);
            parts.Remove(j);
        }
    }
}
