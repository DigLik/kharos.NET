using System.Runtime.InteropServices;

namespace Kharos.Mod.Sdk.Runtime;

/// <summary>
/// Часть кадра системы — главный блок или побочная таблица: столбцы f64 (столбец k — с k·n) и номера сущностей в
/// памяти вне GC (их адреса отдаются хосту), счёт этого вызова и указатель «сущность → строка».
/// </summary>
internal sealed unsafe class FramePart(int width)
{
    private int[] _rowOf = [];
    private int[] _stamp = [];
    private int _generation;
    private bool _indexed;
    private bool _owned;

    /// <summary>Столбцов f64.</summary>
    public int Width { get; } = width;

    /// <summary>На сколько сущностей выделено.</summary>
    public int Capacity { get; private set; }

    public double* Data { get; private set; }
    public uint* Entities { get; private set; }

    /// <summary>Сущностей в этом вызове.</summary>
    public int Count { get; private set; }

    /// <summary>Часть над чужими буферами (SystemContext.Over: тесты, сверка) — не освобождает их.</summary>
    public static FramePart Over(int width, double* data, uint* entities, int count)
    {
        var part = new FramePart(width) { Data = data, Entities = entities, Capacity = count };
        part.Begin(count);
        return part;
    }

    /// <summary>Буферы не меньше чем на n сущностей; false — нет памяти.</summary>
    public bool Reserve(int n)
    {
        if (n <= Capacity && Data is not null)
            return true;
        var capacity = Math.Max(n, Math.Max(16, Capacity + Capacity / 2));
        try
        {
            Free();
            Data = (double*)NativeMemory.AlignedAlloc((nuint)Math.Max(1, Width) * (nuint)capacity * sizeof(double), 16);
            Entities = (uint*)NativeMemory.AlignedAlloc((nuint)capacity * sizeof(uint), 16);
            Capacity = capacity;
            _owned = true;
            return true;
        }
        catch (OutOfMemoryException)
        {
            Free();
            return false;
        }
    }

    /// <summary>Начало вызова: счёт сущностей, указатель строк — заново.</summary>
    public void Begin(int count)
    {
        Count = count;
        _indexed = false;
    }

    public ReadOnlySpan<uint> EntitySpan => new(Entities, Count);

    /// <summary>Столбец с номера offset (в столбцах).</summary>
    public Span<double> Column(int offset) => new(Data + (long)offset * Count, Count);

    /// <summary>Строка сущности; нет — -1. Указатель строится при первом вызове за вызов системы (O(n)).</summary>
    public int RowOf(uint entity)
    {
        if (!_indexed)
            Index();
        return entity < (uint)_stamp.Length && _stamp[entity] == _generation ? _rowOf[entity] : -1;
    }

    public void Free()
    {
        if (_owned)
        {
            NativeMemory.AlignedFree(Data);
            NativeMemory.AlignedFree(Entities);
        }
        Data = null;
        Entities = null;
        Capacity = 0;
        _owned = false;
    }

    private void Index()
    {
        _indexed = true;
        if (++_generation == int.MaxValue)
        {
            Array.Clear(_stamp);
            _generation = 1;
        }
        var entities = EntitySpan;
        uint top = 0;
        foreach (var entity in entities)
            top = Math.Max(top, entity);
        if (entities.Length > 0 && top >= (uint)_stamp.Length)
        {
            var size = (int)Math.Min(int.MaxValue - 64, Math.Max(top + 1, (uint)_stamp.Length * 2));
            Array.Resize(ref _stamp, size);
            Array.Resize(ref _rowOf, size);
        }
        for (var row = 0; row < entities.Length; row++)
        {
            _stamp[entities[row]] = _generation;
            _rowOf[entities[row]] = row;
        }
    }
}
