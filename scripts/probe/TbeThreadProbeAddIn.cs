// TbeThreadProbeAddIn.cs
// Stand-alone, minimal EPLAN 2.9 add-in to decide the A/B loading strategy for
// TextBatchEdit large-selection load (see
// docs/design/text-batch-edit/large-load-virtualmode.md, section 11).
//
// WHAT IT DOES
//   Registers a menu item "TBE Thread Probe". With texts selected in the GED,
//   click it, then press [Run] in the probe window. It reads the selected
//   TextBase objects' Contents / per-language strings / page / structure
//   properties / coordinates:
//     (1) on the EPLAN main (UI) thread,
//     (2) on a Task.Run background thread,
//     (3) on a background thread that hops back via EplanMainThreadDispatcher
//         .ExecuteInMainThreadSync,
//   repeats the background read 3 rounds, cross-checks sampled values, keeps a
//   ticking clock (message-pump liveness) and writes everything to
//   %TEMP%\TbeThreadProbe.log.
//
// DECISION RULE
//   Background reads: no exception, values identical to UI-thread reads,
//   CanAccessMainThread()==False on worker, 3 rounds stable, EPLAN stays
//   usable while it runs  -> background read is technically possible on THIS
//   machine (still OUT OF SUPPORT per official docs; xavier decides).
//   Anything else         -> use plan B (UI-thread chunked reads only).
//
// SAFE: READ-ONLY. It never sets Contents, never locks the project/selection,
// never modifies settings. Unregister/remove the DLL when done.
//
// Build/deploy: see build-probe.ps1 next to this file.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.Base.Internal;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.Graphics;
using Eplan.EplApi.Gui;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.Scripting;
using Label = System.Windows.Forms.Label;

namespace TbeThreadProbe
{
    public class TbeThreadProbeAddIn : IEplAddIn
    {
        public bool OnInit() { return true; }
        public bool OnExit() { return true; }
        public bool OnRegister(ref bool bLoadOnStart) { bLoadOnStart = true; return true; }
        public bool OnUnregister() { return true; }
        public bool OnInitGui()
        {
            try { new Eplan.EplApi.Gui.Menu().AddMenuItem("TBE Thread Probe", ProbeAction.ActionName); }
            catch (Exception ex) { Log("OnInitGui menu failed: " + ex); }
            return true;
        }

        internal static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "TbeThreadProbe.log");

