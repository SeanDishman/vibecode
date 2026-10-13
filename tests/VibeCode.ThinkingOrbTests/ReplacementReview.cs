using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeCode.Services;
using VibeCode.UI;

internal static class ReplacementReview
{
    private static readonly DependencyProperty Phase = (DependencyProperty)typeof(OrbitSpinner)
        .GetField("PhaseProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static readonly MethodInfo Render = typeof(OrbitSpinner).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly ThinkingOrbStyle[] Replacements = [ThinkingOrbStyle.Helix, ThinkingOrbStyle.Knot];

    internal static void Run()
    {
        var report = new StringBuilder("# Replacement motion and performance\n\n");
        foreach (var theme in new[] { "Dark", "Cli" })
        {
            Program.Theme(theme);
            var ink = (Brush)Application.Current.Resources["Accent"];
            CheckIncumbents(theme);
            foreach (var style in Replacements)
            {
                CheckSeam(style, ink);
                Atlas(theme, style, ink);
            }
        }
        Program.Theme("Dark");
        foreach (var style in Replacements) Benchmark(style, report);
        File.WriteAllText(Path.Combine(Program.OutputDirectory, "replacement-performance.md"), report.ToString());
        File.WriteAllText(Path.Combine(Program.OutputDirectory, "preview.html"), PreviewPage);
    }

    private static (OrbitSpinner Orb, Action<DrawingContext> Draw) Renderer(ThinkingOrbStyle style, Brush ink, int size)
    {
        var orb = new OrbitSpinner { StyleKind = style, Ink = ink, Width = size, Height = size, Spin = false };
        orb.Measure(new Size(size, size)); orb.Arrange(new Rect(0, 0, size, size));
        orb.UpdateLayout();
        return (orb, Render.CreateDelegate<Action<DrawingContext>>(orb));
    }

    private static void CheckSeam(ThinkingOrbStyle style, Brush ink)
    {
        var before = Pixels(Program.Frame(style, 88, ink, 0.999999));
        var after = Pixels(Program.Frame(style, 88, ink, 0));
        var meanChange = before.Zip(after, (a, b) => Math.Abs(a - b)).Average();
        Program.Check(meanChange < 0.10, $"{style} has a continuous seam, not just equal frames at integer phases");
    }

    private static void CheckIncumbents(string theme)
    {
        var file = $"orbs-{theme.ToLowerInvariant()}.png";
        var baseline = Path.GetFullPath(Path.Combine(Program.OutputDirectory, "..", "baseline", file));
        if (!File.Exists(baseline)) return;
        var before = new BitmapImage(new Uri(baseline));
        var after = new BitmapImage(new Uri(Path.Combine(Program.OutputDirectory, file)));
        // The first four 44-DIP rows (14 DIP vertical padding) are the four styles explicitly kept by the user.
        var rect = new Int32Rect(0, 0, before.PixelWidth, 4 * 58);
        Program.Check(Pixels(new CroppedBitmap(before, rect)).SequenceEqual(Pixels(new CroppedBitmap(after, rect))),
            $"Globe, Gyroscope, Atom and Morph remain pixel-identical in {theme}");
    }

    private static void Atlas(string theme, ThinkingOrbStyle style, Brush ink)
    {
        const int width = 192, height = 96, columns = 10, frames = 90;
        var sizes = new[] { 22, 32, 72 };
        var positions = new[] { new Point(15, 37), new Point(55, 32), new Point(105, 12) };
        var renderers = sizes.Select(size => Renderer(style, ink, size)).ToArray();
        var frame = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var canvas = new DrawingVisual();
        using (var atlas = canvas.RenderOpen())
            for (var i = 0; i < frames; i++)
            {
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle((Brush)Application.Current.Resources["Bg0"], null, new Rect(0, 0, width, height));
                    for (var glyph = 0; glyph < renderers.Length; glyph++)
                    {
                        renderers[glyph].Orb.SetValue(Phase, i / (double)frames);
                        drawing.PushTransform(new TranslateTransform(positions[glyph].X, positions[glyph].Y));
                        renderers[glyph].Draw(drawing);
                        drawing.Pop();
                    }
                }
                frame.Clear(); frame.Render(visual);
                var snapshot = frame.Clone(); snapshot.Freeze();
                atlas.DrawImage(snapshot, new Rect(i % columns * width, i / columns * height, width, height));
            }
        var bitmap = new RenderTargetBitmap(columns * width, frames / columns * height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Program.OutputDirectory, $"{style.ToString().ToLowerInvariant()}-{theme.ToLowerInvariant()}-motion.png"));
        encoder.Save(stream);
    }

    private static void Benchmark(ThinkingOrbStyle style, StringBuilder report)
    {
        var renderers = Enumerable.Range(0, 8).Select(_ => Renderer(style, (Brush)Application.Current.Resources["Accent"], 22)).ToArray();
        var surfaces = renderers.Select(_ => new DrawingVisual()).ToArray();
        void Frame(int frame)
        {
            for (var i = 0; i < renderers.Length; i++)
            {
                renderers[i].Orb.SetValue(Phase, (frame / 180.0 + i / 8.0) % 1);
                using var drawing = surfaces[i].RenderOpen();
                renderers[i].Draw(drawing);
            }
        }
        for (var frame = 0; frame < 12; frame++) Frame(frame);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var elapsed = Stopwatch.StartNew();
        for (var frame = 0; frame < 180; frame++) Frame(frame);
        elapsed.Stop();
        var perFrame = elapsed.Elapsed.TotalMilliseconds / 180;
        report.AppendLine($"- {style}: eight simultaneous 22-DIP indicators, **{perFrame:0.000} ms** average CPU drawing time per frame; " +
            $"{(GC.GetAllocatedBytesForCurrentThread() - allocated) / 180:N0} bytes of WPF drawing data per eight-indicator frame. Full six-second phase range sampled; no raster readback included.");
        Program.Check(perFrame < 8, $"eight {style} indicators stay within an 8 ms CPU drawing budget");
        foreach (var renderer in renderers)
        {
            var pens = (Pen[])typeof(OrbitSpinner).GetField("_filamentBody", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer.Orb)!;
            Program.Check(pens.All(pen => pen.IsFrozen && pen.Brush.IsFrozen), "shared material resources are frozen");
        }
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var bitmap = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }

    private const string PreviewPage = """
        <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Helix and Knot · VibeCode</title>
        <style>
        :root{color-scheme:dark;font-family:Segoe UI,sans-serif;background:#1b1b1e;color:#eeeeef}
        body{max-width:680px;margin:64px auto;padding:0 24px}h1{font-size:26px;font-weight:500;margin:0 0 12px}
        p{color:#b9b9c1;font-size:14px;line-height:1.6;margin:0 0 28px}.row{display:flex;align-items:center;gap:32px;border-top:1px solid #343438;padding:24px 0}
        h2{font-size:17px;font-weight:500;margin:0 0 6px}small{color:#a4a4b0;font-size:12px}canvas{width:192px;height:96px;flex:none}
        .controls{display:flex;gap:12px;align-items:center;margin-top:20px}button,select{font:inherit;font-size:13px;background:#29292e;color:#eee;border:1px solid #55555f;border-radius:6px;padding:8px 12px}
        button:focus-visible,select:focus-visible,input:focus-visible{outline:2px solid #88b6ff;outline-offset:3px}input{flex:1;accent-color:#88b6ff;min-width:60px}
        @media(max-width:520px){body{margin:32px auto}.row{gap:12px;flex-wrap:wrap}.controls{flex-wrap:wrap}}
        </style>
        <h1>Helix and Knot</h1><p>Native VibeCode renders. Each row shows the 22 px chat indicator, the 32 px picker preview, and a 72 px detail view. The full motion cycle is six seconds.</p>
        <div class="row"><canvas id="helix" width="192" height="96" aria-label="Helix thinking animation"></canvas><div><h2>Helix</h2><small>Two strands, one continuous twist.</small></div></div>
        <div class="row"><canvas id="knot" width="192" height="96" aria-label="Knot thinking animation"></canvas><div><h2>Knot</h2><small>A single strand weaving through itself.</small></div></div>
        <div class="controls"><button id="play">Pause</button><label>Theme <select id="theme"><option value="dark">Dark</option><option value="cli">CLI</option></select></label><input id="phase" aria-label="Animation position" type="range" min="0" max="89" value="0"></div>
        <script>
        const ids=['helix','knot'],images={},slider=document.querySelector('#phase'),play=document.querySelector('#play'),theme=document.querySelector('#theme');
        let playing=!matchMedia('(prefers-reduced-motion: reduce)').matches,index=0,start=performance.now();
        for(const mode of ['dark','cli'])for(const id of ids){const img=new Image();images[id+'-'+mode]=img;img.src=id+'-'+mode+'-motion.png';img.onload=draw}
        function draw(){for(const id of ids){const img=images[id+'-'+theme.value];if(img?.complete&&img.naturalWidth)document.getElementById(id).getContext('2d').drawImage(img,(index%10)*192,Math.floor(index/10)*96,192,96,0,0,192,96)}slider.value=index;play.textContent=playing?'Pause':'Play'}
        play.onclick=()=>{playing=!playing;start=performance.now()-index/15*1000;draw()};theme.onchange=draw;slider.oninput=()=>{playing=false;index=+slider.value;draw()};
        document.addEventListener('visibilitychange',()=>{start=performance.now()-index/15*1000});
        function tick(now){if(playing&&!document.hidden){index=Math.floor((now-start)/1000*15)%90;draw()}requestAnimationFrame(tick)}requestAnimationFrame(tick);draw();
        </script></html>
        """;
}
