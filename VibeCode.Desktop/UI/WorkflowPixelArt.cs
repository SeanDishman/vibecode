using System.Windows;
using System.Windows.Media;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// Pixel sprites for the tavern and office views, drawn from small text templates. Each colour of a sprite is baked
/// into one frozen geometry (runs of same-coloured pixels merged), so a whole villager is five or six fills however
/// many pixels it has, and the result is cached per template, palette and scale.
/// </summary>
internal static class PixelArt
{
    private static readonly Dictionary<string, Drawing> Cache = new(StringComparer.Ordinal);
    // Looked up every frame for every villager, so the key is a value tuple: a cached sprite costs no allocation.
    private static readonly Dictionary<(string Id, Pose Pose, bool Flip, int Shade, Color? Shirt, Prop Prop, double Scale), Drawing> Characters = new();

    public enum Pose { Stand, Walk, Wave, Sleep, Mug, Sit, Cheer, Lute, Strum, LuteRest }

    /// <summary>What a character carries for its role: a Bridge worker's tool belt or the central planner's scroll.</summary>
    public enum Prop { None, Belt, Scroll }

    // One art pixel per character. h hair, s skin, e eye, c shirt, d shirt shade, p trousers, b boots, m mug,
    // l leather, y brass, t tool steel, w paper, r ribbon, u lute body, k dark wood.
    private static readonly string[] Stand =
    [
        "...hhh...", "..hhhhh..", "..hsssh..", "..seses..", "..sssss..", "...sss...", "..cdcdc..",
        ".ccccccc.", ".scccccs.", ".scccccs.", "..ppppp..", "..pp.pp..", "..pp.pp..", "..bb.bb..",
    ];
    private static readonly string[] Walk =
    [
        "...hhh...", "..hhhhh..", "..hsssh..", "..seses..", "..sssss..", "...sss...", "..cdcdc..",
        ".ccccccc.", "s.ccccc.s", "..ccccc..", "..ppppp..", ".pp...pp.", ".pp...pp.", "bb.....bb",
    ];
    private static readonly string[] Wave =
    [
        "...hhh..s", "..hhhhh.s", "..hsssh.s", "..seses.c", "..sssss.c", "...sss.c.", "..cdcdcc.",
        ".ccccccc.", ".sccccc..", ".sccccc..", "..ppppp..", "..pp.pp..", "..pp.pp..", "..bb.bb..",
    ];
    private static readonly string[] Sleep =
    [
        ".........", "...hhh...", "..hhhhh..", "..hsssh..", "..sssss..", "...sss...", "..cdcdc..",
        ".ccccccc.", ".scccccs.", ".scccccs.", "..ppppp..", "..pp.pp..", "..pp.pp..", "..bb.bb..",
    ];
    private static readonly string[] Mug =
    [
        "...hhh...", "..hhhhh..", "..hsssh..", "..seses..", "..sssss..", "...sss...", "..cdcdc..",
        ".ccccccc.", ".scccccmm", ".scccccmm", "..ppppp..", "..pp.pp..", "..pp.pp..", "..bb.bb..",
    ];
    // Seated at a desk: the desk hides the legs, the arms reach forward to the keyboard.
    private static readonly string[] Sit =
    [
        "...hhh...", "..hhhhh..", "..hsssh..", "..seses..", "..sssss..", "...sss...", "..cdcdc..",
        ".ccccccc.", "sccccccss", ".ccccccc.", "..ppppp..", ".........", ".........", ".........",
    ];
    // A finish: the mug raised high, the same arm as the wave.
    private static readonly string[] Cheer =
    [
        "...hhh.mm", "..hhhhhmm", "..hsssh.s", "..seses.c", "..sssss.c", "...sss.c.", "..cdcdcc.",
        ".ccccccc.", ".sccccc..", ".sccccc..", "..ppppp..", "..pp.pp..", "..pp.pp..", "..bb.bb..",
    ];
    // The tavern bard: a lute across the body, neck to the left, the right hand strumming low then high.
    private static readonly string[] Lute =
    [
        "...hhh...", "..hhhhh..", "..hsssh..", "..seses..", "..sssss..", "...sss...", "..cdcdc..",
        ".ccccccc.", "kkkuuucc.", ".s.ukuus.", "..puuup..", "..pp.pp..", "..pp.pp..", "..bb.bb..",
    ];
    private static readonly string[] Strum =
    [
        "...hhh...", "..hhhhh..", "..hsssh..", "..seses..", "..sssss..", "...sss...", "..cdcdc..",
        ".ccccccc.", "kkkuuucs.", ".s.ukuu..", "..puuup..", "..pp.pp..", "..pp.pp..", "..bb.bb..",
    ];
    // Music paused: the lute stands at his side, neck in hand.
    private static readonly string[] LuteRest =
    [
        "...hhh...", "..hhhhh..", "..hsssh..", "..seses..", "..sssss..", "...sss...", "..cdcdc.k",
        ".ccccccck", ".scccccsk", ".scccccsk", "..pppppuu", "..pp.ppuu", "..pp.ppuu", "..bb.bb..",
    ];

