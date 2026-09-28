// Runs the mock Messages API on its own, for manual testing:
//   dotnet run --project tools/Claudette.MockApi -- [port]
// Then start Claudette or claude with ANTHROPIC_BASE_URL=http://127.0.0.1:<port> and any ANTHROPIC_API_KEY.
using Claudette.MockApi;

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8787;
await using var mock = await MockAnthropicApi.StartAsync(port);
Console.WriteLine($"Mock Messages API on {mock.BaseAddress} - press Ctrl+C to stop.");
var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
await stop.Task;
