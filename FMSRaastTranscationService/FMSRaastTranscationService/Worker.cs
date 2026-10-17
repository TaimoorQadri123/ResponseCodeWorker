using FMSISSTransactionService.Helpers;
using FMSRaastTranscationService.Services;

namespace FMSRaastTranscationService
{
    public class Worker(ServiceRun serviceRun) : BackgroundService
    {
        public static bool is_running;
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // StartServices is synchronous; yield first so host startup is not blocked
            await Task.Yield();
            Logger.Simple($"================== SERVICE STARTED at: {DateTimeOffset.Now} ==============");

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    serviceRun.StartServices();
                    await Task.Delay(ServiceRun.CycleDelay, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown while waiting for the next cycle
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "ExecuteAsync");
                throw;
            }
            finally
            {
                is_running = false;
                Logger.Simple($"================== SERVICE STOPPED at: {DateTimeOffset.Now} ==============");
            }
        }
    }
}
