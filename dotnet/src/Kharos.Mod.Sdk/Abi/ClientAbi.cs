// ABI клиентского мода (docs/dotnet.md, §4.2, §4.3): общий файл Kharos.Mod.Sdk, Kharos.Plugins и Kharos.Client
// (подключён ссылкой). Только числа договора и описание форматов. Всё little-endian, без выравнивания.
//
// Клиентский мод (client.wasm, ModLoadInfo.Side = "client") экспортирует kh_abi_version, kh_alloc, kh_free, kh_shared,
// kh_load (как хост, в out — ModManifest с Looks, Bank, Voices, Minimap) и:
//   kh_look(look) -> i32                   облик вида Looks[look]; в out — поток облика (ниже)
//   kh_bank(entry, variant, rate) -> i32   запись банка Bank[entry], вариант variant при частоте rate;
//                                          в out — channels:u8 frames:u32 f32 × frames × channels (канал за каналом)
//   kh_frame(ptr, len) -> i32              кадр клиента: на входе — вход кадра, в out — выход кадра (ниже)
// kh_prepare, kh_system, kh_event, kh_command у клиентского мода нет.
//
// Строка str — u16 длина байт и UTF-8. Цвет rgba — u32: r | g << 8 | b << 16 | a << 24 (как Color.Rgba клиента).
//
// ===== Поток облика (kh_look) =====
//   width:u16 height:u16 flags:u8 (LookFlag*)
//   [origin: x:f64 y:f64]  anchorX:f64 anchorY:f64  directions:u16 phases:u16 fps:f32  [step:f32]  layer:u8
//   [shadow: offset:f32 opacity:f32]  icon:u16  lamps:u16 (along:f32 across:f32 radius:f32)×lamps
//   [beam: along:f32 length:f32 near:f32 spread:f32 level:f32]
//   [portrait: width:u16 height:u16 hasOrigin:u8 [x:f64 y:f64] команды холста… End]
//   кадры: phases × directions (фаза за фазой, внутри — направления), каждый — команды холста… End
// Команды холста (CanvasOp*, координаты — от начала чертежа, f64; цвет — u32 0xRRGGBB, тон стороны — в старшем байте):
//   0 End · 1 Rect x y w h color · 2 Circle x y r color · 3 Ring x y r width color · 4 Line ax ay bx by width color
//   5 Poly n:u16 (x y)×n color · 6 Pixel x y color · 7 Translate dx dy · 8 Light x y size level
// Клиент проигрывает их на своём холсте (Kharos.Client.Looks.Canvas) — кадр совпадает с рисунком на C# до байта.
//
// ===== Вход кадра (kh_frame, хост → мод) =====
//   time:f64 dt:f64 tick:u64 alpha:f64 clock:f64 tickDt:f64 player:u32
//   screenW:f32 screenH:f32 scale:f32  cameraX:f64 cameraY:f64 zoom:f64
//   ввод: flags:u8 (InputFlag*) x:f32 y:f32 (экран; вне окна — NaN) down:u8 pressed:u8 released:u8 (Button*) wheel:f32
//         keys:u16 str×keys (зажатые) hits:u16 str×hits (нажатые за кадр) text:str
//   интерфейс: width:f32 height:f32 now:f64 flags:u8 (UiFlag*) ids:u16, на номер: id:str state:u8 (IdFlag*)
//         x:f32 y:f32 w:f32 h:f32 (место прошлого кадра) [held: u:f32 v:f32] [click: button:u8 mods:u8 (ClickMod*)]
//   звук: has:u8 now:f64 ended:u16 handle:u32×ended (отзвучавшие голоса мода)
//   сигналы хоста: count:u16 (tick:u64 name:str len:u32 payload)×count
//   атмосфера прошлого кадра: light:f32 windX:f32 windY:f32 precipitation:f32
//
// ===== Выход кадра (kh_frame, мод → хост): записи op:u8 и тело, по порядку =====
// Интерфейс (порядок записей — порядок вызовов Gui):
//   0x10 Open style · 0x11 Close · 0x12 Text text:str textStyle · 0x13 Image kind:u8 (1 монета, 2 мини-карта) w:f32 h:f32
//   0x14 ImageLook proto:i32 w:f32 h:f32 team:u8 · 0x15 Space w:f32 h:f32 grow:f32
//   0x16 Button id:str flags:u8 (1 active, 2 disabled) style — открывает узел кнопки, закрывает Close
//   0x17 TipText id:str text:str · 0x18 TipBegin · 0x19 TipEnd anchor
//   style: mask:u32 (StyleBit*), затем поля по возрастанию битов: id str, direction u8, self u8 u8, gap f32,
//          padding f32×4 (верх, право, низ, лево), align u8, justify u8, width f32, height f32, minWidth f32,
//          maxWidth f32, minHeight f32, maxHeight f32, grow f32, skin u8, background rgba, outline rgba, alpha f32,
//          anchor; флаги shrink, wrap, clip, scroll, passive — только битом
//   anchor: mask:u8 (left 1, right 2, top 4, bottom 8, centerX 16, centerY 32) и f32 на каждый бит; 0x80 — пустая
//          привязка (у корня — левый верхний угол); в TipEnd mask 0 — без привязки (подсказка у указателя)
//   textStyle: mask:u8 (TextBit*): font u8, color rgba, shadow rgba, lift f32, wrap f32; ellipsis — битом
// Поверх мира (в тайлах, толщина — в пикселях экрана; width NaN — заливка):
//   0x20 Line x1 y1 x2 y2:f32 rgba width:f32 · 0x21 Circle x y r:f32 rgba width:f32 · 0x22 Rect x1 y1 x2 y2:f32 rgba width:f32
// Эффекты:
//   0x30 Particle x y vx vy life:f32 color:u32 (0xRRGGBB или 0xAARRGGBB) size:f32 drift:u8
//   0x31 Decal x1 y1 x2 y2 width:f32 r g b:f32 opacity density life:f32
// Звук:
//   0x40 Play name:str volume pan rate:f32 · 0x41 SoundAt name:str x y:f64 volume reach detune:f32
//   0x42 Loop name:str volume pan:f32 · 0x47 Volume bus:u8 volume:f32
//   0x43 Voice handle:u32 source:u8 (0 граф: len:u32 KHVG; 1 вид: name:str) bus:u8 into:u32 (голос мода, 0 — шина)
//        intoNode:u16 at:f64 (NaN — сейчас) length:f32 (NaN — пока не отпустят) note:f32 velocity:f32
//        В KHVG соединения с voice ≠ 0 — номера голосов мода (handle), клиент подставляет свои.
//   0x44 Automate handle:u32 node:u16 param:u8 kind:u8 value:f32 at:f64 tau:f32 · 0x45 Place handle gain:f32 pan:f32
//   0x46 Release handle fade:f32
// Хосту: 0x50 Send order:str len:u32 payload
// Камера и атмосфера (SDK пишет Camera после обработчиков камеры и в конце кадра; рисунки поверх мира берут толщину
// по камере на момент записи): 0x60 Camera x y zoom:f64 · 0x61 Atmosphere mask:u8 (1 light, 2 wind, 4 precipitation)
//   [light:f32] [windX windY:f32] [precipitation:f32] · 0x62 Palette 12 × u32 0xRRGGBB (своя, чужая, союзная, ничья ×
//   тёмный, основной, светлый)

