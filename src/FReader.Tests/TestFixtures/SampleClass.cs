using System;
using System.Collections.Generic;

namespace TestFixtures;

/// <summary>
/// A sample class for testing FReader tools.
/// </summary>
public class SampleClass<T>
{
    private readonly int _value;
    private readonly string _name;

    public SampleClass(int value, string name)
    {
        _value = value;
        _name = name;
    }

    public SampleClass() : this(0, "default") { }

    public int Value => _value;

    public string Name
    {
        get { return _name; }
        set { }
    }

    public string ComputeResult<TInput>(TInput input, int multiplier)
    {
        var result = input?.ToString() ?? "";
        for (int i = 0; i < multiplier; i++)
            result += result;
        return result;
    }

    public string ComputeResult(int multiplier)
    {
        return ComputeResult(_value, multiplier);
    }

    public async Task<string> ProcessAsync(string data, CancellationToken ct = default)
    {
        await Task.Delay(1, ct);
        return data.Trim();
    }

    public static SampleClass<T> Create(string name, int value)
    {
        return new SampleClass<T>(value, name);
    }

    public override string ToString() => $"{_name}: {_value}";

    ~SampleClass() { }
}

public static class HelperClass
{
    public static int Add(int a, int b) => a + b;

    public static int Add(int a, int b, int c) => a + b + c;
}
