using System.Collections.Concurrent;
using System.Text;
using RemoteScreen.Client.UI;
using RemoteScreen.Shared;

namespace RemoteScreen.Client;

public sealed partial class MainForm
{
    private static readonly Color TermBg = Color.FromArgb(22, 22, 24);
    private static readonly Color TermText = Color.FromArgb(226, 226, 230);
    private static readonly Color TermError = Color.FromArgb(255, 118, 110);
    private static readonly Color TermPrompt = Color.FromArgb(110, 190, 255);
    private static readonly Color TermInfo = Color.FromArgb(135, 135, 145);

    private readonly RichTextBox _termOut = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = TermBg,
        ForeColor = TermText,
        BorderStyle = BorderStyle.None,
        WordWrap = false,
        DetectUrls = false,
        HideSelection = false,
    };
    private readonly InputBox _termIn = new() { Dock = DockStyle.Fill, Placeholder = "Napište příkaz a stiskněte Enter (např. ls -la)" };
    private readonly Label _lblCwd = new() { AutoSize = true, Anchor = AnchorStyles.Left, BackColor = Color.Transparent };
    private readonly FlatButton _btnRun = new() { Text = "Spustit", Glyph = Glyph.Play, Kind = ButtonKind.Primary };
    private readonly FlatButton _btnKill = new() { Text = "Zastavit", Glyph = Glyph.Stop, Kind = ButtonKind.Secondary, Enabled = false };
    private readonly System.Windows.Forms.Timer _termFlushTimer = new() { Interval = 60 };
    private readonly ConcurrentQueue<(string Text, Color Color)> _termQueue = new();

    private readonly List<string> _history = new();
    private int _historyIndex;
    private int _execId;
    private int? _runningExec;
    private string _cwd = "~";

    private void BuildTerminalPage()
    {
        _termOut.Font = Theme.Mono;
        _termIn.Font = Theme.Mono;
        _lblCwd.Font = Theme.Mono;
        _lblCwd.ForeColor = TermPrompt;
        _lblCwd.Margin = new Padding(4, 0, 8, 0);

        var clear = new FlatButton { Glyph = Glyph.Clear, Kind = ButtonKind.Ghost };
        clear.Click += (_, _) => _termOut.Clear();
        new ToolTip().SetToolTip(clear, "Vymazat výstup");

        // výstup v tmavém rámečku se zaoblenými rohy
        var outFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 6, 12), BackColor = TermBg };
        outFrame.Controls.Add(_termOut);
        var outHost = new Card { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = TermBg, Margin = Padding.Empty };
        outHost.Controls.Add(outFrame);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 5, Height = 54, Padding = new Padding(0, 12, 0, 0), BackColor = Color.Transparent };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_lblCwd, 0, 0);
        bottom.Controls.Add(_termIn, 1, 0);
        bottom.Controls.Add(_btnRun, 2, 0);
        bottom.Controls.Add(_btnKill, 3, 0);
        bottom.Controls.Add(clear, 4, 0);

        _pageTerminal.Controls.Add(outHost);
        _pageTerminal.Controls.Add(bottom);
        outHost.BringToFront();

        _btnRun.Click += (_, _) => RunCommand();
        _btnKill.Click += (_, _) =>
        {
            if (_runningExec is int id) _client.PostJson(MsgType.ExecKill, new ExecKill(id));
        };
        _termIn.Box.KeyDown += OnTermKeyDown;
        _termFlushTimer.Tick += (_, _) => FlushTerminal();
    }

    private void InitTerminal()
    {
        _cwd = _client.Info.HomeDir ?? "~";
        UpdateCwdLabel();
        EnqueueTerminal((_targetWindows
                            ? "Příkazy se spouštějí v PowerShellu na vzdáleném PC (neinteraktivně – programy, které čekají na vstup, skončí).\n"
                            : "Příkazy se spouštějí v zsh na Macu (neinteraktivně – programy, které čekají na vstup, skončí).\n") +
                        "↑/↓ = historie   ·   cd mění složku   ·   clear vymaže okno   ·   Ctrl+C zastaví běžící příkaz\n\n", TermInfo);
        _termFlushTimer.Start();
    }

    private void OnTermKeyDown(object? sender, KeyEventArgs e)
    {
        var box = _termIn.Box;
        switch (e.KeyCode)
        {
            case Keys.Enter:
                RunCommand();
                e.SuppressKeyPress = true;
                break;
            case Keys.Up when _history.Count > 0:
                _historyIndex = Math.Max(0, _historyIndex - 1);
                box.Text = _history[_historyIndex];
                box.SelectionStart = box.TextLength;
                e.Handled = true;
                break;
            case Keys.Down when _history.Count > 0:
                _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
                box.Text = _historyIndex < _history.Count ? _history[_historyIndex] : "";
                box.SelectionStart = box.TextLength;
                e.Handled = true;
                break;
            case Keys.C when e.Control && box.SelectionLength == 0 && _runningExec is int id:
                _client.PostJson(MsgType.ExecKill, new ExecKill(id));
                e.SuppressKeyPress = true;
                break;
        }
    }

    private void RunCommand()
    {
        string cmd = _termIn.Text.Trim();
        if (cmd.Length == 0) return;
        if (_runningExec != null)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        if (_history.Count == 0 || _history[^1] != cmd) _history.Add(cmd);
        _historyIndex = _history.Count;
        _termIn.Box.Clear();

        if (cmd is "clear" or "cls")
        {
            _termOut.Clear();
            return;
        }

        EnqueueTerminal($"{ShortCwd()}{(_targetWindows ? ">" : " %")} ", TermPrompt);
        EnqueueTerminal(cmd + "\n", Color.White);
        _runningExec = ++_execId;
        _btnKill.Enabled = true;
        _client.PostJson(MsgType.Exec, new ExecRequest(_runningExec.Value, cmd));
    }

    private void OnExecExit(ExecExit e)
    {
        if (e.Id != _runningExec) return;
        if (e.ExitCode != 0) EnqueueTerminal($"[ukončeno s kódem {e.ExitCode}]\n", TermInfo);
        EnqueueTerminal("\n", TermText);
        _runningExec = null;
        _btnKill.Enabled = false;
        _cwd = e.Cwd;
        UpdateCwdLabel();
        FlushTerminal();
    }

    private string ShortCwd()
    {
        string home = _client.Info.HomeDir ?? "";
        return home.Length > 0 && _cwd.StartsWith(home, StringComparison.Ordinal) ? "~" + _cwd[home.Length..] : _cwd;
    }

    private void UpdateCwdLabel() => _lblCwd.Text = ShortCwd() + (_targetWindows ? " >" : " %");

    /// <summary>Lze volat z libovolného vlákna – výstup se do okna vypisuje dávkově.</summary>
    private void EnqueueTerminal(string text, Color color) => _termQueue.Enqueue((text, color));

    private void FlushTerminal()
    {
        if (_termQueue.IsEmpty || IsDisposed) return;

        var sb = new StringBuilder();
        Color? current = null;

        void Emit()
        {
            if (sb.Length == 0 || current == null) return;
            _termOut.SelectionStart = _termOut.TextLength;
            _termOut.SelectionLength = 0;
            _termOut.SelectionColor = current.Value;
            _termOut.AppendText(sb.ToString());
            sb.Clear();
        }

        int budget = 4000; // max. položek na jedno překreslení
        while (budget-- > 0 && _termQueue.TryDequeue(out var item))
        {
            if (current != item.Color) { Emit(); current = item.Color; }
            sb.Append(item.Text);
        }
        Emit();

        // omezit délku historie
        if (_termOut.TextLength > 600_000)
        {
            _termOut.ReadOnly = false;
            _termOut.Select(0, _termOut.TextLength - 400_000);
            _termOut.SelectedText = "";
            _termOut.ReadOnly = true;
            _termOut.SelectionStart = _termOut.TextLength;
        }
        _termOut.ScrollToCaret();
    }
}
