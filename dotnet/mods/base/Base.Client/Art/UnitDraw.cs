using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Чертежи юнитов и турелей, вид сверху — перенос lib/units.luau (а он — unitArt.ts) один в один: тот же холст, те же
/// цвета и фигуры, поэтому и пиксели те же. Рисуются в пикселях спрайта, 16 на тайл, начало координат — центр юнита;
/// каждое направление — заново: повёрнутый чертёж растеризуется чище, чем повёрнутая картинка. Массивы цветов — с нуля.
/// </summary>
internal static class UnitDraw
{
    /// <summary>Кадров хода наземного юнита: протектор и звенья повторяются через столько пикселей.</summary>
    public const int Phases = 4;

    /// <summary>Маска цвета стороны: спрайт рисуется один раз на все стороны, цвет подставляет шейдер палитрой.</summary>
    public static readonly uint[] MaskTeam = [Canvas.Toned(0x1f63d8, 0), Canvas.Toned(0x5aa9ff, 1), Canvas.Toned(0xe4f4ff, 2)];

    private const uint Ink = 0x0b111b;
    private static readonly uint[] Steel = [0x1c2b3e, 0x2d4560, 0x41617f, 0x6184a3, 0x9bb9d1];
    private static readonly uint[] Iron = [0x1e2024, 0x3a3d43, 0x5b5f66, 0x8b9097, 0xc3c7cc];
    private static readonly uint[] Hazard = [0x7a5a16, 0xc9962b, 0xf0c95a];
    private static readonly uint[] Energy = [0x4f9dff, 0x9fd0ff, 0xf2fbff];

    /// <summary>Чертёж: холст, угол, цвета стороны (тёмный, основной, светлый), фаза хода.</summary>
    public delegate void Art(Canvas g, double angle, uint[] team, int phase);

    /// <summary>Перо, повёрнутое вместе с юнитом: координаты — вперёд (along) и вправо (across) от центра.</summary>
    private readonly struct Pen(Canvas g, double angle)
    {
        private readonly double _sin = ArtTrig.Of(angle).Sin, _cos = ArtTrig.Of(angle).Cos;

        private double X(double along, double across) => along * _cos - across * _sin;
        private double Y(double along, double across) => along * _sin + across * _cos;

        /// <summary>Брусок вдоль хода: от from до to по оси «вперёд», со сдвигом across вбок, толщиной width.</summary>
        public void Bar(double from, double to, double across, double width, uint color) =>
            g.Line(X(from, across), Y(from, across), X(to, across), Y(to, across), width, color);

        /// <summary>Брусок поперёк хода: на расстоянии along вперёд, от from до to по оси «вправо».</summary>
        public void Cross(double along, double from, double to, double width, uint color) =>
            g.Line(X(along, from), Y(along, from), X(along, to), Y(along, to), width, color);

        public void Dot(double along, double across, double radius, uint color) => g.Circle(X(along, across), Y(along, across), radius, color);

        /// <summary>Отрезок между двумя точками, каждая — вперёд и вправо от центра.</summary>
        public void Line(double fromAlong, double fromAcross, double toAlong, double toAcross, double width, uint color) =>
            g.Line(X(fromAlong, fromAcross), Y(fromAlong, fromAcross), X(toAlong, toAcross), Y(toAlong, toAcross), width, color);
    }

    /// <summary>Поперечные полоски через Phases пикселей на отрезке от from до to: звенья гусеницы или протектор шины.</summary>
    private static void Treads(Pen p, double from, double to, double across, double half, int phase, uint color)
    {
        var along = from + phase;
        while (along - Phases >= from)
            along -= Phases;
        while (along <= to)
        {
            p.Cross(along, across - half, across + half, 1, color);
            along += Phases;
        }
    }

    /// <summary>Шина: чёрный брусок длиной 2·half вдоль хода, по которому бежит протектор.</summary>
    private static void Tire(Pen p, double along, double across, double half, double width, int phase)
    {
        p.Bar(along - half, along + half, across, width, Ink);
        Treads(p, along - half + 0.5, along + half - 0.5, across, width / 2 - 0.5, phase, Iron[2]);
    }

