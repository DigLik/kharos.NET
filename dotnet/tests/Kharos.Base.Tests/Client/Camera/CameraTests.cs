using Base.Client;

namespace Kharos.Base.Tests;

/// <summary>Камера: разгон, колесо, полёт, бросок мышью, замер скорости (packages/tests/camera.test.ts).</summary>
public sealed class CameraTests
{
    private const double Frame = 1.0 / 60;

    private static (CameraModel Camera, CameraMotion Motion) Setup()
    {
        var camera = new CameraModel { Width = 1280, Height = 720 };
        return (camera, new CameraMotion(camera));
    }

    /// <summary>Гоняет кадры: seconds секунд по frame.</summary>
    private static void Run(CameraMotion motion, double seconds, double thrustX = 0, double thrustY = 0, double frame = Frame)
    {
        for (double t = 0; t < seconds - 1e-9; t += frame)
            motion.Update(frame, thrustX, thrustY);
    }

    [Fact(DisplayName = "клавиши разгоняют камеру постепенно, а отпущенные — она тормозит и встаёт")]
    public void KeysAccelerateGraduallyAndReleasedCameraStops()
    {
        var (camera, motion) = Setup();
        motion.Update(Frame, 900, 0);
        Assert.True(motion.VelocityX > 0);
        Assert.True(motion.VelocityX < 200);
        Run(motion, 1, 900, 0);
        Assert.True(motion.VelocityX > 890);

        var released = camera.X;
        Run(motion, 0.1);
        // Ещё катится, но медленнее.
        Assert.True(camera.X > released);
        Assert.True(motion.VelocityX < 900);
        Assert.True(motion.VelocityX > 0);
        Run(motion, 3);
        Assert.Equal(0, motion.VelocityX);
        var stopped = camera.X;
        Run(motion, 1);
        Assert.Equal(stopped, camera.X);
    }

    [Fact(DisplayName = "колесо меняет масштаб плавно, и точка под курсором остаётся на месте")]
    public void WheelZoomsSmoothlyAndKeepsPointUnderCursor()
    {
        var (camera, motion) = Setup();
        var (anchorX, anchorY) = (900.0, 200.0);
        var before = camera.ScreenToTile(anchorX, anchorY);
        motion.ZoomBy(2, anchorX, anchorY);
        motion.Update(Frame);
        Assert.True(camera.Zoom > 32);
        Assert.True(camera.Zoom < 48);
        Run(motion, 1);
        Assert.Equal(64, camera.Zoom);
        var after = camera.ScreenToTile(anchorX, anchorY);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);

        // Колесо копит цель: два щелчка подряд — вдвое дальше, но не за пределы.
        motion.ZoomBy(0.5, anchorX, anchorY);
        motion.ZoomBy(0.5, anchorX, anchorY);
        Run(motion, 1);
        Assert.Equal(16, camera.Zoom);
        for (var i = 0; i < 20; i++)
            motion.ZoomBy(0.5, anchorX, anchorY);
        Run(motion, 1);
        Assert.Equal(camera.ClampZoom(0), camera.Zoom);
        motion.ZoomBy(2, anchorX, anchorY);
        Run(motion, 1);
        Assert.Equal(camera.ClampZoom(0) * 2, camera.Zoom);
    }

    [Fact(DisplayName = "полёт к точке: разгон, торможение без перелёта и остановка точно на цели — даже при длинных кадрах")]
    public void FlightAcceleratesBrakesWithoutOvershootAndStopsOnTarget()
    {
        foreach (var frame in new[] { Frame, 0.25 })
        {
            var (camera, motion) = Setup();
            motion.FlyTo(500, -300);
            var distance = double.PositiveInfinity;
            var steps = 0;
            var speeds = new List<double>();
            while (motion.Flying && steps++ < 1000)
            {
                motion.Update(frame);
                var focus = camera.Focus;
                var next = Math.Sqrt((focus.X - 500) * (focus.X - 500) + (focus.Y + 300) * (focus.Y + 300));
                Assert.True(next <= distance + 1e-9);
                speeds.Add(distance - next);
                distance = next;
            }
            Assert.False(motion.Flying);
            Assert.Equal(500, camera.Focus.X, 6);
            Assert.Equal(-300, camera.Focus.Y, 6);
            // Не скачок: сначала разгон, потом торможение.
            if (frame == Frame)
            {
                var peak = speeds.IndexOf(speeds.Skip(1).Max());
                Assert.True(peak > 2);
                Assert.True(speeds[^1] < speeds[peak] / 100);
            }
        }
    }

    [Fact(DisplayName = "клавиши прерывают полёт")]
    public void KeysInterruptFlight()
    {
        var (_, motion) = Setup();
        motion.FlyTo(500, 0);
        Run(motion, 0.1);
        Assert.True(motion.Flying);
        motion.Update(Frame, 0, 900);
        Assert.False(motion.Flying);
    }

    [Fact(DisplayName = "брошенная мышью камера катится дальше, а остановленная перед отпусканием — нет")]
    public void ThrownCameraKeepsRollingAndStoppedOneDoesNot()
    {
        var (camera, motion) = Setup();
        for (var i = 0; i < 10; i++)
        {
            motion.Drag(-30, 0);
            motion.Update(Frame);
        }
        var dragged = camera.X;
        Assert.Equal(30 * 10 / 32.0, dragged, 6);
        motion.Release();
        Assert.True(motion.VelocityX > 1000);
        Run(motion, 0.5);
        Assert.True(camera.X > dragged + 1);

        var (_, still) = Setup();
        for (var i = 0; i < 10; i++)
        {
            still.Drag(-30, 0);
            still.Update(Frame);
        }
        Run(still, 0.3);
        still.Release();
        Assert.Equal(0, still.VelocityX);
    }

    [Fact(DisplayName = "замер скорости: полёт считается, а прыжок по мини-карте — нет")]
    public void SpeedMeterCountsFlightButNotMinimapJump()
    {
        var (_, motion) = Setup();
        Run(motion, 1, 900, 0);
        motion.Measure(Frame);
        motion.Update(Frame, 900, 0);
        motion.Measure(Frame);
        // 900 пикселей в секунду при 32 пикселях на тайл.
        Assert.Equal(900 / 32.0, motion.Speed, 0);

        motion.Jump(10000, 10000);
        motion.Update(Frame);
        motion.Measure(Frame);
        Assert.Equal(0, motion.Speed);
        Assert.Equal(0, motion.VelocityX);
    }
}
