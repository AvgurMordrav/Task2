using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GeneticPacking.Core;
using System.Diagnostics;
using System.Threading.Channels;
using System.Net.Http.Json;
using System.Text.Json;

namespace GeneticPacking.App;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;
    private Packing? _best;
    private bool _closed;
    private List<GenerationSnapshot> _history = new();
    private int _visibleFrames;
    private int _plannedGenerations;
    private string _activeMode = "";
    private static readonly string[] CountNames = { "Count11", "Count12", "Count21", "Count22", "Count13", "Count31", "Count33" };
    private sealed record Inputs(int[] Counts, int Population, int Generations, double Mutation, int Seed);

    private VerificationScenario? Scenario => ScenarioBox.SelectedIndex > 0
        ? VerificationScenario.All[ScenarioBox.SelectedIndex - 1] : null;

    public MainWindow()
    {
        InitializeComponent();
        ScenarioText.Text = "Выберите тестовый набор или задайте свои количества. Все параметры редактируются вручную.";
    }

    private void Scenario_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (ScenarioText is null) return;
        var scenario = Scenario;
        ScenarioText.Text = scenario is null ? "Свои параметры: значения задаются вручную." :
            $"{scenario.Name}: популяция 40, поколений 20. Порядок типов: 1×1, 1×2, 2×1, 2×2, 1×3, 3×1, 3×3.";
        PopulationBox.IsEnabled = scenario is null;
        GenerationsBox.IsEnabled = scenario is null;
        if (scenario is null) return;
        for (int i = 0; i < CountNames.Length; i++)
            this.FindControl<NumericUpDown>(CountNames[i])!.Value = scenario.Counts[i];
        PopulationBox.Value = scenario.Population;
        GenerationsBox.Value = scenario.Generations;
    }

    private decimal Read(string name) => this.FindControl<NumericUpDown>(name)!.Value
        ?? throw new ArgumentException("Заполните все числовые поля.");

    private int ReadInt(string name)
    {
        decimal value = Read(name);
        if (value != decimal.Truncate(value))
            throw new ArgumentException("Количество фигур, популяция, эпохи и seed должны быть целыми.");
        return checked((int)value);
    }

    private Inputs ReadInputs()
    {
        int[] counts = CountNames.Select(ReadInt).ToArray();
        if (counts.Any(n => n < 0) || counts.Sum() < 1 || counts.Sum() > 200)
            throw new ArgumentException("Общее количество фигур должно быть от 1 до 200.");
        int population = Scenario?.Population ?? ReadInt("PopulationBox");
        int generations = Scenario?.Generations ?? ReadInt("GenerationsBox");
        int seed = ReadInt("SeedBox");
        double mutation = (double)Read("MutationBox");
        if (population < 2 || population > 200 || generations < 1 || generations > 2000 ||
            mutation < 0 || mutation > 1 || seed < 0)
            throw new ArgumentException("Проверьте диапазоны параметров.");
        return new(counts, population, generations, mutation, seed);
    }

    private void SetBusy(bool busy)
    {
        InputPanel.IsEnabled = !busy;
        StartButton.IsEnabled = !busy;
        PopulationBox.IsEnabled = !busy && Scenario is null;
        GenerationsBox.IsEnabled = !busy && Scenario is null;
        StopButton.IsEnabled = busy;
    }

    private GeneticSolver Solver(Inputs input, bool parallel, int? generations = null) =>
        new(input.Counts, input.Population, generations ?? input.Generations, input.Mutation, parallel, input.Seed, false);

    private async void Start_Click(object? sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        CancellationTokenSource? source = null;
        try
        {
            var input = ReadInputs();
            int mode = ModeBox.SelectedIndex;
            const int frameDelay = 1000;
            var serviceAddresses = (ServicesBox.Text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (mode == 3 && (serviceAddresses.Length == 0 || serviceAddresses.Any(a => !Uri.TryCreate(a, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))))
                throw new ArgumentException("Укажите HTTP-адреса сервисов, каждый с новой строки.");
            source = new CancellationTokenSource();
            _cts = source;
            SetBusy(true);
            _history = new();
            _visibleFrames = 0;
            _best = null;
            Draw(); DrawHistory();
            TimingText.Text = "";
            double? serialSeconds = null, parallelSeconds = null;
            if (mode == 3)
            {
                await RunDistributed(input, serviceAddresses, source.Token);
                return;
            }
            if (mode == 2)
            {
                await Task.Run(() =>
                {
                    Solver(input, false, 1).RunDetailed(source.Token, _ => { });
                    Solver(input, true, 1).RunDetailed(source.Token, _ => { });
                });
                source.Token.ThrowIfCancellationRequested();
            }
            void UpdateTiming()
            {
                TimingText.Text = $"Последовательно: {(serialSeconds.HasValue ? serialSeconds.Value.ToString("F3") + " с" : "—")}\n" +
                    $"Параллельно: {(parallelSeconds.HasValue ? parallelSeconds.Value.ToString("F3") + " с" : "—")}";
            }
            foreach (bool parallel in mode == 2 ? new[] { false, true } : new[] { mode == 1 })
            {
                source.Token.ThrowIfCancellationRequested();
                double seconds = await CalculateAndShow(input, parallel, frameDelay, source, elapsed =>
                {
                    if (parallel) parallelSeconds = elapsed; else serialSeconds = elapsed;
                    UpdateTiming();
                });
                if (parallel) parallelSeconds = seconds; else serialSeconds = seconds;
                UpdateTiming();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) await ShowError(ex.Message); }
        finally
        {
            source?.Dispose(); _cts = null;
            if (!_closed) SetBusy(false);
        }
    }

    private async Task RunDistributed(Inputs input, string[] addresses, CancellationToken token)
    {
        _activeMode = "Распределённый";
        _plannedGenerations = checked(input.Generations * addresses.Length);
        _history = new(); _visibleFrames = 0;
        int completed = 0;
        var errors = new List<string>();
        var frames = Channel.CreateUnbounded<(string Address, ProgressDto Progress)>(
            new UnboundedChannelOptions { SingleReader = true });
        using var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        async Task RunOne(string address, int index)
        {
            try
            {
                var request = new SolveRequest(input.Counts, input.Population, input.Generations, input.Mutation,
                    (int)(((long)input.Seed + index) % (int.MaxValue + 1L)));
                using var message = new HttpRequestMessage(HttpMethod.Post, address.TrimEnd('/') + "/solve") { Content = JsonContent.Create(request) };
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
                string? line;
                int lastGeneration = 0;
                while ((line = await reader.ReadLineAsync(token)) is not null)
                {
                    var progress = JsonSerializer.Deserialize<ProgressDto>(line) ?? throw new InvalidDataException("Пустой ответ сервиса.");
                    lastGeneration = progress.Generation;
                    await frames.Writer.WriteAsync((address, progress), token);
                }
                if (lastGeneration != input.Generations) throw new InvalidDataException("Соединение закрыто до завершения вычислений.");
                completed++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { errors.Add($"{address}: {ex.Message}"); }
        }
        async Task ReceiveAll()
        {
            try { await Task.WhenAll(addresses.Select(RunOne)); }
            finally { frames.Writer.TryComplete(); }
        }
        Task receiving = ReceiveAll();
        try
        {
            await foreach (var (address, progress) in frames.Reader.ReadAllAsync(token))
            {
                var packing = progress.ToPacking();
                if (_best is null || packing.Area < _best.Area) _best = packing;
                // The horizontal axis counts displayed frames, because services can
                // report the same generation in a different order.
                var frame = new GenerationSnapshot(_history.Count + 1, _best, _best, progress.MeanArea, null, TimeSpan.Zero);
                _history.Add(frame); _visibleFrames = _history.Count;
                Draw(); DrawHistory();
                TimingText.Text = $"{address}: поколение {progress.Generation}/{input.Generations}; лучшая площадь: {_best.Area}";
                await WaitForFrame(1000, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await receiving; }
        if (token.IsCancellationRequested) { TimingText.Text = "Остановлено. Последний показанный результат сохранён."; return; }
        TimingText.Text = $"Завершено сервисов: {completed}/{addresses.Length}. Лучший результат: {(_best?.Area.ToString() ?? "нет")}" +
            (errors.Count > 0 ? "\nОшибки:\n" + string.Join("\n", errors) : "");
    }

    // The producer computes independently of the display delay, so timings
    // measure computation rather than animation. No epoch is dropped or skipped.
    private async Task<double> CalculateAndShow(Inputs input, bool parallel, int frameDelay, CancellationTokenSource source,
        Action<double> measured)
    {
        _activeMode = parallel ? "Параллельный" : "Последовательный";
        _plannedGenerations = input.Generations;
        _history = new();
        _visibleFrames = 0;
        DrawHistory();
        var channel = Channel.CreateUnbounded<GenerationSnapshot>(new UnboundedChannelOptions
        { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
        Task<double> computation = Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            try
            {
                Solver(input, parallel).RunDetailed(source.Token,
                    frame => channel.Writer.TryWrite(frame));
                return watch.Elapsed.TotalSeconds;
            }
            catch (Exception ex) { channel.Writer.TryComplete(ex); throw; }
            finally { channel.Writer.TryComplete(); }
        });
        bool timingShown = false;
        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(source.Token))
            {
                if (_closed) { source.Cancel(); source.Token.ThrowIfCancellationRequested(); }
                _history.Add(frame);
                _visibleFrames = _history.Count;
                ShowFrame(frame);
                if (computation.IsCompletedSuccessfully && !timingShown)
                {
                    measured(computation.Result); timingShown = true;
                }
                await WaitForFrame(frameDelay, source.Token);
            }
            double seconds = await computation;
            measured(seconds);
            source.Token.ThrowIfCancellationRequested();
            return seconds;
        }
        finally
        {
            // Always join the worker, including stop/close and rendering errors.
            if (!computation.IsCompleted) source.Cancel();
            await computation;
        }
    }

    private static async Task WaitForFrame(int delayMilliseconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (delayMilliseconds > 0)
            await Task.Delay(delayMilliseconds, token);
        else
            // Yield to rendering and input without introducing a timed delay.
            // Even a precomputed frame queue stays responsive to the Stop button.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        token.ThrowIfCancellationRequested();
    }

    private void ShowFrame(GenerationSnapshot frame)
    {
        _best = frame.Best;
        Draw(); DrawHistory();
    }

    private async Task ShowError(string message)
    {
        var close = new Button { Content = "Закрыть", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var dialog = new Window
        {
            Title = "Ошибка", Width = 420, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 15,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, close }
            }
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private void History_SizeChanged(object? sender, SizeChangedEventArgs e) => DrawHistory();

    private void DrawHistory()
    {
        if (HistoryCanvas is null) return;
        HistoryCanvas.Children.Clear();
        var frames = _history.Take(_visibleFrames).ToList();
        if (frames.Count == 0 || HistoryCanvas.Bounds.Width < 100 || HistoryCanvas.Bounds.Height < 70) return;
        double minimum = Math.Max(0, Math.Floor(_history.Min(f => (double)f.BestSoFar.Area) * 0.95));
        double maximum = Math.Max(minimum + 1, Math.Ceiling(_history.Max(f => f.MeanArea) * 1.05));
        double w = HistoryCanvas.Bounds.Width - 100, h = HistoryCanvas.Bounds.Height - 55;
        double X(int generation) => 65 + (generation - 1) * w / Math.Max(1, _plannedGenerations - 1);
        double Y(double area) => 15 + h * (1 - (area - minimum) / (maximum - minimum));
        void Label(string text, double x, double y)
        {
            var label = new TextBlock { Text = text, FontSize = 11 };
            Canvas.SetLeft(label, x); Canvas.SetTop(label, y); HistoryCanvas.Children.Add(label);
        }
        for (int tick = 0; tick <= 3; tick++)
        {
            double area = minimum + (maximum - minimum) * tick / 3;
            HistoryCanvas.Children.Add(new Line { StartPoint = new Point(65, Y(area)),
                EndPoint = new Point(65 + w, Y(area)), Stroke = Brushes.LightGray, StrokeThickness = 1 });
            Label(area.ToString("F1"), 2, Y(area) - 7);
        }
        foreach (bool mean in new[] { true, false })
        {
            var points = new Avalonia.Collections.AvaloniaList<Point>();
            foreach (var f in frames)
                points.Add(new Point(X(f.Generation), Y(mean ? f.MeanArea : f.BestSoFar.Area)));
            HistoryCanvas.Children.Add(new Polyline { Points = points, Stroke = mean ? Brushes.Red : Brushes.Blue, StrokeThickness = 2 });
            var dot = new Ellipse { Width = 6, Height = 6, Fill = mean ? Brushes.Red : Brushes.Blue };
            Canvas.SetLeft(dot, points[^1].X - 3); Canvas.SetTop(dot, points[^1].Y - 3);
            HistoryCanvas.Children.Add(dot);
        }
        Label("1", 65, h + 18);
        Label(_plannedGenerations.ToString(), 65 + w - 15, h + 18);
        Label($"Площадь; {_activeMode}; {(_activeMode == "Распределённый" ? "кадр" : "эпоха")} {frames[^1].Generation}/{_plannedGenerations}", 65, h + 34);
    }

    private void Stop_Click(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel(); StopButton.IsEnabled = false;
    }
    protected override void OnClosed(EventArgs e)
    {
        _closed = true; _cts?.Cancel(); base.OnClosed(e);
    }

    private void Draw()
    {
        PackingCanvas.Children.Clear();
        if (_best is null || PackingCanvas.Bounds.Width < 60 || PackingCanvas.Bounds.Height < 60)
            return;

        double scale = Math.Min((PackingCanvas.Bounds.Width - 40) / _best.Width,
                                (PackingCanvas.Bounds.Height - 40) / _best.Height);
        double bottom = PackingCanvas.Bounds.Height - 20;
        string[] colors = { "#9DD6FA", "#B9E7A5", "#F5D98B", "#E5B8ED", "#F5B1B1", "#83D6BE", "#F3A668" };
        var frame = new Rectangle
        {
            Width = _best.Width * scale,
            Height = _best.Height * scale,
            Stroke = Brushes.Black,
            StrokeThickness = 2
        };
        Canvas.SetLeft(frame, 20);
        Canvas.SetTop(frame, bottom - frame.Height);
        PackingCanvas.Children.Add(frame);

        foreach (var rect in _best.Rectangles)
        {
            var shape = new Rectangle
            {
                Width = rect.Width * scale,
                Height = rect.Height * scale,
                Fill = new SolidColorBrush(Color.Parse(colors[rect.Id % colors.Length])),
                Stroke = Brushes.Black,
                StrokeThickness = 1
            };
            Canvas.SetLeft(shape, 20 + rect.X * scale);
            Canvas.SetTop(shape, bottom - (rect.Y + rect.Height) * scale);
            PackingCanvas.Children.Add(shape);
        }
    }

    private void Canvas_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        Draw();
    }
}
