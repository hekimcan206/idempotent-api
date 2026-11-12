using System.Text.Json;
using BenchmarkDotNet.Running;
using IdempotentApi.Benchmarks;
using Testcontainers.Redis;

// dotnet run -c Release --project benchmarks/IdempotentApi.Benchmarks -- [all|duplicates|wait|overhead|ratelimit|store|micro]
// Redis comes from Testcontainers unless REDIS_CONNECTION is set. Results go to benchmark-results/<scenario>.json.
var scenario = args.FirstOrDefault() ?? "all";
if (scenario == "micro")
{
    BenchmarkSwitcher.FromAssembly(typeof(MicroBenchmarks).Assembly).Run(args[1..]);
    return 0;
}

var outputDir = Path.GetFullPath(Environment.GetEnvironmentVariable("BENCH_OUTPUT") ?? "benchmark-results");
Directory.CreateDirectory(outputDir);

RedisContainer? container = null;
var connection = Environment.GetEnvironmentVariable("REDIS_CONNECTION");
if (string.IsNullOrEmpty(connection))
{
    container = new RedisBuilder("redis:7-alpine").Build();
    await container.StartAsync();
    connection = container.GetConnectionString();
}

try
{
    var scenarios = new Scenarios(connection, Console.Out);
    var machine = Machine.Describe();
    var runs = new Dictionary<string, Func<Task<object>>>
    {
        ["duplicates"] = () => scenarios.DuplicatesAsync(),
        ["wait"] = () => scenarios.WaitModeAsync(),
        ["overhead"] = () => scenarios.OverheadAsync(),
        ["ratelimit"] = () => scenarios.RateLimitAsync(),
        ["store"] = () => scenarios.StoreLatencyAsync(),
    };

    foreach (var (name, run) in runs)
    {
        if (scenario != "all" && scenario != name)
        {
            continue;
        }

        var result = await run();
        var json = JsonSerializer.Serialize(new { scenario = name, machine, result }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(outputDir, name + ".json"), json + "\n");
    }
}
finally
{
    if (container is not null)
    {
        await container.DisposeAsync();
    }
}

return 0;
