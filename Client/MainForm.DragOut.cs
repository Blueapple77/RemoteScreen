using RemoteScreen.Client.UI;
using RemoteScreen.Shared;

namespace RemoteScreen.Client;

/// <summary>Tažení souborů z druhého počítače (Finder / Průzkumník v obrazu) ven z okna do Windows.</summary>
public sealed partial class MainForm
{
    private bool _dragOutBusy;
    private TaskCompletionSource<DragFiles>? _dragFilesWaiter;

    private static string DragTempRoot => Path.Combine(Path.GetTempPath(), "RemoteScreen-drag");

    /// <summary>Myš opustila obraz se stisknutým tlačítkem. Pokud se na Macu táhnou soubory, převezmeme tažení.</summary>
    private async Task DragOutAsync()
    {
        if (_dragOutBusy || !_streaming) return;
        _dragOutBusy = true;
        try
        {
            var tcs = new TaskCompletionSource<DragFiles>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _dragFilesWaiter, tcs)?.TrySetCanceled();
            _client.Post(MsgType.DragQuery);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            Interlocked.CompareExchange(ref _dragFilesWaiter, null, tcs);
            if (done != tcs.Task || !tcs.Task.IsCompletedSuccessfully) return;

            var info = tcs.Task.Result;
            if (info.Items.Length == 0) return; // na Macu se netáhne soubor (např. okno) – nic nedělat

            // zrušit tažení na Macu (Esc) a pustit tlačítko, ať tam nic nezůstane „viset“
            _client.Post(MsgType.Key, Bin.Key(0x35, true));
            _client.Post(MsgType.Key, Bin.Key(0x35, false));
            _client.Post(MsgType.MouseButton, Bin.MouseButton(0, false, 1, 0.5f, 0.5f));

            long total = info.Items.Sum(i => i.Size);
            string what = FileTransfers.Describe(info.Roots);
            _view.ShowToast($"⇣ {what} – připravuji…", Theme.Accent, sticky: true, progress: 0);
            SetTransfer($"⇣ {what} 0 %");

            // stáhnout do dočasné složky
            string temp = Path.Combine(DragTempRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(temp);
            var transfers = new List<Transfer>();
            foreach (var item in info.Items)
            {
                // relativní cesta od druhého počítače nesmí vést mimo dočasnou složku („../“)
                string? local = FileTransfers.SafeLocalPath(temp, item.Relative);
                if (local == null)
                {
                    _view.ShowToast("Přenos odmítnut – neplatná cesta souboru: " + item.Relative, Theme.Danger);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                int id = Interlocked.Increment(ref _transferId);
                var t = new Transfer
                {
                    LocalPath = local, Name = Path.GetFileName(local), Size = item.Size, Silent = true,
                    Completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously),
                };
                _downloads[id] = t;
                transfers.Add(t);
                _client.PostJson(MsgType.Download, new DownloadRequest(id, item.Path));
            }

            // Kde uživatel pustil tlačítko, pokud to udělal dřív, než bylo staženo (typicky u prvního
            // přenosu nebo velkých souborů). Pak soubory uložíme právě tam – ne do Stažených souborů.
            Point? releasedAt = null;
            void CheckRelease()
            {
                if (releasedAt == null && (Control.MouseButtons & MouseButtons.Left) == 0) releasedAt = Cursor.Position;
            }
            CheckRelease();

            var all = Task.WhenAll(transfers.Select(t => t.Completion!.Task));
            var uiTick = System.Diagnostics.Stopwatch.StartNew();
            while (!all.IsCompleted)
            {
                await Task.WhenAny(all, Task.Delay(30));
                CheckRelease();
                if (all.IsCompleted) break;
                if (uiTick.ElapsedMilliseconds < 200) continue;
                uiTick.Restart();
                if (_disconnected || !_client.IsConnected)
                {
                    foreach (var t in transfers) t.Completion!.TrySetResult("Spojení bylo přerušeno.");
                    break;
                }
                long done2 = transfers.Sum(t => t.Done);
                int pct = total > 0 ? (int)(done2 * 100 / total) : 100;
                _view.ShowToast($"⇣ {what} – {FileTransfers.ProgressText(done2, total)}", Theme.Accent, sticky: true, progress: pct);
                SetTransfer($"⇣ {what} {pct} %");
            }
            var errors = (await all).Where(e => e != null).ToList();
            if (errors.Count > 0)
            {
                _view.ShowToast($"Přenos z {_targetName} selhal: " + errors[0], Theme.Danger);
                return;
            }

            var roots = info.Roots.Select(r => Path.Combine(temp, r)).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            if (roots.Length == 0) return;

            CheckRelease();
            if (releasedAt == null)
            {
                // tlačítko je pořád stisknuté → pokračovat jako běžné tažení ve Windows
                _view.ShowToast("Pusťte soubory, kam potřebujete (Průzkumník, plocha, aplikace…)", Theme.Accent);
                _view.SuppressDrop = true;
                DragDropEffects effect;
                try
                {
                    var data = new DataObject(DataFormats.FileDrop, roots);
                    effect = _view.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
                }
                finally
                {
                    _view.SuppressDrop = false;
                }
                if (effect != DragDropEffects.None)
                {
                    _view.ShowToast($"Hotovo – {what} zkopírováno do Windows.", Theme.Success);
                    return;
                }
                RemoteScreen.Host.Log.Info("Tažení ve Windows skončilo bez cíle – ukládám podle místa puštění.");
                releasedAt = Cursor.Position;
            }

            // tlačítko bylo puštěné dřív, než se soubory stáhly (nebo tažení nic nepřijalo):
            // plocha / okno Průzkumníka pod místem puštění, jinak Stažené soubory
            string? folder = releasedAt is Point rp && !_view.RectangleToScreen(_view.ClientRectangle).Contains(rp)
                ? Host.WinPlatform.LocalFolderAt(rp) : null;
            bool toDownloads = folder == null;
            string saved = SaveTo(folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), roots);
            string where = toDownloads ? "Stažené soubory"
                : folder == Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) ? "Plocha"
                : "složka „" + Path.GetFileName(folder!.TrimEnd('\\')) + "“";
            _view.ShowToast($"Hotovo – {what} uloženo: {where}", Theme.Success);
            // Stažené soubory nejsou vidět – ukázat je; u plochy/okna Průzkumníka soubory uživatel vidí hned
            if (toDownloads)
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{saved}\""); } catch { }
        }
        catch (Exception ex)
        {
            _view.ShowToast($"Přenos z {_targetName} selhal: " + ex.Message, Theme.Danger);
        }
        finally
        {
            _dragOutBusy = false;
            SetTransfer(null);
        }
    }

    /// <summary>Přesune stažené položky do cílové složky (bez přepisování). Vrací cestu k první.</summary>
    private static string SaveTo(string downloads, string[] roots)
    {
        Directory.CreateDirectory(downloads);
        string first = downloads;
        foreach (var src in roots)
        {
            string name = Path.GetFileName(src);
            string dest = Path.Combine(downloads, name);
            for (int i = 2; File.Exists(dest) || Directory.Exists(dest); i++)
                dest = Path.Combine(downloads, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
            if (Directory.Exists(src)) Directory.Move(src, dest); else File.Move(src, dest);
            if (first == downloads) first = dest;
        }
        return first;
    }

    /// <summary>Smaže dočasné soubory z dřívějších tažení (volá se při zavření okna).</summary>
    private static void CleanupDragTemp()
    {
        try
        {
            if (!Directory.Exists(DragTempRoot)) return;
            foreach (var dir in Directory.GetDirectories(DragTempRoot))
            {
                // nedávné nechat – Průzkumník je může ještě kopírovat
                if (Directory.GetCreationTime(dir) < DateTime.Now.AddHours(-1))
                    try { Directory.Delete(dir, true); } catch { }
            }
        }
        catch { }
    }
}