    /// <summary>Ноги пехотинца: на кадрах 1 и 3 одна ступня впереди, другая сзади; на 0 и 2 обе под плечами.</summary>
    private static void Legs(Pen p, int phase)
    {
        var stride = phase == 1 ? 2.6 : phase == 3 ? -2.6 : 0;
        if (stride == 0)
            return;
        foreach (var across in (ReadOnlySpan<double>)[-1.5, 1.5])
        {
            var along = across < 0 ? stride : -stride;
            p.Dot(along, across, 1.3, Ink);
            p.Dot(along, across, 0.7, Iron[2]);
        }
    }

    /// <summary>Колёса лёгкой машины: по два с каждого борта.</summary>
    private static void Wheels(Pen p, double side, double spread, int phase)
    {
        foreach (var across in (ReadOnlySpan<double>)[-side, side])
            foreach (var wheel in (ReadOnlySpan<double>)[-spread, spread])
                Tire(p, wheel, across, 2, 3, phase);
    }

    /// <summary>Гусеницы и корпус тяжёлой машины.</summary>
    private static void HeavyHull(Pen p, uint[] team, int phase)
    {
        foreach (var side in (ReadOnlySpan<double>)[-7.5, 7.5])
        {
            p.Bar(-11, 11, side, 6, Ink);
            p.Bar(-10, 10, side, 4, Iron[1]);
            Treads(p, -10, 10, side, 2, phase, Iron[0]);
        }
        p.Bar(-9, 10, 0, 12, Ink);
        p.Bar(-8, 9, 0, 10, Steel[1]);
        p.Bar(-8, 9, -4, 2, Steel[2]);
        p.Bar(-8, -6, 0, 10, team[0]);
    }

    /// <summary>
    /// Пушка главного здания: широкий купол цвета команды с линзой и спаренный излучатель с голубыми концами. Купол —
    /// along пикселей вперёд от центра, стволы длиной reach. Та же пушка стоит на крыше главного здания и на MCV.
    /// </summary>
    private static void CoreGun(Pen p, double along, double reach, uint[] team)
    {
        foreach (var side in (ReadOnlySpan<double>)[-2.2, 2.2])
        {
            p.Bar(along + 2, along + reach, side, 2.8, Ink);
            p.Bar(along + 2.5, along + reach - 0.5, side, 1.4, Iron[3]);
            p.Dot(along + reach, side, 1.3, Energy[1]);
            p.Dot(along + reach, side, 0.6, Energy[2]);
        }
        p.Dot(along, 0, 7.2, Ink);
        p.Dot(along, 0, 6.2, Steel[2]);
        p.Dot(along - 0.8, 0, 4.8, team[0]);
        p.Dot(along - 1.8, -1.8, 2, team[1]);
        p.Dot(along + 2.2, 0, 1.8, Ink);
        p.Dot(along + 2.2, 0, 1.1, Energy[0]);
    }

    // ------------------------------------------------------------------ юниты

    /// <summary>MCV: широкая гусеничная машина с кабиной спереди (пушка будущего ядра едет на крыше отдельной турелью mcv).</summary>
    public static void Mcv(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        foreach (var side in (ReadOnlySpan<double>)[-8, 8])
        {
            p.Bar(-13, 13, side, 7, Ink);
            p.Bar(-12, 12, side, 5, Iron[1]);
            Treads(p, -12, 12, side, 2.5, phase, Iron[0]);
        }
        p.Bar(-11, 12, 0, 14, Ink);
        p.Bar(-10, 11, 0, 12, Steel[1]);
        p.Bar(-10, 11, -5, 2, Steel[2]);
        p.Bar(6, 11, 0, 10, Steel[3]);
        p.Bar(8, 10, 0, 6, team[2]);
    }

    /// <summary>Строитель: небольшая колёсная машина со стрелой.</summary>
    public static void Builder(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        foreach (var side in (ReadOnlySpan<double>)[-4, 4])
            foreach (var wheel in (ReadOnlySpan<double>)[-3.5, 3.5])
                Tire(p, wheel, side, 2, 3, phase);
        p.Bar(-6, 6, 0, 8, Ink);
        p.Bar(-5, 5, 0, 6, Hazard[1]);
        p.Bar(-5, 5, -2.5, 1, Hazard[2]);
        p.Bar(-5, -3, 0, 6, Hazard[0]);
        p.Bar(2, 4, 0, 4, team[1]);
        p.Bar(-1, 8, 2, 2, Ink);
        p.Dot(8, 2, 1.5, Iron[3]);
    }