namespace Kharos.Mod.Abi;

/// <summary>Числа договора клиентского мода (форматы — в начале файла).</summary>
internal static class ClientAbi
{
    /// <summary>Сторона клиентского мода в ModLoadInfo.Side.</summary>
    public const string Side = "client";

    // Поток облика.
    public const byte LookOrigin = 1, LookStep = 2, LookShadow = 4, LookMemory = 8, LookBeam = 16, LookPortrait = 32;

    // Команды холста.
    public const byte CanvasEnd = 0, CanvasRect = 1, CanvasCircle = 2, CanvasRing = 3, CanvasLine = 4, CanvasPoly = 5,
        CanvasPixel = 6, CanvasTranslate = 7, CanvasLight = 8;

    // Ввод.
    public const byte InputInside = 1, InputFocused = 2, InputTouch = 4, InputOverUi = 8, InputShift = 16, InputCtrl = 32, InputAlt = 64;
    public const byte ButtonLeft = 1, ButtonRight = 2, ButtonMiddle = 4;

    // Интерфейс: состояние кадра и номеров.
    public const byte UiCovered = 1, UiHoveredButton = 2, UiPressedButton = 4, UiPressedDisabled = 8;
    public const byte IdHovered = 1, IdWithin = 2, IdPressing = 4, IdTipDue = 8, IdClicked = 16, IdHeld = 32;
    public const byte ClickShift = 1, ClickCtrl = 2, ClickAlt = 4, ClickLong = 8;

