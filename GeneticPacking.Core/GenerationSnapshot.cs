namespace GeneticPacking.Core;

public sealed record GenerationSnapshot(int Generation, Packing Best, Packing BestSoFar,
    double MeanArea, double? MeanErrorPercent, TimeSpan ComputeElapsed);
