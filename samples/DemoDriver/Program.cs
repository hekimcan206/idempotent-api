using DemoDriver;

// Usage: DemoDriver http://api-a:8080 http://api-b:8080
var targets = args.Length > 0 ? args : ["http://localhost:8080"];
var clients = targets.Select(t => new HttpClient { BaseAddress = new Uri(t), Timeout = TimeSpan.FromSeconds(30) }).ToList();

foreach (var client in clients)
{
    await WaitUntilReadyAsync(client);
}

Console.WriteLine($"idempotent-api demo against {string.Join(", ", targets)}");
var results = await new DemoScenarios(clients, Console.Out).RunAllAsync();
return results.All(r => r.Passed) ? 0 : 1;

static async Task WaitUntilReadyAsync(HttpClient client)
{
    for (var attempt = 0; attempt < 60; attempt++)
    {
        try
        {
            using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
            if (response.IsSuccessStatusCode)
            {
                return;
            }
        }
        catch (HttpRequestException)
        {
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    throw new TimeoutException($"{client.BaseAddress} did not become ready within 60 s.");
}
