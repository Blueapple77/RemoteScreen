using System.Collections.Concurrent;
using RemoteScreen.Client.UI;
using RemoteScreen.Shared;

namespace RemoteScreen.Client;

public sealed partial class MainForm
{
    private sealed class Transfer
    {
        public required string LocalPath;
        public required string Name;
        public FileStream? Stream;
        public long Size, Done, LastReported;
        /// <summary>true = přenos pro tažení z Macu, neukazovat na záložce Soubory</summary>
        public bool Silent;
        public TaskCompletionSource<string?>? Completion;
    }

    private readonly InputBox _txtPath = new() { Dock = DockStyle.Fill };
    private readonly ListView _lvFiles = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = true,
        AllowDrop = true,
        HideSelection = false,
        BorderStyle = BorderStyle.None,
    };
    private readonly ThinProgress _fileProgress = new() { Width = 200, Visible = false, Margin = new Padding(12, 14, 4, 0) };
    private readonly Label _lblFiles = new() { AutoSize = true, Margin = new Padding(8, 9, 0, 0), BackColor = Color.Transparent };
    private readonly Panel _filesBanner = new() { Dock = DockStyle.Top, Height = 0, Visible = false };
    private readonly Label _filesBannerText = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
    private readonly ImageList _fileIcons = new() { ImageSize = new Size(22, 22), ColorDepth = ColorDepth.Depth32Bit };
    private readonly ConcurrentDictionary<int, Transfer> _downloads = new();
    private int _transferId;
    private string _remotePath = "";
    private DirEntry[] _entries = Array.Empty<DirEntry>();

    private void BuildFilesPage()
    {
        _fileIcons.Images.Add("dir", GlyphBitmap(Glyph.Folder, Color.FromArgb(240, 180, 50)));
        _fileIcons.Images.Add("file", GlyphBitmap("", Theme.TextDim));
        _lvFiles.SmallImageList = _fileIcons;
        _lvFiles.BackColor = Theme.Surface;
        _lvFiles.ForeColor = Theme.Text;
        _lvFiles.Font = Theme.Body;
        _lvFiles.Columns.Add("Název", 460);
        _lvFiles.Columns.Add("Velikost", 110, HorizontalAlignment.Right);
        _lvFiles.Columns.Add("Změněno", 160);

        FlatButton Btn(string text, string glyph, ButtonKind kind, Action onClick, string? tip = null)
        {
            var b = new FlatButton { Text = text, Glyph = glyph, Kind = kind };
            b.Click += (_, _) => onClick();
            if (tip != null) new ToolTip().SetToolTip(b, tip);
            return b;
        }

        // horní lišta s cestou
        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 48, ColumnCount = 5, BackColor = Color.Transparent, Padding = new Padding(0, 0, 0, 8) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(Btn("", Glyph.Up, ButtonKind.Ghost, () => RequestList(ParentPath(_remotePath)), "O úroveň výš (Backspace)"), 0, 0);
        top.Controls.Add(Btn("", Glyph.Home, ButtonKind.Ghost, () => RequestList("~"), "Domovská složka"), 1, 0);
        top.Controls.Add(Btn("", Glyph.Refresh, ButtonKind.Ghost, () => RequestList(_remotePath), "Obnovit (F5)"), 2, 0);
        top.Controls.Add(_txtPath, 3, 0);
        top.Controls.Add(Btn("Přejít", Glyph.Send, ButtonKind.Secondary, () => RequestList(_txtPath.Text)), 4, 0);

        // pruh s chybou (např. macOS nepovolil přístup)
        _filesBanner.BackColor = Theme.Blend(Theme.Warning, Theme.Panel, Theme.Dark ? 0.78f : 0.82f);
        _filesBannerText.ForeColor = Theme.Text;
        _filesBannerText.Font = Theme.Body;
        var bannerIcon = new Label { Dock = DockStyle.Left, Width = 40, Text = Glyph.Warning, Font = Theme.Icon(14f), ForeColor = Theme.Warning, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent };
        var bannerClose = new FlatButton { Glyph = Glyph.Close, Kind = ButtonKind.Ghost, Dock = DockStyle.Right };
        bannerClose.Click += (_, _) => HideFilesBanner();
        _filesBanner.Controls.Add(_filesBannerText);
        _filesBanner.Controls.Add(bannerIcon);
        _filesBanner.Controls.Add(bannerClose);
        _filesBannerText.BringToFront();

        // seznam v kartě
        var listCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(6), Margin = Padding.Empty };
        listCard.Controls.Add(_lvFiles);
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 8), BackColor = Color.Transparent };
        listHost.Controls.Add(listCard);

        // spodní lišta s akcemi
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, BackColor = Color.Transparent };
        actions.Controls.AddRange(new Control[]
        {
            Btn("Nahrát na Mac…", Glyph.Upload, ButtonKind.Primary, UploadViaDialogToFolder, "Soubory i složky můžete také přetáhnout myší do seznamu"),
            Btn("Stáhnout do PC…", Glyph.Download, ButtonKind.Secondary, DownloadSelected),
            Btn("Nová složka", Glyph.NewFolder, ButtonKind.Secondary, MakeDirectory),
            Btn("Otevřít na Macu", Glyph.Open, ButtonKind.Secondary, OpenOnMac),
            Btn("Do koše", Glyph.Delete, ButtonKind.Ghost, DeleteSelected, "Přesune vybrané položky do koše na Macu (Delete)"),
            _fileProgress,
            _lblFiles,
        });
        _lblFiles.Font = Theme.Small;
        _lblFiles.ForeColor = Theme.TextDim;

        _pageFiles.Controls.Add(listHost);
        _pageFiles.Controls.Add(_filesBanner);
        _pageFiles.Controls.Add(top);
        _pageFiles.Controls.Add(actions);
        listHost.BringToFront();

        _txtPath.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { RequestList(_txtPath.Text); e.SuppressKeyPress = true; }
        };
        _lvFiles.ItemActivate += (_, _) => OpenSelected(); // dvojklik nebo Enter
        _lvFiles.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Back) RequestList(ParentPath(_remotePath));
            else if (e.KeyCode == Keys.Delete) DeleteSelected();
            else if (e.KeyCode == Keys.F5) RequestList(_remotePath);
        };
        _lvFiles.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        _lvFiles.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) _ = UploadToCurrentFolderAsync(files);
        };
        _lvFiles.Resize += (_, _) =>
        {
            int rest = _lvFiles.ClientSize.Width - _lvFiles.Columns[1].Width - _lvFiles.Columns[2].Width - 4;
            if (rest > 200) _lvFiles.Columns[0].Width = rest;
        };
    }

    private void InitFiles()
    {
        _remotePath = _client.Info.HomeDir ?? "~";
        _lblFiles.Text = "Tip: soubory a složky můžete do seznamu přetáhnout myší.";
    }

    private void DisposeFiles()
    {
        foreach (var t in _downloads.Values)
        {
            t.Stream?.Dispose();
            if (t.Stream != null && t.Done < t.Size) try { File.Delete(t.LocalPath); } catch { }
        }
        _downloads.Clear();
    }

    private static Bitmap GlyphBitmap(string glyph, Color color)
    {
        var bmp = new Bitmap(22, 22);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(color);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, Theme.Icon(12f), brush, new RectangleF(0, 0, 22, 22), sf);
        return bmp;
    }

    private void ShowFilesBanner(string text)
    {
        _filesBannerText.Text = text;
        _filesBanner.Height = Math.Max(48, TextRenderer.MeasureText(text, Theme.Body, new Size(Math.Max(300, _pageFiles.Width - 140), 0), TextFormatFlags.WordBreak).Height + 24);
        _filesBanner.Visible = true;
    }

    private void HideFilesBanner() => _filesBanner.Visible = false;

    // ---------------- cesty ----------------

    /// <summary>Spojení cesty na vzdáleném počítači – Mac používá /, Windows \ (C:\Users\…).</summary>
    private static string Combine(string dir, string name)
    {
        char sep = IsWindowsPath(dir) ? '\\' : '/';
        if (dir == "/") return "/" + name;
        return dir.TrimEnd('/', '\\') + sep + name.Replace(sep == '\\' ? '/' : '\\', sep);
    }

    private static bool IsWindowsPath(string p) => p.Length >= 2 && p[1] == ':' || p.Contains('\\');

    private static string ParentPath(string path)
    {
        if (IsWindowsPath(path))
        {
            string t = path.TrimEnd('\\', '/');
            if (t.Length <= 2) return t + "\\";            // C: → C:\
            int w = t.LastIndexOfAny(new[] { '\\', '/' });
            if (w <= 2) return t[..2] + "\\";              // C:\Users → C:\
            return t[..w];
        }
        path = path.TrimEnd('/');
        int i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} kB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    // ---------------- výpis ----------------

    private void RequestList(string path)
    {
        _client.PostJson(MsgType.ListDir, new PathRequest(path.Trim()));
    }

    private void OnDirListing(DirListing l)
    {
        if (!l.Ok)
        {
            ShowFilesBanner($"Složku {l.Path} nelze otevřít: {l.Error}");
            _txtPath.Text = _remotePath;
            return;
        }

        HideFilesBanner();
        _remotePath = l.Path;
        _txtPath.Text = l.Path;
        _entries = l.Entries;
        _lblFiles.ForeColor = Theme.TextDim;
        _lblFiles.Text = $"{l.Entries.Count(e => e.IsDir)} složek, {l.Entries.Count(e => !e.IsDir)} souborů";

        _lvFiles.BeginUpdate();
        _lvFiles.Items.Clear();
        foreach (var e in l.Entries)
        {
            var item = new ListViewItem(e.Name, e.IsDir ? "dir" : "file") { Tag = e };
            item.SubItems.Add(e.IsDir ? "" : FormatSize(e.Size));
            item.SubItems.Add(e.Modified.ToString("g"));
            if (e.Name.StartsWith('.')) item.ForeColor = Theme.TextDim;
            _lvFiles.Items.Add(item);
        }
        _lvFiles.EndUpdate();
    }

    private IEnumerable<DirEntry> SelectedEntries() =>
        _lvFiles.SelectedItems.Cast<ListViewItem>().Select(i => (DirEntry)i.Tag!);

    private void OpenSelected()
    {
        var sel = SelectedEntries().ToList();
        if (sel.Count == 1 && sel[0].IsDir) RequestList(Combine(_remotePath, sel[0].Name));
        else if (sel.Count > 0) DownloadSelected();
    }

    // ---------------- stahování ----------------

    private void DownloadSelected()
    {
        var files = SelectedEntries().Where(e => !e.IsDir).ToList();
        if (files.Count == 0)
        {
            MessageBox.Show(this, "Vyberte jeden nebo více souborů. Složky stahovat nelze – v Terminálu je můžete nejdřív zazipovat: zip -r slozka.zip slozka",
                "Stáhnout", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var targets = new List<(DirEntry Entry, string Local)>();
        if (files.Count == 1)
        {
            using var dlg = new SaveFileDialog { FileName = files[0].Name, Title = "Uložit soubor z Macu" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            targets.Add((files[0], dlg.FileName));
        }
        else
        {
            using var dlg = new FolderBrowserDialog { Description = "Kam uložit soubory z Macu", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            targets.AddRange(files.Select(f => (f, Path.Combine(dlg.SelectedPath, f.Name))));
        }

        foreach (var (entry, local) in targets)
        {
            int id = Interlocked.Increment(ref _transferId);
            _downloads[id] = new Transfer { LocalPath = local, Name = entry.Name, Size = entry.Size };
            _client.PostJson(MsgType.Download, new DownloadRequest(id, Combine(_remotePath, entry.Name)));
        }
        ShowTransferStatus($"Stahuji {targets.Count} soubor(ů)…", 0);
    }

    // následující tři metody běží na vlákně čtení
    private void OnFileStart(FileStart s)
    {
        if (!_downloads.TryGetValue(s.TransferId, out var t)) return;
        try
        {
            t.Size = s.Size;
            t.Stream = new FileStream(t.LocalPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        }
        catch (Exception ex)
        {
            _downloads.TryRemove(s.TransferId, out _);
            t.Completion?.TrySetResult($"Nelze zapsat {t.LocalPath}: {ex.Message}");
            if (!t.Silent) Ui(() => ShowTransferError($"Nelze zapsat {t.LocalPath}: {ex.Message}"));
        }
    }

    private void OnFileChunk(byte[] payload)
    {
        var (id, data) = Bin.ReadFileChunk(payload);
        if (!_downloads.TryGetValue(id, out var t) || t.Stream == null) return;
        try
        {
            t.Stream.Write(data);
        }
        catch (Exception ex)
        {
            // plný disk apod. – přenos ukončit, ne shodit čtení spojení
            _downloads.TryRemove(id, out _);
            try { t.Stream.Dispose(); } catch { }
            try { File.Delete(t.LocalPath); } catch { }
            t.Completion?.TrySetResult($"Nelze zapsat {t.LocalPath}: {ex.Message}");
            if (!t.Silent) Ui(() => ShowTransferError($"Nelze zapsat {t.LocalPath}: {ex.Message}"));
            return;
        }
        t.Done += data.Count;
        Interlocked.Add(ref _xferBytes, data.Count);
        if (!t.Silent && t.Done - t.LastReported > 2 * 1024 * 1024)
        {
            t.LastReported = t.Done;
            int pct = t.Size > 0 ? (int)(t.Done * 100 / t.Size) : 0;
            Ui(() => ShowTransferStatus($"Stahuji {t.Name}: {FormatSize(t.Done)} / {FormatSize(t.Size)}", pct));
        }
    }

    private void OnFileEnd(FileEnd e)
    {
        if (!_downloads.TryRemove(e.TransferId, out var t)) return;
        t.Stream?.Dispose();
        if (!e.Ok)
        {
            try { File.Delete(t.LocalPath); } catch { }
            t.Completion?.TrySetResult($"{t.Name}: {e.Error}");
            if (!t.Silent) Ui(() => ShowTransferError($"Stažení {t.Name} selhalo: {e.Error}"));
            return;
        }
        t.Completion?.TrySetResult(null);
        if (t.Silent) return;
        bool last = _downloads.Values.All(x => x.Silent);
        Ui(() =>
        {
            if (last) HideTransfer($"Staženo: {t.LocalPath}");
            else ShowTransferStatus($"Staženo {t.Name}, zbývá {_downloads.Count}…", 100);
        });
    }

    // ---------------- nahrávání do aktuální složky ----------------

    private void UploadViaDialogToFolder()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = $"Nahrát na Mac do {_remotePath}" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _ = UploadToCurrentFolderAsync(dlg.FileNames);
    }

    private async Task UploadToCurrentFolderAsync(string[] paths)
    {
        var names = paths.Select(p => Path.GetFileName(p.TrimEnd('\\', '/'))).ToList();
        var existing = names.Where(n => _entries.Any(e => e.Name == n)).ToList();
        bool overwrite = false;
        if (existing.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"Na Macu už existuje:\n{string.Join("\n", existing.Take(10))}{(existing.Count > 10 ? "\n…" : "")}\n\n" +
                "Ano = přepsat\nNe = uložit pod novým názvem (např. „soubor 2.txt“)",
                "Nahrát na Mac", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return;
            overwrite = answer == DialogResult.Yes;
        }

        string targetDir = _remotePath;
        var result = await UploadAsync(paths, targetDir, overwrite, (text, pct) => ShowTransferStatus(text, pct));
        if (result.Error != null) ShowTransferError(result.Error);
        else if (result.Files > 0) HideTransfer($"Nahráno {result.Files} soubor(ů) do {targetDir}");
    }

    private void OnFileOpResult(FileOpResult r)
    {
        if (!r.Ok)
        {
            string msg = r.Op switch
            {
                "upload" => $"Nahrání {Path.GetFileName(r.Path)} selhalo: ",
                "delete" => $"Smazání {Path.GetFileName(r.Path)} selhalo: ",
                "mkdir" => "Vytvoření složky selhalo: ",
                _ => r.Op + ": ",
            } + r.Message;
            if (_open != PanelKind.Files) _view.ShowToast(msg, Theme.Danger);
            ShowFilesBanner(msg);
        }
        else if (r.Op == "delete")
        {
            _lblFiles.ForeColor = Theme.TextDim;
            _lblFiles.Text = $"Přesunuto do koše: {Path.GetFileName(r.Path)}";
        }

        if (r.Ok && ParentPath(r.Path).TrimEnd('/', '\\') == _remotePath.TrimEnd('/', '\\'))
            RequestList(_remotePath); // změna v aktuální složce → obnovit výpis
    }

    // ---------------- další operace ----------------

    private void MakeDirectory()
    {
        var name = Dialogs.Prompt(this, "Nová složka", $"Název nové složky v {_remotePath}:", "Nová složka");
        if (string.IsNullOrWhiteSpace(name)) return;
        _client.PostJson(MsgType.MakeDir, new PathRequest(Combine(_remotePath, name.Trim())));
    }

    private void DeleteSelected()
    {
        var sel = SelectedEntries().ToList();
        if (sel.Count == 0) return;
        string list = string.Join("\n", sel.Take(10).Select(e => e.Name)) + (sel.Count > 10 ? $"\n… a dalších {sel.Count - 10}" : "");
        if (MessageBox.Show(this, $"Přesunout do koše na Macu?\n\n{list}", "Do koše",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        foreach (var e in sel) _client.PostJson(MsgType.Delete, new PathRequest(Combine(_remotePath, e.Name)));
    }

    private void OpenOnMac()
    {
        var sel = SelectedEntries().ToList();
        string path = sel.Count > 0 ? Combine(_remotePath, sel[0].Name) : _remotePath;
        _client.PostJson(MsgType.Action, new ActionRequest("open", path));
    }

    private void ShowTransferStatus(string text, int percent)
    {
        _fileProgress.Visible = true;
        _fileProgress.Value = percent;
        _lblFiles.ForeColor = Theme.Text;
        _lblFiles.Text = text;
    }

    private void HideTransfer(string text)
    {
        _fileProgress.Visible = false;
        _lblFiles.ForeColor = Theme.Success;
        _lblFiles.Text = text;
    }

    private void ShowTransferError(string text)
    {
        _fileProgress.Visible = false;
        _lblFiles.ForeColor = Theme.Danger;
        _lblFiles.Text = text;
    }
}
