using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// Tile URLs for the radar view. The reflectivity mosaic is the National Weather Service's own product, served as
/// WMS by NOAA's public GeoServer (the same data behind radar.weather.gov); the basemap and place labels are Esri's
/// free world canvas services. Nothing here needs a key.
/// </summary>
public static class RadarTiles
{
    /// <summary>Half the circumference of the Web Mercator world, in metres - the EPSG:3857 axis limit.</summary>
    public const double MercatorOrigin = 20037508.342789244;
    public const int TileSize = 256;

    private const string Wms = "https://opengeo.ncep.noaa.gov/geoserver/conus/conus_bref_qcd/ows";
    private const string RadarLayer = "conus_bref_qcd";

    public static string CapabilitiesUrl => $"{Wms}?service=WMS&version=1.3.0&request=GetCapabilities";

    // Esri tile services address tiles as /{z}/{row}/{col} - note row (y) comes before column (x).
    public static string Basemap(int z, int x, int y) =>
        $"https://services.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Dark_Gray_Base/MapServer/tile/{z}/{y}/{x}";

    public static string Labels(int z, int x, int y) =>
        $"https://services.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Dark_Gray_Reference/MapServer/tile/{z}/{y}/{x}";

    /// <summary>A single radar tile, requested as a WMS GetMap over the tile's own EPSG:3857 bounding box.</summary>
    public static string Radar(int z, int x, int y, string? time)
    {
        var span = 2 * MercatorOrigin / (1 << z);
        var minX = -MercatorOrigin + x * span;
        var maxY = MercatorOrigin - y * span;
        var bbox = string.Join(",", new[] { minX, maxY - span, minX + span, maxY }
            .Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));

        var url = $"{Wms}?service=WMS&version=1.3.0&request=GetMap&layers={RadarLayer}" +
                  $"&crs=EPSG:3857&bbox={bbox}&width={TileSize}&height={TileSize}" +
                  "&format=image/png&transparent=true";
        if (!string.IsNullOrEmpty(time)) url += "&time=" + Uri.EscapeDataString(time);
        return url;
    }
}

/// <summary>
/// A small slippy map: Esri basemap underneath, either the NWS reflectivity mosaic or SPC categorical outlook on top,
/// place labels above that, and a marker for the user's saved location. Drag to pan, wheel to zoom. Tiles and vector
/// polygons are drawn straight into the render pass, so panning stays smooth no matter how much is on screen.
/// </summary>
public sealed class RadarMap : FrameworkElement
{
    public const int MinZoom = 3;
    public const int MaxZoom = 11;

