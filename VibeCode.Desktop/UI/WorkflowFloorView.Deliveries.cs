using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

// The floor's deliveries. When every seat on the lounge couches is taken, two movers carry another couch in through the
// front door and set it down somewhere free; when a desk earns its next look, a technician walks in with a hammer, works
// the upgrade into the desk in a cloud of dust and carries the old computer out. With motion off the couch and the new
// screens simply appear.
public sealed partial class WorkflowFloorView
{
    /// <summary>Couches on the floor at most, the lounge's own included. A new one also needs a free spot that leaves
    /// every station reachable, so a cramped floor stops sooner.</summary>
    private const int MaxCouches = 4;
    private const int MaxTechnicians = 3;
    private const double CouchW = 44, MoverGap = 18, CouchLands = 0.45, PlaceSeconds = 1.3, InstallSeconds = 3, SparkleSeconds = 1.4;
    /// <summary>Seconds per hammer blow: raised for the first half, landing on the desk at the halfway point.</summary>
    private const double Swing = 0.5;
    private static readonly Color MoverShirt = PixelArt.C("#D9822B"), TechShirt = PixelArt.C("#2AA198"), Cap = PixelArt.C("#24303F"),
        OldBox = PixelArt.C("#B9AF95"), Dust = PixelArt.C("#E8E4D8");

    private enum Errand { Couch, Desk }
    private enum Leg { In, Work, Out }

    private sealed class Delivery
    {
        public required Errand Errand;
        /// <summary>The couch's corner, or where the technician stands to work.</summary>
        public required Point Spot;
        public string DeskId = "";
        public int Tier;
        public List<Point> Path = new();
        public double Length, Travelled, Since;
        public Leg Leg = Leg.In;
        public bool Done;
    }

    /// <summary>Couches the floor has grown to, the lounge's own included. Kept across layouts; each layout places as many
    /// of them as it has room for.</summary>
    private int _couchCount = 1;
    private readonly List<Point> _couches = new();
    private readonly List<Delivery> _deliveries = new();
    /// <summary>The look each desk shows, and whether the town had counted its chat's tokens when it was shown. An upgrade
    /// shows once its technician has installed it, so this trails <see cref="DeskTierOf"/> while one is on the way.</summary>
    private readonly Dictionary<string, (int Tier, bool Measured)> _shownTiers = new(StringComparer.Ordinal);
    /// <summary>The layout that last had no room for another couch, so a full lounge does not search every frame.</summary>
    private string _noCouchRoom = "";

    /// <summary>Couches on the floor, the lounge's own included. Exposed for verification.</summary>
    public int CouchCount => _couches.Count;
    /// <summary>Movers and technicians on the floor: their errand ("couch" or "desk"), its leg ("in", "work" or "out") and
    /// where the leading one's feet are, in this element's DIPs. Exposed for verification.</summary>
    public IReadOnlyList<(string Errand, string Leg, Point Foot)> Deliveries =>
        _deliveries.Select(d => (d.Errand.ToString().ToLowerInvariant(), d.Leg.ToString().ToLowerInvariant(), Dip(Along(d.Path, d.Travelled).At))).ToArray();
    /// <summary>The look a desk shows, which trails the one its chat has earned while a technician is on the way.
    /// Exposed for verification.</summary>
    public int? ShownDeskTier(string agentId) => _shownTiers.TryGetValue(agentId, out var shown) ? shown.Tier : null;

    // ---- couches ----------------------------------------------------------------------------------------------------

    /// <summary>The lounge's couch, then as many delivered ones as this layout has room for.</summary>
    private void PlaceCouches()
    {
        _couches.Clear();
        _couches.Add(_couch);
        while (_couches.Count < _couchCount && FindCouchSpot() is { } spot) AddCouch(spot);
    }

