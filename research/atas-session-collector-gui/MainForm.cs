using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AtasSessionCollector;

public sealed class MainForm : Form
{
    private readonly ToolPaths _tools = new();

    private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short, Width = 120 };
    private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short, Width = 120 };
    private readonly ComboBox _session = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly ComboBox _speed = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly TextBox _instrument = new() { Text = "MNQ", ReadOnly = true, Width = 90 };
    private readonly TextBox _output = new() { Text = "", Width = 360, ReadOnly = true };   // Datenordner (portabel, per Durchsuchen gewaehlt)
    private readonly Button _browse = new() { Text = "\u2026", Width = 32 };

    private readonly Label _summary = new() { AutoSize = false, Height = 54, Dock = DockStyle.Top, Padding = new Padding(4) };
    private readonly ListView _grid = new() { View = View.Details, FullRowSelect = true, GridLines = true, Dock = DockStyle.Fill };

    private readonly Button _start = new() { Text = "Start", Width = 110, Height = 34 };
    private readonly Button _resume = new() { Text = "Fortsetzen", Width = 110, Height = 34 };
    private readonly Button _stop = new() { Text = "Kontrolliert stoppen", Width = 150, Height = 34, Enabled = false };
    private readonly Button _openDir = new() { Text = "Ausgabeordner oeffnen", Width = 170, Height = 34 };
    private readonly Button _copyRec = new() { Text = "Recorder-Ordner kopieren", Width = 190, Height = 34 };
    private readonly Button _hardstop = new() { Text = "ALLES BEENDEN (Not-Aus)", Width = 190, Height = 34, ForeColor = Color.White, BackColor = Color.Firebrick, FlatStyle = FlatStyle.Flat };

    private readonly Label _stepLabel = new() { AutoSize = false, Dock = DockStyle.Top, Height = 24, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 18, Style = ProgressBarStyle.Continuous };
    private readonly TextBox _details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 8f), WordWrap = false };

    private readonly Dictionary<DateOnly, ListViewItem> _rows = new();
    private CollectorRunner? _runner;
    private CancellationTokenSource? _cts;
    private RunPlan? _plan;

    public MainForm()
    {
        Text = $"ATAS-Session-Collector v{Program.AppVersion}";
        Width = 1020; Height = 740; StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(900, 600);

        foreach (var s in SessionCalendar.Sessions) _session.Items.Add(s.DisplayName);
        _session.SelectedIndex = 0;
        _speed.Items.AddRange(new object[] { "0.1x", "0.3x", "0.5x", "1x", "5x", "10x", "100x", "1000x" });
        _speed.SelectedItem = "100x";
        _from.Value = new DateTime(2026, 10, 26);
        _to.Value = new DateTime(2026, 10, 28);
        var savedData = AppPaths.LoadDataDir();           // dauerhaft gespeicherter Datenordner (falls vorhanden)
        // Portabel/universell: ohne gespeicherte Wahl den Standard NEBEN der EXE vorbelegen (kein festes F:).
        _output.Text = !string.IsNullOrWhiteSpace(savedData) ? savedData! : AppPaths.DefaultDataDir;
        ApplyPrefill();   // optionale Startwerte per Kommandozeile (Komfort/Deep-Link)

        BuildLayout();

        _from.ValueChanged += (_, _) => RefreshPreview();
        _to.ValueChanged += (_, _) => RefreshPreview();
        _session.SelectedIndexChanged += (_, _) => RefreshPreview();
        _speed.SelectedIndexChanged += (_, _) => RefreshPreview();
        _output.TextChanged += (_, _) => RefreshPreview();
        _browse.Click += (_, _) => BrowseOutput();
        _start.Click += async (_, _) => await StartRun(resume: false);
        _resume.Click += async (_, _) => await StartRun(resume: true);
        _stop.Click += (_, _) => StopRun();
        _openDir.Click += (_, _) => OpenDir();
        _copyRec.Click += (_, _) => CopyRecorderPath();
        _hardstop.Click += (_, _) => HardStop();

        RefreshPreview();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        EnsureDataDir();
    }

    // Beim ersten Start (oder ungueltigem gespeicherten Pfad) den Datenordner waehlen lassen und dauerhaft speichern.
    // Kein stiller Fallback: wird keiner gewaehlt, bleibt die App ohne Datenordner (Start zeigt dann einen klaren Fehler).
    private void EnsureDataDir()
    {
        // Universell: ist der (ggf. portabel vorbelegte) Datenordner nutzbar, wird er still angelegt + dauerhaft
        // gespeichert (kein Zwang auf ein bestimmtes Laufwerk). Nur wenn er NICHT beschreibbar ist (z. B. Paket in
        // schreibgeschuetztem Pfad), zur Auswahl auffordern.
        if (!string.IsNullOrWhiteSpace(_output.Text) && AppPaths.Validate(_output.Text, out _))
        {
            AppPaths.EnsureSubdirs(_output.Text);
            AppPaths.SaveDataDir(_output.Text);
            RefreshPreview();
            return;
        }
        MessageBox.Show(this,
            "Bitte den Datenordner waehlen. Darunter werden Collections, Recordings und Logs abgelegt.\n" +
            "Der Ordner wird dauerhaft gespeichert und von Collector und Recorder verwendet.",
            "Datenordner waehlen", MessageBoxButtons.OK, MessageBoxIcon.Information);
        ChooseDataDir();
    }

    private void ChooseDataDir()
    {
        using var dlg = new FolderBrowserDialog { Description = "Datenordner fuer ATAS-Session-Collector waehlen", UseDescriptionForTitle = true };
        if (Directory.Exists(_output.Text)) dlg.SelectedPath = _output.Text;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (!AppPaths.Validate(dlg.SelectedPath, out var why))
        {
            MessageBox.Show(this, "Datenordner nicht verwendbar: " + why + "\nKein Ausweich-Speicherort – bitte anderen Ordner waehlen.",
                "Datenordner ungueltig", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        AppPaths.EnsureSubdirs(dlg.SelectedPath);
        AppPaths.SaveDataDir(dlg.SelectedPath);
        _output.Text = dlg.SelectedPath;
        RefreshPreview();
    }

    private void ApplyPrefill()
    {
        var a = Environment.GetCommandLineArgs();
        string Get(string k) { int i = Array.FindIndex(a, x => x.Equals(k, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : ""; }
        if (DateOnly.TryParse(Get("--from"), System.Globalization.CultureInfo.InvariantCulture, out var f)) _from.Value = f.ToDateTime(TimeOnly.MinValue);
        if (DateOnly.TryParse(Get("--to"), System.Globalization.CultureInfo.InvariantCulture, out var t)) _to.Value = t.ToDateTime(TimeOnly.MinValue);
        var sess = Get("--session");
        int si = Array.FindIndex(SessionCalendar.Sessions.ToArray(), x => x.Name.Equals(sess, StringComparison.OrdinalIgnoreCase));
        if (si >= 0) _session.SelectedIndex = si;
        var sp = Get("--speed");
        if (sp != "" && _speed.Items.Contains(sp)) _speed.SelectedItem = sp;
        var outp = Get("--output");
        if (outp != "") _output.Text = outp;
    }

    private void BuildLayout()
    {
        var inputs = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(8), Height = 150 };
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        void Row(string l1, Control c1, string? l2 = null, Control? c2 = null)
        {
            inputs.Controls.Add(new Label { Text = l1, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) });
            inputs.Controls.Add(c1);
            inputs.Controls.Add(new Label { Text = l2 ?? "", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) });
            inputs.Controls.Add(c2 ?? new Label());
        }

        Row("Von (Datum):", _from, "Bis (Datum):", _to);
        Row("Session:", _session, "Geschwindigkeit:", _speed);
        var outPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        outPanel.Controls.Add(_output); outPanel.Controls.Add(_browse);
        Row("Instrument:", _instrument, "Datenordner:", outPanel);

        var runBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8, 6, 8, 6) };
        runBar.Controls.AddRange(new Control[] { _start, _resume, _stop, _openDir, _copyRec, _hardstop });

        _grid.Columns.Add("Datum", 90);
        _grid.Columns.Add("Status", 150);
        _grid.Columns.Add("Lokal (Session)", 150);
        _grid.Columns.Add("Deutsch (Berlin)", 150);
        _grid.Columns.Add("UTC", 150);
        _grid.Columns.Add("Trades", 80, HorizontalAlignment.Right);
        _grid.Columns.Add("Volumen", 80, HorizontalAlignment.Right);
        _grid.Columns.Add("Hinweis", 140);

        var gridHost = new Panel { Dock = DockStyle.Fill };
        gridHost.Controls.Add(_grid);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 420 };
        split.Panel1.Controls.Add(gridHost);
        split.Panel1.Controls.Add(_summary);
        var detailsHost = new Panel { Dock = DockStyle.Fill };
        detailsHost.Controls.Add(_details);
        detailsHost.Controls.Add(new Label { Text = "Verlauf / Details:", Dock = DockStyle.Top, Height = 18, Padding = new Padding(2) });
        split.Panel2.Controls.Add(detailsHost);
        split.Panel2.Controls.Add(_progress);
        split.Panel2.Controls.Add(_stepLabel);

        Controls.Add(split);
        Controls.Add(runBar);
        Controls.Add(inputs);
    }

    // ---------------- Vorschau ----------------
    private SessionDef CurrentSession => SessionCalendar.Sessions[_session.SelectedIndex];

    private (DateOnly from, DateOnly to) Range()
    {
        var f = DateOnly.FromDateTime(_from.Value.Date);
        var t = DateOnly.FromDateTime(_to.Value.Date);
        return f <= t ? (f, t) : (t, f);
    }

    private void RefreshPreview()
    {
        var (from, to) = Range();
        var s = CurrentSession;
        var days = SessionCalendar.PlanRange(from, to, s);
        _rows.Clear();
        _grid.BeginUpdate();
        _grid.Items.Clear();
        foreach (var d in days)
        {
            var it = new ListViewItem(d.Date.ToString("yyyy-MM-dd"));
            it.SubItems.Add(d.WillRecord ? (d.Status == DayStatus.EarlyCloseTrading ? "geplant (verkuerzt)" : "geplant") : d.Status.ToString());
            it.SubItems.Add($"{d.LocalStart:HH:mm}\u2013{d.LocalEnd:HH:mm}");
            it.SubItems.Add($"{d.BerlinStart:HH:mm}\u2013{d.BerlinEnd:HH:mm}");
            it.SubItems.Add($"{d.UtcStart:HH:mm}\u2013{d.UtcEnd:HH:mm}");
            it.SubItems.Add("");
            it.SubItems.Add("");
            it.SubItems.Add(d.Reason ?? "");
            if (!d.WillRecord) it.ForeColor = Color.Gray;
            _grid.Items.Add(it);
            _rows[d.Date] = it;
        }
        _grid.EndUpdate();

        int planned = days.Count(d => d.WillRecord), skipped = days.Count - planned;
        string collName = SessionCalendar.CollectionFolderName(_instrument.Text, s, from, to);
        string dd = _output.Text;
        string recPath = string.IsNullOrWhiteSpace(dd) ? "(Datenordner waehlen)" : AppPaths.Recordings(dd);
        _summary.Text =
            $"Session: {s.DisplayName}  |  Zeitzone: {s.TzIana}  |  Geschwindigkeit: {_speed.SelectedItem}  |  Instrument: {_instrument.Text}\n" +
            $"Geplante Aufnahme-Tage: {planned}   (kalendarisch uebersprungen: {skipped})   |   Ausgabe-Collection: {collName}\n" +
            $"Recorder-Ordner (einmalig in ATAS als 'Output-Ordner' setzen): {recPath}";
    }

    private void BrowseOutput() => ChooseDataDir();

    private void OpenDir()
    {
        string dir = !string.IsNullOrWhiteSpace(_output.Text) ? AppPaths.Collections(_output.Text) : _output.Text;
        try
        {
            if (string.IsNullOrWhiteSpace(dir)) { MessageBox.Show(this, "Noch kein Datenordner gewaehlt."); return; }
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, "Ordner nicht zu oeffnen: " + ex.Message); }
    }

    private void CopyRecorderPath()
    {
        if (string.IsNullOrWhiteSpace(_output.Text)) { MessageBox.Show(this, "Noch kein Datenordner gewaehlt."); return; }
        var rec = AppPaths.Recordings(_output.Text);
        try { Clipboard.SetText(rec); MessageBox.Show(this, "Recorder-Ordner in die Zwischenablage kopiert:\n\n" + rec + "\n\nDiesen Pfad in ATAS beim Indikator 'OF Replay Recorder' als 'Output-Ordner' eintragen (einmalige Einrichtung).", "Recorder-Ordner", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        catch (Exception ex) { MessageBox.Show(this, "Kopieren fehlgeschlagen: " + ex.Message); }
    }

    // ---------------- Lauf ----------------
    private async Task StartRun(bool resume)
    {
        var (from, to) = Range();
        var s = CurrentSession;
        var days = SessionCalendar.PlanRange(from, to, s);
        if (days.Count(d => d.WillRecord) == 0)
        {
            MessageBox.Show(this, "Im gewaehlten Bereich liegt kein Handelstag (nur Wochenende/Feiertage).", "Nichts zu tun", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string dataDir = _output.Text;
        if (!AppPaths.Validate(dataDir, out var dWhy))
        {
            MessageBox.Show(this, "Datenordner fehlt oder ist nicht beschreibbar: " + dWhy + "\nBitte Datenordner waehlen (kein Ausweich-Speicherort).",
                "Datenordner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ChooseDataDir();
            return;
        }
        AppPaths.EnsureSubdirs(dataDir);

        var checks = Preconditions.CheckAll(_tools, dataDir);
        var failed = checks.Where(c => !c.Ok).ToList();
        if (failed.Count > 0)
        {
            MessageBox.Show(this,
                "Vor dem Start fehlen Voraussetzungen:\n\n" + string.Join("\n", failed.Select(c => $"\u2717 {c.Title}: {c.Detail}")),
                "Voraussetzungen nicht erfuellt", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            AppendDetail("Voraussetzungspruefung:");
            foreach (var c in checks) AppendDetail($"  [{(c.Ok ? "OK" : "FEHLT")}] {c.Title}: {c.Detail}");
            return;
        }

        _plan = new RunPlan
        {
            Instrument = _instrument.Text,
            Session = s,
            Speed = (string)_speed.SelectedItem!,
            OutputRoot = AppPaths.Collections(dataDir),
            From = from,
            To = to,
            AllDays = days,
            ProbeExe = _tools.ProbeExe,
            Python = _tools.Python,
            CollectorScript = _tools.CollectorScript,
            RecordingsDir = AppPaths.Recordings(dataDir),
        };

        SetRunning(true);
        _progress.Maximum = Math.Max(1, _plan.RecordDays.Count);
        _progress.Value = 0;
        AppendDetail($"\n=== {(resume ? "FORTSETZEN" : "START")}  {DateTime.Now:HH:mm:ss} === {_plan.RecordDays.Count} Tag(e), Speed {_plan.Speed}");

        _runner = new CollectorRunner();
        _runner.Log += line => BeginInvoke(() => AppendDetail(line));
        _runner.Step += step => BeginInvoke(() => _stepLabel.Text = step);
        _runner.DayStarted += (day, idx, total) => BeginInvoke(() =>
        {
            _stepLabel.Text = $"Tag {idx}/{total}: {day.Date:yyyy-MM-dd} wird aufgenommen \u2026";
            SetRowStatus(day.Date, "laeuft \u2026", Color.DarkBlue);
        });
        _runner.DayFinished += res => BeginInvoke(() =>
        {
            UpdateRowResult(res);
            if (res.Outcome is DayOutcome.Ok or DayOutcome.Reused or DayOutcome.NoData or DayOutcome.ReusedNoData)
                _progress.Value = Math.Min(_progress.Maximum, _progress.Value + 1);
        });

        _cts = new CancellationTokenSource();
        RunSummary? summary = null; string? error = null;
        try
        {
            summary = await Task.Run(() => _runner.RunAsync(_plan, _cts.Token));
        }
        catch (OperationCanceledException) { error = "Lauf abgebrochen."; }
        catch (Exception ex) { error = ex.Message; }

        SetRunning(false);
        if (error != null)
        {
            _stepLabel.Text = "Abgebrochen.";
            AppendDetail("Fehler: " + error);
            MessageBox.Show(this, error, "Abgebrochen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (summary != null) ShowSummary(summary);
    }

    private void StopRun()
    {
        _runner?.RequestStop();
        _stop.Enabled = false;
        _stop.Text = "Stoppe nach aktuellem Fenster \u2026";
        _stepLabel.Text = "Kontrollierter Stopp angefordert \u2013 der laufende Tag wird sauber beendet.";
        AppendDetail("[Bedienung] Kontrolliert stoppen angefordert (kein Prozess-Kill).");
    }

    // Not-Aus: Dieses Programm und ALLE zugehoerigen Hintergrundprozesse (Aufnahme-Steuerung + deren
    // Python-Prozesse) sofort hart beenden. ATAS (OFT.Platform) wird NICHT angefasst.
    private void HardStop()
    {
        var r = MessageBox.Show(this,
            "NOT-AUS: Dieses Programm wird komplett beendet und alle zugehoerigen Hintergrundprozesse\n" +
            "(Aufnahme-Steuerung 'AtasReplayProbe' und deren Python-Prozesse) werden SOFORT hart abgebrochen.\n\n" +
            "ATAS selbst wird NICHT beendet. Ein gerade laufender Tag wird NICHT sauber finalisiert\n" +
            "(fuer sauberes Beenden stattdessen 'Kontrolliert stoppen' verwenden).\n\n" +
            "Wirklich alles sofort beenden?",
            "Alles beenden (Not-Aus)", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) return;

        // 1) Laufende Aufnahme-Steuerung samt Kindprozessen (Python) toeten.
        try { _runner?.KillCurrentTree(); } catch { }
        try { _cts?.Cancel(); } catch { }

        // 2) Etwaige verwaiste Probe-Prozesse (eindeutiger Name) samt Kindern toeten – NIE ATAS/OFT.Platform.
        KillProbeProcesses();

        // 3) Dieses Programm hart beenden – nichts bleibt im Hintergrund.
        Environment.Exit(0);
    }

    // Toetet nur Prozesse mit dem eindeutigen Namen unserer Aufnahme-Steuerung (samt deren Kindprozessen,
    // z. B. Python). ATAS (OFT.Platform) oder beliebige andere python.exe werden NICHT angefasst.
    private static void KillProbeProcesses()
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName("AtasReplayProbe"); } catch { return; }
        foreach (var p in procs)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }
    }

    private void SetRunning(bool running)
    {
        _start.Enabled = _resume.Enabled = !running;
        _from.Enabled = _to.Enabled = _session.Enabled = _speed.Enabled = _output.Enabled = _browse.Enabled = !running;
        _stop.Enabled = running;
        if (!running) _stop.Text = "Kontrolliert stoppen";
        _openDir.Enabled = true;
    }

    private void SetRowStatus(DateOnly d, string status, Color color)
    {
        if (_rows.TryGetValue(d, out var it)) { it.SubItems[1].Text = status; it.ForeColor = color; }
    }

    private void UpdateRowResult(DayResult r)
    {
        if (!_rows.TryGetValue(r.Date, out var it)) return;
        it.SubItems[1].Text = r.OutcomeText;
        it.SubItems[5].Text = r.Trades > 0 ? r.Trades.ToString("#,0") : "";
        it.SubItems[6].Text = r.Volume > 0 ? r.Volume.ToString("#,0") : "";
        it.SubItems[7].Text = r.Detail;
        if (r.RuntimeSec > 0) it.SubItems[7].Text += $"  ({CollectorRunner.FormatDur(r.RuntimeSec)})";
        it.ForeColor = r.Outcome switch
        {
            DayOutcome.Ok => Color.DarkGreen,
            DayOutcome.Reused => Color.SeaGreen,
            DayOutcome.NoData or DayOutcome.ReusedNoData => Color.DarkGoldenrod,
            DayOutcome.Failed => Color.Firebrick,
            DayOutcome.Stopped => Color.DimGray,
            _ => Color.Gray,
        };
    }

    private void ShowSummary(RunSummary s)
    {
        _stepLabel.Text = s.Complete ? "Fertig \u2013 alle geplanten Tage in gemeinsamer trades.csv." : "Beendet \u2013 unvollstaendig (siehe Hinweise).";
        AppendDetail($"\n=== ERGEBNIS: {(s.Complete ? "VOLLSTAENDIG" : "UNVOLLSTAENDIG")}  Trades={s.TotalTrades:#,0}  Volumen={s.TotalVolume:#,0}  CSV={s.CsvBytes / (1024.0 * 1024):0.0} MiB ===");
        if (s.StopReason != "") AppendDetail("Grund: " + s.StopReason);
        AppendDetail("Ausgabeordner: " + s.CollectionDir);
        string msg = (s.Complete ? "Sammellauf vollstaendig." : "Sammellauf beendet (unvollstaendig).") +
            $"\n\nTrades gesamt: {s.TotalTrades:#,0}\nVolumen gesamt: {s.TotalVolume:#,0}\ntrades.csv: {s.CsvBytes / (1024.0 * 1024):0.0} MiB" +
            (s.StopReason == "" ? "" : "\n\nHinweis: " + s.StopReason) +
            "\n\nAusgabeordner:\n" + s.CollectionDir;
        MessageBox.Show(this, msg, s.Complete ? "Fertig" : "Beendet",
            MessageBoxButtons.OK, s.Complete ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void AppendDetail(string line)
    {
        if (_details.TextLength > 200_000) _details.Text = _details.Text[^100_000..];
        _details.AppendText(line + Environment.NewLine);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_start.Enabled && _runner != null) // laeuft gerade
        {
            var r = MessageBox.Show(this,
                "Ein Aufnahmelauf laeuft. Jetzt beenden? Der laufende Tag wird NICHT sauber finalisiert, wenn das Programm hart geschlossen wird.\n\nEmpfehlung: 'Kontrolliert stoppen' verwenden und warten.",
                "Lauf aktiv", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) { e.Cancel = true; return; }
            _runner?.RequestStop();
        }
        base.OnFormClosing(e);
    }
}
