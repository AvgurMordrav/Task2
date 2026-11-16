namespace GeneticPacking.Core;

public sealed record VerificationScenario(string Name, int[] Counts, int Population = 40, int Generations = 20)
{
    public static IReadOnlyList<VerificationScenario> All { get; } = new[]
    {
        new VerificationScenario("Тест 1", new[] { 8, 0, 0, 5, 0, 0, 4 }),
        new VerificationScenario("Тест 2", new[] { 12, 0, 0, 9, 0, 0, 4 }),
        new VerificationScenario("Тест 3", new[] { 59, 2, 2, 50, 2, 3, 30 })
    };
    public override string ToString() => Name;
}
