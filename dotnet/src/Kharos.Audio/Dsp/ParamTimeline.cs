using Kharos.Audio.Graph;

namespace Kharos.Audio.Dsp;

/// <summary>
/// Расписание AudioParam по спецификации Web Audio (§ AudioParam automation): setValueAtTime, линейный и
/// экспоненциальный спуск, setTargetAtTime, отмена. Время запросов не убывает: прошедшие события снимаются.
/// </summary>
internal sealed class ParamTimeline
{
    private readonly List<Event> _pending = [];
    private State _state;

    public ParamTimeline(float value)
    {
        _state.AnchorValue = value;
        _state.AnchorTime = double.NegativeInfinity;
        Value = value;
    }

    /// <summary>Последнее вычисленное значение (без входов и ограничения).</summary>
    public float Value { get; private set; }

    /// <summary>Нет событий и нет идущего setTarget: значение постоянно.</summary>
    public bool IsIdle => _pending.Count == 0 && !_state.Target;

    /// <summary>Добавить событие; now — время контекста в миг вызова (нужно рампам без предыдущего события).</summary>
    public void Insert(AutomationKind kind, float value, double time, double timeConstant, double now)
    {
        if (double.IsNaN(time))
            time = now;
        time = Math.Max(time, 0);
        switch (kind)
        {
            case AutomationKind.Cancel:
                _pending.RemoveAll(e => e.Time >= time);
                return;
            case AutomationKind.CancelAndHold:
                CancelAndHold(time);
                return;
            case AutomationKind.Linear or AutomationKind.Exponential:
                // Рампа без предыдущего события начинается с нынешнего значения в миг вызова.
                if (!_state.History && !_pending.Exists(e => e.Time <= time))
                    Add(new Event(AutomationKind.Set, Peek(now), now, 0, now));
                break;
        }
        Add(new Event(kind, value, time, timeConstant, now));
    }

    /// <summary>Значение в миг t (t не убывает между вызовами).</summary>
    public float At(double t)
    {
        var index = 0;
        var value = Evaluate(ref _state, t, ref index);
        if (index > 0)
            _pending.RemoveRange(0, index);
        // Цель setTarget достигнута — дальше значение постоянно.
        if (_state.Target && Math.Abs(value - _state.TargetTo) <= 1e-7f * Math.Max(1f, Math.Abs(_state.TargetTo)) &&
            (_pending.Count == 0 || _pending[0].Kind is AutomationKind.Set or AutomationKind.Target))
        {
            _state.Target = false;
            _state.AnchorValue = _state.TargetTo;
            _state.AnchorTime = t;
            value = _state.TargetTo;
        }
        Value = value;
        return value;
    }

    /// <summary>
    /// Значение постоянно на [from, to]: вызывает <see cref="At"/>(from) и проверяет, что до to ничего не меняется.
    /// </summary>
    public bool Steady(double from, double to, out float value)
    {
        value = At(from);
        if (_state.Target)
            return false;
        if (_pending.Count == 0)
            return true;
        var next = _pending[0];
        return next.Kind is AutomationKind.Set or AutomationKind.Target && next.Time > to;
    }

    private float Peek(double t)
    {
        var state = _state;
        var index = 0;
        return Evaluate(ref state, t, ref index);
    }

    private void CancelAndHold(double time)
    {
        var hold = Peek(time);
        var first = _pending.FindIndex(e => e.Time >= time);
        var kind = first >= 0 && _pending[first].Kind is AutomationKind.Linear or AutomationKind.Exponential
            ? _pending[first].Kind
            : AutomationKind.Set;
        _pending.RemoveAll(e => e.Time >= time);
        if (kind == AutomationKind.Exponential && hold == 0f)
            kind = AutomationKind.Set;
        Add(new Event(kind, hold, time, 0, time));
    }

    private void Add(Event added)
    {
        // Событие того же вида в тот же миг заменяет прежнее (спецификация).
        var same = _pending.FindIndex(e => e.Time == added.Time && e.Kind == added.Kind);
        if (same >= 0)
        {
            _pending[same] = added;
            return;
        }
        var at = _pending.Count;
        while (at > 0 && _pending[at - 1].Time > added.Time)
            at--;
        _pending.Insert(at, added);
    }

    private float Evaluate(ref State s, double t, ref int index)
    {
        while (index < _pending.Count)
        {
            var e = _pending[index];
            if (e.Kind is AutomationKind.Linear or AutomationKind.Exponential)
            {
                if (t >= e.Time)
                {
                    s.AnchorTime = e.Time;
                    s.AnchorValue = e.Value;
                    s.Target = false;
                    s.History = true;
                    index++;
                    continue;
                }
                double t0;
                float v0;
                if (s.Target)
                {
                    // Рампа после setTarget: начало — с мига вызова рампы, но не раньше начала setTarget.
                    t0 = Math.Max(s.TargetStart, e.Inserted);
                    if (t < t0)
                        return TargetValue(in s, t);
                    v0 = TargetValue(in s, t0);
                }
                else
                {
                    t0 = s.AnchorTime;
                    v0 = s.AnchorValue;
                    if (double.IsNegativeInfinity(t0))
                        return v0;
                }
                var share = (t - t0) / (e.Time - t0);
                if (e.Kind == AutomationKind.Linear)
                    return (float)(v0 + (e.Value - v0) * share);
                // Экспонента между значениями разных знаков или от нуля держит начальное значение.
                if (v0 * e.Value <= 0f)
                    return v0;
                return (float)(v0 * Math.Pow(e.Value / (double)v0, share));
            }
            if (t < e.Time)
                break;
            if (e.Kind == AutomationKind.Set || e.TimeConstant <= 0)
            {
                s.AnchorTime = e.Time;
                s.AnchorValue = e.Value;
                s.Target = false;
            }
            else
            {
                var from = s.Target ? TargetValue(in s, e.Time) : s.AnchorValue;
                s.Target = true;
                s.TargetStart = e.Time;
                s.TargetFrom = from;
                s.TargetTo = e.Value;
                s.TargetTau = e.TimeConstant;
                s.AnchorTime = e.Time;
                s.AnchorValue = from;
            }
            s.History = true;
            index++;
        }
        return s.Target ? TargetValue(in s, t) : s.AnchorValue;
    }

    private static float TargetValue(in State s, double t) =>
        (float)(s.TargetTo + (s.TargetFrom - (double)s.TargetTo) * Math.Exp(-(t - s.TargetStart) / s.TargetTau));

    private readonly record struct Event(AutomationKind Kind, float Value, double Time, double TimeConstant, double Inserted);

    private struct State
    {
        public double AnchorTime;
        public float AnchorValue;
        public bool History;
        public bool Target;
        public double TargetStart;
        public float TargetFrom;
        public float TargetTo;
        public double TargetTau;
    }
}
