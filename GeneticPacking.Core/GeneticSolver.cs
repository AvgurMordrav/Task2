namespace GeneticPacking.Core;

public class GeneticSolver
{
    private readonly List<Rect> _shapes = new();
    private readonly int _populationSize;
    private readonly int _generations;
    private readonly double _mutationRate;
    private readonly Random _random;
    private readonly bool _parallel;
    private readonly bool _stopAtOptimum;

    private class Individual
    {
        public int[] Order { get; }
        public Packing? Result { get; set; }

        public Individual(int[] order)
        {
            Order = order;
        }
    }

    public GeneticSolver(int[] counts, int populationSize, int generations, double mutationRate, bool parallel = true, int? seed = null, bool stopAtOptimum = true)
    {
        if (counts is null || counts.Length != 7 || counts.Any(n => n < 0) || counts.Sum(n => (long)n) == 0)
            throw new ArgumentException("Нужны семь неотрицательных количеств и хотя бы одна фигура.");
        if (populationSize < 2 || generations < 1)
            throw new ArgumentException("Популяция должна быть не меньше 2, поколений — не меньше 1.");
        if (!double.IsFinite(mutationRate) || mutationRate < 0 || mutationRate > 1)
            throw new ArgumentException("Вероятность мутации должна быть от 0 до 1.");

        int[] widths = { 1, 1, 2, 2, 1, 3, 3 };
        int[] heights = { 1, 2, 1, 2, 3, 1, 3 };
        for (int type = 0; type < 7; type++)
            for (int i = 0; i < counts[type]; i++)
                _shapes.Add(new Rect(_shapes.Count, widths[type], heights[type]));

        _random = seed.HasValue ? new Random(seed.Value) : new Random();
        _parallel = parallel;
        _stopAtOptimum = stopAtOptimum;
        _populationSize = populationSize;
        _generations = generations;
        _mutationRate = mutationRate;
    }

    public Packing Run(CancellationToken token, Action<int, Packing> report)
        => RunDetailed(token, frame => report(frame.Generation, frame.Best));

    public Packing RunDetailed(CancellationToken token, Action<GenerationSnapshot> report, long? optimum = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var row = new List<Rect>();
        int x = 0;
        foreach (Rect shape in _shapes)
        {
            row.Add(new Rect(shape.Id, shape.Width, shape.Height, x, 0));
            x += shape.Width;
        }
        Packing best = new Packing(row);
        long totalArea = _shapes.Sum(r => (long)r.Width * r.Height);
        // Generation zero is a baseline, not an evaluated population.

        var population = new List<Individual>();
        for (int i = 0; i < _populationSize; i++)
        {
            if (token.IsCancellationRequested)
                return best;
            int[] order = Enumerable.Range(0, _shapes.Count).ToArray();
            for (int j = order.Length - 1; j > 0; j--)
            {
                int k = _random.Next(j + 1);
                (order[j], order[k]) = (order[k], order[j]);
            }
            population.Add(new Individual(order));
        }

        for (int generation = 1; generation <= _generations; generation++)
        {
            void Evaluate(Individual individual)
            {
                if (!token.IsCancellationRequested && individual.Result is null)
                    individual.Result = Pack(individual.Order, token);
            }
            if (_parallel)
                Parallel.ForEach(population, Evaluate);
            else
                foreach (var individual in population)
                {
                    if (token.IsCancellationRequested) break;
                    Evaluate(individual);
                }

            foreach (Individual individual in population)
                if (individual.Result is not null && individual.Result.Area < best.Area)
                    best = individual.Result;

            bool complete = population.All(i => i.Result is not null);
            if (complete)
            {
                var generationBest = population.MinBy(i => i.Result!.Area)!.Result!;
                double mean = population.Average(i => (double)i.Result!.Area);
                report(new GenerationSnapshot(generation, generationBest, best, mean,
                    optimum.HasValue ? 100.0 * (mean - optimum.Value) / optimum.Value : null, watch.Elapsed));
            }
            if (token.IsCancellationRequested || (_stopAtOptimum && best.Area == totalArea) || generation == _generations)
                break;

            population.Sort((a, b) => a.Result!.Area.CompareTo(b.Result!.Area));
            var next = new List<Individual> { population[0] };
            while (next.Count < _populationSize)
            {
                if (token.IsCancellationRequested)
                    return best;
                int[] order = Crossover(Select(population).Order, Select(population).Order);
                if (order.Length > 1 && _random.NextDouble() < _mutationRate)
                {
                    int a = _random.Next(order.Length);
                    int b = (a + 1 + _random.Next(order.Length - 1)) % order.Length;
                    (order[a], order[b]) = (order[b], order[a]);
                }
                next.Add(new Individual(order));
            }
            population = next;
        }
        return best;
    }

    private Individual Select(List<Individual> population)
    {
        Individual a = population[_random.Next(population.Count)];
        Individual b = population[_random.Next(population.Count)];
        return a.Result!.Area <= b.Result!.Area ? a : b;
    }

    private int[] Crossover(int[] first, int[] second)
    {
        int cut = _random.Next(1, first.Length + 1);
        var order = first.Take(cut).ToList();
        foreach (int id in second)
            if (!order.Contains(id))
                order.Add(id);
        return order.ToArray();
    }

    private Packing? Pack(int[] order, CancellationToken token)
    {
        var placed = new List<Rect>();
        var xs = new SortedSet<int> { 0 };
        var ys = new SortedSet<int> { 0 };
        int width = 0;
        int height = 0;

        foreach (int id in order)
        {
            Rect shape = _shapes[id];
            Rect? best = null;
            long bestArea = long.MaxValue;
            foreach (int y in ys)
                foreach (int x in xs)
                {
                    if (token.IsCancellationRequested)
                        return null;
                    var candidate = new Rect(id, shape.Width, shape.Height, x, y);
                    if (placed.Any(r => candidate.Intersects(r)))
                        continue;
                    long area = (long)Math.Max(width, x + shape.Width) * Math.Max(height, y + shape.Height);
                    if (area < bestArea)
                    {
                        bestArea = area;
                        best = candidate;
                    }
                }
            placed.Add(best!);
            width = Math.Max(width, best!.X + best.Width);
            height = Math.Max(height, best.Y + best.Height);
            xs.Add(best.X + best.Width);
            ys.Add(best.Y + best.Height);
        }

        bool changed;
        do
        {
            changed = false;
            foreach (Rect rect in placed)
            {
                while (rect.Y > 0)
                {
                    if (token.IsCancellationRequested)
                        return null;
                    rect.Y--;
                    if (placed.Any(r => r != rect && rect.Intersects(r)))
                    {
                        rect.Y++;
                        break;
                    }
                    changed = true;
                }
                while (rect.X > 0)
                {
                    if (token.IsCancellationRequested)
                        return null;
                    rect.X--;
                    if (placed.Any(r => r != rect && rect.Intersects(r)))
                    {
                        rect.X++;
                        break;
                    }
                    changed = true;
                }
            }
        } while (changed);

        return token.IsCancellationRequested ? null : new Packing(placed);
    }
}
