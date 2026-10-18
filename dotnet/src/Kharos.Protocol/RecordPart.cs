using Kharos.Engine.World;

namespace Kharos.Protocol;

/// <summary>
/// Компонент сущности вне мира — запомненной (призрак здания): значения, как в хранилище, — столбцы f64 (строка —
/// номером в строках мира) и байты по <see cref="FieldDef.Slot"/>. Уходит <see cref="DeltaWriter.Create(uint, IReadOnlyList{RecordPart})"/>.
/// </summary>
public sealed record RecordPart(ComponentDef Def, double[] Columns, byte[][] Bytes);