    private void AddCouch(Point at)
    {
        var index = _couches.Count;
        _couches.Add(at);
        Block(new Rect(at.X, at.Y, CouchW, 15));
        for (var i = 0; i < 3; i++)
            Add(Zone.Couch, new Spot($"couch:{index * 3 + i}", Zone.Couch, new Point(at.X + 9 + i * 13, at.Y + 18), Facing.Down, Seated: true));
    }

    private void InstallCouch(Point at)
    {
        _couchCount = Math.Max(_couchCount, _couches.Count + 1);
        AddCouch(at);
        _static = BuildStatic();
        BuildProps();
    }

    /// <summary>Every seat on the couches is taken: send for another, unless one is on its way or there is no room.</summary>
    private void OrderCouchIfFull(int wanting, double time, bool moving)
    {
        if (wanting < _spots[Zone.Couch].Count || _couches.Count >= MaxCouches || _noCouchRoom == _layoutKey + "|" + _couches.Count
            || _deliveries.Any(d => d.Errand == Errand.Couch)) return;
        if (FindCouchSpot() is not { } spot)
        {
            _noCouchRoom = _layoutKey + "|" + _couches.Count;
            return;
        }
        if (!moving) { InstallCouch(spot); return; }
        // In through the front door to the middle of the couch's front, where its sitters' feet will go.
        var path = PathFrom(_entrance.Foot, new Point(spot.X + CouchW / 2, spot.Y + 26));
        _deliveries.Add(new Delivery { Errand = Errand.Couch, Spot = spot, Path = path, Length = Length(path), Since = time });
    }

    /// <summary>The free place nearest the lounge for another couch: inside one room, clear of the desk pods, every station
    /// and the furniture, with room for its sitters, and cutting no station off from the front door.</summary>
    private Point? FindCouchSpot()
    {
        var reserved = CouchKeepOut();
        Rect[] rooms = [_kitchen, _center, _meeting, _library, _server];
        var candidates = new List<(double Distance, Point At)>();
        for (var y = WallH + 16; y + 30 <= _fh - BottomWall - 4; y += Cell)
            for (var x = Side + 6.0; x + CouchW + 6 <= _fw - Side; x += Cell)
            {
                var footprint = new Rect(x - 4, y, CouchW + 8, 30);
                if (!rooms.Any(room => room.Contains(footprint)) || reserved.Any(area => area.IntersectsWith(footprint))
                    || !Clear(new Rect(x, y, CouchW, 24))) continue;
                candidates.Add(((x - _couch.X) * (x - _couch.X) + (y - _couch.Y) * (y - _couch.Y), new Point(x, y)));
            }
        foreach (var (_, at) in candidates.OrderBy(c => c.Distance))
            if (KeepsFloorConnected(new Rect(at.X, at.Y, CouchW, 15), Enumerable.Range(0, 3).Select(i => new Point(at.X + 9 + i * 13, at.Y + 18))))
                return at;
        return null;
    }

    /// <summary>What a new couch must leave alone: the pods with their aisles, the lobby, every station and the
    /// furniture beside it, the couches already down and the strip under the top wall.</summary>
    private List<Rect> CouchKeepOut()
    {
        var bottom = _fh - BottomWall;
        var door = _fw / 2;
        var keepOut = new List<Rect>
        {
            new(0, 0, _fw, WallH + 16),
            new(door - 40, bottom - 28, 80, 28),
            new(_table.X - 12, _table.Y - 14, 72, 50),
            new(_counter.X - 2, _counter.Y - 2, 42, 30),
            new(_cooler.X - 3, _cooler.Y, 15, 38),
            new(_kiosk.X - 3, _kiosk.Y, 32, 30),
            new(_printer.X - 3, _printer.Y - 3, 24, 26),
            new(_armchair.X - 3, _armchair.Y - 6, 30, 26),
            new(_kitchen.Right - 20, bottom - 26, 20, 26),
        };
        foreach (var pod in _pods) keepOut.Add(Inflate(pod.Area, 8, 8));
        foreach (var couch in _couches) keepOut.Add(new Rect(couch.X - 6, couch.Y - 4, CouchW + 12, 36));
        foreach (var delivery in _deliveries.Where(d => d.Errand == Errand.Couch)) keepOut.Add(new Rect(delivery.Spot.X - 6, delivery.Spot.Y - 4, CouchW + 12, 36));
        foreach (var t in _terminals) keepOut.Add(new Rect(t.X - 2, t.Y - 2, 24, 26));
        foreach (var cabinet in _cabinets) keepOut.Add(new Rect(cabinet.X - 2, cabinet.Y, 14, 26));
        foreach (var plant in Plants()) keepOut.Add(new Rect(plant.X - 4, plant.Y - 12, 16, 22));
        return keepOut;
    }

