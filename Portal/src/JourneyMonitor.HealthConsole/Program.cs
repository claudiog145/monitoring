using JourneyMonitor.HealthConsole;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => e.Cancel = HealthConsoleApp.HandleCancelKeyPress(args, cancellation);

try
{
    return await HealthConsoleApp.RunAsync(args, Console.Out, services: null, delay: null, cancellation.Token);
}
catch (Exception ex)
{
    // Connectivity/auth failures from the stores: report them without a stack trace.
    Console.Error.WriteLine($"Error: {ex.GetType().Name}: {ex.Message}");
    return 3;
}