    private static readonly HttpClient Http = CreateHttp();
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new();
    private static readonly ConcurrentDictionary<string, byte> Pending = new();
    private static readonly ConcurrentDictionary<string, int> Failures = new();
    // Higher concurrency fills the first viewport / zoom step faster without hammering NOAA/Esri.
    private static readonly SemaphoreSlim Gate = new(10);

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Add("User-Agent", "VibeCode/1.0 (desktop app; radar view)");
        return http;
    }

    private Point _dragFrom;
    private bool _dragging;
    // Last fully-composited basemap snapshot, used under the live tiles so fast zoom never flashes black.
    private ImageSource? _holdFrame;
    private double _holdZoom, _holdLat, _holdLon;
    private double _holdW, _holdH;
    private int _holdMissing = int.MaxValue; // basemap holes when the snapshot was taken (0 = complete)
    private bool _prefetchQueued;
    private bool _capturing; // true while RenderTargetBitmap.Render re-enters OnRender

    public RadarMap()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.SizeAll;
        // Fant scales parent-tile fallbacks more cleanly than nearest/linear when zooming hard.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Fant);
        Loaded += (_, _) => QueuePrefetch();
    }

    // ---- dependency properties ----
    private static FrameworkPropertyMetadata Redraw(object def) =>
        new(def, FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty CenterLatProperty =
        DependencyProperty.Register(nameof(CenterLat), typeof(double), typeof(RadarMap), Redraw(39.5));
    public static readonly DependencyProperty CenterLonProperty =
        DependencyProperty.Register(nameof(CenterLon), typeof(double), typeof(RadarMap), Redraw(-98.35));
    public static readonly DependencyProperty ZoomProperty =
        DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(RadarMap), Redraw(4.2));
    public static readonly DependencyProperty RadarOpacityProperty =
        DependencyProperty.Register(nameof(RadarOpacity), typeof(double), typeof(RadarMap), Redraw(0.85));
    public static readonly DependencyProperty ShowRadarProperty =
        DependencyProperty.Register(nameof(ShowRadar), typeof(bool), typeof(RadarMap), Redraw(true));
    public static readonly DependencyProperty FrameTimeProperty =
        DependencyProperty.Register(nameof(FrameTime), typeof(string), typeof(RadarMap), Redraw(null!));
    public static readonly DependencyProperty ShowLabelsProperty =
        DependencyProperty.Register(nameof(ShowLabels), typeof(bool), typeof(RadarMap), Redraw(true));
    public static readonly DependencyProperty MarkerLatProperty =
        DependencyProperty.Register(nameof(MarkerLat), typeof(double?), typeof(RadarMap), Redraw(null!));
    public static readonly DependencyProperty MarkerLonProperty =
        DependencyProperty.Register(nameof(MarkerLon), typeof(double?), typeof(RadarMap), Redraw(null!));
    public static readonly DependencyProperty MarkerLabelProperty =
        DependencyProperty.Register(nameof(MarkerLabel), typeof(string), typeof(RadarMap), Redraw(""));
    public static readonly DependencyProperty MarkerBrushProperty =
        DependencyProperty.Register(nameof(MarkerBrush), typeof(Brush), typeof(RadarMap),
            Redraw(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xF5))));

    public double CenterLat { get => (double)GetValue(CenterLatProperty); set => SetValue(CenterLatProperty, value); }
    public double CenterLon { get => (double)GetValue(CenterLonProperty); set => SetValue(CenterLonProperty, value); }
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public double RadarOpacity { get => (double)GetValue(RadarOpacityProperty); set => SetValue(RadarOpacityProperty, value); }
    public bool ShowRadar { get => (bool)GetValue(ShowRadarProperty); set => SetValue(ShowRadarProperty, value); }
    public string? FrameTime { get => (string?)GetValue(FrameTimeProperty); set => SetValue(FrameTimeProperty, value); }
    public bool ShowLabels { get => (bool)GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }
    public double? MarkerLat { get => (double?)GetValue(MarkerLatProperty); set => SetValue(MarkerLatProperty, value); }
    public double? MarkerLon { get => (double?)GetValue(MarkerLonProperty); set => SetValue(MarkerLonProperty, value); }
    public string MarkerLabel { get => (string)GetValue(MarkerLabelProperty); set => SetValue(MarkerLabelProperty, value); }
    public Brush MarkerBrush { get => (Brush)GetValue(MarkerBrushProperty); set => SetValue(MarkerBrushProperty, value); }

    private SpcOutlook? _spcOutlook;
    /// <summary>Current categorical outlook drawn as a vector layer. Null hides the SPC layer.</summary>
    public SpcOutlook? SpcOutlook
    {
        get => _spcOutlook;
        set
        {
            if (ReferenceEquals(_spcOutlook, value)) return;
            _spcOutlook = value;
            InvalidateVisual();
        }
    }

    // ---- Web Mercator projection (world pixels at a given zoom) ----
    private static double WorldSize(double z) => RadarTiles.TileSize * Math.Pow(2, z);

    private static double LonToX(double lon, double z) => (lon + 180.0) / 360.0 * WorldSize(z);

    private static double LatToY(double lat, double z)
    {
        var s = Math.Clamp(Math.Sin(lat * Math.PI / 180.0), -0.9999, 0.9999);
        return (0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI)) * WorldSize(z);
    }

    private static double XToLon(double x, double z) => x / WorldSize(z) * 360.0 - 180.0;

    private static double YToLat(double y, double z)
    {
        var n = Math.PI - 2 * Math.PI * y / WorldSize(z);
        return 180.0 / Math.PI * Math.Atan(Math.Sinh(n));
    }

    /// <summary>The integer tile level actually fetched, and how much it is stretched to hit the fractional zoom.</summary>
    private (int level, double scale) TileLevel()
    {
        var level = Math.Clamp((int)Math.Round(Zoom), MinZoom, MaxZoom);
        return (level, Math.Pow(2, Zoom - level));
    }

    private Point LatLonToScreen(double lat, double lon)
    {
        var (level, scale) = TileLevel();
        var cx = LonToX(CenterLon, level);
        var cy = LatToY(CenterLat, level);
        return new Point(
            (LonToX(lon, level) - cx) * scale + ActualWidth / 2,
            (LatToY(lat, level) - cy) * scale + ActualHeight / 2);
    }

    private (double lat, double lon) ScreenToLatLon(Point p)
    {
        var (level, scale) = TileLevel();
        var cx = LonToX(CenterLon, level);
        var cy = LatToY(CenterLat, level);
        return (YToLat((p.Y - ActualHeight / 2) / scale + cy, level),
                XToLon((p.X - ActualWidth / 2) / scale + cx, level));
    }

    // ---- interaction ----
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragFrom = e.GetPosition(this);
        _dragging = true;
        CaptureMouse();
        Focus();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        var now = e.GetPosition(this);
        var (level, scale) = TileLevel();
        var cx = LonToX(CenterLon, level) - (now.X - _dragFrom.X) / scale;
        var cy = LatToY(CenterLat, level) - (now.Y - _dragFrom.Y) / scale;

        // Clamp vertically: past ~85 degrees Mercator stretches to infinity and the view would tear.
        cy = Math.Clamp(cy, 0, WorldSize(level));
        CenterLon = XToLon(cx, level);
        CenterLat = Math.Clamp(YToLat(cy, level), -85, 85);
        _dragFrom = now;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomAt(e.GetPosition(this), e.Delta > 0 ? 0.5 : -0.5);
        e.Handled = true;
    }

    /// <summary>Zooms while keeping whatever is under <paramref name="anchor"/> pinned to that spot.</summary>
    public void ZoomAt(Point anchor, double delta)
    {
        var before = ScreenToLatLon(anchor);
        Zoom = Math.Clamp(Zoom + delta, MinZoom, MaxZoom);
        var after = ScreenToLatLon(anchor);
        CenterLat = Math.Clamp(CenterLat + (before.lat - after.lat), -85, 85);
        CenterLon += before.lon - after.lon;
    }

    public void ZoomBy(double delta) => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), delta);

    public void CenterOn(double lat, double lon, double? zoom = null)
    {
        CenterLat = Math.Clamp(lat, -85, 85);
        CenterLon = lon;
        if (zoom is not null) Zoom = Math.Clamp(zoom.Value, MinZoom, MaxZoom);
    }

    // ---- tiles ----
    private static bool TryGetCached(string url, out ImageSource img) => Cache.TryGetValue(url, out img!);

    /// <summary>Kick off a download if needed. Returns the cached image when already warm.</summary>
    private ImageSource? RequestTile(string url)
    {
        if (TryGetCached(url, out var img)) return img;
        if (Failures.TryGetValue(url, out var fails) && fails >= 3) return null;
        if (Pending.TryAdd(url, 0)) _ = FetchAsync(url);
        return null;
    }

    private async Task FetchAsync(string url)
    {
        await Gate.WaitAsync();
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();   // required: the decode happens off the UI thread
            Cache[url] = bmp;
            Failures.TryRemove(url, out _);
        }
        catch
        {
            // A tile can 404 simply because the mosaic has no coverage there; give up after a few tries so a
            // dead URL cannot spin forever, but let transient failures retry on the next render.
            Failures.AddOrUpdate(url, 1, (_, n) => n + 1);
        }
        finally
        {
            Gate.Release();
            Pending.TryRemove(url, out _);
            await Dispatcher.InvokeAsync(InvalidateVisual, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private enum TileDrawKind { Missing, Parent, Exact }

    /// <summary>
    /// Draw one map tile. Prefer the exact zoom tile; if it is still loading, stretch a coarser parent tile into
    /// place so the screen never goes black while zooming or on first paint of a high zoom.
    /// </summary>
    private TileDrawKind DrawTile(
        DrawingContext dc,
        Func<int, int, int, string> urlFor,
        int level,
        int tx,
        int ty,
        double cx,
        double cy,
        double scale,
        double tilePx,
        bool requestParents)
    {
        var count = 1 << level;
        var wrapped = ((tx % count) + count) % count;
        var dest = TileRect(tx, ty, cx, cy, scale, tilePx);

        var exactUrl = urlFor(level, wrapped, ty);
        if (RequestTile(exactUrl) is { } exact)
        {
            dc.DrawImage(exact, dest);
            return TileDrawKind.Exact;
        }

        // Walk up parent zoom levels and crop/stretch the covering parent into this cell.
        for (var d = 1; d <= 6 && level - d >= MinZoom; d++)
        {
            var parentLevel = level - d;
            var size = 1 << d;
            var parentCount = 1 << parentLevel;
            var parentWrapped = wrapped >> d;
            var parentY = ty >> d;
            if (parentY < 0 || parentY >= parentCount) continue;

            var parentUrl = urlFor(parentLevel, parentWrapped, parentY);
            if (TryGetCached(parentUrl, out var parentImg))
            {
                // Continuous parent origin covering this continuous tile index.
                var parentTx = (int)Math.Floor(tx / (double)size) * size;
                var parentTy = (int)Math.Floor(ty / (double)size) * size;
                var parentDest = new Rect(
                    (parentTx * RadarTiles.TileSize - cx) * scale + ActualWidth / 2,
                    (parentTy * RadarTiles.TileSize - cy) * scale + ActualHeight / 2,
                    tilePx * size + 0.5,
                    tilePx * size + 0.5);

                dc.PushClip(new RectangleGeometry(dest));
                dc.DrawImage(parentImg, parentDest);
                dc.Pop();
                return TileDrawKind.Parent;
            }

            if (requestParents) RequestTile(parentUrl);
        }

        return TileDrawKind.Missing;
    }

    private Rect TileRect(int tx, int ty, double cx, double cy, double scale, double tilePx) => new(
        (tx * RadarTiles.TileSize - cx) * scale + ActualWidth / 2,
        (ty * RadarTiles.TileSize - cy) * scale + ActualHeight / 2,
        tilePx + 0.5,   // half-pixel overlap hides seams between neighbouring tiles
        tilePx + 0.5);

    /// <summary>
    /// Paint the last good basemap snapshot transformed into the current camera so rapid zoom/pan never blanks
    /// the whole view while the new integer tile set is still downloading.
    /// </summary>
    private void DrawHoldFrame(DrawingContext dc, int level, double scale, double cx, double cy)
    {
        if (_holdFrame is null || _holdW < 1 || _holdH < 1) return;

        // Project the four corners of the held view into the current camera and draw the bitmap into that quad
        // as an axis-aligned rect (good enough for pure zoom / small pans).
        var holdLevel = Math.Clamp((int)Math.Round(_holdZoom), MinZoom, MaxZoom);
        var holdScale = Math.Pow(2, _holdZoom - holdLevel);
        var holdCx = LonToX(_holdLon, holdLevel);
        var holdCy = LatToY(_holdLat, holdLevel);

        // Map held-frame pixel (px,py) → lat/lon via held camera, then → current screen.
        Point MapHeld(double px, double py)
        {
            var worldX = holdCx + (px - _holdW / 2) / holdScale;
            var worldY = holdCy + (py - _holdH / 2) / holdScale;
            // Reproject world pixels from holdLevel into current level space.
            var factor = Math.Pow(2, level - holdLevel);
            var sx = (worldX * factor - cx) * scale + ActualWidth / 2;
            var sy = (worldY * factor - cy) * scale + ActualHeight / 2;
            return new Point(sx, sy);
        }

        var tl = MapHeld(0, 0);
        var br = MapHeld(_holdW, _holdH);
        var dest = new Rect(tl, br);
        if (dest.Width < 1 || dest.Height < 1 || dest.Width > ActualWidth * 8 || dest.Height > ActualHeight * 8)
            return;

        dc.PushOpacity(0.95);
        dc.DrawImage(_holdFrame, dest);
        dc.Pop();
    }

    private void CaptureHoldFrame()
    {
        if (_capturing || ActualWidth < 2 || ActualHeight < 2) return;
        _capturing = true;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(this);
            rtb.Freeze();
            _holdFrame = rtb;
            _holdZoom = Zoom;
            _holdLat = CenterLat;
            _holdLon = CenterLon;
            _holdW = ActualWidth;
            _holdH = ActualHeight;
            _holdMissing = 0;
        }
        catch
        {
            // Snapshot is a best-effort smoothness aid; never let capture failures break the map.
        }
        finally
        {
            _capturing = false;
        }
    }

    private bool CameraMatchesHold() =>
        _holdFrame is not null
        && Math.Abs(_holdZoom - Zoom) <= 0.001
        && Math.Abs(_holdLat - CenterLat) <= 0.00001
        && Math.Abs(_holdLon - CenterLon) <= 0.00001
        && Math.Abs(_holdW - ActualWidth) <= 0.5
        && Math.Abs(_holdH - ActualHeight) <= 0.5;

    private void QueuePrefetch()
    {
        if (_prefetchQueued || ActualWidth < 2 || ActualHeight < 2) return;
        _prefetchQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _prefetchQueued = false;
            PrefetchAroundView();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Warm basemap / labels / radar for the current view, a 1-tile pad, coarser parents, and one zoom step in/out.
    /// Parents first so fallback has something to stretch during the next wheel flick.
    /// </summary>
    private void PrefetchAroundView()
    {
        if (ActualWidth < 2 || ActualHeight < 2) return;

        var (level, scale) = TileLevel();
        var cx = LonToX(CenterLon, level);
        var cy = LatToY(CenterLat, level);
        var count = 1 << level;
        var halfW = ActualWidth / 2 / scale;
        var halfH = ActualHeight / 2 / scale;
        var x0 = (int)Math.Floor((cx - halfW) / RadarTiles.TileSize) - 1;
        var x1 = (int)Math.Floor((cx + halfW) / RadarTiles.TileSize) + 1;
        var y0 = (int)Math.Floor((cy - halfH) / RadarTiles.TileSize) - 1;
        var y1 = (int)Math.Floor((cy + halfH) / RadarTiles.TileSize) + 1;

        void Want(Func<int, int, int, string> urlFor, int z, int x0z, int x1z, int y0z, int y1z)
        {
            var n = 1 << z;
            for (var ty = y0z; ty <= y1z; ty++)
            {
                if (ty < 0 || ty >= n) continue;
                for (var tx = x0z; tx <= x1z; tx++)
                {
                    var wrapped = ((tx % n) + n) % n;
                    RequestTile(urlFor(z, wrapped, ty));
                }
            }
        }

        // Coarse → fine so parent fallback is warm before high-res tiles finish.
        for (var z = MinZoom; z <= level; z++)
        {
            var shift = level - z;
            var zx0 = x0 >> shift;
            var zx1 = x1 >> shift;
            var zy0 = y0 >> shift;
            var zy1 = y1 >> shift;
            Want(RadarTiles.Basemap, z, zx0, zx1, zy0, zy1);
            if (ShowLabels) Want(RadarTiles.Labels, z, zx0, zx1, zy0, zy1);
            if (ShowRadar) Want((zz, x, y) => RadarTiles.Radar(zz, x, y, FrameTime), z, zx0, zx1, zy0, zy1);
        }

        // One step deeper (centre of the view only) so the next zoom-in already has a head start.
        if (level < MaxZoom)
        {
            var z = level + 1;
            var midX = (x0 + x1) / 2;
            var midY = Math.Clamp((y0 + y1) / 2, 0, count - 1);
            var zx0 = midX * 2 - 1;
            var zx1 = midX * 2 + 2;
            var zy0 = midY * 2 - 1;
            var zy1 = midY * 2 + 2;
            Want(RadarTiles.Basemap, z, zx0, zx1, zy0, zy1);
            if (ShowLabels) Want(RadarTiles.Labels, z, zx0, zx1, zy0, zy1);
            if (ShowRadar) Want((zz, x, y) => RadarTiles.Radar(zz, x, y, FrameTime), z, zx0, zx1, zy0, zy1);
        }
    }

    // ---- rendering ----
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        // A background fill also gives the control a hit-test surface, so dragging works over empty ocean.
        dc.DrawRectangle(Background(), null, new Rect(0, 0, ActualWidth, ActualHeight));

        var (level, scale) = TileLevel();
        var tilePx = RadarTiles.TileSize * scale;
        var cx = LonToX(CenterLon, level);
        var cy = LatToY(CenterLat, level);
        var count = 1 << level;

        var halfW = ActualWidth / 2 / scale;
        var halfH = ActualHeight / 2 / scale;
        var x0 = (int)Math.Floor((cx - halfW) / RadarTiles.TileSize);
        var x1 = (int)Math.Floor((cx + halfW) / RadarTiles.TileSize);
        var y0 = (int)Math.Floor((cy - halfH) / RadarTiles.TileSize);
        var y1 = (int)Math.Floor((cy + halfH) / RadarTiles.TileSize);

        // If the camera moved since the last solid snapshot, paint that snapshot first so the user never
        // stares at a black void while the new tile set is in flight. Skip while capturing the snapshot itself.
        if (!_capturing && !CameraMatchesHold()) DrawHoldFrame(dc, level, scale, cx, cy);

        int basemapTotal = 0, basemapExact = 0;

        void Pass(Func<int, int, int, string> url, double opacity, bool scoreBasemap)
        {
            if (opacity <= 0) return;
            if (opacity < 1) dc.PushOpacity(opacity);
            for (var ty = y0; ty <= y1; ty++)
            {
                if (ty < 0 || ty >= count) continue;
                for (var tx = x0; tx <= x1; tx++)
                {
                    var kind = DrawTile(dc, url, level, tx, ty, cx, cy, scale, tilePx, requestParents: true);
                    if (!scoreBasemap) continue;
                    basemapTotal++;
                    if (kind == TileDrawKind.Exact) basemapExact++;
                }
            }
            if (opacity < 1) dc.Pop();
        }

        Pass(RadarTiles.Basemap, 1.0, scoreBasemap: true);
        if (ShowRadar)
            Pass((z, x, y) => RadarTiles.Radar(z, x, y, FrameTime), Math.Clamp(RadarOpacity, 0, 1), false);
        DrawSpcOutlook(dc);
        if (ShowLabels) Pass(RadarTiles.Labels, 0.9, false);

        DrawMarker(dc);
        DrawAttribution(dc);

        if (_capturing) return;

        // Snapshot only when every basemap cell has its exact zoom tile (not a stretched parent).
        // That freezes a sharp frame for the next zoom/pan without baking blur or black holes.
        if (basemapTotal > 0 && basemapExact == basemapTotal && !CameraMatchesHold())
        {
            Dispatcher.BeginInvoke(CaptureHoldFrame, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else if (basemapTotal > 0)
        {
            _holdMissing = basemapTotal - basemapExact;
        }

        QueuePrefetch();
    }

    private Brush Background() =>
        Application.Current?.TryFindResource("CodeBg") as Brush ?? Brushes.Black;

    private void DrawSpcOutlook(DrawingContext dc)
    {
        if (_spcOutlook is null) return;
        foreach (var area in _spcOutlook.Areas.OrderBy(area => area.Rank))
        {
            var fillColor = ParseColor(area.Fill, Colors.Gray);
            var strokeColor = ParseColor(area.Stroke, Colors.White);
            var fill = new SolidColorBrush(fillColor) { Opacity = 0.42 };
            var stroke = new SolidColorBrush(strokeColor) { Opacity = 0.96 };
            fill.Freeze();
            stroke.Freeze();
            var pen = new Pen(stroke, area.Rank >= 5 ? 2.2 : 1.7) { LineJoin = PenLineJoin.Round };
            pen.Freeze();

            foreach (var polygon in area.Polygons)
            {
                var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
                using (var context = geometry.Open())
                foreach (var ring in polygon.Rings)
                {
                    if (ring.Count < 3) continue;
                    context.BeginFigure(LatLonToScreen(ring[0].Lat, ring[0].Lon), isFilled: true, isClosed: true);
                    context.PolyLineTo(ring.Skip(1).Select(point => LatLonToScreen(point.Lat, point.Lon)).ToList(),
                        isStroked: true, isSmoothJoin: true);
                }
                geometry.Freeze();
                dc.DrawGeometry(fill, pen, geometry);
            }
        }
    }

    private static Color ParseColor(string value, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch { return fallback; }
    }

    /// <summary>Draws the saved location as a haloed dot, so it stays readable over both dark land and bright radar.</summary>
    private void DrawMarker(DrawingContext dc)
    {
        if (MarkerLat is not { } lat || MarkerLon is not { } lon) return;

        var p = LatLonToScreen(lat, lon);
        if (p.X < -40 || p.Y < -40 || p.X > ActualWidth + 40 || p.Y > ActualHeight + 40) return;

        var fill = MarkerBrush;
        var halo = new SolidColorBrush(((fill as SolidColorBrush)?.Color ?? Colors.DodgerBlue)) { Opacity = 0.22 };
        halo.Freeze();

        dc.DrawEllipse(halo, null, p, 17, 17);
        dc.DrawEllipse(null, new Pen(fill, 1.6) { }, p, 9.5, 9.5);
        dc.DrawEllipse(fill, new Pen(Brushes.White, 1.4), p, 4.5, 4.5);

        if (string.IsNullOrWhiteSpace(MarkerLabel)) return;

        var text = new FormattedText(MarkerLabel, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Application.Current?.TryFindResource("Ui") as FontFamily ?? new FontFamily("Segoe UI"),
                FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            11.5, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var box = new Rect(p.X - text.Width / 2 - 6, p.Y + 14, text.Width + 12, text.Height + 5);
        var plate = new SolidColorBrush(Color.FromArgb(0xCC, 0x0E, 0x11, 0x16));
        plate.Freeze();
        dc.DrawRoundedRectangle(plate, new Pen(fill, 1), box, 4, 4);
        dc.DrawText(text, new Point(box.X + 6, box.Y + 2));
    }

    /// <summary>Esri's basemap terms require visible attribution; NOAA is credited alongside it as the data source.</summary>
    private void DrawAttribution(DrawingContext dc)
    {
        var source = _spcOutlook is null ? "NOAA/NWS  ·  Esri" : "NOAA/NWS/SPC  ·  Esri";
        var text = new FormattedText(source, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9.5,
            new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var pad = 6.0;
        var plate = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
        plate.Freeze();
        dc.DrawRectangle(plate, null,
            new Rect(ActualWidth - text.Width - pad * 2, ActualHeight - text.Height - 4, text.Width + pad * 2, text.Height + 4));
        dc.DrawText(text, new Point(ActualWidth - text.Width - pad, ActualHeight - text.Height - 2));
    }
}
