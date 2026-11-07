namespace GameShift.Core.Presentation;

/// <summary>A point of a sparkline in the drawing box, in DIP from the top left.</summary>
public readonly record struct SparkPoint(double X, double Y);

/// <summary>
/// The last <see cref="Capacity"/> samples of one live value, oldest first. A sample that could not
/// be read is pushed as <see cref="double.NaN"/>, and the line breaks there instead of dropping to 0.
/// </summary>
public sealed class SparklineBuffer
{
    /// <summary>A minute of samples taken once a second.</summary>
    public const int DefaultCapacity = 60;

    private readonly double[] _values;
    private int _start;
    private int _count;

    /// <param name="capacity">Samples kept; pick it so the line covers the same time as the other tiles.</param>
    public SparklineBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _values = new double[capacity];
    }

    public int Capacity => _values.Length;

    /// <summary>Raised after every push or clear, on the thread that made it.</summary>
    public event EventHandler? Changed;

    public int Count => _count;

    /// <summary>Adds a sample; the oldest falls out once the buffer is full. Infinity is stored as a gap.</summary>
    public void Push(double value)
    {
        if (!double.IsFinite(value)) value = double.NaN;

        if (_count < Capacity)
        {
            _values[(_start + _count) % Capacity] = value;
            _count++;
        }
        else
        {
            _values[_start] = value;
            _start = (_start + 1) % Capacity;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The samples, oldest first.</summary>
    public double[] Snapshot()
    {
        var copy = new double[_count];
        for (int i = 0; i < _count; i++)
            copy[i] = _values[(_start + i) % Capacity];
        return copy;
    }
}

/// <summary>
/// Where a sparkline's samples go in its box. Every sample has a fixed slot, the newest at the right
/// edge, so the line moves at one speed while the buffer fills. The scale runs from
/// <c>bottom</c> to <c>floorTop</c>, or to the highest sample shown when that is higher, so a spike
/// reaches the top instead of leaving the box.
/// </summary>
public static class SparklineScale
{
    /// <summary>The value at the top edge for these samples. Gaps do not count.</summary>
    public static double Top(IReadOnlyList<double> values, double bottom, double floorTop)
    {
        double top = floorTop;
        foreach (var v in values)
        {
            if (double.IsFinite(v) && v > top) top = v;
        }
        return top > bottom ? top : bottom + 1;
    }

    /// <summary>
    /// The line as runs of readable samples; a gap ends a run. A run can hold a single point,
    /// which the caller draws as a dot.
    /// </summary>
    /// <param name="values">Samples, oldest first; only the last <paramref name="capacity"/> are shown.</param>
    /// <param name="inset">Kept free on every edge so the stroke is not cut off; usually half the stroke width.</param>
    public static IReadOnlyList<IReadOnlyList<SparkPoint>> Segments(
        IReadOnlyList<double> values, int capacity, double width, double height,
        double bottom, double floorTop, double inset)
    {
        var segments = new List<IReadOnlyList<SparkPoint>>();
        double usableWidth = width - 2 * inset;
        double usableHeight = height - 2 * inset;
        if (values.Count == 0 || capacity < 2 || !(usableWidth > 0) || !(usableHeight > 0))
            return segments;

        int shown = Math.Min(values.Count, capacity);
        var visible = new double[shown];
        for (int i = 0; i < shown; i++)
            visible[i] = values[values.Count - shown + i];

        double top = Top(visible, bottom, floorTop);
        double step = usableWidth / (capacity - 1);
        int firstSlot = capacity - shown;

        List<SparkPoint>? run = null;
        for (int i = 0; i < shown; i++)
        {
            double v = visible[i];
            if (!double.IsFinite(v))
            {
                if (run != null) segments.Add(run);
                run = null;
                continue;
            }

            double share = (Math.Clamp(v, bottom, top) - bottom) / (top - bottom);
            run ??= new List<SparkPoint>();
            run.Add(new SparkPoint(inset + (firstSlot + i) * step, inset + usableHeight * (1 - share)));
        }
        if (run != null) segments.Add(run);
        return segments;
    }
}
