using Microsoft.Extensions.Options;

namespace DemoGenerator.Runtime;

/// <summary>
/// The live feeder: every <see cref="DemoOptions.FeederIntervalSeconds"/> it writes the minutes since the
/// last tick for every company that has been reset, with the active scenarios applied. Before the first
/// tick it recovers the state of companies reset by an earlier run of the service.
/// </summary>
public sealed class FeederService(DemoEngine engine, IOptions<DemoOptions> options, ILogger<FeederService> logger) : BackgroundService
{
    private volatile bool _running = options.Value.FeederEnabled;

    public bool IsRunning => _running;

    public void Start() => _running = true;

    public void Stop() => _running = false;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.FeederIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!engine.Restored) await engine.RestoreAsync(stoppingToken);
                if (_running)
                {
                    var commands = await engine.FeedOnceAsync(stoppingToken);
                    if (commands > 0) logger.LogInformation("Feeder wrote up to {LastFedAt:o} ({Commands} commands)", engine.LastFedAt, commands);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Feeder tick failed: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