        internal static void Log(string msg)
        {
            var line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" +
                       Thread.CurrentThread.ManagedThreadId + "] " + msg;
            try { File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* logging must never crash the probe */ }
            Debug.WriteLine(line);
        }
    }

    public class ProbeAction : IEplAction
    {
        public const string ActionName = "TbeThreadProbeAction";

        [DeclareAction(ActionName)]
        public bool Execute(ActionCallingContext ctx)
        {
            try
            {
                var f = new ProbeForm();
                f.Show();
                return true;
            }
            catch (Exception ex)
            {
                TbeThreadProbeAddIn.Log("Execute failed: " + ex);
                MessageBox.Show(ex.ToString(), "TBE Thread Probe");
                return false;
            }
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool OnRegister(ref string Name, ref int Ordinal) { return true; }
    }

    // One pure-data snapshot per TextBase: only strings/doubles/bools cross the
    // thread boundary, never EPLAN objects (we only touch t.* inside the read
    // routine, whichever thread that runs on).
    internal sealed class RowSnap
    {
        public long Dbid;
        public bool Valid;
        public bool AutoTrans;
        public string Langs = "";
        public string Strings = "";   // lang=value joined
        public string Page = "";
        public string Plant = "";
        public string Place = "";
        public string Location = "";
        public double X, Y;
        public string Error = "";

        public string CompareKey()
        {
            return Dbid + "|" + Valid + "|" + AutoTrans + "|" + Langs + "|" +
                   Strings + "|" + Page + "|" + Plant + "|" + Place + "|" +
                   Location + "|" + X.ToString("0.###") + "|" + Y.ToString("0.###");
        }
    }

    public class ProbeForm : Form
    {
        private readonly Button _btnRun;
        private readonly Button _btnClose;
        private readonly Label _lblClock;
        private readonly Label _lblHint;
        private readonly TextBox _txt;
        private readonly System.Windows.Forms.Timer _timer;
        private int _ticks;

        public ProbeForm()
        {
            Text = "TBE thread probe (read-only)";
            Width = 760; Height = 560;
            StartPosition = FormStartPosition.CenterScreen;

            _lblHint = new Label
            {
                Left = 10, Top = 8, Width = 730, Height = 34,
                Text = "1) Select text objects in the GED (5000-10000 recommended).  " +
                       "2) press Run. 3) while it runs, click EPLAN menus / switch pages. " +
                       "Log: %TEMP%\\TbeThreadProbe.log"
            };
            _btnRun = new Button { Left = 10, Top = 44, Width = 100, Text = "Run" };
            _btnClose = new Button { Left = 120, Top = 44, Width = 100, Text = "Close" };
            _lblClock = new Label { Left = 240, Top = 48, Width = 200, Text = "pump tick: 0" };
            _txt = new TextBox
            {
                Left = 10, Top = 78, Width = 725, Height = 440,
                Multiline = true, ScrollBars = ScrollBars.Both,
                ReadOnly = true, Font = new System.Drawing.Font("Consolas", 9f),
                WordWrap = false
            };

            Controls.AddRange(new Control[] { _lblHint, _btnRun, _btnClose, _lblClock, _txt });
            _btnRun.Click += async (object s, EventArgs e) => await RunAsync();
            _btnClose.Click += delegate { Close(); };

            _timer = new System.Windows.Forms.Timer { Interval = 200 };
            _timer.Tick += delegate { _ticks++; _lblClock.Text = "pump tick: " + _ticks; };
            _timer.Start();
        }

        private static TextBase[] GetSelectedTexts()
        {
            var ss = new SelectionSet
            {
                // Probe must not change locking behaviour of the running EPLAN session.
                LockProjectByDefault = false,
                LockSelectionByDefault = false
            };
            var sel = ss.Selection ?? Array.Empty<StorableObject>();
            return sel.OfType<TextBase>().ToArray();
        }

        private static RowSnap ReadOne(TextBase t)
        {
            var s = new RowSnap();
            try
            {
                s.Dbid = t.DatabaseIdentifier;
                s.Valid = t.IsValid;
                s.AutoTrans = t.IsAutomaticallyTranslated;
                var c = t.Contents;
                if (c != null)
                {
                    // Exact API shape proven by product code (TextBatchEditForm.cs:1616-1624):
                    //   var langs = new LanguageList(); c.GetLanguageList(ref langs);
                    //   langs.Count / langs.get_Language(k)
                    var langs = new LanguageList();
                    c.GetLanguageList(ref langs);
                    var names = new List<string>();
                    var vals = new List<string>();
                    for (var k = 0; k < langs.Count; k++)
                    {
                        var l = langs.get_Language(k);
                        names.Add(l.ToString());
                        string v;
                        try { v = c.GetString(l); }
                        catch (Exception ex) { v = "ERR:" + ex.GetType().Name; }
                        vals.Add(l + "=" + (v ?? "").Replace('\r', ' ').Replace('\n', ' '));
                    }
                    s.Langs = string.Join(",", names);
                    s.Strings = string.Join(";", vals.Take(4)); // cap log volume
                    // also exercise the single-language/internal path used for untranslated texts
                    try { GC.KeepAlive(c.InternalString); } catch (Exception ex) { s.Error = (s.Error + " InternalString:" + ex.GetType().Name).Trim(); }
                }
                var p = t.Page;
                if (p != null)
                {
                    s.Page = p.Name ?? "";
                    try { s.Plant = Str(p.Properties.DESIGNATION_FULLPLANT); } catch { }
                    try { s.Place = Str(p.Properties.DESIGNATION_FULLPLACEOFINSTALLATION); } catch { }
                    try { s.Location = Str(p.Properties.DESIGNATION_FULLLOCATION); } catch { }
                }
                var pt = t.Location;
                s.X = pt.X; s.Y = pt.Y;
            }
            catch (Exception ex)
            {
                s.Error = ex.GetType().Name + ": " + ex.Message;
            }
            return s;
        }

        private static string Str(PropertyValue v)
        {
            if (v == null || v.IsEmpty) { return ""; }
            return (v.ToString() ?? "").Trim();
        }

        private sealed class ReadResult
        {
            public long Ms;
            public List<RowSnap> Rows = new List<RowSnap>();
            public string ThreadInfo = "";
            public string FatalError = "";
        }

        private static ReadResult ReadOnCurrentThread(TextBase[] texts, int n)
        {
            var r = new ReadResult();
            r.ThreadInfo = "managedId=" + Thread.CurrentThread.ManagedThreadId +
                           " apartment=" + Thread.CurrentThread.GetApartmentState();
            try
            {
                // Reflection on purpose: avoid any dependency on the exact
                // return type of CanAccessMainThread across 2.9 patch levels.
                var disp = new EplanMainThreadDispatcher();
                var mi = typeof(EplanMainThreadDispatcher).GetMethod("CanAccessMainThread");
                var rv = mi == null ? "(method not found)" : Convert.ToString(mi.Invoke(disp, null));
                r.ThreadInfo += " CanAccessMainThread=" + rv;
            }
            catch (Exception ex) { r.FatalError += "CanAccessMainThread threw: " + ex + Environment.NewLine; }

            var sw = Stopwatch.StartNew();
            for (var i = 0; i < n; i++)
            {
                r.Rows.Add(ReadOne(texts[i]));
                if (i > 0 && i % 500 == 0) TbeThreadProbeAddIn.Log("...read " + i + "/" + n);
            }
            sw.Stop();
            r.Ms = sw.ElapsedMilliseconds;
            return r;
        }

        private async Task RunAsync()
        {
            _btnRun.Enabled = false;
            _txt.Clear();
            Action<string> Out = delegate(string s) { _txt.AppendText(s + Environment.NewLine); TbeThreadProbeAddIn.Log(s); };

            try
            {
                File.Delete(TbeThreadProbeAddIn.LogPath);
                Out("=== TBE thread probe ===");
                TextBase[] texts;
                try { texts = GetSelectedTexts(); }
                catch (Exception ex)
                {
                    Out("FATAL: cannot read SelectionSet: " + ex.GetType().Name + " " + ex.Message);
                    Out(ex.StackTrace ?? "");
                    return;
                }
                Out("selected TextBase count = " + texts.Length);
                if (texts.Length == 0)
                {
                    Out("Nothing selected in the GED. Select text objects first, then Run again.");
                    return;
                }

                var n = texts.Length;
                var sample = Math.Min(n, 20);
                var step = Math.Max(1, n / sample);
                var sampleIdx = new List<int>();
                for (var si = 0; si < sample; si++)
                {
                    var idx = si * step;
                    if (idx >= n) { idx = n - 1; }
                    if (!sampleIdx.Contains(idx)) { sampleIdx.Add(idx); }
                }

                // ---- (1) UI thread baseline ----
                Out("");
                Out("[1] reading on UI/main thread (n=" + n + ") ...");
                Application.DoEvents();
                var uiRes = ReadOnCurrentThread(texts, n);
                Out("    " + uiRes.ThreadInfo);
                Out("    elapsed ms = " + uiRes.Ms + " (" + (n / Math.Max(1.0, uiRes.Ms) * 1000.0).ToString("0") + " rows/s)");
                var errors = uiRes.Rows.Where(r => r.Error.Length > 0).Take(5).ToList();
                foreach (var e in errors) { Out("    row error dbid=" + e.Dbid + ": " + e.Error); }

                // ---- (2) Task.Run background thread, 3 rounds ----
                var bgRounds = new List<ReadResult>();
                for (var round = 1; round <= 3; round++)
                {
                    Out("");
                    Out("[2." + round + "] reading on Task.Run background thread (n=" + n + ") ...");
                    await Task.Delay(300); // let the pump paint
                    var roundNo = round;
                    var bg = await Task.Run(() =>
                    {
                        var rr = ReadOnCurrentThread(texts, n);
                        TbeThreadProbeAddIn.Log("background round " + roundNo + " done");
                        return rr;
                    });
                    bgRounds.Add(bg);
                    Out("    " + bg.ThreadInfo);
                    Out("    elapsed ms = " + bg.Ms + " (" + (n / Math.Max(1.0, bg.Ms) * 1000.0).ToString("0") + " rows/s)");
                    var fatal = bg.FatalError;
                    var berr = bg.Rows.Where(r => r.Error.Length > 0).Take(5).ToList();
                    if (fatal.Length > 0) { Out("    FATAL: " + fatal.Replace("\n", "\n    ")); }
                    foreach (var e in berr) { Out("    row error dbid=" + e.Dbid + ": " + e.Error); }
                }

                // ---- (3) hop back to main thread from worker via dispatcher ----
                Out("");
                Out("[3] background -> ExecuteInMainThreadSync x50 hops ...");
                long hopMs;
                string hopErr = "";
                try
                {
                    hopMs = await Task.Run(() =>
                    {
                        var sw = Stopwatch.StartNew();
                        // Reflection on purpose: ExecuteInMainThreadSync has 3
                        // overloads with custom delegate types
                        // (ExecuteInEplanMainThreadDelegate(1/2/3)); reflection
                        // keeps this probe compilable against any 2.9 patch level
                        // and reports the exact parameter shape if it fails.
                        var dispType = typeof(EplanMainThreadDispatcher);
                        var disp = Activator.CreateInstance(dispType);
                        var mi = dispType.GetMethods()
                            .First(m => m.Name == "ExecuteInMainThreadSync"
                                        && m.GetParameters().Length == 2);
                        var delType = mi.GetParameters()[0].ParameterType;
                        TbeThreadProbeAddIn.Log("ExecuteInMainThreadSync picked overload: " + mi + " delegate=" + delType);
                        Func<object, object> body = o =>
                        {
                            var tt = texts[Convert.ToInt32(o) % texts.Length];
                            return tt.IsValid;
                        };
                        var del = Delegate.CreateDelegate(delType, body.Target, body.Method);
                        for (var i = 0; i < 50; i++) { mi.Invoke(disp, new[] { del, (object)i }); }
                        sw.Stop();
                        return sw.ElapsedMilliseconds;
                    });
                    Out("    50 hops elapsed ms = " + hopMs + " (avg " + (hopMs / 50.0).ToString("0.00") + " ms/hop)");
                }
                catch (Exception ex)
                {
                    hopMs = -1;
                    hopErr = ex.GetType().Name + ": " + ex.Message;
                    Out("    hop call failed (note exact delegate signature needed?): " + hopErr);
                }

                // ---- (4) value cross-check UI vs each background round ----
                Out("");
                Out("[4] value cross-check (" + sampleIdx.Count + " sampled rows x " + 3 + " rounds)");
                var allMatch = true;
                foreach (var idx in sampleIdx)
                {
                    var keyUi = uiRes.Rows[idx].CompareKey();
                    for (var r2 = 0; r2 < bgRounds.Count; r2++)
                    {
                        var bg = bgRounds[r2];
                        var keyBg = bg.Rows[idx].CompareKey();
                        if (keyUi != keyBg)
                        {
                            allMatch = false;
                            Out("    MISMATCH round=" + (r2 + 1) + " row=" + idx + " dbid=" + uiRes.Rows[idx].Dbid);
                            Out("      UI: " + keyUi);
                            Out("      BG: " + keyBg);
                        }
                    }
                }
                if (allMatch) { Out("    all sampled rows identical: PASS"); }

                // ---- verdict ----
                Out("");
                var mainThreadId = Thread.CurrentThread.ManagedThreadId;
                var bgClean = bgRounds.All(r =>
                    r.Rows.All(x => x.Error.Length == 0) && r.FatalError.Length == 0);
                // Must actually have run on a different managed thread (id logged in ThreadInfo).
                var bgReallyBackground = bgRounds.All(r =>
                    r.ThreadInfo.Contains("managedId=") &&
                    !r.ThreadInfo.Contains("managedId=" + mainThreadId + " "));
                var verdict = (bgClean && allMatch && bgReallyBackground)
                    ? "TECHNICALLY POSSIBLE on this machine (still OUT OF SUPPORT - xavier decides A vs B)"
                    : "NOT VIABLE -> use plan B (UI-thread chunked reads + let the message pump breathe)";
                Out("VERDICT: " + verdict);
                Out("");
                Out("Manual check required: was EPLAN responsive (menus/page switch) during [2.*]?");
            }
            catch (Exception ex)
            {
                Out("FATAL (outer): " + ex);
            }
            finally
            {
                _btnRun.Enabled = true;
            }
        }
    }
}
