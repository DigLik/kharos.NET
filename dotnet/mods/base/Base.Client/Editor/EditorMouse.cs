using Base.Shared.Orders;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Мышь редактора на карте (createEdit в editorPanel.ts): пока панель открыта, левая и правая кнопки — её. Кисть красит
/// землю или кладёт покрытие протяжкой (по тайлу за раз), здание, юнит и месторождение встают щелчком там, где призрак;
/// «Выбор» — щелчок, рамка, Shift, перетаскивание выбранного разом; правый щелчок и Delete сносят. Призраки: здание —
/// белым или красным по правилу места, юнит — свободным спрайтом (это не сущность: не выбирается и не в счёт мира),
/// красным там, где ему не встать; месторождение и кисть — рамкой.
/// </summary>
internal sealed class EditorMouse(EditorModel model)
{
    // Цвета TS (cursorPass.ts, selectionPass.ts).
    private const uint Allowed = 0x59ff73, Forbidden = 0xff4d40, Pick = 0x9fe0ff, Box = 0xd6e6f5;
    private const uint GhostAllowed = 0xffffff, GhostForbidden = 0xff3324;
    private const float GhostAlpha = 0.7f;

    /// <summary>Что тянут мышью: выбранное разом или рамку выделения.</summary>
    private (bool Group, double X, double Y)? _drag;
    private (int X, int Y)? _lastTile;

    private BaseState State => model.State;

    public void Update(ClientContext c)
    {
        var input = c.Input;
        model.Prune(c);
        model.Hover = input.Inside ? ((int)Math.Floor(input.WorldX), (int)Math.Floor(input.WorldY)) : null;
        if (!input.Inside || input.OverUi && _drag is null)
        {
            if (input.Released(PointerButton.Left))
                Cancel();
            Draw(c, ghosts: false);
            return;
        }
        var (wx, wy) = (input.WorldX, input.WorldY);
        var (x, y) = model.Hover!.Value;
        if (input.Hits.Contains("delete") || input.Hits.Contains("backspace"))
            model.EraseSelection(c);
        if (input.Hits.Contains("escape"))
            model.ClearPicks();

        if (input.Pressed(PointerButton.Right))
            Secondary(c, wx, wy);
        else if (model.Tool == "select")
            Select(c, wx, wy, x, y, input);
        else if (input.Pressed(PointerButton.Left) || input.Down(PointerButton.Left) && _lastTile != (x, y))
            Brush(c, x, y, input.Pressed(PointerButton.Left));
        if (input.Released(PointerButton.Left))
            _lastTile = null;
        Draw(c, ghosts: true);
    }

    /// <summary>Кисти, здание, юнит, месторождение: щелчок (pressed) или протяжка по новому тайлу.</summary>
    private void Brush(ClientContext c, int x, int y, bool pressed)
    {
        _lastTile = (x, y);
        switch (model.Tool)
        {
            case "paint":
                model.Send(c, EditPaintOrder.Name, new EditPaintOrder { X = x, Y = y, Size = model.Size, Brush = model.Brush });
                break;
            case "paving":
                model.Send(c, EditPaveOrder.Name, new EditPaveOrder { Tiles = EditorModel.Square(x, y, model.Size), Kind = model.Pave, Player = model.Player });
                break;
            case "building" when pressed:
                if (model.World.Spec(c, model.Building) is { } spec)
                {
                    var (bx, by) = model.World.Where(c, spec, x, y);
                    model.Send(c, EditBuildingOrder.Name, new EditBuildingOrder { Type = model.Building, X = bx, Y = by, Player = model.Player });
                }
                break;
            case "unit" when pressed:
                model.Send(c, EditUnitOrder.Name, new EditUnitOrder { Type = model.Unit, X = x + 0.5, Y = y + 0.5, Player = model.Player });
                break;
            case "deposit" when pressed:
                var (dx, dy) = EditorModel.DepositSpot(x, y);
                model.Send(c, EditDepositOrder.Name, new EditDepositOrder { X = dx, Y = dy, Kind = model.Deposit, Reserve = model.Reserve });
                break;
            default:
                if (model.ModBrush is { } brush && (pressed || brush.Size))
                    model.Send(c, brush.Order, new EditBrushCall
                    {
                        Brush = brush.Id, Option = model.Options.GetValueOrDefault(brush.Id) ?? brush.Options.FirstOrDefault(),
                        X = x, Y = y, Size = brush.Size ? model.Size : 1, Player = model.Player,
                    });
                break;
        }
    }