    /// <summary>Пехотинец: плечи, голова и оружие.</summary>
    public static void Infantry(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        Legs(p, phase);
        p.Cross(0, -3.5, 3.5, 5, Ink);
        p.Cross(0, -2.5, 2.5, 3, team[0]);
        p.Bar(0, 5, 2.5, 1.2, Ink);
        p.Dot(0.5, 0, 2, Ink);
        p.Dot(0.5, 0, 1.2, Steel[4]);
    }

    /// <summary>Грузовик: кабина спереди и открытый кузов с рудой.</summary>
    public static void Truck(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        foreach (var side in (ReadOnlySpan<double>)[-4.5, 4.5])
            foreach (var wheel in (ReadOnlySpan<double>)[-5, 0, 5])
                Tire(p, wheel, side, 1.5, 3, phase);
        p.Bar(-8, 8, 0, 9, Ink);
        // Кузов: борта и руда внутри.
        p.Bar(-7, 2, 0, 7, Iron[2]);
        p.Bar(-6, 1, 0, 5, 0x6b2f1e);
        p.Bar(-5, 0, -1, 2, 0xb5562e);
        p.Dot(-2, 1, 1, 0xe58a4a);
        // Кабина.
        p.Bar(3, 7, 0, 7, Hazard[1]);
        p.Bar(3, 7, -3, 1, Hazard[2]);
        p.Bar(5, 6, 0, 5, team[2]);
        // Излучатель транспортного луча за кабиной.
        p.Dot(2, 0, 1.8, Ink);
        p.Dot(2, 0, 1.2, 0x5ff2d0);
        p.Dot(1.6, -0.4, 0.5, 0xd8fff6);
    }

    /// <summary>Ракетчик: пехотинец с трубой на плече.</summary>
    public static void Rocketeer(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        Legs(p, phase);
        p.Cross(0, -3.5, 3.5, 5, Ink);
        p.Cross(0, -2.5, 2.5, 3, team[0]);
        p.Bar(-4, 6, -2.5, 3.2, Ink);
        p.Bar(-3, 5, -2.5, 1.6, Iron[3]);
        p.Dot(5.5, -2.5, 1, Hazard[2]);
        p.Dot(0.5, 0.5, 2, Ink);
        p.Dot(0.5, 0.5, 1.2, Steel[4]);
    }

    /// <summary>Огнемётчик: баллоны за спиной, брандспойт в руках и горящий запальник на конце.</summary>
    public static void Flamer(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        Legs(p, phase);
        // Баллоны за спиной.
        foreach (var across in (ReadOnlySpan<double>)[-1.6, 1.6])
        {
            p.Dot(-2.2, across, 1.6, Ink);
            p.Dot(-2.2, across, 1, Hazard[1]);
        }
        p.Cross(0, -3.5, 3.5, 5, Ink);
        p.Cross(0, -2.5, 2.5, 3, team[0]);
        // Брандспойт и запальник.
        p.Bar(-1, 6, 1.8, 2.4, Ink);
        p.Bar(0, 5, 1.8, 1, Iron[3]);
        p.Dot(6.2, 1.8, 1.1, 0xff8a2a);
        p.Dot(0.5, -0.3, 2, Ink);
        p.Dot(0.5, -0.3, 1.2, Steel[4]);
    }

    /// <summary>Зенитка: лёгкое колёсное шасси с поворотной платформой и спаренной автопушкой, задранной вверх.</summary>
    public static void Flak(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        Wheels(p, 4.5, 4.5, phase);
        p.Bar(-7, 6, 0, 8, Ink);
        p.Bar(-6, 5, 0, 6, Steel[1]);
        p.Bar(-6, 5, -2.5, 1, Steel[3]);
        p.Bar(-6, -4, 0, 6, team[0]);
        // Платформа и два ствола.
        p.Dot(-0.5, 0, 3.6, Ink);
        p.Dot(-0.5, 0, 2.8, Steel[2]);
        foreach (var across in (ReadOnlySpan<double>)[-1.4, 1.4])
        {
            p.Bar(0, 9, across, 1.8, Ink);
            p.Bar(1, 8.5, across, 0.8, Iron[4]);
        }
        p.Dot(-0.5, 0, 1.4, team[1]);
    }

