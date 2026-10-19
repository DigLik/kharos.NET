namespace Base.Client;

/// <summary>
/// Движение камеры с разгоном и торможением — перенос cameraMotion.ts. Клавиши задают скорость, к которой камера
/// разгоняется, а отпущенные — камера плавно тормозит. Тянут мышью — камера идёт за ней точно, а брошенная катится
/// дальше. Колесо задаёт масштаб, который камера плавно догоняет, держа на месте точку под курсором. Полёт к точке —
/// разгон и торможение без перелёта. Мини-карта переставляет камеру сразу (<see cref="Jump"/>), и это гасит всё
/// движение. Заодно меряет, как быстро камера несётся над картой: от этого зависит звук полёта.
/// </summary>
internal sealed class CameraMotion(CameraModel camera)
{
    /// <summary>Разгон от клавиш: за столько секунд камера набирает две трети скорости.</summary>
    private const double Acceleration = 0.15;

    /// <summary>Торможение: отпущенная камера катится ещё немного и за несколько таких отрезков встаёт.</summary>
    private const double Deceleration = 0.25;

    /// <summary>Медленнее этого, пикселей экрана в секунду, камера уже стоит.</summary>
    private const double Rest = 4;

    /// <summary>Масштаб догоняет колесо: за столько секунд он проходит две трети пути.</summary>
    private const double ZoomSmoothing = 0.09;

    /// <summary>Ближе этого к цели масштаб встаёт на неё, в долях масштаба.</summary>
    private const double ZoomArrived = 0.001;

    /// <summary>Полёт к точке: пружина без перелёта. Чем больше, тем быстрее: почти на месте камера через 5 / Flight секунд.</summary>
    private const double Flight = 6;

    /// <summary>Ближе этого к цели, в пикселях экрана, полёт закончен.</summary>
    private const double FlightArrived = 0.5;

    /// <summary>Скорость мыши, которой тянут камеру, сглаживается за столько секунд: случайный рывок не бросает камеру.</summary>
    private const double DragSmoothing = 0.05;

    /// <summary>Если мышь стояла дольше этого перед тем, как её отпустили, — камеру остановили, а не бросили. В секундах.</summary>
    private const double FlingWindow = 0.08;

    public CameraModel Camera { get; } = camera;

    /// <summary>Скорость камеры, пикселей экрана в секунду.</summary>
    public double VelocityX, VelocityY;

    /// <summary>
    /// Как быстро камера неслась над картой в прошлом кадре, тайлов в секунду: сдвиг и то, как разбегались углы экрана от зума.
    /// </summary>
    public double Speed { get; private set; }

    /// <summary>Простое управление, для редактора: без разгона, инерции, полёта и плавного масштаба — всё сразу.</summary>
    public bool Simple { get; set; }

    // Масштаб, к которому идёт камера; null — масштаб не меняется. Точка экрана, которая стоит на месте, пока он меняется.
    private double? _zoomTarget;
    private (double X, double Y) _zoomAnchor;

    // Куда камера летит сама, в тайлах, и её скорость в полёте, тайлов в секунду. null — не летит.
    private (double X, double Y)? _flight;
    private double _flightVelocityX, _flightVelocityY;

    // Тянут ли камеру мышью, на сколько пикселей с прошлого кадра, с какой скоростью и сколько секунд назад был сдвиг.
    private bool _dragging;
    private double _draggedX, _draggedY, _dragVelocityX, _dragVelocityY, _sinceDrag = double.PositiveInfinity;

    // Где камера была на прошлом замере; null — её переставили, и скачок не считается полётом.
    private (double X, double Y, double Zoom)? _last;

    /// <summary>Летит ли камера сама к точке.</summary>
    public bool Flying => _flight is not null;

    /// <summary>Колесо: масштаб в factor раз от того, к которому камера уже идёт. Точка экрана (x, y) остаётся на месте.</summary>
    public void ZoomBy(double factor, double x, double y)
    {
        if (Simple)
        {
            Camera.ZoomTo(Camera.ClampZoom(Camera.Zoom * factor), x, y);
            return;
        }
        _zoomTarget = Camera.ClampZoom((_zoomTarget ?? Camera.Zoom) * factor);
        _zoomAnchor = (x, y);
    }

    /// <summary>Камеру тянут мышью на (dx, dy) пикселей экрана: она идёт за мышью сразу, без разгона.</summary>
    public void Drag(double dx, double dy)
    {
        Camera.MoveBy(-dx / Camera.Zoom, -dy / Camera.Zoom);
        _dragging = true;
        _draggedX += dx;
        _draggedY += dy;
        _sinceDrag = 0;
        _flight = null;
        VelocityX = VelocityY = 0;
    }

    /// <summary>Мышь отпустили: если её ещё тянули в последний миг, камера катится дальше с той же скоростью.</summary>
    public void Release()
    {
        if (!_dragging)
            return;
        _dragging = false;
        if (_sinceDrag <= FlingWindow && !Simple)
        {
            VelocityX = -_dragVelocityX;
            VelocityY = -_dragVelocityY;
        }
        _draggedX = _draggedY = _dragVelocityX = _dragVelocityY = 0;
    }