    public const int Width = 9, Height = 14;

    private static readonly Color[] Hair = [C("#2B1D14"), C("#1B1B1F"), C("#E0B954"), C("#A5482A"), C("#9AA0A6"), C("#5B3A29"), C("#3E5C9A")];
    private static readonly Color[] Skin = [C("#F5D2B3"), C("#E8B48F"), C("#C68B5C"), C("#8D5A3B"), C("#F1C7A0")];
    private static readonly Color[] Shirt = [C("#4F8FE8"), C("#E8574F"), C("#5DBB63"), C("#B05FD6"), C("#F0A030"), C("#2FB5B0"),
        C("#E86FA4"), C("#7C8CF0"), C("#C9A227"), C("#6FA86B")];
    private static readonly Color[] Trousers = [C("#2C3550"), C("#3B2E25"), C("#2E3B2C"), C("#463A5C")];

    /// <summary>A character's colours, stable for the same agent id. <paramref name="shirt"/> replaces the hashed
    /// shirt - a provider's guild tabard - while hair, skin and trousers stay the agent's own.</summary>
    public static Dictionary<char, Color> Palette(string id, Color? shirt = null)
    {
        var h = Pick(Hair, id, 1); var s = Pick(Skin, id, 2); var c = shirt ?? Pick(Shirt, id, 3); var p = Pick(Trousers, id, 4);
        return new()
        {
            ['h'] = h, ['s'] = s, ['e'] = C("#1A1420"), ['c'] = c, ['d'] = Shade(c, 0.78), ['p'] = p,
            ['b'] = C("#2A1D16"), ['m'] = C("#E2A33B"), ['l'] = C("#5C3A1E"), ['y'] = C("#E0B954"), ['t'] = C("#AEB6C0"),
            ['w'] = C("#F3E9D0"), ['r'] = C("#C8402E"), ['u'] = C("#C07A32"), ['k'] = C("#4A2E18"),
        };
    }

    public static Color ShirtOf(string id) => Pick(Shirt, id, 3);

    /// <summary>The sprite for <paramref name="pose"/> in <paramref name="id"/>'s colours, one art pixel per
    /// <paramref name="scale"/> DIPs. Mirrored when <paramref name="flip"/>; <paramref name="night"/> (0..1) shades
    /// the colours toward the dark the way the scenery around it is shaded. <paramref name="shirt"/> and
    /// <paramref name="prop"/> dress it for its provider and role.</summary>
    public static Drawing Character(string id, Pose pose, double scale, bool flip = false, double night = 0,
        Color? shirt = null, Prop prop = Prop.None)
    {
        // Ten steps of shade are indistinguishable from continuous and keep the sprite cache small.
        var shade = (int)Math.Round(Math.Clamp(night, 0, 1) * 10);
        var key = (id, pose, flip, shade, shirt, prop, scale);
        if (Characters.TryGetValue(key, out var cached)) return cached;
        if (Characters.Count > 2000) Characters.Clear();
        var rows = Dress(pose switch
        {
            Pose.Walk => Walk, Pose.Wave => Wave, Pose.Sleep => Sleep, Pose.Mug => Mug, Pose.Sit => Sit, Pose.Cheer => Cheer,
            Pose.Lute => Lute, Pose.Strum => Strum, Pose.LuteRest => LuteRest, _ => Stand,
        }, prop);
        if (flip) rows = rows.Select(r => new string(r.Reverse().ToArray())).ToArray();
        var palette = Palette(id, shirt);
        if (shade > 0)
            foreach (var symbol in palette.Keys.ToArray()) palette[symbol] = Night(palette[symbol], shade / 10.0);
        return Characters[key] = Sprite("char:" + id + ":" + pose + (flip ? ":f" : "") + ":" + shade + ":" + shirt + ":" + prop, rows, palette, scale);
    }