    /// <summary>Багги: узкая быстрая машина с дугой безопасности. Пассажира с миниганом рисует турель gunner поверх.</summary>
    public static void Buggy(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        Wheels(p, 4.5, 4, phase);
        p.Bar(-6, 7, 0, 7, Ink);
        p.Bar(-5, 6, 0, 5, Iron[2]);
        p.Bar(-5, 6, -2, 1, Iron[3]);
        p.Bar(-5, -3, 0, 5, team[0]);
        // Водитель за лобовым стеклом и дуга над ним.
        p.Bar(4.5, 6, 0, 4, team[2]);
        p.Dot(2.5, 0, 1.3, Ink);
        p.Dot(2.5, 0, 0.8, Steel[4]);
        p.Cross(1, -3.5, 3.5, 1, Ink);
    }

    /// <summary>Лазерная машина: излучатель на шасси.</summary>
    public static void Lancer(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        Wheels(p, 4.5, 4.5, phase);
        p.Bar(-7, 6, 0, 8, Ink);
        p.Bar(-6, 5, 0, 6, Steel[1]);
        p.Bar(-6, 5, -2.5, 1, Steel[3]);
        p.Bar(-6, -4, 0, 6, team[0]);
        p.Bar(-2, 8, 0, 3.4, Ink);
        p.Bar(-1, 7, 0, 1.6, Steel[4]);
        p.Dot(8, 0, 1.6, 0xff4d6d);
        p.Dot(-2, 0, 3.2, Ink);
        p.Dot(-2, 0, 2.2, team[1]);
    }

    /// <summary>Танк: гусеницы, корпус и погон под башню. Башня — отдельная турель, её рисует CannonTurret поверх.</summary>
    public static void Tank(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        HeavyHull(p, team, phase);
        p.Bar(6, 9, 0, 8, Steel[2]);
        p.Cross(7.5, -3, 3, 1, Steel[0]);
        p.Dot(-1, 0, 6.4, Ink);
        p.Dot(-1, 0, 5.6, Steel[0]);
    }

    /// <summary>
    /// Артиллерия: гаубица на колёсном лафете. Две пары колёс, станины с сошниками назад, щит расчёта и длинный ствол
    /// с дульным тормозом вперёд — ствол длиннее корпуса, видно издалека.
    /// </summary>
    public static void Artillery(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        // Станины расходятся назад, на концах — сошники.
        p.Line(-1, -3, -12, -6, 2.4, Ink);
        p.Line(-1, 3, -12, 6, 2.4, Ink);
        p.Line(-1, -3, -11.5, -5.8, 1, Iron[2]);
        p.Line(-1, 3, -11.5, 5.8, 1, Iron[2]);
        p.Cross(-12, -7.5, -4.5, 1.6, Iron[3]);
        p.Cross(-12, 4.5, 7.5, 1.6, Iron[3]);
        foreach (var along in (ReadOnlySpan<double>)[-3, 3])
            foreach (var across in (ReadOnlySpan<double>)[-6, 6])
                Tire(p, along, across, 2.2, 3, phase);
        // Лафет.
        p.Bar(-6, 5, 0, 9, Ink);
        p.Bar(-5, 4, 0, 7, Steel[1]);
        p.Bar(-5, 4, -2.8, 1, Steel[3]);
        p.Bar(-5, -3, 0, 7, team[0]);
        // Щит расчёта.
        p.Cross(2.5, -5, 5, 2, Ink);
        p.Cross(2.5, -4.5, 4.5, 1, Steel[3]);
        // Ствол с дульным тормозом.
        p.Bar(-2, 14, 0, 3.4, Ink);
        p.Bar(-1, 13, 0, 1.8, Iron[3]);
        p.Bar(-1, 13, -0.6, 0.6, Iron[4]);
        p.Bar(12.5, 15, 0, 4.2, Ink);
        p.Bar(13, 14.5, 0, 2.6, Iron[2]);
        p.Dot(-1.5, 0, 2.6, Ink);
        p.Dot(-1.5, 0, 1.8, team[1]);
    }