    /// <summary>Камера летит так, чтобы точка (x, y) в тайлах оказалась в середине видимой части.</summary>
    public void FlyTo(double x, double y)
    {
        if (Simple)
        {
            Jump(x, y);
            return;
        }
        if (_flight is null)
            _flightVelocityX = _flightVelocityY = 0;
        _flight = (x, y);
        VelocityX = VelocityY = 0;
    }

    /// <summary>Сразу ставит точку (x, y) в тайлах в середину видимой части: без полёта, разгона и звука.</summary>
    public void Jump(double x, double y)
    {
        Camera.CenterOn(x, y);
        _flight = null;
        VelocityX = VelocityY = 0;
        _last = null;
    }

    /// <summary>
    /// Раз в кадр двигает камеру. (thrustX, thrustY) — скорость, которую просят клавиши, пикселей экрана в секунду: к ней
    /// камера разгоняется, а без неё тормозит. Клавиши прерывают полёт.
    /// </summary>
    public void Update(double seconds, double thrustX = 0, double thrustY = 0)
    {
        if (seconds <= 0)
            return;

        // Скорость мыши — за кадр, сглаженная: по ней камеру бросают.
        var drag = 1 - Math.Exp(-seconds / DragSmoothing);
        _dragVelocityX += (_draggedX / seconds - _dragVelocityX) * drag;
        _dragVelocityY += (_draggedY / seconds - _dragVelocityY) * drag;
        _draggedX = _draggedY = 0;
        _sinceDrag += seconds;

        var thrust = thrustX != 0 || thrustY != 0;
        if (thrust)
            _flight = null;
        // Простое управление: скорость — ровно та, что просят клавиши.
        var blend = Simple ? 1 : 1 - Math.Exp(-seconds / (thrust ? Acceleration : Deceleration));
        VelocityX += (thrustX - VelocityX) * blend;
        VelocityY += (thrustY - VelocityY) * blend;
        if (!thrust && Math.Sqrt(VelocityX * VelocityX + VelocityY * VelocityY) < Rest)
            VelocityX = VelocityY = 0;
        if (VelocityX != 0 || VelocityY != 0)
            Camera.MoveBy(VelocityX * seconds / Camera.Zoom, VelocityY * seconds / Camera.Zoom);

        if (_flight is { } goal)
        {
            // Пружина без перелёта, решённая точно: шаг устойчив при любой длине кадра.
            // Сдвиг от цели x(t) = (x0 + (v0 + ωx0)t)e^(-ωt), скорость v(t) = (v0 - ω(v0 + ωx0)t)e^(-ωt).
            var focus = Camera.Focus;
            var fade = Math.Exp(-Flight * seconds);
            var pullX = _flightVelocityX + Flight * (focus.X - goal.X);
            var pullY = _flightVelocityY + Flight * (focus.Y - goal.Y);
            var offsetX = (focus.X - goal.X + pullX * seconds) * fade;
            var offsetY = (focus.Y - goal.Y + pullY * seconds) * fade;
            _flightVelocityX = (_flightVelocityX - Flight * pullX * seconds) * fade;
            _flightVelocityY = (_flightVelocityY - Flight * pullY * seconds) * fade;
            var arrived = Math.Sqrt(offsetX * offsetX + offsetY * offsetY) * Camera.Zoom < FlightArrived
                && Math.Sqrt(_flightVelocityX * _flightVelocityX + _flightVelocityY * _flightVelocityY) * Camera.Zoom < FlightArrived * Flight;
            Camera.CenterOn(goal.X + (arrived ? 0 : offsetX), goal.Y + (arrived ? 0 : offsetY));
            if (arrived)
            {
                _flight = null;
                _flightVelocityX = _flightVelocityY = 0;
            }
        }

        if (_zoomTarget is { } wanted)
        {
            var target = Camera.ClampZoom(wanted);
            var next = Camera.Zoom * Math.Pow(target / Camera.Zoom, 1 - Math.Exp(-seconds / ZoomSmoothing));
            var arrived = Math.Abs(Math.Log(target / next)) < ZoomArrived;
            // В полёте масштаб меняется вокруг середины экрана: туда камера и летит.
            var anchor = _flight is not null ? Camera.ViewCenter : _zoomAnchor;
            Camera.ZoomTo(arrived ? target : next, anchor.X, anchor.Y);
            if (arrived)
                _zoomTarget = null;
        }
    }

    /// <summary>Замер после всех сдвигов кадра, включая упор в границы карты: сколько камера на самом деле пролетела.</summary>
    public void Measure(double seconds)
    {
        var last = _last;
        _last = (Camera.X, Camera.Y, Camera.Zoom);
        if (last is not { } before || seconds <= 0)
        {
            Speed = 0;
            return;
        }
        var pan = Math.Sqrt((Camera.X - before.X) * (Camera.X - before.X) + (Camera.Y - before.Y) * (Camera.Y - before.Y));
        // Зум — как быстро уходят к краям или набегают из-за них углы экрана.
        var halfDiagonal = Math.Sqrt(Camera.Width * Camera.Width + Camera.Height * Camera.Height) / 2;
        var dive = Math.Abs(halfDiagonal / Camera.Zoom - halfDiagonal / before.Zoom);
        Speed = (pan + dive) / seconds;
    }
}
