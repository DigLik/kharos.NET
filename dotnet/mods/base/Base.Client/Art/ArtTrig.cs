namespace Base.Client;

/// <summary>
/// Синус и косинус для чертежей. Чертёж юнита повёрнут на углы 2π·d/64 (и угол портрета −π/4): значения тригонометрии,
/// из которых растеризуются пиксели TS-клиента (эталон — Bun, dump.ts), лежат здесь битами. Библиотеки C на разных
/// платформах (в том числе musl внутри wasm) расходятся в последнем знаке, а одна лишняя единица в конце переворачивает
/// пиксель на границе фигуры. Угол вне таблицы считается Math.Sin и Math.Cos.
/// Таблица: Math.sin((d / 64) * Math.PI * 2) и Math.cos(...) для d от 0 до 63.
/// </summary>
internal static class ArtTrig
{
    private const double Turn = Math.PI * 2;
    private const int Directions = 64;

    private static readonly ulong[] Sins =
    [
        0x0000000000000000, 0x3fb917a6bc29b42c, 0x3fc8f8b83c69a60a, 0x3fd294062ed59f05,
        0x3fd87de2a6aea963, 0x3fde2b5d3806f63b, 0x3fe1c73b39ae68c8, 0x3fe44cf325091dd6,
        0x3fe6a09e667f3bcd, 0x3fe8bc806b151740, 0x3fea9b66290ea1a3, 0x3fec38b2f180bdb0,
        0x3fed906bcf328d46, 0x3fee9f4156c62ddb, 0x3fef6297cff75cb0, 0x3fefd88da3d12525,
        0x3ff0000000000000, 0x3fefd88da3d12526, 0x3fef6297cff75cb0, 0x3fee9f4156c62ddb,
        0x3fed906bcf328d46, 0x3fec38b2f180bdb1, 0x3fea9b66290ea1a4, 0x3fe8bc806b151742,
        0x3fe6a09e667f3bcd, 0x3fe44cf325091dd6, 0x3fe1c73b39ae68c8, 0x3fde2b5d3806f63e,
        0x3fd87de2a6aea965, 0x3fd294062ed59f06, 0x3fc8f8b83c69a617, 0x3fb917a6bc29b43c,
        0x3ca1a62633145c07, 0xbfb917a6bc29b42b, 0xbfc8f8b83c69a60e, 0xbfd294062ed59f01,
        0xbfd87de2a6aea961, 0xbfde2b5d3806f63b, 0xbfe1c73b39ae68c6, 0xbfe44cf325091dd4,
        0xbfe6a09e667f3bcc, 0xbfe8bc806b15173e, 0xbfea9b66290ea1a3, 0xbfec38b2f180bdb0,
        0xbfed906bcf328d44, 0xbfee9f4156c62dda, 0xbfef6297cff75caf, 0xbfefd88da3d12526,
        0xbff0000000000000, 0xbfefd88da3d12526, 0xbfef6297cff75cb0, 0xbfee9f4156c62ddb,
        0xbfed906bcf328d45, 0xbfec38b2f180bdb1, 0xbfea9b66290ea1a5, 0xbfe8bc806b151740,
        0xbfe6a09e667f3bce, 0xbfe44cf325091dda, 0xbfe1c73b39ae68c8, 0xbfde2b5d3806f640,
        0xbfd87de2a6aea96e, 0xbfd294062ed59f08, 0xbfc8f8b83c69a61b, 0xbfb917a6bc29b425,
    ];

    private static readonly ulong[] Coss =
    [
        0x3ff0000000000000, 0x3fefd88da3d12526, 0x3fef6297cff75cb0, 0x3fee9f4156c62dda,
        0x3fed906bcf328d46, 0x3fec38b2f180bdb1, 0x3fea9b66290ea1a3, 0x3fe8bc806b151741,
        0x3fe6a09e667f3bcd, 0x3fe44cf325091dd6, 0x3fe1c73b39ae68c9, 0x3fde2b5d3806f63e,
        0x3fd87de2a6aea964, 0x3fd294062ed59f05, 0x3fc8f8b83c69a60d, 0x3fb917a6bc29b438,
        0x3c91a62633145c07, 0xbfb917a6bc29b42f, 0xbfc8f8b83c69a608, 0xbfd294062ed59f02,
        0xbfd87de2a6aea962, 0xbfde2b5d3806f63c, 0xbfe1c73b39ae68c6, 0xbfe44cf325091dd5,
        0xbfe6a09e667f3bcc, 0xbfe8bc806b151741, 0xbfea9b66290ea1a4, 0xbfec38b2f180bdb0,
        0xbfed906bcf328d46, 0xbfee9f4156c62dda, 0xbfef6297cff75cb0, 0xbfefd88da3d12525,
        0xbff0000000000000, 0xbfefd88da3d12526, 0xbfef6297cff75cb0, 0xbfee9f4156c62ddb,
        0xbfed906bcf328d47, 0xbfec38b2f180bdb1, 0xbfea9b66290ea1a5, 0xbfe8bc806b151742,
        0xbfe6a09e667f3bce, 0xbfe44cf325091dda, 0xbfe1c73b39ae68c8, 0xbfde2b5d3806f63f,
        0xbfd87de2a6aea96d, 0xbfd294062ed59f07, 0xbfc8f8b83c69a619, 0xbfb917a6bc29b421,
        0xbcaa79394c9e8a0a, 0x3fb917a6bc29b407, 0x3fc8f8b83c69a60c, 0x3fd294062ed59f00,
        0x3fd87de2a6aea967, 0x3fde2b5d3806f63a, 0x3fe1c73b39ae68c5, 0x3fe44cf325091dd7,
        0x3fe6a09e667f3bcb, 0x3fe8bc806b15173d, 0x3fea9b66290ea1a3, 0x3fec38b2f180bdaf,
        0x3fed906bcf328d44, 0x3fee9f4156c62dda, 0x3fef6297cff75caf, 0x3fefd88da3d12526,
    ];

    private const ulong PortraitSin = 0xbfe6a09e667f3bcd, PortraitCos = 0x3fe6a09e667f3bcd;

    public static (double Sin, double Cos) Of(double angle)
    {
        if (angle == -Math.PI / 4)
            return (BitConverter.UInt64BitsToDouble(PortraitSin), BitConverter.UInt64BitsToDouble(PortraitCos));
        var d = (int)Math.Round(angle / Turn * Directions);
        if (d >= 0 && d < Directions && angle == (double)d / Directions * Turn)
            return (BitConverter.UInt64BitsToDouble(Sins[d]), BitConverter.UInt64BitsToDouble(Coss[d]));
        return (Math.Sin(angle), Math.Cos(angle));
    }
}
