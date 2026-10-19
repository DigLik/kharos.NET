namespace Base.Client;

/// <summary>
/// Камера игры без движка — перенос camera.ts: куда смотрим и в каком масштабе (центр экрана в тайлах, пикселей на тайл),
/// размер экрана, пределы масштаба, пересчёт между экраном и тайлами. Чистая модель: ею пользуются <see cref="CameraMotion"/>
/// и тесты, а раздел камеры переписывает её в <c>c.Camera</c> и обратно каждый кадр. Отступов под интерфейс у подачи нет:
/// видимая часть — весь экран.
/// </summary>
internal sealed class CameraModel
{
    public const double MinZoom = 4, MaxZoom = 96;

    /// <summary>Центр экрана в тайлах.</summary>
    public double X, Y;

    /// <summary>Пикселей на тайл. Менять через <see cref="ZoomTo"/>: он держит масштаб в допустимых пределах.</summary>
    public double Zoom = 32;

    /// <summary>Нижняя граница масштаба. Её поднимает карта: слишком мелкий экран не помещается в окно местности.</summary>
    public double LowZoom = MinZoom;

    /// <summary>Размер экрана в логических пикселях.</summary>
    public double Width = 1, Height = 1;

    /// <summary>Середина видимой части холста в пикселях экрана.</summary>
    public (double X, double Y) ViewCenter => (Width / 2, Height / 2);

    /// <summary>Тайл в середине видимой части.</summary>
    public (double X, double Y) Focus => ScreenToTile(ViewCenter.X, ViewCenter.Y);

    /// <summary>Ставит камеру так, чтобы точка (x, y) в тайлах оказалась в середине видимой части.</summary>
    public void CenterOn(double x, double y)
    {
        var focus = Focus;
        X += x - focus.X;
        Y += y - focus.Y;
    }

    /// <summary>Видимая часть в тайлах: левый верхний и правый нижний углы.</summary>
    public ((double X, double Y) From, (double X, double Y) To) Visible => (ScreenToTile(0, 0), ScreenToTile(Width, Height));

    /// <summary>Сдвиг в тайлах.</summary>
    public void MoveBy(double dx, double dy)
    {
        X += dx;
        Y += dy;
    }

    /// <summary>Масштаб zoom, приведённый в допустимые пределы.</summary>
    public double ClampZoom(double zoom) => Math.Min(MaxZoom, Math.Max(Math.Max(MinZoom, LowZoom), zoom));

    /// <summary>Высота камеры: 0 — ближе всего к земле, 1 — дальше всего.</summary>
    public double Altitude => Math.Log(MaxZoom / Zoom) / Math.Log(MaxZoom / MinZoom);

    /// <summary>Меняет масштаб; точка экрана (anchorX, anchorY) остаётся на месте. По умолчанию это середина видимой части.</summary>
    public void ZoomTo(double zoom, double? anchorX = null, double? anchorY = null)
    {
        var next = ClampZoom(zoom);
        var offsetX = (anchorX ?? ViewCenter.X) - Width / 2;
        var offsetY = (anchorY ?? ViewCenter.Y) - Height / 2;
        X += offsetX / Zoom - offsetX / next;
        Y += offsetY / Zoom - offsetY / next;
        Zoom = next;
    }

    /// <summary>Пиксели экрана → тайлы (дробные).</summary>
    public (double X, double Y) ScreenToTile(double screenX, double screenY) => (X + (screenX - Width / 2) / Zoom, Y + (screenY - Height / 2) / Zoom);

    /// <summary>Тайлы → пиксели экрана.</summary>
    public (double X, double Y) TileToScreen(double tileX, double tileY) => (Width / 2 + (tileX - X) * Zoom, Height / 2 + (tileY - Y) * Zoom);
}