    /// <summary>Whether no furniture stands on the floor cells in <paramref name="area"/>.</summary>
    private bool Clear(Rect area)
    {
        var cols = _blocked.GetLength(0);
        var rows = _blocked.GetLength(1);
        for (var cx = Math.Max(0, (int)(area.Left / Cell)); cx < cols && cx * Cell <= area.Right; cx++)
            for (var cy = Math.Max(0, (int)(area.Top / Cell)); cy < rows && cy * Cell <= area.Bottom; cy++)
                if (_blocked[cx, cy] && area.Contains(new Point((cx + 0.5) * Cell, (cy + 0.5) * Cell))) return false;
        return true;
    }

    /// <summary>Whether, with <paramref name="couch"/> down, every station and seat is still reachable from the door.</summary>
    private bool KeepsFloorConnected(Rect couch, IEnumerable<Point> seats)
    {
        var saved = _blocked;
        _blocked = (bool[,])saved.Clone();
        try
        {
            Block(couch);
            var cols = _blocked.GetLength(0);
            var rows = _blocked.GetLength(1);
            var start = Open(CellOf(_entrance.Foot));
            if (start < 0) return false;
            var reached = new bool[cols * rows];
            var queue = new Queue<int>();
            reached[start] = true;
            queue.Enqueue(start);
            while (queue.TryDequeue(out var cell))
            {
                var (x, y) = (cell % cols, cell / cols);
                foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                {
                    if (nx < 0 || ny < 0 || nx >= cols || ny >= rows || _blocked[nx, ny] || reached[ny * cols + nx]) continue;
                    reached[ny * cols + nx] = true;
                    queue.Enqueue(ny * cols + nx);
                }
            }
            return _spots.Values.SelectMany(spots => spots).Select(s => s.Foot).Concat(_seats.Values.Select(s => s.Foot))
                .Concat(_visits.Values.Select(s => s.Foot)).Concat(seats)
                .All(foot => Open(CellOf(foot)) is var cell && cell >= 0 && reached[cell]);
        }
        finally { _blocked = saved; }
    }

    // ---- desk upgrades ----------------------------------------------------------------------------------------------

    /// <summary>The look a desk shows: what its chat has earned, unless an upgrade is still on its way.</summary>
    private int ShownTier(string agentId, AgentActivityNode? agent) =>
        _shownTiers.TryGetValue(agentId, out var shown) ? shown.Tier : DeskTierOf(agent);

    /// <summary>Bring every desk's look up to date. An upgrade the floor sees happen - the town counted this chat before and
    /// counts more now - waits for a technician to carry the new screens in. The first count, town improvements being
    /// switched, a downgrade and a still floor all show at once. Returns whether a desk changed, so the caller rebakes.</summary>
    private bool UpdateDeskTiers(double time, bool moving)
    {
        var changed = false;
        foreach (var (id, desk) in _desks)
        {
            if (!_byId.TryGetValue(id, out var agent)) continue;
            var earned = DeskTierOf(agent);
            var measured = WorkflowTown.Enabled && WorkflowTown.Instance.Measured(agent.Chat.SessionId);
            if (!_shownTiers.TryGetValue(id, out var shown))
            {
                _shownTiers[id] = (earned, measured);
                continue;
            }
            var install = _deliveries.FirstOrDefault(d => d.Errand == Errand.Desk && d.DeskId == id && d.Leg != Leg.Out);
            if (earned == shown.Tier)
            {
                if (measured != shown.Measured) _shownTiers[id] = (earned, measured);
                if (install is not null) SendHome(install, time);
                continue;
            }
            if (earned > shown.Tier && shown.Measured && measured && moving)
            {
                // A further upgrade while the first is still on its way rides along with it.
                if (install is not null) install.Tier = Math.Max(install.Tier, earned);
                else if (_deliveries.Count(d => d.Errand == Errand.Desk && d.Leg != Leg.Out) < MaxTechnicians) StartInstall(id, desk, earned, time);
                continue;
            }
            _shownTiers[id] = (earned, measured);
            if (install is not null) SendHome(install, time);
            changed = true;
        }
        return changed;
    }

