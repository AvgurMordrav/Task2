using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GeneticPacking.Core;

// End-to-end scenarios against a real ASP.NET Core server on a loopback port.
var serviceDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../GeneticPacking.Service/bin/Debug/net10.0/GeneticPacking.Service.dll"));
using var server = Process.Start(new ProcessStartInfo("dotnet", $"\"{serviceDll}\" --urls http://127.0.0.1:19587") { RedirectStandardOutput = true, RedirectStandardError = true })!;
using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:19587"), Timeout = TimeSpan.FromSeconds(20) };
try
{
    bool ready = false;
    for (int i = 0; i < 100 && !ready; i++)
    {
        try { ready = (await client.GetAsync("/health")).IsSuccessStatusCode; }
        catch (HttpRequestException) { await Task.Delay(100); }
    }
    if (!ready) throw new Exception("Сервис не запустился.");
    var request = new SolveRequest(new[] { 8, 0, 0, 5, 0, 0, 3 }, 12, 4, 0.25, 123);
    using var response = await client.PostAsJsonAsync("/solve", request);
    response.EnsureSuccessStatusCode();
    var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    if (lines.Length != 4) throw new Exception("Неверное количество поколений.");
    long previous = long.MaxValue;
    for (int i = 0; i < lines.Length; i++)
    {
        var frame = JsonSerializer.Deserialize<ProgressDto>(lines[i])!;
        var packing = frame.ToPacking();
        if (frame.Generation != i + 1 || packing.Rectangles.Count != 16 || packing.Area > previous ||
            packing.Rectangles.Select(r => r.Id).Distinct().Count() != 16 ||
            packing.Rectangles.Any(a => a.X < 0 || a.Y < 0 || packing.Rectangles.Any(b => a != b && a.Intersects(b))))
            throw new Exception("Некорректный результат упаковки.");
        previous = packing.Area;
    }
    using var invalid = await client.PostAsJsonAsync("/solve", request with { Counts = new int[7] });
    if (invalid.StatusCode != HttpStatusCode.BadRequest) throw new Exception("Сервис не отклонил неверный запрос.");
    using var cancel = new CancellationTokenSource();
    using var msg = new HttpRequestMessage(HttpMethod.Post, "/solve") { Content = JsonContent.Create(request with { Generations = 2000 }) };
    using var streaming = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
    cancel.Cancel();
    using var health = await client.GetAsync("/health");
    health.EnsureSuccessStatusCode();
    Console.WriteLine("Успешно: доступность, поток поколений и корректность упаковки, неверные параметры, разрыв соединения.");
}
finally { if (!server.HasExited) server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
