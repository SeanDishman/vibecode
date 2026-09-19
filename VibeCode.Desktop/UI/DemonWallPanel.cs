using System.Windows;
using System.Windows.Controls;

namespace VibeCode.UI;

/// <summary>
/// The Demon Mode wall: one FEATURED pane — the orchestrator — holding a block of cells in the top-left corner,
/// with every worker filling the cells around it.
///
/// It exists because the orchestrator has to read as "the one you type into" without being the only thing you can
/// see. The first attempt gave it a row of its own above the worker grid, which made it span the FULL width: at a
/// sixteen-session roster that is five worker columns, so the pane the user is meant to focus on was five times
/// wider than everything it is supposed to sit among — a letterbox strip with an empty transcript. A 2x2 block is
/// four times a worker's area, which reads as the big one at a glance, and — unlike a dedicated row — costs the
/// workers nothing: the wall is still one uniform grid, so no pane is squeezed to make room.
///
/// The roster is the user's choice (4-17 sessions), and most sizes have no exact 2x2-plus-uniform-grid answer: at
/// nine workers a 4x4 wall leaves three dead cells. So the arrangement is SEARCHED rather than derived — every grid
/// and every feature block up to 3x3 is scored, and a layout that fills the wall exactly beats one that leaves
/// holes. That is what lets the feature be 3x2 at ten workers: the orchestrator, the one pane that is meant to be
/// big, absorbs the slack instead of the wall showing gaps. A full seventeen-session team still lands on the same
/// 4x5 grid with a 2x2 feature it always used — that layout is exact, so nothing outscores it.
///
/// Everything here is measured in CELLS, never in pixels, so the same arrangement holds at any window size.
/// </summary>
public sealed class DemonWallPanel : Panel
{
    /// <summary>The smallest featured block, and the one a full team gets: two cells in each direction.</summary>
    private const int FeatureSpan = 2;

    /// <summary>The largest, reached only when growing the feature is what fills the wall. Beyond three cells in a
    /// direction the orchestrator stops being the prominent pane and starts being the wall.</summary>
    private const int MaxFeatureSpan = 3;

    /// <summary>A feature may be at most this many worker-cells in area, so 3x3 (nine, over twice the normal block)
    /// is out. Growing to 1.5x reads as "this one matters"; growing to 2.25x reads as a mistake.</summary>
    private const int MaxFeatureCells = 6;

    private const int MaxRows = 4;
    private const int MaxColumns = 6;

    /// <summary>Cell shapes to aim for, as width÷height. A worker cell wants to be a little wider than tall — that
    /// is what a 4x5 wall on a 16:9 display gives, and it is the shape a chat transcript reads best in. The feature
    /// is held closer to square so it never becomes the letterbox strip this layout was written to replace.</summary>
    private const double TargetCellAspect = 1.35;
    private const double TargetFeatureAspect = 1.2;

    /// <summary>What a wall of this size looks like: the grid, and the block the orchestrator holds inside it.</summary>
    private readonly record struct WallPlan(int Rows, int Columns, int FeatureRows, int FeatureColumns);

