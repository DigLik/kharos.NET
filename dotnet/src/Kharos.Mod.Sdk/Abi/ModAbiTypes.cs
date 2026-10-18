// Структуры ABI мода (MemoryPack): общий файл Kharos.Mod.Sdk и Kharos.Plugins. В Plugins типы открытые (их видит
// движок), в SDK — внутренние (мододел видит их через API SDK).

using MemoryPack;

namespace Kharos.Mod.Abi;

/// <summary>Что хост отдаёт моду в kh_load: версия, сторона, зерно, схема мира и данные мода.</summary>
[MemoryPackable]
#if KHAROS_PLUGINS
public
#else
internal
#endif
sealed partial class ModLoadInfo
{
    /// <summary>Версия ABI хоста.</summary>
    public int Abi { get; set; } = ModAbi.Version;

    /// <summary>"host" или "client".</summary>
    public string Side { get; set; } = "host";

    /// <summary>Зерно мира.</summary>
    public ulong Seed { get; set; }

    /// <summary>Схема мира: все компоненты всех модов, закрытая к моменту загрузки.</summary>
    public ModComponentInfo[] Components { get; set; } = [];

    /// <summary>Данные мода: data/*.json после патчей, по имени файла без расширения.</summary>
    public ModDataFile[] Data { get; set; } = [];

    /// <summary>Идущая партия — запись из data/games.json; пусто — партии нет (тесты).</summary>
    public string Game { get; set; } = "";
}

/// <summary>Компонент схемы.</summary>
[MemoryPackable]
#if KHAROS_PLUGINS
public
#else
internal
#endif
sealed partial class ModComponentInfo
{
    /// <summary>Имя компонента.</summary>
    public string Name { get; set; } = "";

    /// <summary>Номер компонента в схеме: им компонент назван в буфере команд.</summary>
    public int Id { get; set; }

    /// <summary>Поля по порядку объявления.</summary>
    public ModFieldInfo[] Fields { get; set; } = [];
}

/// <summary>Поле компонента.</summary>
[MemoryPackable]
#if KHAROS_PLUGINS
public
#else
internal
#endif
sealed partial class ModFieldInfo
{
    /// <summary>Имя поля.</summary>
    public string Name { get; set; } = "";

    /// <summary>Вид: ModAbi.Kind* (число, флаг, строка, байты).</summary>
    public byte Kind { get; set; }

    /// <summary>Номер поля в компоненте: им поле названо в записях Add и Set.</summary>
    public int Index { get; set; }

    /// <summary>Номер столбца f64 среди столбцов компонента (у байтовых — среди байтовых полей).</summary>
    public int Slot { get; set; }
}

/// <summary>Файл данных мода.</summary>
[MemoryPackable]
#if KHAROS_PLUGINS
public
#else
internal
#endif
sealed partial class ModDataFile
{
    /// <summary>Имя файла без .json.</summary>
    public string Name { get; set; } = "";

    /// <summary>Содержимое (UTF-8 JSON).</summary>
    public byte[] Bytes { get; set; } = [];
}

/// <summary>Что мод объявил: системы, обработчики событий, приказы (хост); облики, звуки (клиент). Отдаётся из kh_load.</summary>
[MemoryPackable]
#if KHAROS_PLUGINS
public
#else
internal
#endif
sealed partial class ModManifest
{
    /// <summary>Версия ABI, под которую собран мод.</summary>
    public int Abi { get; set; } = ModAbi.Version;

    /// <summary>Системы; номер в массиве — id для kh_prepare и kh_system.</summary>
    public ModSystemInfo[] Systems { get; set; } = [];

    /// <summary>События, на которые есть обработчик; номер — id для kh_event.</summary>
    public string[] Events { get; set; } = [];

    /// <summary>Приказы игроков; номер — id для kh_command.</summary>
    public string[] Orders { get; set; } = [];

    /// <summary>Клиент: виды «раздел/имя», у которых мод рисует облик; номер — id для kh_look.</summary>
    public string[] Looks { get; set; } = [];

    /// <summary>Клиент: записи банка звуков; номер — id для kh_bank.</summary>
    public ModBankInfo[] Bank { get; set; } = [];

    /// <summary>Клиент: голоса по имени — готовые графы KHVG (c.audio:voice по виду).</summary>
    public ModVoiceInfo[] Voices { get; set; } = [];

    /// <summary>Клиент: цвета мини-карты 0xRRGGBB, 4 биома × 4 вида местности (биом за биомом); пусто — по умолчанию.</summary>
    public uint[] Minimap { get; set; } = [];
}

/// <summary>Запись банка звуков клиентского мода: имя, шина и сколько вариантов (kh_bank на каждый).</summary>
[MemoryPackable]
#if KHAROS_PLUGINS
public
#else
internal
#endif
sealed partial class ModBankInfo
{
    /// <summary>Имя записи (как в c.audio:play, sample узла голоса).</summary>
    public string Name { get; set; } = "";

    /// <summary>Шина: battle, world, interface, music.</summary>
    public string Bus { get; set; } = "world";

    /// <summary>Вариантов (звучат вразнобой).</summary>
    public int Variants { get; set; } = 1;
}

/// <summary>Голос клиентского мода по имени: граф в двоичном формате KHVG.</summary>
[MemoryPackable]
#if KHAROS_PLUGINS
public
#else
internal
#endif
sealed partial class ModVoiceInfo
{
    /// <summary>Имя голоса.</summary>
    public string Name { get; set; } = "";

    /// <summary>Граф голоса (KHVG).</summary>
    public byte[] Graph { get; set; } = [];
}

/// <summary>Объявление системы — как SystemSpec движка.</summary>
[MemoryPackable]
#if KHAROS_PLUGINS
public
#else
internal
#endif
sealed partial class ModSystemInfo
{
    /// <summary>Полное имя системы.</summary>
    public string Name { get; set; } = "";

    /// <summary>Короткое имя для After/Before; null — часть имени после последней точки.</summary>
    public string? Key { get; set; }

    /// <summary>Компоненты запроса; их столбцы — в кадре, в этом порядке.</summary>
    public string[] Query { get; set; } = [];

    /// <summary>Условия на поля-столбцы.</summary>
    public ModWhereInfo[] Where { get; set; } = [];

    /// <summary>Чьи столбцы вернуть в мир после системы — подмножество Query.</summary>
    public string[] Write { get; set; } = [];

    /// <summary>Раз в сколько тиков.</summary>
    public int Every { get; set; } = 1;

    /// <summary>Идти после этих систем.</summary>
    public string[] After { get; set; } = [];

    /// <summary>Идти до этих систем.</summary>
    public string[] Before { get; set; } = [];

    /// <summary>Встать на место системы с этим именем.</summary>
    public string? Instead { get; set; }
}

/// <summary>Условие запроса: поле компонента равно значению.</summary>
[MemoryPackable]
#if KHAROS_PLUGINS
public
#else
internal
#endif
sealed partial class ModWhereInfo
{
    /// <summary>Компонент.</summary>
    public string Component { get; set; } = "";

    /// <summary>Поле-столбец.</summary>
    public string Field { get; set; } = "";

    /// <summary>Вид значения: ModAbi.Kind* (число, флаг или строка).</summary>
    public byte Kind { get; set; }

    /// <summary>Число или флаг (0/1).</summary>
    public double Number { get; set; }

    /// <summary>Строка.</summary>
    public string? Text { get; set; }
}