    private void StartInstall(string deskId, Desk desk, int tier, double time)
    {
        // Beside the desk, by its screen, clear of the chair.
        var spot = new Point(desk.X + 32, desk.Y + 31);
        var path = PathFrom(_entrance.Foot, spot);
        _deliveries.Add(new Delivery { Errand = Errand.Desk, Spot = spot, DeskId = deskId, Tier = tier, Path = path, Length = Length(path), Since = time });
    }

    // ---- the errands ------------------------------------------------------------------------------------------------

    private void AdvanceDeliveries(double time, double dt, bool moving)
    {
        if (!moving) { FinishDeliveries(relayout: false); return; }
        for (var i = _deliveries.Count - 1; i >= 0; i--)
        {
            var delivery = _deliveries[i];
            switch (delivery.Leg)
            {
                case Leg.In:
                    delivery.Travelled = Math.Min(delivery.Length, delivery.Travelled + WalkSpeed * dt);
                    if (delivery.Travelled >= delivery.Length) { delivery.Leg = Leg.Work; delivery.Since = time; }
                    break;
                case Leg.Work when delivery.Errand == Errand.Couch:
                    if (!delivery.Done && time - delivery.Since >= CouchLands) { delivery.Done = true; InstallCouch(delivery.Spot); }
                    if (time - delivery.Since >= PlaceSeconds) SendHome(delivery, time);
                    break;
                case Leg.Work:
                    if (time - delivery.Since < InstallSeconds) break;
                    delivery.Done = true;
                    _shownTiers[delivery.DeskId] = (delivery.Tier, true);
                    BuildProps();
                    SendHome(delivery, time);
                    break;
                case Leg.Out:
                    delivery.Travelled = Math.Min(delivery.Length, delivery.Travelled + WalkSpeed * dt);
                    if (delivery.Travelled >= delivery.Length) _deliveries.RemoveAt(i);
                    break;
            }
        }
    }

    /// <summary>Back out through the front door from wherever the errand stands.</summary>
    private void SendHome(Delivery delivery, double time)
    {
        var from = Along(delivery.Path, delivery.Travelled).At;
        delivery.Path = PathFrom(from, _entrance.Foot);
        delivery.Length = Length(delivery.Path);
        delivery.Travelled = 0;
        delivery.Leg = Leg.Out;
        delivery.Since = time;
    }

    /// <summary>Motion stopped or the floor was laid out again: what was on its way is simply there.</summary>
    private void FinishDeliveries(bool relayout)
    {
        if (_deliveries.Count == 0) return;
        var rebake = false;
        foreach (var delivery in _deliveries.Where(d => d.Leg != Leg.Out && !d.Done))
        {
            if (delivery.Errand == Errand.Desk) { _shownTiers[delivery.DeskId] = (delivery.Tier, true); rebake = true; }
            else if (relayout) _couchCount = Math.Max(_couchCount, _couches.Count + 1);
            else InstallCouch(delivery.Spot);
        }
        _deliveries.Clear();
        if (rebake && !relayout) BuildProps();
    }

    // ---- drawing ----------------------------------------------------------------------------------------------------

