namespace SPI.Twamp.Collector;

// Only start counting the next missing peer after the previous one has missed three cycles.
public sealed class CollectorPriority(int higherPeers)
{
    private readonly int[] missed = new int[higherPeers];
    public bool MayCollect(bool[] ready)
    {
        if (ready.Length != missed.Length) throw new ArgumentException("Unexpected priority list.");
        for (int i = 0; i < ready.Length; i++)
        {
            if (ready[i])
            {
                Array.Clear(missed, i, missed.Length - i);
                return false;
            }
            if (missed[i] < 3 && ++missed[i] < 3) return false;
            if (missed[i] == 3)
            {
                missed[i] = 4;
                if (i + 1 < ready.Length) return false;
            }
        }
        return true;
    }
}