    /// <summary>A template with its role prop: a belt with a buckle and a spanner at the hip, or a rolled scroll held
    /// at whichever side has a free hand.</summary>
    private static string[] Dress(string[] rows, Prop prop)
    {
        if (prop == Prop.None) return rows;
        var art = rows.Select(r => r.ToCharArray()).ToArray();
        if (prop == Prop.Belt && new string(art[10]) == "..ppppp..")
        {
            art[10] = "..llyll..".ToCharArray();
            if (art[11][7] == '.') art[11][7] = art[12][7] = 't';
        }
        else if (prop == Prop.Scroll)
        {
            // Beside a hand at the right, at the left, or hanging under a hand swung out mid-stride.
            foreach (var (x, top, hand) in new[] { (8, 7, 7), (0, 7, 1), (8, 9, 8) })
            {
                var held = art[8][hand] == 's' || hand != 8 && art[9][hand] == 's';
                if (!held || !Enumerable.Range(top, 4).All(y => art[y][x] == '.')) continue;
                art[top][x] = art[top + 1][x] = art[top + 3][x] = 'w';
                art[top + 2][x] = 'r';
                break;
            }
        }
        return art.Select(r => new string(r)).ToArray();
    }

    public static Color Night(Color c, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(c.A, (byte)(c.R + (10 - c.R) * t), (byte)(c.G + (14 - c.G) * t), (byte)(c.B + (30 - c.B) * t));
    }