    /// <summary>Each errand into the depth-sorted scene: movers with the couch between them, a technician with its hammer.</summary>
    private void StageDeliveries(DrawingContext dc, List<(double Base, int Order, Action Draw)> scene, double time)
    {
        foreach (var delivery in _deliveries)
        {
            var d = delivery;
            var (lead, facing) = Along(d.Path, d.Travelled);
            if (d.Errand == Errand.Desk)
            {
                scene.Add((lead.Y, 1, () => DrawTechnician(dc, d, lead, facing, time)));
                continue;
            }
            var behind = Along(d.Path, Math.Max(0, d.Travelled - MoverGap));
            scene.Add((Math.Max(lead.Y, behind.At.Y), 1, () => DrawMovers(dc, d, lead, facing, behind.At, behind.Facing, time)));
            // The couch being set down, before it is part of the furniture.
            if (d.Leg == Leg.Work && !d.Done)
            {
                var drop = Math.Max(0, 1 - (time - d.Since) / CouchLands) * 5;
                scene.Add((d.Spot.Y + 15, 0, () => CouchArt(dc, new Point(d.Spot.X, d.Spot.Y - drop))));
            }
        }
    }

    private void DrawMovers(DrawingContext dc, Delivery delivery, Point lead, Facing leadFacing, Point back, Facing backFacing, double time)
    {
        var working = delivery.Leg == Leg.Work;
        // Setting the couch down, both face it; otherwise they walk the aisle one behind the other.
        Helper(dc, "mover:1:" + delivery.Spot, back, working ? Facing.Up : backFacing, Math.Max(0, delivery.Travelled - MoverGap), working, MoverShirt);
        Helper(dc, "mover:0:" + delivery.Spot, lead, working ? Facing.Up : leadFacing, delivery.Travelled, working, MoverShirt);
        if (delivery.Leg != Leg.In) return;
        // The couch carried between them at hand height, its seat cushions up.
        var a = Dip(new Point(back.X, back.Y - 8));
        var b = Dip(new Point(lead.X, lead.Y - 8));
        if ((b - a).Length < _p) b = new Point(a.X + 6 * _p, a.Y);
        dc.DrawLine(new Pen(Fill(Outline), 9 * _p), a, b);
        dc.DrawLine(new Pen(Fill(CouchDark), 7 * _p), a, b);
        dc.DrawLine(new Pen(Fill(Couch), 4 * _p), new Point(a.X, a.Y - _p), new Point(b.X, b.Y - _p));
        dc.DrawLine(new Pen(Fill(CouchHi), 1 * _p), new Point(a.X, a.Y - 3 * _p), new Point(b.X, b.Y - 3 * _p));
    }

    private void DrawTechnician(DrawingContext dc, Delivery delivery, Point foot, Facing facing, double time)
    {
        var working = delivery.Leg == Leg.Work;
        Helper(dc, "tech:" + delivery.DeskId, foot, working ? Facing.Up : facing, delivery.Travelled, working, TechShirt);
        if (working)
        {
            // Raised over its shoulder, then down on the corner of the desk (see HammerStrike), blow after blow.
            if (Landed(time - delivery.Since))
                Hammer(dc, new Rect(foot.X - 5, foot.Y - 8.5, 6, 1), new Rect(foot.X - 6.5, foot.Y - 10.5, 1.5, 3.5));
            else
                Hammer(dc, new Rect(foot.X + 3.5, foot.Y - 15, 1, 6), new Rect(foot.X + 2, foot.Y - 16.5, 4, 1.5));
            return;
        }
        if (delivery.Leg == Leg.Out && delivery.Done)
        {
            // The old computer, carried out in both arms.
            var box = new Rect(foot.X - 4, foot.Y - 11, 8, 6);
            Box(dc, Outline, box.X - 0.5, box.Y - 0.5, box.Width + 1, box.Height + 1);
            Box(dc, OldBox, box.X, box.Y, box.Width, box.Height);
            Box(dc, PixelArt.C("#3A3E46"), box.X + 1.5, box.Y + 1, 5, 3);
            return;
        }
        // Walking in, the hammer in its hand, head up.
        var hand = new Point(facing == Facing.Left ? foot.X - 5 : foot.X + 5, foot.Y - 7);
        Hammer(dc, new Rect(hand.X - 0.5, hand.Y - 3, 1, 4), new Rect(hand.X - 1.5, hand.Y - 4.5, 3, 1.5));
    }