    // Записи выхода кадра.
    public const byte OpOpen = 0x10, OpClose = 0x11, OpText = 0x12, OpImage = 0x13, OpImageLook = 0x14, OpSpace = 0x15,
        OpButton = 0x16, OpTipText = 0x17, OpTipBegin = 0x18, OpTipEnd = 0x19;
    public const byte OpLine = 0x20, OpCircle = 0x21, OpRect = 0x22;
    public const byte OpParticle = 0x30, OpDecal = 0x31;
    public const byte OpPlay = 0x40, OpSoundAt = 0x41, OpLoop = 0x42, OpVoice = 0x43, OpAutomate = 0x44, OpPlace = 0x45,
        OpRelease = 0x46, OpVolume = 0x47;
    public const byte OpSend = 0x50;
    public const byte OpCamera = 0x60, OpAtmosphere = 0x61, OpPalette = 0x62;

    public const byte ImageCoin = 1, ImageMinimap = 2;
    public const byte ButtonActive = 1, ButtonDisabled = 2;
    public const byte VoiceGraph = 0, VoiceKind = 1;
    public const byte AtmosphereLight = 1, AtmosphereWind = 2, AtmospherePrecipitation = 4;

    // Стиль узла: биты маски.
    public const uint StyleId = 1 << 0, StyleDirection = 1 << 1, StyleSelf = 1 << 2, StyleGap = 1 << 3, StylePadding = 1 << 4,
        StyleAlign = 1 << 5, StyleJustify = 1 << 6, StyleWidth = 1 << 7, StyleHeight = 1 << 8, StyleMinWidth = 1 << 9,
        StyleMaxWidth = 1 << 10, StyleMinHeight = 1 << 11, StyleMaxHeight = 1 << 12, StyleGrow = 1 << 13, StyleShrink = 1 << 14,
        StyleWrap = 1 << 15, StyleSkin = 1 << 16, StyleBackground = 1 << 17, StyleOutline = 1 << 18, StyleAlpha = 1 << 19,
        StyleClip = 1 << 20, StyleScroll = 1 << 21, StyleAnchor = 1 << 22, StylePassive = 1 << 23;

    public const byte AnchorLeft = 1, AnchorRight = 2, AnchorTop = 4, AnchorBottom = 8, AnchorCenterX = 16, AnchorCenterY = 32;
    public const byte TextFont = 1, TextColor = 2, TextShadow = 4, TextLift = 8, TextWrap = 16, TextEllipsis = 32;

    // Инструменты клиента (kh.tool): общие с хостом — 1, 2, 3, 4 (Position и Facing — сглаженные), 5–9; памяти мода
    // (10) нет. Свои:
    //  20 Each      in: k:u32 component:u32×k   out: count:u32 entity:u32×count — со всеми компонентами, по номерам
    //  21 Pose      in: n:u32 entity:u32×n      out: (x:f64 y:f64 angle:f64)×n — сглажено между тиками; нет — NaN
    //  22 Pick      in: x:f64 y:f64             out: entity:u32 (0 — никого): юнит по радиусу, здание по прямоугольнику
    //  23 Within    in: x1 y1 x2 y2:f64 k:u32 component:u32×k   out: count:u32 entity:u32×count
    //  24 Sees      in: n:u32 (x:f64 y:f64)×n   out: u8×n — видит ли игрок тайл сейчас (тумана нет — всё)
    //  25 Explored  то же — разведан ли
    //  26 Measure   in: font:u8 text (UTF-8)    out: width:f32 — ширина строки шрифтом интерфейса
    //  27 Column    in: component:u32 field:u32 n:u32 entity:u32×n   out: f64×n (нет компонента — NaN)
    public const int ToolEach = 20, ToolPose = 21, ToolPick = 22, ToolWithin = 23, ToolSees = 24, ToolExplored = 25,
        ToolMeasure = 26, ToolColumn = 27;
}
