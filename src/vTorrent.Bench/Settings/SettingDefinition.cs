using System;

namespace vTorrent.Bench.Settings;

public sealed class SettingDefinition
{
    public string Group { get; init; } = "";
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public Type ValueType { get; init; } = typeof(int);
    public object Min { get; init; } = 0;
    public object Max { get; init; } = int.MaxValue;
    public object Step { get; init; } = 1;
    public Func<object> Getter { get; init; } = () => 0;
    public Action<object> Setter { get; init; } = _ => { };
    public object? InitialValue { get; set; }

    public object Increase()
    {
        var current = Getter();
        object next;
        if (ValueType == typeof(int))
            next = Math.Min((int)(object)Max, (int)current + (int)(object)Step);
        else if (ValueType == typeof(float))
            next = Math.Min((float)(object)Max, (float)current + (float)(object)Step);
        else if (ValueType == typeof(long))
            next = Math.Min((long)(object)Max, (long)current + (long)(object)Step);
        else if (ValueType == typeof(bool))
            next = !(bool)current;
        else if (ValueType.IsEnum)
        {
            var values = Enum.GetValues(ValueType);
            var idx = Array.IndexOf(values, current);
            next = values.GetValue((idx + 1) % values.Length)!;
        }
        else return current;
        Setter(next);
        return next;
    }

    public object Decrease()
    {
        var current = Getter();
        object next;
        if (ValueType == typeof(int))
            next = Math.Max((int)(object)Min, (int)current - (int)(object)Step);
        else if (ValueType == typeof(float))
            next = Math.Max((float)(object)Min, (float)current - (float)(object)Step);
        else if (ValueType == typeof(long))
            next = Math.Max((long)(object)Min, (long)current - (long)(object)Step);
        else if (ValueType == typeof(bool))
            next = !(bool)current;
        else if (ValueType.IsEnum)
        {
            var values = Enum.GetValues(ValueType);
            var idx = Array.IndexOf(values, current);
            next = values.GetValue((idx - 1 + values.Length) % values.Length)!;
        }
        else return current;
        Setter(next);
        return next;
    }

    public string FormatValue()
    {
        var val = Getter();
        if (ValueType == typeof(bool)) return (bool)val ? "yes" : "no";
        if (ValueType.IsEnum) return val.ToString()!;
        return val.ToString()!;
    }

    public bool HasChanged() => InitialValue != null && !Equals(InitialValue, Getter());
    public string FormatInitial() => InitialValue?.ToString() ?? "?";
}