    /// <summary>A small hammer: a wooden handle and a steel head, each outlined so it reads against the floor.</summary>
    private void Hammer(DrawingContext dc, Rect handle, Rect head)
    {
        Box(dc, Outline, handle.X - 0.5, handle.Y - 0.5, handle.Width + 1, handle.Height + 1);
        Box(dc, Wood, handle.X, handle.Y, handle.Width, handle.Height);
        Box(dc, Outline, head.X - 0.5, head.Y - 0.5, head.Width + 1, head.Height + 1);
        Box(dc, Metal, head.X, head.Y, head.Width, head.Height);
        Box(dc, MetalHi, head.X, head.Y, head.Width, 0.5);
    }

    /// <summary>Whether the hammer is down on the desk this far into the install: the second half of every swing.</summary>
    private static bool Landed(double age) => age % Swing >= Swing / 2;

    /// <summary>Where a technician working from <paramref name="foot"/> brings its hammer down: the desk's near corner.</summary>
    private static Point HammerStrike(Point foot) => new(foot.X - 5.75, foot.Y - 7);

    /// <summary>A mover or technician: a worker in its company shirt and a cap, walking or busy with its hands.</summary>
    private void Helper(DrawingContext dc, string id, Point foot, Facing facing, double stride, bool working, Color shirt)
    {
        var (art, flip) = working ? ((int)Math.Floor(_time * 5 + Hash(id) * 4) % 2 == 0 ? "term1" : "term2", false) : WalkPose(facing, stride);
        dc.DrawEllipse(Fill(Color.FromArgb(70, 0, 0, 0)), null, Dip(new Point(foot.X, foot.Y - 0.5)), 4.5 * _p, 1.4 * _p);
        Sprite(dc, id, art, flip, foot, 0, _p, shirt);
        Box(dc, Cap, foot.X - 3, foot.Y - 15, 6, 2);
        if (facing == Facing.Down && !working) Box(dc, Cap, foot.X - 4, foot.Y - 13, 8, 1);
    }