    /// <summary>Разрядник: тяжёлое шасси с погоном и кожухами питания. Катушка — отдельная турель (ArcTurret).</summary>
    public static void Tesla(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        HeavyHull(p, team, phase);
        foreach (var side in (ReadOnlySpan<double>)[-3.5, 3.5])
            p.Bar(5, 9, side, 2, Energy[0]);
        p.Dot(0, 0, 6.6, Ink);
        p.Dot(0, 0, 5.8, Iron[1]);
    }

    /// <summary>Носитель: колёсное шасси танка — по три колеса с борта и плоская палуба с четырьмя гнёздами под турели.</summary>
    public static void Carrier(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        foreach (var side in (ReadOnlySpan<double>)[-9, 9])
            foreach (var wheel in (ReadOnlySpan<double>)[-8, 0, 8])
            {
                p.Bar(wheel - 3, wheel + 3, side, 4.6, Ink);
                p.Bar(wheel - 2.3, wheel + 2.3, side, 3.2, Iron[1]);
                Treads(p, wheel - 2.3, wheel + 2.3, side, 1.6, phase, Iron[3]);
            }
        p.Bar(-12, 12, 0, 15, Ink);
        p.Bar(-11, 11, 0, 13, Steel[1]);
        p.Bar(-11, 11, -5.5, 2, Steel[2]);
        p.Bar(-11, -9, 0, 13, team[0]);
        p.Bar(10, 12, 0, 8, team[2]);
        p.Cross(0.5, -5.5, 5.5, 1, Steel[0]);
    }

    /// <summary>Машина снабжения: длинное шестиколёсное шасси, кабина спереди, за ней — кузов с ящиками патронов под тентом.</summary>
    public static void Supplier(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        foreach (var side in (ReadOnlySpan<double>)[-7, 7])
            foreach (var wheel in (ReadOnlySpan<double>)[-8, -2.5, 6])
                Tire(p, wheel, side, 2, 3.4, phase);
        p.Bar(-12, 11, 0, 11, Ink);
        p.Bar(-11, 10, 0, 9, Steel[1]);
        p.Bar(-11, 10, -3.8, 1.4, Steel[2]);
        // Кузов: тент в цвет стороны и ящики под ним рядами.
        p.Bar(-11, 2, 0, 10, Ink);
        p.Bar(-10.5, 1.5, 0, 9, team[0]);
        foreach (var along in (ReadOnlySpan<double>)[-8.5, -4.5, -0.5])
            foreach (var across in (ReadOnlySpan<double>)[-2.4, 2.4])
            {
                p.Bar(along - 1.4, along + 1.4, across, 3.6, Ink);
                p.Bar(along - 1, along + 1, across, 2.8, 0x5b6b2e);
                p.Bar(along - 1, along + 1, across - 0.9, 0.8, 0x8a9c4a);
            }
        p.Cross(-11, -4.5, 4.5, 1, Hazard[1]);
        // Кабина.
        p.Bar(3, 10, 0, 10, Ink);
        p.Bar(3.5, 9.5, 0, 9, Hazard[1]);
        p.Bar(3.5, 9.5, -3.5, 1.2, Hazard[2]);
        p.Bar(8, 9.5, 0, 7, team[2]);
        // Мачта излучателя раздачи на заднем борту.
        p.Dot(-11.5, 3.5, 2, Ink);
        p.Dot(-11.5, 3.5, 1.3, 0x5ff2d0);
        p.Dot(-11.9, 3.1, 0.5, 0xd8fff6);
    }

    /// <summary>Дрон: четыре винта крестом и маленький корпус.</summary>
    public static void Drone(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        foreach (var (along, across) in (ReadOnlySpan<(double, double)>)[(4, 4), (4, -4), (-4, 4), (-4, -4)])
        {
            p.Line(0, 0, along, across, 2.4, Ink);
            p.Line(0, 0, along, across, 1, Iron[2]);
            p.Dot(along, across, 2.8, Ink);
            p.Dot(along, across, 1.8, Iron[3]);
            p.Dot(along, across, 0.7, Iron[0]);
        }
        p.Bar(0, 6, 0, 1.6, Ink);
        p.Dot(0, 0, 3.2, Ink);
        p.Dot(0, 0, 2.2, team[0]);
        p.Dot(0.5, -0.5, 1, team[2]);
    }

