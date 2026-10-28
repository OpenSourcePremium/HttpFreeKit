using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace HttpFreeKit.Core;


public sealed class TrafficStore
{
    public ObservableCollection<TrafficEntry> Items { get; } = new();
    public bool Paused { get; set; }
    public int MaxItems { get; set; } = 3000;
    public long RequestBodyBudgetBytes { get; set; } = 200L * 1024 * 1024;

    private readonly ConcurrentQueue<TrafficEntry> _queue = new();
    private readonly DispatcherTimer _timer;

    public TrafficStore()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    public void Enqueue(TrafficEntry entry)
    {
        if (!Paused) _queue.Enqueue(entry);
    }

    public void EnqueueBreakpoint(TrafficEntry entry) => _queue.Enqueue(entry);

    private void Flush()
    {
        int n = 0;
        while (n < 400 && _queue.TryDequeue(out var e)) { Items.Add(e); n++; }
        while (Items.Count > MaxItems)
        {
            int removableIndex = Items.ToList().FindIndex(item => !item.IsBreakpointPending);
            if (removableIndex < 0) break;
            Items.RemoveAt(removableIndex);
        }
        TrimRequestBodies();
    }

    private void TrimRequestBodies()
    {
        long total = Items.Sum(item => item.RequestReplayBody?.LongLength ?? 0);
        if (total <= RequestBodyBudgetBytes) return;
        foreach (var item in Items)
        {
            if (total <= RequestBodyBudgetBytes) break;
            if (item.IsBreakpointPending || item.IsHarImported || item.RequestReplayBody is not { } body) continue;
            total -= body.LongLength;
            item.RequestReplayBody = null;
            item.RequestBodyIncomplete = true;
        }
    }

    public void Clear()
    {
        var pending = new Dictionary<long, TrafficEntry>();
        foreach (var item in Items.Where(item => item.IsBreakpointPending))
            pending[item.Id] = item;

        while (_queue.TryDequeue(out var queued))
            if (queued.IsBreakpointPending)
                pending[queued.Id] = queued;

        Items.Clear();
        foreach (var item in pending.Values.OrderBy(item => item.Id))
            Items.Add(item);
    }
}