    protected override Size MeasureOverride(Size availableSize)
    {
        // Inside the Bridge surface both dimensions are finite. Fall back to the children's own desired size if
        // this panel is ever put somewhere unconstrained, rather than dividing by infinity.
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        foreach (var (child, rect) in Layout(new Size(width, height)))
            child.Measure(rect.Size);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (child, rect) in Layout(finalSize)) child.Arrange(rect);
        return finalSize;
    }

    /// <summary>Where every visible child goes. Collapsed children are skipped outright — expand-to-focus and
    /// minimize both work by collapsing a pane's container, and a skipped child must not hold a cell open.</summary>
    private IEnumerable<(UIElement Child, Rect Rect)> Layout(Size size)
    {
        var visible = new List<UIElement>();
        foreach (UIElement child in InternalChildren)
            if (child.Visibility != Visibility.Collapsed) visible.Add(child);
        if (visible.Count == 0) yield break;

        var feature = visible.FirstOrDefault(IsFeatured);
        // One pane on screen (expand-to-focus) gets everything; with no orchestrator among the visible panes there
        // is nothing to feature, so fall back to the plain uniform wall the rest of the Bridge uses.
        if (feature is null || visible.Count == 1)
        {
            var plainRows = PlainRows(visible.Count);
            var plainColumns = (int)Math.Ceiling(visible.Count / (double)plainRows);
            var (px, py) = Edges(size, plainRows, plainColumns);
            for (var i = 0; i < visible.Count; i++)
                yield return (visible[i], Cell(px, py, i % plainColumns, i / plainColumns, 1, 1));
            yield break;
        }

        var plan = Plan(visible.Count - 1, Aspect(size));
        var (x, y) = Edges(size, plan.Rows, plan.Columns);
        yield return (feature, Cell(x, y, 0, 0, plan.FeatureColumns, plan.FeatureRows));

        // Row-major over every cell the feature does not already occupy, in roster order, so a worker keeps the
        // same place on the wall for as long as the team lives.
        var next = 0;
        var workers = visible.Where(c => !ReferenceEquals(c, feature)).ToList();
        for (var row = 0; row < plan.Rows && next < workers.Count; row++)
        for (var column = 0; column < plan.Columns && next < workers.Count; column++)
        {
            if (row < plan.FeatureRows && column < plan.FeatureColumns) continue;   // the featured block
            yield return (workers[next++], Cell(x, y, column, row, 1, 1));
        }
    }

    /// <summary>The wall's own shape, used to judge what a cell inside it would look like. Falls back to a wide
    /// display when the panel is measured unconstrained, which is the only time there is nothing to divide.</summary>
    private static double Aspect(Size size) =>
        size.Width > 0 && size.Height > 0 && !double.IsInfinity(size.Width) && !double.IsInfinity(size.Height)
            ? size.Width / size.Height
            : 16.0 / 9.0;

    /// <summary>
    /// The best arrangement for <paramref name="workerCount"/> workers plus the featured pane.
    ///
    /// Every grid up to 4x6 and every feature block up to six cells (2x2, 2x3, 3x2) is a candidate; the winner is the
    /// one with the lowest score, where an empty cell costs far more than an imperfect shape. In practice that means:
    /// fill the wall if any layout can, and among the layouts that do, take the one whose cells look most like a chat
    /// pane. A candidate is only considered when its feature leaves at least one worker column beside it — a feature
    /// that spans every column is the letterbox this class exists to avoid.
    /// </summary>
    private static WallPlan Plan(int workerCount, double aspect)
    {
        var best = default(WallPlan);
        var bestScore = double.MaxValue;
        for (var rows = FeatureSpan; rows <= MaxRows; rows++)
        for (var columns = FeatureSpan + 1; columns <= MaxColumns; columns++)
        for (var featureRows = FeatureSpan; featureRows <= Math.Min(MaxFeatureSpan, rows); featureRows++)
        for (var featureColumns = FeatureSpan;
             featureColumns <= Math.Min(MaxFeatureSpan, columns - 1);
             featureColumns++)
        {
            if (featureRows * featureColumns > MaxFeatureCells) continue;
            var holes = rows * columns - featureRows * featureColumns - workerCount;
            // Negative: the workers do not fit. A hole per column or more means a whole row's worth of dead space,
            // which no shape bonus should ever buy back.
            if (holes < 0 || holes >= columns) continue;
            var cell = aspect * rows / columns;
            var score = 4.0 * holes
                        + Off(cell, TargetCellAspect)
                        + 0.6 * Off(featureRows * featureColumns, FeatureSpan * FeatureSpan)
                        + 0.8 * Off(cell * featureColumns / featureRows, TargetFeatureAspect);
            if (score >= bestScore) continue;
            bestScore = score;
            best = new WallPlan(rows, columns, featureRows, featureColumns);
        }
        if (bestScore < double.MaxValue) return best;

        // More panes than the search can arrange (nothing in Demon Mode gets here, but a wall is drawn from whatever
        // is on screen, so it must never come back empty-handed): widen a full-depth grid until they fit.
        var wide = Math.Max(FeatureSpan + 1, (int)Math.Ceiling((workerCount + FeatureSpan * FeatureSpan) / (double)MaxRows));
        while (MaxRows * wide - FeatureSpan * FeatureSpan < workerCount) wide++;
        return new WallPlan(MaxRows, wide, FeatureSpan, FeatureSpan);
    }

    /// <summary>How far a ratio sits from what it should be, measured multiplicatively so that twice-too-wide and
    /// half-as-wide cost the same. Zero is a perfect match.</summary>
    private static double Off(double actual, double target) => Math.Abs(Math.Log(actual / target));

    /// <summary>Density of the fallback wall — the same rule <c>DualMonitorBridgePolicy.RowsForVisiblePaneCount</c>
    /// applies to an ordinary Bridge, so a Demon wall with nothing to feature looks like every other one.</summary>
    private static int PlainRows(int paneCount) => paneCount switch { <= 2 => 1, <= 6 => 2, _ => 3 };

    /// <summary>The cell boundaries, snapped to whole pixels ONCE so that every cell's right edge is exactly its
    /// neighbour's left edge. Sizing each cell independently (width/columns per pane, as UniformGrid does) leaves a
    /// sub-pixel seam that layout rounding turns into a one-pixel overlap between adjacent panes — two 1px borders
    /// drawn on top of each other, which is visible as a thicker line down some columns and not others.</summary>
    private static (double[] X, double[] Y) Edges(Size size, int rows, int columns)
    {
        var x = new double[columns + 1];
        var y = new double[rows + 1];
        for (var c = 0; c <= columns; c++) x[c] = Math.Round(size.Width * c / columns);
        for (var r = 0; r <= rows; r++) y[r] = Math.Round(size.Height * r / rows);
        return (x, y);
    }

    private static Rect Cell(double[] x, double[] y, int column, int row, int columnSpan, int rowSpan) =>
        new(x[column], y[row],
            Math.Max(0, x[Math.Min(column + columnSpan, x.Length - 1)] - x[column]),
            Math.Max(0, y[Math.Min(row + rowSpan, y.Length - 1)] - y[row]));

    /// <summary>The pane the wall is built around. Read off the bound view-model rather than the child's index, so
    /// the crown cannot drift onto whichever pane happens to be first after a restore or a close.</summary>
    private static bool IsFeatured(UIElement child) =>
        (child as FrameworkElement)?.DataContext is ChatViewModel { IsDemonOrchestrator: true };
}
