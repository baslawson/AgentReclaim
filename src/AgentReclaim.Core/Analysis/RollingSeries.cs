namespace AgentReclaim.Core.Analysis;

/// <summary>The last few values of something, oldest first, for a sparkline. Small and fixed, so it can run all the time.</summary>
public sealed class RollingSeries(int capacity)
{
    private readonly double[] _values = new double[capacity];
    private int _next;
    private int _count;

    public void Add(double value)
    {
        _values[_next] = value;
        _next = (_next + 1) % capacity;
        _count = Math.Min(_count + 1, capacity);
    }

    public double[] ToArray()
    {
        var result = new double[_count];
        for (var i = 0; i < _count; i++) result[i] = _values[(_next - _count + i + capacity) % capacity];
        return result;
    }
}
