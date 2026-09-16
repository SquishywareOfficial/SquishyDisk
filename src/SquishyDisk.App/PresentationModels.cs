using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using SquishyDisk.Core;

namespace SquishyDisk.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
public sealed class CellViewModel(int row, TestDirection direction) : Observable
{
    public int Row { get; } = row;
    public TestDirection Direction { get; } = direction;
    public string AccessibleName => $"Run {Direction.ToString().ToLowerInvariant()} for workload {Row + 1}";
    public string Value { get; private set; } = "—";
    public string Hint { get; private set; } = "Not run";
    public void Set(PassResult? result, ResultUnit unit, string? hint = null)
    {
        Value = result == null ? "—" : result.Value(unit).ToString(unit == ResultUnit.IOPS ? "N0" : "N2", CultureInfo.CurrentCulture);
        Hint = hint ?? (result == null ? "Not run" : $"Pass {result.Pass} · {UnitLabel(unit)}");
        Changed(nameof(Value)); Changed(nameof(Hint));
    }
    public static string UnitLabel(ResultUnit unit) => unit switch { ResultUnit.MBs => "MB/s", ResultUnit.GBs => "GB/s", ResultUnit.IOPS => "IOPS", _ => "µs average" };
}
public sealed class RowViewModel(int index, Workload workload) : Observable
{
    public int Index { get; } = index;
    public Workload Workload { get; } = workload;
    public string Title => Workload.Label;
    public string Detail => Workload.Detail;
    public string AccessibleName => $"Run {Workload.Label}, queue depth {Workload.QueueDepth}, {Workload.Threads} threads";
    public CellViewModel Read { get; } = new(index, TestDirection.Read);
    public CellViewModel Write { get; } = new(index, TestDirection.Write);
    public bool CanRun { get; private set; } = true;
    public void Enable(bool value) { CanRun = value; Changed(nameof(CanRun)); }
}
public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}
