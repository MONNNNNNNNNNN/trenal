namespace Trenal.Core;

/// <summary>Keys flowing from the UI thread to the engine thread, with push-back for multi-line paste.</summary>
public sealed class KeyQueue
{
    readonly LinkedList<Key> keys = new();
    readonly object gate = new();

    public void Add(IEnumerable<Key> items)
    {
        lock (gate)
        {
            foreach (var k in items) keys.AddLast(k);
            Monitor.PulseAll(gate);
        }
    }

    public void PushFront(IReadOnlyList<Key> items)
    {
        lock (gate)
        {
            for (int i = items.Count - 1; i >= 0; i--) keys.AddFirst(items[i]);
            Monitor.PulseAll(gate);
        }
    }

    public Key Take()
    {
        lock (gate)
        {
            while (keys.Count == 0) Monitor.Wait(gate);
            var k = keys.First!.Value;
            keys.RemoveFirst();
            return k;
        }
    }

    public bool Available
    {
        get { lock (gate) return keys.Count > 0; }
    }

    public void Clear()
    {
        lock (gate) keys.Clear();
    }
}
