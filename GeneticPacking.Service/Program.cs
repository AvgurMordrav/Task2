using System.Text.Json;
using System.Threading.Channels;
using GeneticPacking.Core;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/solve", async (SolveRequest request, HttpContext context) =>
{
    if (request.Counts is null || request.Counts.Length != 7 || request.Counts.Any(x => x < 0) ||
        request.Counts.Sum(x => (long)x) is < 1 or > 200 || request.Population is < 2 or > 200 ||
        request.Generations is < 1 or > 2000 || !double.IsFinite(request.Mutation) || request.Mutation is < 0 or > 1 || request.Seed < 0)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Некорректные параметры задачи.");
        return;
    }
    context.Response.ContentType = "application/x-ndjson";
    var channel = Channel.CreateUnbounded<ProgressDto>();
    var token = context.RequestAborted;
    var worker = Task.Run(() =>
    {
        try
        {
            var solver = new GeneticSolver(request.Counts, request.Population, request.Generations, request.Mutation, true, request.Seed, false);
            solver.RunDetailed(token, frame => channel.Writer.TryWrite(ProgressDto.From(frame)));
            channel.Writer.TryComplete();
        }
        catch (Exception ex) { channel.Writer.TryComplete(ex); }
    });
    try
    {
        await foreach (var frame in channel.Reader.ReadAllAsync(token))
        {
            await context.Response.WriteAsync(JsonSerializer.Serialize(frame) + "\n", token);
            await context.Response.Body.FlushAsync(token);
        }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    finally { await worker; }
});
app.Run();