    /// <summary>Build (once) a sprite: each char of <paramref name="rows"/> keys a colour of <paramref name="palette"/>;
    /// anything else is transparent.</summary>
    public static Drawing Sprite(string key, string[] rows, IReadOnlyDictionary<char, Color> palette, double scale)
    {
        var cacheKey = key + "|" + scale.ToString("0.###");
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;
        if (Cache.Count > 2000) Cache.Clear();
        var group = new DrawingGroup();
        foreach (var (symbol, color) in palette)
        {
            var geometry = new StreamGeometry();
            var any = false;
            using (var context = geometry.Open())
            {
                for (var y = 0; y < rows.Length; y++)
                {
                    var row = rows[y];
                    for (var x = 0; x < row.Length; x++)
                    {
                        if (row[x] != symbol) continue;
                        var run = 1;
                        while (x + run < row.Length && row[x + run] == symbol) run++;
                        context.BeginFigure(new Point(x * scale, y * scale), true, true);
                        context.PolyLineTo([new Point((x + run) * scale, y * scale), new Point((x + run) * scale, (y + 1) * scale),
                            new Point(x * scale, (y + 1) * scale)], false, false);
                        x += run - 1;
                        any = true;
                    }
                }
            }
            if (!any) continue;
            geometry.Freeze();
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            group.Children.Add(new GeometryDrawing(brush, null, geometry));
        }
        group.Freeze();
        return Cache[cacheKey] = group;
    }

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    public static Color Shade(Color c, double f) => Color.FromArgb(c.A, (byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    private static Color Pick(Color[] options, string id, int salt)
    {
        unchecked
        {
            var h = 17 + salt * 31;
            foreach (var ch in id) h = h * 31 + ch;
            return options[(int)((uint)h % options.Length)];
        }
    }
}

public enum SceneWeather { Clear, Cloudy, Rain, Storm, Snow, Fog }

/// <summary>Sky colours, darkness and sun/moon for an hour of the day, plus the weather a scene should show.</summary>
public readonly record struct SceneSky(double Hour, Color Top, Color Bottom, double Darkness, SceneWeather Weather, string Source)
{
    // Hour, sky top, sky horizon, darkness laid over the scenery (lights are drawn after it, so they glow).
    private static readonly (double Hour, Color Top, Color Bottom, double Dark)[] Keys =
    [
        (0, PixelArt.C("#0A0F24"), PixelArt.C("#1A2446"), 0.60),
        (4.6, PixelArt.C("#0A0F24"), PixelArt.C("#1A2446"), 0.60),
        (6.0, PixelArt.C("#3A3D78"), PixelArt.C("#F09A7A"), 0.30),
        (7.4, PixelArt.C("#5DA6E6"), PixelArt.C("#BFE2F6"), 0.0),
        (12, PixelArt.C("#4A9BE6"), PixelArt.C("#B5DDF6"), 0.0),
        (17.2, PixelArt.C("#5A9FDE"), PixelArt.C("#C9E4F2"), 0.03),
        (18.6, PixelArt.C("#5B4C8C"), PixelArt.C("#F2915E"), 0.22),
        (19.8, PixelArt.C("#262A5A"), PixelArt.C("#7A4F7A"), 0.45),
        (21, PixelArt.C("#0A0F24"), PixelArt.C("#1A2446"), 0.60),
        (24, PixelArt.C("#0A0F24"), PixelArt.C("#1A2446"), 0.60),
    ];

    public bool Night => Darkness > 0.36;
    public bool LampsOn => Darkness > 0.16 || Weather is SceneWeather.Storm;
    public bool SunUp => Hour is >= 6 and < 19.4;

    public string Label => (Hour is >= 4.6 and < 7.4 ? "Dawn" : Hour is >= 7.4 and < 17.2 ? "Day" : Hour is >= 17.2 and < 20.4 ? "Dusk" : "Night")
        + " · " + Weather switch
        {
            SceneWeather.Storm => "Thunderstorm", SceneWeather.Cloudy => "Cloudy", _ => Weather.ToString(),
        };

    /// <summary>The sky for <paramref name="sky"/> ("auto" follows the local clock) and <paramref name="weather"/>
    /// ("auto" follows the Weather extension when it has a reading, otherwise drifts slowly on its own).</summary>
    public static SceneSky Resolve(string sky, string weather, DateTime local)
    {
        var hour = sky switch
        {
            "dawn" => 6.6, "day" => 12.5, "dusk" => 19.1, "night" => 23.2,
            _ => local.TimeOfDay.TotalHours,
        };
        var (kind, source) = weather switch
        {
            "clear" => (SceneWeather.Clear, "chosen"), "cloudy" => (SceneWeather.Cloudy, "chosen"), "rain" => (SceneWeather.Rain, "chosen"),
            "storm" => (SceneWeather.Storm, "chosen"), "snow" => (SceneWeather.Snow, "chosen"), "fog" => (SceneWeather.Fog, "chosen"),
            _ => Auto(local),
        };
        var i = 0;
        while (i < Keys.Length - 2 && Keys[i + 1].Hour <= hour) i++;
        var (h0, top0, bottom0, dark0) = Keys[i];
        var (h1, top1, bottom1, dark1) = Keys[i + 1];
        var t = h1 <= h0 ? 0 : (hour - h0) / (h1 - h0);
        var top = Lerp(top0, top1, t);
        var bottom = Lerp(bottom0, bottom1, t);
        var dark = dark0 + (dark1 - dark0) * t;
        // Weather greys the sky and dims the street; a storm most of all.
        var (grey, extra) = kind switch
        {
            SceneWeather.Cloudy => (0.35, 0.05), SceneWeather.Rain => (0.55, 0.12), SceneWeather.Storm => (0.72, 0.22),
            SceneWeather.Snow => (0.45, 0.04), SceneWeather.Fog => (0.6, 0.06), _ => (0.0, 0.0),
        };
        if (grey > 0)
        {
            var overcast = Lerp(PixelArt.C("#6E7887"), PixelArt.C("#1C2230"), Math.Clamp(dark / 0.6, 0, 1));
            top = Lerp(top, PixelArt.Shade(overcast, 0.8), grey);
            bottom = Lerp(bottom, overcast, grey);
        }
        return new(hour, top, bottom, Math.Clamp(dark + extra, 0, 0.72), kind, source);
    }

    /// <summary>Live conditions from the Weather extension when it is on and has read something; otherwise a slow
    /// drift that changes every few minutes, so rain and storms still come round.</summary>
    private static (SceneWeather, string) Auto(DateTime local)
    {
        var service = WeatherService.Instance;
        if (!service.HasReading && !service.IsLoading)
        {
            _ = service.RefreshAsync();
        }

        if (service.HasReading && service.Conditions.Length > 0)
        {
            var c = service.Conditions.ToLowerInvariant();
            var live = c.Contains("thunder") || c.Contains("storm") ? SceneWeather.Storm
                : c.Contains("snow") || c.Contains("sleet") || c.Contains("flurr") || c.Contains("ice") ? SceneWeather.Snow
                : c.Contains("rain") || c.Contains("shower") || c.Contains("drizzle") ? SceneWeather.Rain
                : c.Contains("fog") || c.Contains("mist") || c.Contains("haze") || c.Contains("smoke") ? SceneWeather.Fog
                : c.Contains("cloud") || c.Contains("overcast") ? SceneWeather.Cloudy : SceneWeather.Clear;
            return (live, "live" + (service.Place.Length > 0 ? ", " + service.Place : ""));
        }
        var slot = (long)(local - DateTime.UnixEpoch).TotalMinutes / 7;
        var roll = (uint)(slot * 2654435761L) % 100;
        var winter = local.Month is 12 or 1 or 2;
        var drift = roll switch
        {
            < 34 => SceneWeather.Clear, < 52 => SceneWeather.Cloudy, < 74 => SceneWeather.Rain,
            < 88 => SceneWeather.Storm, < 94 => SceneWeather.Fog, _ => winter ? SceneWeather.Snow : SceneWeather.Rain,
        };
        return (drift, "drifting");
    }

    /// <summary>Where the sun (by day) or moon (by night) sits, as fractions of the sky's width and height.</summary>
    public (double X, double Y, bool Sun) Orb()
    {
        if (SunUp)
        {
            var t = (Hour - 6) / 13.4;
            return (0.08 + 0.84 * t, 0.72 - 0.58 * Math.Sin(t * Math.PI), true);
        }
        var n = (Hour >= 19.4 ? Hour - 19.4 : Hour + 4.6) / 10.6;
        return (0.1 + 0.8 * n, 0.6 - 0.45 * Math.Sin(n * Math.PI), false);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }
}

/// <summary>Rain, snow, lightning and fog over a scene. Positions are pure functions of time and a seed, so the
/// weather costs no state and freezes in place when motion is off.</summary>
internal static class SceneWeatherFx
{
    /// <param name="variant">Gives each of several windows its own drops; with one shared pattern, every window on a
    /// wall showed the same streaks in the same places.</param>
    public static void Draw(DrawingContext dc, Rect area, SceneSky sky, double time, double groundY, Func<Color, Brush> fill,
        Func<Color, double, Pen> pen, int variant = 0)
    {
        switch (sky.Weather)
        {
            case SceneWeather.Rain or SceneWeather.Storm:
                Rain(dc, area, sky.Weather == SceneWeather.Storm ? 260 : 170, time, groundY, sky.Night, pen, fill, variant);
                break;
            case SceneWeather.Snow:
                Snow(dc, area, time, fill, variant);
                break;
            case SceneWeather.Fog:
                for (var i = 0; i < 4; i++)
                {
                    var y = groundY - 30 - i * 46 + Math.Sin(time * 0.15 + i) * 6;
                    var drift = (time * (6 + i * 3)) % area.Width;
                    var band = new Rect(area.Left - area.Width + drift, y - 26, area.Width * 3, 52);
                    dc.DrawRectangle(fill(Color.FromArgb((byte)(46 - i * 6), 0xD8, 0xDE, 0xE8)), null, band);
                }
                break;
        }
    }

    private static void Rain(DrawingContext dc, Rect area, int count, double time, double groundY, bool night, Func<Color, double, Pen> pen,
        Func<Color, Brush> fill, int variant)
    {
        var streak = pen(night ? Color.FromArgb(120, 0x9F, 0xB8, 0xE0) : Color.FromArgb(140, 0xC9, 0xD8, 0xEE), 1.2);
        var height = area.Height + 40;
        for (var i = 0; i < count; i++)
        {
            var seed = unchecked(i * 7919 + variant * 1_000_003);
            var x0 = Noise(seed) * (area.Width + 120) - 60;
            var speed = 520 + Noise(seed + 1) * 260;
            var y = (Noise(seed + 2) * height + time * speed) % height - 20;
            var x = x0 + (y + 20) * 0.18;
            dc.DrawLine(streak, new Point(area.Left + x, area.Top + y), new Point(area.Left + x - 3.2, area.Top + y - 14));
            // A splash where a drop meets the street.
            if (y > groundY - area.Top && Noise(seed + 3) > 0.6)
            {
                var r = 1.5 + ((time * 3 + Noise(seed + 4)) % 1) * 3;
                dc.DrawEllipse(null, pen(Color.FromArgb(70, 0xC9, 0xD8, 0xEE), 0.8), new Point(area.Left + x, groundY + 6 + Noise(seed + 5) * 30), r * 1.8, r * 0.6);
            }
        }
    }

    private static void Snow(DrawingContext dc, Rect area, double time, Func<Color, Brush> fill, int variant)
    {
        var flake = fill(Color.FromArgb(220, 0xF4, 0xF7, 0xFF));
        var height = area.Height + 20;
        for (var i = 0; i < 180; i++)
        {
            var seed = unchecked(i * 104729 + variant * 1_000_003);
            var speed = 22 + Noise(seed + 1) * 30;
            var y = (Noise(seed + 2) * height + time * speed) % height - 10;
            var x = Noise(seed) * area.Width + Math.Sin(time * 0.8 + i + variant) * 10;
            var r = 1 + Noise(seed + 3) * 1.6;
            dc.DrawRectangle(flake, null, new Rect(area.Left + x, area.Top + y, r * 1.6, r * 1.6));
        }
    }

    /// <summary>The sky above a scene's horizon: gradient, stars by night, the sun or moon on its arc, drifting
    /// clouds that thicken with the weather, and birds on a fair day. Shared by the tavern and the office.</summary>
    public static void DrawSky(DrawingContext dc, double width, double horizon, SceneSky sky, double time,
        Func<Color, Brush> fill, Func<Color, double, Pen> stroke)
    {
        var gradient = new LinearGradientBrush(sky.Top, sky.Bottom, 90);
        gradient.Freeze();
        dc.DrawRectangle(gradient, null, new Rect(0, 0, width, horizon));
        var overcast = sky.Weather is SceneWeather.Rain or SceneWeather.Storm or SceneWeather.Fog;
        if (sky.Night && !overcast)
        {
            for (var i = 0; i < 110; i++)
            {
                var sx = SceneWeatherFx.Noise(i * 3 + 1) * width;
                var sy = SceneWeatherFx.Noise(i * 3 + 2) * horizon * 0.62;
                var twinkle = 0.45 + 0.55 * Math.Abs(Math.Sin(time * (0.6 + SceneWeatherFx.Noise(i) * 1.6) + i));
                var size = SceneWeatherFx.Noise(i * 3 + 3) > 0.9 ? 3 : 2;
                dc.DrawRectangle(fill(Color.FromArgb((byte)(200 * twinkle * Math.Min(1, (sky.Darkness - 0.3) * 3)), 0xF4, 0xF1, 0xDC)), null,
                    new Rect(Math.Round(sx), Math.Round(sy), size, size));
            }
        }
        DrawOrb(dc, OrbAt(sky, width, horizon), sky, fill);
        // Clouds: a few by day, a low ceiling in rain.
        var count = sky.Weather switch
        {
            SceneWeather.Clear => 3, SceneWeather.Cloudy => 7, SceneWeather.Fog => 5, SceneWeather.Snow => 7, _ => 10,
        };
        var cloud = sky.Weather is SceneWeather.Rain or SceneWeather.Storm
            ? Mix(Color.FromRgb(0x5A, 0x62, 0x70), Color.FromRgb(0x1E, 0x22, 0x2C), sky.Darkness / 0.72)
            : Mix(Color.FromRgb(0xF4, 0xF6, 0xFA), Color.FromRgb(0x3A, 0x40, 0x58), sky.Darkness / 0.72);
        for (var i = 0; i < count; i++)
        {
            var w = 90 + SceneWeatherFx.Noise(i * 5 + 11) * 160;
            var speed = 4 + SceneWeatherFx.Noise(i * 5 + 12) * 9;
            var cx = (SceneWeatherFx.Noise(i * 5 + 13) * (width + w * 2) + time * speed) % (width + w * 2) - w;
            var cy = 18 + SceneWeatherFx.Noise(i * 5 + 14) * horizon * (count > 6 ? 0.32 : 0.4);
            var alpha = count > 6 ? 0.92 : 0.8;
            var ink = fill(Alpha(cloud, alpha));
            var h = Math.Round(w * 0.22);
            dc.DrawRectangle(ink, null, new Rect(Math.Round(cx), Math.Round(cy + h * 0.5), Math.Round(w), Math.Round(h * 0.7)));
            dc.DrawRectangle(ink, null, new Rect(Math.Round(cx + w * 0.15), Math.Round(cy + h * 0.15), Math.Round(w * 0.45), Math.Round(h * 0.6)));
            dc.DrawRectangle(ink, null, new Rect(Math.Round(cx + w * 0.42), Math.Round(cy - h * 0.12), Math.Round(w * 0.32), Math.Round(h * 0.75)));
        }
        // Birds by day, fireflies on a clear night.
        if (!sky.Night && sky.Weather is SceneWeather.Clear or SceneWeather.Cloudy)
        {
            var bird = stroke(Color.FromArgb(160, 0x2A, 0x2F, 0x3A), 1.6);
            for (var i = 0; i < 4; i++)
            {
                var bx = (time * (26 + i * 5) + i * 310) % (width + 200) - 100;
                var by = horizon * 0.18 + i * 22 + Math.Sin(time * 1.3 + i) * 8;
                var flap = Math.Sin(time * 9 + i * 2) * 3;
                dc.DrawLine(bird, new Point(bx - 6, by - flap), new Point(bx, by));
                dc.DrawLine(bird, new Point(bx, by), new Point(bx + 6, by - flap));
            }
        }
    }

    /// <summary>Where <see cref="DrawSky"/> puts the sun or moon in a sky this wide down to this horizon.</summary>
    public static Point OrbAt(SceneSky sky, double width, double horizon)
    {
        var (ox, oy, _) = sky.Orb();
        return new Point(width * ox, horizon * oy);
    }

    /// <summary>How much of the sun or moon shows through the weather.</summary>
    public static double OrbVeil(SceneSky sky) =>
        sky.Weather is SceneWeather.Rain or SceneWeather.Storm or SceneWeather.Fog ? 0.25 : sky.Weather == SceneWeather.Cloudy ? 0.6 : 1;

    /// <summary>The sun or moon at <paramref name="orb"/>. A sky has one: a scene seen through several windows places it
    /// once, in the scene's own coordinates, and each window's clip shows it or not.</summary>
    public static void DrawOrb(DrawingContext dc, Point orb, SceneSky sky, Func<Color, Brush> fill)
    {
        dc.PushOpacity(OrbVeil(sky));
        if (sky.SunUp)
        {
            dc.DrawEllipse(fill(Color.FromArgb(60, 0xFF, 0xE9, 0x9A)), null, orb, 46, 46);
            dc.DrawEllipse(fill(Color.FromRgb(0xFF, 0xE2, 0x7A)), null, orb, 24, 24);
        }
        else
        {
            dc.DrawEllipse(fill(Color.FromArgb(40, 0xE8, 0xEE, 0xFF)), null, orb, 40, 40);
            dc.DrawEllipse(fill(Color.FromRgb(0xEE, 0xEC, 0xDC)), null, orb, 19, 19);
            dc.DrawEllipse(fill(Color.FromRgb(0xD2, 0xCF, 0xBE)), null, new Point(orb.X - 6, orb.Y - 4), 5, 5);
            dc.DrawEllipse(fill(Color.FromRgb(0xD2, 0xCF, 0xBE)), null, new Point(orb.X + 7, orb.Y + 6), 3.5, 3.5);
        }
        dc.Pop();
    }

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    private static Color Alpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(Math.Round(color.A * alpha), 0, 255), color.R, color.G, color.B);

    /// <summary>How bright the current lightning flash is, 0 when none. Strikes land every few seconds in a storm.</summary>
    public static double Lightning(SceneSky sky, double time, out double boltX)
    {
        boltX = 0;
        if (sky.Weather != SceneWeather.Storm) return 0;
        const double Period = 7.3;
        var slot = Math.Floor(time / Period);
        var at = time - slot * Period;
        var offset = 1 + Noise((int)slot * 31 + 7) * 4.5;
        boltX = 0.1 + Noise((int)slot * 13 + 3) * 0.8;
        var t = at - offset;
        if (t < 0 || t > 0.55) return 0;
        // Two quick flickers then a fade, the way a real strike reads.
        return t < 0.08 ? 1 : t < 0.14 ? 0.25 : t < 0.22 ? 0.85 : Math.Max(0, 0.85 - (t - 0.22) * 2.6);
    }

    public static double Noise(int seed)
    {
        unchecked
        {
            var h = (uint)seed * 2654435761u;
            h ^= h >> 15; h *= 0x2c1b3c6d; h ^= h >> 12; h *= 0x297a2d39; h ^= h >> 15;
            return (h & 0xFFFFFF) / (double)0x1000000;
        }
    }
}
