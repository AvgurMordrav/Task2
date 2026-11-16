namespace GeneticPacking.Core;

public sealed record SolveRequest(int[] Counts, int Population, int Generations, double Mutation, int Seed);
public sealed record RectangleDto(int Id, int Width, int Height, int X, int Y);
public sealed record ProgressDto(int Generation, double MeanArea, RectangleDto[] Rectangles)
{
    public static ProgressDto From(GenerationSnapshot snapshot) => new(snapshot.Generation, snapshot.MeanArea,
        snapshot.BestSoFar.Rectangles.Select(r => new RectangleDto(r.Id, r.Width, r.Height, r.X, r.Y)).ToArray());
    public Packing ToPacking() => new(Rectangles.Select(r => new Rect(r.Id, r.Width, r.Height, r.X, r.Y)).ToList());
}
