namespace Kharos.Client.Ui;

public sealed partial class Gui
{
    /// <summary>Затемнение под модальным окном.</summary>
    public static readonly Color Dim = new(0, 0, 0, 140);

    private HashSet<string> _keeps = [], _nextKeeps = [];
    private int _modalFrom, _nextModal = -1;

    /// <summary>Лежит ли в прошлом кадре модальное окно: ввод ниже него не доходит ни до интерфейса, ни до модов.</summary>
    public bool ModalOpen { get; private set; }
    /// <summary>Номер самого верхнего модального окна прошлого кадра.</summary>
    public string? ModalId { get; private set; }

    /// <summary>Клавиши игры молчат: в поле ввода печатают или над миром открыто модальное окно.</summary>
    public bool CapturesKeys => Typing || ModalOpen;

    /// <summary>
    /// Модальное окно: затемняет экран, забирает ввод (под него не достаётся ни наведение, ни щелчки, ни колесо, ни
    /// клавиши игры) и закрывается по Escape. Панель — по центру. Возвращает true в кадре, когда окно просят закрыть:
    /// Escape или (closeOnBackdrop) щелчок по затемнению; решает вызывающий — перестать описывать окно.
    /// </summary>
    public bool Modal(string id, Style panel, Action children, bool closeOnBackdrop = false)
    {
        Open(new Style
        {
            Id = id, Direction = Direction.Stack, Anchor = new Anchor(Left: 0, Right: 0, Top: 0, Bottom: 0),
            Background = Dim, Modal = true,
        });
        try
        {
            if (panel.Skin is null && panel.Background is null) panel.Skin = Ui.Skin.Panel;
            panel.Id ??= id + ":panel";
            panel.Self ??= (Align.Center, Align.Center);
            Box(panel, children);
        }
        finally { Close(); }
        return KeyHit("escape") && (ModalId is null || ModalId == id) || closeOnBackdrop && Clicked(id) is not null;
    }

    /// <summary>Нажата ли клавиша в этом кадре.</summary>
    public bool KeyHit(string key) => _hitKeys.Contains(key);

    /// <summary>Модальные окна — после остальных корневых панелей, со сохранением порядка.</summary>
    private void StackModals()
    {
        if (!_top.Exists(node => node.Style.Modal)) return;
        var modals = _top.Where(node => node.Style.Modal).ToList();
        _top.RemoveAll(node => node.Style.Modal);
        _top.AddRange(modals);
    }
}
