namespace Kharos.Engine.Terrain;

/// <summary>Параметры генератора земли (<c>GeneratorConfig</c> из TS, те же имена). Один набор — всегда один мир.</summary>
public sealed record GeneratorConfig
{
    /// <summary>Зерно. В TS — число, но генератор видит его только через <c>ToInt32</c>, так что int — то же самое.</summary>
    public int Seed { get; init; } = 1337;
    /// <summary>Характерный размер зон в тайлах: больше — крупнее плато, пустыни и болота.</summary>
    public double ZoneScale { get; init; } = 56;
    /// <summary>На сколько тайлов искажаются границы зон.</summary>
    public double ZoneWarp { get; init; } = 30;
    /// <summary>Высота, ниже которой болото.</summary>
    public double SwampLevel { get; init; } = 0.37;
    /// <summary>Высота, выше которой скалы. Между уровнями — песок.</summary>
    public double RockLevel { get; init; } = 0.59;
    /// <summary>Характерный размер биомов в тайлах.</summary>
    public double BiomeScale { get; init; } = 240;
    /// <summary>Доля клеток 16×16 с горами (если место целиком на плато).</summary>
    public double PeakChance { get; init; } = 0.3;
    /// <summary>Радиус главной вершины группы, в тайлах.</summary>
    public double PeakMinRadius { get; init; } = 1;
    public double PeakMaxRadius { get; init; } = 2;
    /// <summary>Доли двойных и тройных гор; остальные одиночные.</summary>
    public double DoubleChance { get; init; } = 0.35;
    public double TripleChance { get; init; } = 0.15;
    /// <summary>Поправки биомов относительно эрга: сдвиг уровня болот, уровня скал и множитель частоты гор.</summary>
    public double SaltSwampShift { get; init; } = -0.05;
    public double SaltRockShift { get; init; } = 0.05;
    public double SaltPeakFactor { get; init; } = 1;
    public double RedSwampShift { get; init; } = -0.06;
    public double RedRockShift { get; init; } = -0.07;
    public double RedPeakFactor { get; init; } = 2.5;
    public double MarshSwampShift { get; init; } = 0.09;
    public double MarshRockShift { get; init; } = 0.04;
    public double MarshPeakFactor { get; init; } = 0.5;
    /// <summary>На сколько высота поднимается над уровнем скал до следующего яруса плато.</summary>
    public double TierStep { get; init; } = 0.065;
    /// <summary>Характерная длина участков кромки, обрывистых или пологих, в тайлах.</summary>
    public double CliffScale { get; init; } = 20;
    /// <summary>Доля обрывистой кромки: 0 — все пологие, 1 — все обрывы.</summary>
    public double CliffShare { get; init; } = 0.35;
    /// <summary>В какой доле клеток 40×40 есть месторождение. Генератор земли его не читает.</summary>
    public double DepositChance { get; init; } = 0.95;
    /// <summary>Во сколько раз запас месторождений больше обычного.</summary>
    public double DepositRichness { get; init; } = 1;
    /// <summary>Чистая пустыня: ровный песок эрга без скал, болот и гор — заготовка для редактора.</summary>
    public bool Blank { get; init; }

    /// <summary>Генератор по умолчанию (<c>DEFAULT_CONFIG</c>).</summary>
    public static GeneratorConfig Default { get; } = new();
}
