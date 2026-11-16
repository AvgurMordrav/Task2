namespace GeneticPacking.Core;

public class Packing
{
    public IReadOnlyList<Rect> Rectangles { get; }
    public int Width { get; }
    public int Height { get; }
    public long Area => (long)Width * Height;

    public Packing(List<Rect> rectangles)
    {
        Rectangles = rectangles.AsReadOnly();
        Width = rectangles.Max(r => r.X + r.Width);
        Height = rectangles.Max(r => r.Y + r.Height);
    }
}
