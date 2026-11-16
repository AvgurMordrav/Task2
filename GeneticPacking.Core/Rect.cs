namespace GeneticPacking.Core;

public class Rect
{
    public int Id { get; }
    public int Width { get; }
    public int Height { get; }
    public int X { get; internal set; }
    public int Y { get; internal set; }

    public Rect(int id, int width, int height, int x = 0, int y = 0)
    {
        Id = id;
        Width = width;
        Height = height;
        X = x;
        Y = y;
    }

    public bool Intersects(Rect other)
    {
        return X < other.X + other.Width && X + Width > other.X &&
               Y < other.Y + other.Height && Y + Height > other.Y;
    }
}