    /// <summary>«Выбор»: щелчок — выбрать, мимо — рамка, Shift — добавить или снять, выбранное тащится разом.</summary>
    private void Select(ClientContext c, double wx, double wy, int x, int y, Input input)
    {
        if (input.Pressed(PointerButton.Left))
        {
            var target = model.EntityAt(c, wx, wy);
            var spot = target is null ? model.DepositUnder(c, wx, wy) : null;
            var chosen = target is { } entity ? model.Selection.Contains(entity) : spot is { } s && model.Spots.Contains((s.X, s.Y));
            if (target is null && spot is null)
            {
                if (!input.Shift)
                    model.ClearPicks();
                _drag = (false, wx, wy);
            }
            else if (input.Shift && chosen)
            {
                if (target is { } picked)
                    model.Selection.Remove(picked);
                else
                    model.Spots.Remove((spot!.Value.X, spot.Value.Y));
            }
            else
            {
                if (!input.Shift && !chosen)
                    model.ClearPicks();
                if (target is { } picked && !chosen)
                    model.Selection.Add(picked);
                else if (spot is { } found && !chosen)
                    model.Spots.Add((found.X, found.Y));
                _drag = (true, x, y);
            }
            return;
        }
        if (_drag is not { } drag)
            return;
        if (!drag.Group)
        {
            if (input.Released(PointerButton.Left))
            {
                var (left, top, right, bottom) = (Math.Min(drag.X, wx), Math.Min(drag.Y, wy), Math.Max(drag.X, wx), Math.Max(drag.Y, wy));
                foreach (var entity in c.Within(left, top, right, bottom, State.P.Position))
                    if ((c.Has(entity, State.P.Unit) || c.Has(entity, State.P.Building)) && !model.Selection.Contains(entity))
                        model.Selection.Add(entity);
                foreach (var spot in model.DepositsIn(c, left, top, right, bottom))
                    if (!model.Spots.Contains(spot))
                        model.Spots.Add(spot);
                _drag = null;
            }
            return;
        }
        if ((x, y) != ((int)drag.X, (int)drag.Y) && (model.Selection.Count > 0 || model.Spots.Count > 0))
        {
            // Выбранное тянут вместе: сдвиг целыми тайлами; хост сдвинет всё или ничего.
            var (dx, dy) = (x - (int)drag.X, y - (int)drag.Y);
            model.Send(c, EditMoveOrder.Name, new EditMoveOrder { Entities = [.. model.Selection], Dx = dx, Dy = dy, Deposits = EditorModel.Flat(model.Spots) });
            for (var i = 0; i < model.Spots.Count; i++)
                model.Spots[i] = (model.Spots[i].X + dx, model.Spots[i].Y + dy);
            _drag = (true, x, y);
        }
        if (input.Released(PointerButton.Left))
            _drag = null;
    }

    /// <summary>Правый щелчок сносит: по выбранному — всё выбранное, иначе — то, что под указателем.</summary>
    private void Secondary(ClientContext c, double wx, double wy)
    {
        var target = model.EntityAt(c, wx, wy);
        var spot = target is null ? model.DepositUnder(c, wx, wy) : null;
        if (target is null && spot is null)
            return;
        if (target is { } entity ? model.Selection.Contains(entity) : model.Spots.Contains((spot!.Value.X, spot.Value.Y)))
        {
            model.EraseSelection(c);
            return;
        }
        if (target is { } one)
            model.Send(c, EditEraseOrder.Name, new EditEraseOrder { Entities = [one] });
        else
            model.Send(c, EditRemoveDepositOrder.Name, new EditRemoveDepositOrder { Spots = [spot!.Value.X, spot.Value.Y] });
    }

    public void Cancel()
    {
        _drag = null;
        _lastTile = null;
    }

