using Base.Shared.Signals;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Раздел Editor: содержимое панели редактора (Н6-3, editorPanel.ts) — кисти, виды, месторождения, перетаскивание,
/// призраки. Режим включает оболочка (Shell/Editor*: право редактора, вход из меню и по /editor, сохранение): он приходит
/// флагом <see cref="ShellMode.Editor"/> во входе кадра. Пока он есть:
/// <list type="bullet">
/// <item>на этапе камеры — флаг <see cref="EditorModel.OpenKey"/> («editor.open», bool) в общей памяти мода: его видят все
/// разделы с первого этапа кадра (управление молчит, Esc не открывает меню);</item>
/// <item>на этапе панелей — панель справа (<see cref="EditorPanel"/>);</item>
/// <item>на этапе управления (раньше раздела Controls) — мышь и призраки (<see cref="EditorMouse"/>).</item>
/// </list>
/// Отказ правки (сигнал refused) — в строку состояния панели. Кисти и виды модов — <see cref="EditorExtras"/>.
/// </summary>
internal sealed class EditorSection : Section
{
    private EditorModel _model = null!;
    private EditorPanel _panel = null!;
    private EditorMouse _mouse = null!;

    public override void Setup(ClientBuilder client)
    {
        client.Camera("base.editor.mode", Mode);
        client.Ui("base.editor", Frame);
        client.Controls("base.editor.mouse", Mouse);
        client.Signal(RefusedSignal.Name, (c, signal) =>
        {
            if (!Open(c) || signal.Read<RefusedSignal>() is not { } refused || !refused.Order.StartsWith("edit.", StringComparison.Ordinal))
                return;
            _model.Status = c.Text("host." + refused.Reason);
        });
    }

    public override void Load(BaseState state)
    {
        base.Load(state);
        _model = new EditorModel(state);
        _panel = new EditorPanel(_model);
        _mouse = new EditorMouse(_model);
    }

    /// <summary>Открыт ли редактор в этом кадре (флаг оболочки).</summary>
    public static bool Open(ClientContext c) => (c.Shell & ShellMode.Editor) != 0;

    private void Mode(ClientContext c)
    {
        if (Open(c))
        {
            c.Shared[EditorModel.OpenKey] = true;
            _model.Prepare(c);
        }
        else if (c.Shared.Remove(EditorModel.OpenKey))
        {
            _model.ClearPicks();
            _mouse.Cancel();
        }
    }

    public override void Frame(ClientContext c)
    {
        if (Open(c) && (c.Shell & ShellMode.Menu) == 0)
            _panel.Draw(c);
    }

    private void Mouse(ClientContext c)
    {
        if (Open(c) && (c.Shell & ShellMode.Menu) == 0)
            _mouse.Update(c);
    }
}
