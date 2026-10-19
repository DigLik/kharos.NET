using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Раздел Editor: содержимое панели редактора (Н6-3) — кисти, виды, месторождения, перетаскивание, призраки (editorPanel.ts).</summary>
internal sealed class EditorSection : Section
{
    // TODO(Р): Editor/*; оболочка режима — Kharos.Client/Shell/Editor*. Обработчики — client.Ui("base.editor", Frame) и для кистей client.Controls.
    public override void Setup(ClientBuilder client)
    {
    }

    public override void Frame(ClientContext c)
    {
    }
}