    // ------------------------------------------------------------------ призраки и пометки

    private void Draw(ClientContext c, bool ghosts)
    {
        var draw = c.Draw;
        // Выбранное: рамка у зданий, кольцо у юнитов; месторождения — рамкой.
        foreach (var entity in model.Selection)
        {
            if (c.Pose(entity) is not { } at)
                continue;
            if (State.Size(c, entity) is { } size)
                draw.Rect(at.X, at.Y, at.X + size.W, at.Y + size.H, Pick, 1.5f);
            else
                draw.Circle(at.X, at.Y, State.Radius(c, entity) + 0.15, Pick, 1.5f);
        }
        foreach (var (x, y) in model.Spots)
            draw.Rect(x, y, x + EditorModel.DepositSize, y + EditorModel.DepositSize, Pick, 1.5f);
        if (_drag is { Group: false } box && c.Input.Inside)
            draw.Rect(box.X, box.Y, c.Input.WorldX, c.Input.WorldY, Box, 1, 0.8f);
        model.GhostBlocked = false;
        if (!ghosts || model.Hover is not { } hover)
            return;
        var (hx, hy) = hover;
        switch (model.Tool)
        {
            case "unit":
            {
                // Призрак юнита — свободный спрайт, а не сущность: его нельзя выбрать, и в мире его нет.
                var blocked = !model.CanStand(c, model.Unit, hx, hy);
                model.GhostBlocked = blocked;
                var proto = State.UnitProto(c, model.Unit);
                if (proto >= 0)
                    draw.Sprite(proto, hx + 0.5, hy + 0.5, angle: 0, alpha: GhostAlpha, team: Team(c),
                        layer: State.Data.Find("units/" + model.Unit)?.Text("kind") == "air" ? LookLayer.Air : LookLayer.Ground, tint: blocked ? GhostForbidden : GhostAllowed);
                break;
            }
            case "building" when model.World.Spec(c, model.Building) is { } spec:
            {
                var (x, y) = model.World.Where(c, spec, hx, hy);
                var allowed = model.World.CanPlace(c, spec, x, y);
                var proto = c.ProtoId("buildings/" + model.Building);
                if (proto >= 0)
                    draw.Sprite(proto, x, y, alpha: GhostAlpha, team: Team(c), layer: LookLayer.Building, tint: allowed ? GhostAllowed : GhostForbidden);
                draw.Rect(x, y, x + spec.Width, y + spec.Height, allowed ? Allowed : Forbidden, 1.5f, 0.85f);
                break;
            }
            case "deposit":
            {
                var (x, y) = EditorModel.DepositSpot(hx, hy);
                var free = Enumerable.Range(0, EditorModel.DepositSize * EditorModel.DepositSize)
                    .All(i => c.Terrain(x + i % EditorModel.DepositSize + 0.5, y + i / EditorModel.DepositSize + 0.5, out var info) && info.Terrain == 2 && !info.Foot)
                    && model.DepositsIn(c, x - 1, y - 1, x + EditorModel.DepositSize + 1, y + EditorModel.DepositSize + 1).All(spot => Math.Abs(spot.X - x) >= EditorModel.DepositSize || Math.Abs(spot.Y - y) >= EditorModel.DepositSize);
                draw.Rect(x, y, x + EditorModel.DepositSize, y + EditorModel.DepositSize, free ? Allowed : Forbidden, 1.5f, 0.85f);
                break;
            }
            case "paint" or "paving":
                Frame(draw, hx, hy, model.Size);
                break;
            default:
                if (model.ModBrush is { } brush)
                    Frame(draw, hx, hy, brush.Size ? model.Size : 1);
                break;
        }
    }

    /// <summary>Рамка кисти размера size с серединой на тайле.</summary>
    private static void Frame(Draw draw, int x, int y, int size)
    {
        var from = size / 2;
        draw.Rect(x - from, y - from, x - from + size, y - from + size, Box, 1, 0.8f);
    }

    /// <summary>Сторона призрака: свой, ничей или чужой — по игроку панели.</summary>
    private int Team(ClientContext c) => model.Player == c.Player ? 0 : model.Player == 0 ? 3 : 1;
}
