using RemoteScreen.Client.UI;
using RemoteScreen.Shared;

namespace RemoteScreen.Client;

public sealed partial class MainForm
{
    private bool _uploading;
    private TaskCompletionSource<DropTarget>? _dropTargetWaiter;

    private readonly record struct UploadResult(int Files, string? Error, List<string> Roots);

    /// <summary>Soubory přetažené do obrazu Macu (nebo vybrané tlačítkem „Nahrát na Mac“ na ploše).
    /// x, y = místo puštění v obrazu (0..1), záporné = neznámé.</summary>
    private async Task UploadDroppedAsync(string[] paths, float x = -1, float y = -1)
    {
        if (_uploading)
        {
            _view.ShowToast("Už probíhá nahrávání – počkejte na jeho dokončení.", Theme.Warning);
            return;
        }

        string what = FileTransfers.Describe(paths);
        _view.ShowToast($"⇡ {what} – připravuji…", Theme.Accent, sticky: true, progress: 0);
        var target = await GetDropTargetAsync(x, y);

        UploadResult result;
        try
        {
            result = await UploadAsync(paths, target.Path, overwrite: false, (text, pct) =>
            {
                _view.ShowToast($"⇡ {what} – {text}", Theme.Accent, sticky: true, progress: pct);
                SetTransfer($"⇡ {what} {pct} %");
            });
        }
        finally
        {
            SetTransfer(null);
        }

        if (result.Error != null)
        {
            _view.ShowToast(result.Error, Theme.Danger);
        }
        else if (result.Files > 0 && target.OpenWithApp != null)
        {
            // puštěno nad aplikací → otevřít v ní; skutečné cesty (soubor mohl dostat číslo „report 2.pdf“)
            _client.PostJson(MsgType.Action, new ActionRequest("open_with", target.OpenWithApp + "\n" + string.Join("\n", result.Roots)));
            _view.ShowToast($"Hotovo – otevírám v: {target.Label.Replace("aplikace ", "")}", Theme.Success);
        }
        else if (result.Files > 0)
        {
            _view.ShowToast($"Hotovo – {what} → {_targetName}: {target.Label}", Theme.Success); // co uživatel přetáhl, ne počet souborů uvnitř složek
        }
        else
        {
            _view.ShowToast("Nebylo co nahrát.", Theme.Warning);
        }
    }

    private void UploadViaDialogToMac()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = "Nahrát na Mac (do otevřené složky Finderu, jinak na Plochu)" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _ = UploadDroppedAsync(dlg.FileNames);
        _view.Focus();
    }

    /// <summary>Zeptá se serveru, kam uložit přetažené soubory (složka Finderu v popředí, jinak Plocha).</summary>
    private async Task<DropTarget> GetDropTargetAsync(float x, float y)
    {
        var tcs = new TaskCompletionSource<DropTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _dropTargetWaiter, tcs)?.TrySetCanceled();
        _client.PostJson(MsgType.DropTargetGet, new DropQuery(x, y));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(20000));
        if (done == tcs.Task && tcs.Task.IsCompletedSuccessfully) return tcs.Task.Result;
        Interlocked.CompareExchange(ref _dropTargetWaiter, null, tcs);
        string home = _client.Info.HomeDir ?? "~";
        return new DropTarget(Combine(home, "Desktop"), "Plocha (druhý počítač neodpověděl včas)");
    }

    /// <summary>Nahraje soubory i celé složky (rekurzivně) do složky na druhém počítači – společný kód
    /// s klientem na Macu (čeká na potvrzení každého souboru, hlásí chyby, vrací skutečné cesty).</summary>
    private async Task<UploadResult> UploadAsync(string[] paths, string remoteDir, bool overwrite, Action<string, int> progress)
    {
        if (_uploading) return new UploadResult(0, "Už probíhá jiné nahrávání.", new());
        _uploading = true;
        try
        {
            var r = await _files.UploadAsync(paths, remoteDir, overwrite, progress);
            return new UploadResult(r.Files, r.Error, r.RemoteRoots);
        }
        finally
        {
            _uploading = false;
        }
    }
}