    /// <summary>Летающий грузовик: четыре винта на длинных балках, под ними — грузовой контейнер в жёлто-чёрных полосах.</summary>
    public static void AirTruck(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        foreach (var (along, across) in (ReadOnlySpan<(double, double)>)[(6, 6), (6, -6), (-6, 6), (-6, -6)])
        {
            p.Line(0, 0, along, across, 2.4, Ink);
            p.Line(0, 0, along, across, 1, Iron[2]);
            p.Dot(along, across, 3.2, Ink);
            p.Dot(along, across, 2.2, Iron[3]);
            p.Dot(along, across, 0.8, Iron[0]);
        }
        // Контейнер.
        p.Bar(-4.5, 4.5, 0, 8, Ink);
        p.Bar(-3.5, 3.5, 0, 6, Hazard[1]);
        foreach (var along in (ReadOnlySpan<double>)[-2, 1])
            p.Cross(along, -3, 3, 1, Hazard[0]);
        // Кабина спереди.
        p.Bar(4, 7, 0, 4, Ink);
        p.Bar(4.5, 6.5, 0, 2.4, team[0]);
        p.Dot(6, -0.6, 0.7, team[2]);
    }

    /// <summary>Штурмовик: фюзеляж, крылья с ракетными блоками и хвост.</summary>
    public static void Gunship(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        p.Cross(-1, -11, 11, 6, Ink);
        p.Cross(-1, -10, 10, 4, Steel[1]);
        p.Cross(-2, -10, 10, 1, Steel[3]);
        p.Cross(-1, -10, -7, 4, team[0]);
        p.Cross(-1, 7, 10, 4, team[0]);
        foreach (var side in (ReadOnlySpan<double>)[-6.5, 6.5])
        {
            p.Bar(-2, 6, side, 3.4, Ink);
            p.Bar(-1, 5, side, 1.6, Iron[3]);
            p.Dot(5.5, side, 0.9, Hazard[2]);
        }
        p.Cross(-9, -4, 4, 3.4, Ink);
        p.Cross(-9, -3, 3, 1.6, Steel[2]);
        p.Bar(-10, 10, 0, 6, Ink);
        p.Bar(-9, 9, 0, 4, Steel[2]);
        p.Bar(-9, 9, -1.5, 1, Steel[3]);
        p.Bar(4, 8, 0, 2.6, team[2]);
    }

    /// <summary>Бомбардировщик: широкое крыло-стрела с двумя моторами, бомбовый отсек на брюхе и двойной хвост.</summary>
    public static void Bomber(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        // Крыло-стрела: от носа назад к законцовкам.
        foreach (var side in (ReadOnlySpan<double>)[-1, 1])
        {
            p.Line(4, 0, -6, side * 13, 5, Ink);
            p.Line(4, 0, -6, side * 12.5, 3, Steel[1]);
            p.Line(3, side * 1, -5, side * 11.5, 1, Steel[3]);
            p.Dot(-6, side * 12, 1.4, team[0]);
            // Мотор на крыле.
            p.Bar(-3, 4, side * 6, 3.4, Ink);
            p.Bar(-2, 3, side * 6, 1.8, Iron[3]);
            p.Dot(4, side * 6, 1, Hazard[2]);
        }
        // Двойной хвост.
        foreach (var side in (ReadOnlySpan<double>)[-3, 3])
        {
            p.Bar(-12, -7, side, 2.4, Ink);
            p.Bar(-11, -7, side, 1, Steel[2]);
        }
        // Фюзеляж и бомбовый отсек.
        p.Bar(-11, 11, 0, 6, Ink);
        p.Bar(-10, 10, 0, 4, Steel[2]);
        p.Bar(-10, 10, -1.5, 1, Steel[3]);
        p.Bar(-5, 3, 0, 3, Ink);
        p.Bar(-4, 2, 0, 1.6, Hazard[1]);
        p.Bar(-10, -7, 0, 4, team[0]);
        p.Bar(6, 10, 0, 2.6, team[2]);
    }

    // ------------------------------------------------------------------ турели