    /// <summary>Over everything else: the dust of a technician's hammering and the install's progress on the desk's
    /// screen, the sparkle when the new screens come on, and dust where a couch has just landed.</summary>
    private void DrawDeliveryEffects(DrawingContext dc, double time)
    {
        foreach (var delivery in _deliveries)
        {
            var age = time - delivery.Since;
            if (delivery.Errand == Errand.Desk && _desks.TryGetValue(delivery.DeskId, out var desk))
            {
                var screen = ScreenOf(desk, ShownTier(desk.AgentId, _byId.GetValueOrDefault(desk.AgentId)));
                if (delivery.Leg == Leg.Work)
                {
                    HammerDust(dc, HammerStrike(delivery.Spot), age);
                    var bar = new Rect(screen.Left + 1, screen.Bottom - 3, screen.Width - 2, 1.5);
                    Box(dc, PixelArt.C("#0A0C10"), bar.X - 0.5, bar.Y - 0.5, bar.Width + 1, bar.Height + 1);
                    Box(dc, PixelArt.C("#5FD38D"), bar.X, bar.Y, Math.Max(0.5, bar.Width * Math.Clamp(age / InstallSeconds, 0, 1)), bar.Height);
                }
                else if (delivery.Leg == Leg.Out && delivery.Done && age < SparkleSeconds)
                    Sparkles(dc, Dip(new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2)), age, _p * 0.8);
            }
            else if (delivery.Errand == Errand.Couch && delivery.Leg == Leg.Work && age >= CouchLands)
            {
                var puff = Math.Clamp((age - CouchLands) / (PlaceSeconds - CouchLands), 0, 1);
                dc.PushOpacity(0.55 * (1 - puff));
                foreach (var x in new[] { delivery.Spot.X - 2 - puff * 4, delivery.Spot.X + CouchW + 2 + puff * 4 })
                    dc.DrawEllipse(Fill(Dust), null, Dip(new Point(x, delivery.Spot.Y + 14 - puff * 3)), (2 + puff * 3) * _p, (1.2 + puff * 1.5) * _p);
                dc.Pop();
            }
        }
    }

    /// <summary>Dust off the desk while it is worked on: puffs where the hammer lands each time, and specks drifting off
    /// and fading the whole time. All of it rises from the desk's side, clear of the technician.</summary>
    private void HammerDust(DrawingContext dc, Point strike, double age)
    {
        if (Landed(age))
        {
            var t = (age % Swing - Swing / 2) / (Swing / 2);   // 0 as the head lands, 1 as it comes up again
            dc.PushOpacity(0.8 * (1 - t));
            dc.DrawEllipse(Fill(Dust), null, Dip(new Point(strike.X - 1.5 - t * 4, strike.Y + 0.5 - t * 1.5)), (1.6 + t * 2.4) * _p, (1.1 + t * 1.4) * _p);
            dc.DrawEllipse(Fill(Dust), null, Dip(new Point(strike.X - 0.5 - t * 1.5, strike.Y - 1.5 - t * 3)), (1.2 + t * 2) * _p, (1 + t * 1.2) * _p);
            dc.Pop();
        }
        // Each speck leaves the corner across the desk, rising a little as it fades.
        for (var i = 0; i < 8; i++)
        {
            var cycle = age * 0.9 + i / 8.0;
            var life = cycle % 1;
            var drift = 2 - SceneWeatherFx.Noise(i * 31 + (int)cycle) * 14;
            dc.PushOpacity(0.7 * (1 - life));
            Box(dc, Dust, strike.X + drift * life - 0.5, strike.Y + 1 - life * 6, 1.1, 1.1);
            dc.Pop();
        }
    }

    // ---- paths ------------------------------------------------------------------------------------------------------

    /// <summary>A walking route from <paramref name="from"/> to <paramref name="to"/>, starting where it stands.</summary>
    private List<Point> PathFrom(Point from, Point to)
    {
        var path = Waypoints(from, to);
        path.Insert(0, from);
        return path;
    }

    private static double Length(IReadOnlyList<Point> path)
    {
        var total = 0.0;
        for (var i = 1; i < path.Count; i++) total += (path[i] - path[i - 1]).Length;
        return total;
    }

    /// <summary>The point <paramref name="distance"/> along a path, and the way someone walking it faces there.</summary>
    private static (Point At, Facing Facing) Along(IReadOnlyList<Point> path, double distance)
    {
        if (path.Count == 0) return (default, Facing.Up);
        var facing = Facing.Up;
        for (var i = 1; i < path.Count; i++)
        {
            var step = path[i] - path[i - 1];
            var length = step.Length;
            if (length < 0.01) continue;
            facing = Math.Abs(step.X) >= Math.Abs(step.Y) ? step.X < 0 ? Facing.Left : Facing.Right : step.Y < 0 ? Facing.Up : Facing.Down;
            if (distance <= length) return (path[i - 1] + step * (distance / length), facing);
            distance -= length;
        }
        return (path[^1], facing);
    }

    /// <summary>The walking frame for someone facing <paramref name="facing"/> who has covered <paramref name="stride"/>.</summary>
    private static (string Art, bool Flip) WalkPose(Facing facing, double stride)
    {
        var second = (int)(stride / 3.5) % 2 == 1;
        return facing switch
        {
            Facing.Up => (second ? "back-walk2" : "back-walk1", false),
            Facing.Down => (second ? "front-walk2" : "front-walk1", false),
            var side => (second ? "side" : "side-walk", side == Facing.Right),
        };
    }
}
