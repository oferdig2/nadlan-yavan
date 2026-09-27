using Nadlan.KaekImporter.Geometry;

namespace Nadlan.KaekImporter.Import;

/// <summary>The map view the user chose, in the Greek grid.</summary>
public sealed record MapExtent(double Left, double Right, double Top, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
}

/// <summary>
/// The points to "click" over the user's view, in two passes:
/// 1. every 2*step metres;
/// 2. the points in between (every step metres), except where all the surrounding pass-1 points found nothing -
///    that is open sea or empty land, and asking the site about it again only costs time.
/// Points already inside a known shape are skipped by the caller before it asks the site.
/// </summary>
public sealed class SweepGrid
{
    private readonly MapExtent _extent;
    private readonly double _step;
    private readonly int _columns;
    private readonly int _rows;
    private readonly HashSet<(int Col, int Row)> _empty = new();

    public SweepGrid(MapExtent extent, double stepMetres)
    {
        if (stepMetres <= 0) throw new ArgumentOutOfRangeException(nameof(stepMetres));
        _extent = extent;
        _step = stepMetres;
        _columns = Math.Max(1, (int)Math.Floor(extent.Width / stepMetres));
        _rows = Math.Max(1, (int)Math.Floor(extent.Height / stepMetres));
    }

    public int TotalPoints => _columns * _rows;

    /// <summary>Record that the site found nothing at this point.</summary>
    public void MarkEmpty(GridPoint point) => _empty.Add((point.Col, point.Row));

    /// <summary>Pass 1, then pass 2. Pass 2 is decided lazily, so it sees everything pass 1 found.</summary>
    public IEnumerable<GridPoint> Points()
    {
        for (var row = 0; row < _rows; row += 2)
        {
            for (var col = 0; col < _columns; col += 2)
            {
                yield return At(col, row, pass: 1);
            }
        }

        for (var row = 0; row < _rows; row++)
        {
            for (var col = 0; col < _columns; col++)
            {
                if (col % 2 == 0 && row % 2 == 0)
                {
                    continue; // done in pass 1
                }

                if (!SurroundedByEmpty(col, row))
                {
                    yield return At(col, row, pass: 2);
                }
            }
        }
    }

    private bool SurroundedByEmpty(int col, int row)
    {
        var cols = col % 2 == 0 ? new[] { col } : new[] { col - 1, col + 1 };
        var rows = row % 2 == 0 ? new[] { row } : new[] { row - 1, row + 1 };
        foreach (var c in cols)
        {
            foreach (var r in rows)
            {
                // A neighbour past the edge was never asked about, so it proves nothing.
                if (c >= _columns || r >= _rows || !_empty.Contains((c, r)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // Points sit in the middle of their cell, so the outermost ones are half a step inside the view.
    private GridPoint At(int col, int row, int pass) =>
        new(col, row, pass, new EgsaPoint(_extent.Left + (col + 0.5) * _step, _extent.Top - (row + 0.5) * _step));
}

public readonly record struct GridPoint(int Col, int Row, int Pass, EgsaPoint Position);
