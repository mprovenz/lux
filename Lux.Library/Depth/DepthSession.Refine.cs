using System.Text;
using Lux.DepthModel;
using Lux.Engine.Pipeline;
using Lux.Engine.Pipeline.DepthRefine;
using Lux.Engine.Session;
using Lux.Store;

namespace Lux.Library.Depth;

/// <summary>The refined version of the photo's depth (<see cref="IDepthEdit.RefineDepth"/>): made once from the clean depth with the depth
/// network, stored in the photo's edit database as a version of its own, and from then on handled like every other version (switching,
/// editing, exports).</summary>
internal sealed partial class DepthSession
{
    public DepthJob RefineDepth() => _worker.Enqueue("Refine depth", c =>
    {
        if (_ed is null || _st is null) return new DepthJobResult(DepthJobOutcome.Failed, "The depth of this photo is not ready yet.");
        // the version is made once: a second request switches to it
        var have = _store?.Variants().FirstOrDefault(v => v.Kind == DepthEditStore.KindRefined);
        if (have is not null)
        {
            if (_variant?.Name == have.Name) return new DepthJobResult(DepthJobOutcome.NothingChanged, "This photo already uses its refined depth.");
            c.Report("Switching to the refined depth");
            _store!.ActiveVariant = have.Name;
            Reattach(c);
            return new DepthJobResult(DepthJobOutcome.Done, null) { ChangedRect = NormRect.Full };
        }
        OnnxDepthNetwork net;
        try { net = OnnxDepthNetwork.Open(_s.Lib.DepthModelManager); }
        catch (DepthModelException e) { return new DepthJobResult(DepthJobOutcome.Failed, e.Message); }
        using (net)
        {
            c.CanCancel = true;
            c.Report("Preparing the photo");
            // the refinement starts from the photo's own depth
            _store ??= DepthEditStore.OpenOrCreate(_s.Context);
            string? before = _store.ActiveVariant;
            if (_variant is not null) { _store.ActiveVariant = null; Reattach(c); }
            var st = _st!;
            RefinedDepth refined;
            try
            {
                var progress = new ProgressReporter(u => c.Report("Refining the depth", u.Total > 0 ? Math.Min(1.0, (double)u.Done / u.Total) : null));
                refined = DepthRefiner.Compute(st, net, _s.Engine.Options.Threads, progress, c.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or DepthModelException or ExportRequestException)
            {
                if (before is not null) { _store.ActiveVariant = before; Reattach(c); }
                return e is OperationCanceledException
                    ? new DepthJobResult(DepthJobOutcome.Cancelled, null)
                    : new DepthJobResult(DepthJobOutcome.Failed, "The depth could not be refined: " + e.Message);
            }
            if (!refined.Report.Applied)
            {
                if (before is not null) { _store.ActiveVariant = before; Reattach(c); }
                return new DepthJobResult(DepthJobOutcome.NothingChanged, "The depth was not refined: " + refined.Report.Reason + ".");
            }
            c.CanCancel = false;
            c.Report("Saving the refined depth");
            try
            {
                // a version pinned on the refined depth, made active; then everything is attached again from the database, as for any version
                st.FullDepth = (refined.Depth, refined.W, refined.H);
                var p = DepthEditPersistence.CreateNow(_store, new DepthEditSession(st).Editor, DepthEditStore.KindRefined, DepthEditStore.KindRefined);
                string info = FormattableString.Invariant($"network {refined.Network}\nrules {RefineConstants.RulesVersion}\n");
                _store.WriteBlob(p.Variant!.Id, DepthEditStore.RefineInfoBlob, Encoding.UTF8.GetBytes(info));
                p.Detach();
            }
            catch (DepthEditStoreException e)
            {
                _store.ActiveVariant = before;
                Reattach(c);
                return new DepthJobResult(DepthJobOutcome.Failed, "The refined depth could not be saved: " + e.Message);
            }
            Reattach(c);
            // a capture without other cameras that can check the depth: say that only the sky was corrected
            string? note = refined.Report.SkyOnly ? "Only the sky was corrected: " + refined.Report.Reason : null;
            return new DepthJobResult(DepthJobOutcome.Done, note) { ChangedRect = NormRect.Full };
        }
    }, cancellable: true);
}