    /// <summary>Танковая башня: круглая, с люком и длинным стволом с дульным тормозом.</summary>
    public static void CannonTurret(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        p.Bar(2, 13, 0, 3.6, Ink);
        p.Bar(2, 12.5, 0, 1.6, Iron[3]);
        p.Bar(11.5, 14, 0, 4.4, Ink);
        p.Bar(12, 13.5, 0, 2.6, Iron[2]);
        p.Dot(0, 0, 5.4, Ink);
        p.Dot(0, 0, 4.4, Steel[2]);
        p.Dot(-1, 0, 3.4, team[0]);
        p.Dot(-1.5, -1.5, 1.6, team[1]);
        p.Dot(-2, 1.5, 1, Iron[4]);
    }

    /// <summary>Ракетная турель: квадратный блок с двумя пусковыми трубами.</summary>
    public static void RocketTurret(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        p.Bar(-3, 3, 0, 6.4, Ink);
        p.Bar(-2.4, 2.4, 0, 5, team[0]);
        p.Bar(-2.4, -1.2, 0, 5, team[1]);
        foreach (var side in (ReadOnlySpan<double>)[-1.5, 1.5])
        {
            p.Bar(0, 6, side, 2.2, Ink);
            p.Bar(0.5, 5.5, side, 1, Iron[4]);
            p.Dot(5.6, side, 0.6, Hazard[2]);
        }
    }

    /// <summary>Ремонтная турель: жёлтый купол с рукой-излучателем.</summary>
    public static void RepairTurret(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        p.Bar(0, 5.5, 0, 2.2, Ink);
        p.Bar(0, 5, 0, 1, Iron[3]);
        p.Dot(5.6, 0, 1.5, Ink);
        p.Dot(5.6, 0, 0.9, 0x7dffa8);
        p.Dot(-0.5, 0, 3.4, Ink);
        p.Dot(-0.5, 0, 2.6, Hazard[1]);
        p.Dot(-1.2, -0.8, 1, Hazard[2]);
    }

    /// <summary>Разрядная башня: катушка на кольце и два электрода вперёд со светящимися концами.</summary>
    public static void ArcTurret(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        foreach (var side in (ReadOnlySpan<double>)[-2.6, 2.6])
        {
            p.Bar(2, 11, side, 2.6, Ink);
            p.Bar(2, 10, side, 1.2, Iron[3]);
            p.Dot(11, side, 1.4, Energy[1]);
            p.Dot(11, side, 0.6, Energy[2]);
        }
        p.Dot(0, 0, 5.4, Ink);
        p.Dot(0, 0, 4.6, Iron[2]);
        p.Cross(-3.5, -2.5, 2.5, 1.4, team[0]);
        p.Dot(0, 0, 3.4, Ink);
        p.Dot(0, 0, 2.6, Energy[0]);
        p.Dot(0, 0, 1.2, Energy[2]);
    }

    /// <summary>Пассажир багги: плечи и шлем в цвет команды, миниган — светлый блок стволов с жёлтым дульным срезом.</summary>
    public static void GunnerTurret(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        p.Cross(0, -3.6, 3.6, 4.4, Ink);
        p.Cross(0, -2.8, 2.8, 2.8, team[1]);
        p.Bar(0, 7.5, 1.8, 3, Ink);
        p.Bar(1, 7, 1.8, 1.4, Iron[4]);
        p.Dot(7.2, 1.8, 0.8, Hazard[2]);
        p.Dot(-1, -0.6, 1.5, Ink);
        p.Dot(-1, -0.6, 0.9, team[2]);
    }

    /// <summary>Лазерная турель: купол цвета команды и один излучатель с голубой линзой на конце.</summary>
    public static void LaserTurret(Canvas g, double angle, uint[] team, int phase)
    {
        var p = new Pen(g, angle);
        p.Bar(1, 10, 0, 2.6, Ink);
        p.Bar(1.5, 9.5, 0, 1.2, Iron[3]);
        p.Dot(10, 0, 1.4, Energy[1]);
        p.Dot(10, 0, 0.7, Energy[2]);
        p.Dot(0, 0, 4.6, Ink);
        p.Dot(0, 0, 3.8, Steel[2]);
        p.Dot(-0.6, 0, 2.8, team[0]);
        p.Dot(-1.2, -1.2, 1.1, team[1]);
    }

    /// <summary>Пушка главного здания (и MCV) на крыше.</summary>
    public static void CoreTurret(Canvas g, double angle, uint[] team, int phase) => CoreGun(new Pen(g, angle), 0, 14, team);
}
