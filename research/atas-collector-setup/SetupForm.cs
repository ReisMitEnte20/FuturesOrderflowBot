using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AtasCollectorSetup;

/// <summary>Ein-Seiten-Assistent: Ordner vorschlagen (aenderbar), installieren, Ergebnis ehrlich anzeigen.</summary>
public sealed class SetupForm : Form
{
    private readonly SetupOptions _opt;
    private readonly TextBox _program = new() { Width = 560 };
    private readonly TextBox _data = new() { Width = 560 };
    private readonly CheckBox _shortcuts = new() { Text = "Verknüpfungen im Startmenü und auf dem Desktop erstellen", Checked = true, AutoSize = true };
    private readonly Button _install = new() { Text = "Installieren", Width = 150, Height = 36 };
    private readonly Button _launch = new() { Text = "Collector starten", Width = 150, Height = 36, Enabled = false };
    private readonly Button _close = new() { Text = "Schließen", Width = 110, Height = 36 };
    private readonly Label _status = new() { AutoSize = false, Width = 700, Height = 44, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold) };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 700, Height = 300, Font = new Font("Consolas", 9f), WordWrap = true };

    public SetupForm(SetupOptions opt, SetupEnv env)
    {
        _opt = opt;
        Text = Installer.ProductName + " – Setup";
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(740, 600);

        _program.Text = opt.ProgramDir; _data.Text = opt.DataDir; _shortcuts.Checked = opt.Shortcuts;
        var prev = env.LoadState();

        int y = 14;
        Controls.Add(new Label
        {
            Left = 20, Top = y, Width = 700, Height = 54,
            Text = "Installiert Oberfläche, Aufnahme-Steuerung, Bridge, Python-Laufzeit und den ATAS-Recorder und stellt alle auf denselben Datenordner ein.\n" +
                   (prev != null ? $"Vorhandene Installation (Version {prev.version}) wird aktualisiert; Einstellungen und Aufnahmen bleiben erhalten."
                                 : "ATAS muss installiert und während der Installation geschlossen sein."),
        });
        y += 62;
        Controls.Add(new Label { Left = 20, Top = y, Width = 300, Text = "Programmordner" }); y += 20;
        _program.Left = 20; _program.Top = y; Controls.Add(_program);
        var b1 = new Button { Text = "Ändern…", Left = 590, Top = y - 2, Width = 130, Height = 27 }; b1.Click += (_, _) => Browse(_program, "Programmordner wählen"); Controls.Add(b1);
        y += 36;
        Controls.Add(new Label { Left = 20, Top = y, Width = 600, Text = "Datenordner (Aufnahmen, Collections, Logs – gemeinsam für alle Komponenten)" }); y += 20;
        _data.Left = 20; _data.Top = y; Controls.Add(_data);
        var b2 = new Button { Text = "Ändern…", Left = 590, Top = y - 2, Width = 130, Height = 27 }; b2.Click += (_, _) => Browse(_data, "Datenordner wählen"); Controls.Add(b2);
        y += 36;
        _shortcuts.Left = 20; _shortcuts.Top = y; Controls.Add(_shortcuts);
        y += 32;
        _install.Left = 20; _install.Top = y; _launch.Left = 180; _launch.Top = y; _close.Left = 610; _close.Top = y;
        Controls.Add(_install); Controls.Add(_launch); Controls.Add(_close);
        y += 46;
        _status.Left = 20; _status.Top = y; Controls.Add(_status);
        y += 46;
        _log.Left = 20; _log.Top = y; _log.Height = ClientSize.Height - y - 16; Controls.Add(_log);

        _install.Click += async (_, _) => await RunInstall();
        _close.Click += (_, _) => Close();
        _launch.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Path.Combine(_opt.ProgramDir, Installer.GuiExe)) { UseShellExecute = true, WorkingDirectory = _opt.ProgramDir }); Close(); }
            catch (Exception ex) { MessageBox.Show(this, "Start nicht möglich: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
    }

    private void Browse(TextBox box, string title)
    {
        using var dlg = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true, ShowNewFolderButton = true };
        try { var d = box.Text; while (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) d = Path.GetDirectoryName(d); if (!string.IsNullOrEmpty(d)) dlg.SelectedPath = d; } catch { }
        if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
    }

    private async Task RunInstall()
    {
        _opt.ProgramDir = _program.Text; _opt.DataDir = _data.Text; _opt.Shortcuts = _shortcuts.Checked;
        _install.Enabled = false; _launch.Enabled = false; _program.Enabled = false; _data.Enabled = false; _shortcuts.Enabled = false;
        _log.Clear();
        _status.ForeColor = SystemColors.ControlText; _status.Text = "Installation läuft … (die Abschlussprüfungen dauern einen Moment)";
        var installer = new Installer(_opt, s => BeginInvoke(() => { _log.AppendText(s + "\r\n"); }));
        bool ok = await Task.Run(installer.Run);
        if (ok)
        {
            _status.ForeColor = Color.FromArgb(0, 120, 40);
            _status.Text = "Fertig eingerichtet. Der Collector ist über die Verknüpfung „" + Installer.ProductName + "“ startbereit.";
            _launch.Enabled = true;
        }
        else
        {
            _status.ForeColor = Color.FromArgb(190, 20, 20);
            _status.Text = "Installation NICHT abgeschlossen: " + (installer.Errors.Count > 0 ? installer.Errors[0] : "siehe Protokoll");
            _install.Text = "Erneut versuchen";
        }
        _install.Enabled = true; _program.Enabled = true; _data.Enabled = true; _shortcuts.Enabled = true;
    }
}
