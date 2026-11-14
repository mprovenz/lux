using Lux.Library;

namespace Lux.App.Services;

/// <summary>
/// The Lumen sessions of a scope in the import dialog's three groups, from the library's own classification
/// (<see cref="CaptureItem.LumenSession"/>), and the import with the tone-style choice.
/// </summary>
public sealed class ContractLumenScanner : ILumenScanner
{
    readonly ILibrary _lib;
    public ContractLumenScanner(ILibrary lib) => _lib = lib;

    public Task<LumenScanResult> ScanAsync(CaptureQuery scope, string folderLabel, CancellationToken cancel) => Task.Run(async () =>
    {
        var depth = new List<string>(); var changed = new List<string>(); var unchanged = new List<string>(); int imported = 0;
        int offset = 0;
        while (true)
        {
            var page = await _lib.QueryAsync(scope with { HasLumenSession = true, Offset = offset, Limit = 2000 }, cancel).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                cancel.ThrowIfCancellationRequested();
                switch (item.LumenSession)
                {
                    case LumenSessionState.DepthEdited: depth.Add(item.Id); break;
                    case LumenSessionState.SettingsChanged: changed.Add(item.Id); break;
                    case LumenSessionState.OpenedUnchanged: unchanged.Add(item.Id); break;
                    default: continue;
                }
                if (item.Edits.Source == EditSource.Lumen) imported++;
            }
            offset += page.Items.Count;
            if (page.Items.Count == 0 || offset >= page.Total) break;
        }
        return new LumenScanResult(folderLabel, depth, changed, unchanged, imported);
    }, cancel);

    public IReadOnlyList<long> Import(IReadOnlyCollection<string> captureIds, bool newerToneStyle) => _lib.ImportLumenEdits(captureIds, newerToneStyle);
}
