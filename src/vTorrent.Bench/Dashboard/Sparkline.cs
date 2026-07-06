using System;
using System.Collections.Generic;

namespace vTorrent.Bench.Dashboard;

public sealed class Sparkline
{
    private readonly int _maxSamples;
    private readonly Queue<double> _samples;

    public Sparkline(int maxSamples = 60)
    {
        _maxSamples = maxSamples;
        _samples = new Queue<double>(maxSamples);
    }

    public void Add(double value)
    {
        if (_samples.Count >= _maxSamples) _samples.Dequeue();
        _samples.Enqueue(value);
    }

    public string Render(int width = 30)
    {
        if (_samples.Count == 0) return new string(' ', width);
        var values = new List<double>(_samples);
        double max = 0;
        foreach (var v in values) if (v > max) max = v;
        if (max == 0) max = 1;
        var blocks = " ▁▂▃▄▅▆▇█";
        var chars = new char[Math.Min(width, values.Count)];
        int start = Math.Max(0, values.Count - width);
        for (int i = 0; i < chars.Length; i++)
        {
            var normalized = values[start + i] / max;
            var idx = (int)(normalized * (blocks.Length - 1));
            chars[i] = blocks[Math.Clamp(idx, 0, blocks.Length - 1)];
        }
        return new string(chars).PadLeft(width);
    }
}
