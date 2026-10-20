using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ResponseCodeWorker.Data;
using ResponseCodeWorker.Models;

namespace ResponseCodeWorker
{
    // SERVICE 1: Dummy Data 
    public class InsertWorker : BackgroundService
    {
        private readonly ILogger<InsertWorker> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _config;
        private readonly Random _random = new();

        private static readonly string[] Names =
            { "Ali Khan", "Sara Ahmed", "Usman Raza", "Ayesha Malik", "Hamza Shaikh", "Fatima Noor" };
        private static readonly string[] Channels = { "ATM", "POS", "ECOM", "MOBILE" };
        private static readonly string[] Currencies = { "PKR", "USD", "AED" };
        private static readonly string[] Statuses = { "Pending", "Success", "Declined" };
        private static readonly string[] ResponseCodes = { "00", "05", "51", "91", "96" };

        public InsertWorker(ILogger<InsertWorker> logger,
                            IServiceScopeFactory scopeFactory,
                            IConfiguration config)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = _config.GetValue<int>("WorkerSettings:InsertIntervalSeconds", 10);
            var batchSize = _config.GetValue<int>("WorkerSettings:InsertBatchSize", 1000);

            _logger.LogInformation("InsertWorker started. Every {i} seconds, {b} rows (5-6% NULL ResponseCode).", interval, batchSize);

            while (!stoppingToken.IsCancellationRequested)
            { 
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ResponseDbContext>();

                    // 100 mein se 5 se 6 rows ka ResponseCode NULL
                    var nullMin = batchSize * 5 / 100;
                    var nullMax = batchSize * 6 / 100;
                    var nullCount = _random.Next(nullMin, nullMax + 1);

                    // random positions jahan ResponseCode NULL hoga
                    var nullPositions = Enumerable.Range(0, batchSize)
                        .OrderBy(_ => _random.Next())
                        .Take(nullCount)
                        .ToHashSet();

                    var rows = new List<SrcTxn>();
                    for (int i = 0; i < batchSize; i++)
                    {
                        var merchant = $"M{_random.Next(1000, 9999)}";
                        rows.Add(new SrcTxn
                        {
                            CustomerName = Names[_random.Next(Names.Length)],
                            CardNumber = $"4{_random.Next(100, 999)}********{_random.Next(1000, 9999)}",
                            MerchantId = merchant,
                            Channel = Channels[_random.Next(Channels.Length)],
                            Currency = Currencies[_random.Next(Currencies.Length)],
                            Amount = Math.Round((decimal)(_random.NextDouble() * 10000), 2),
                            Status = Statuses[_random.Next(Statuses.Length)],
                            ResponseCode = nullPositions.Contains(i)
                                ? null
                                : ResponseCodes[_random.Next(ResponseCodes.Length)],
                            Description = $"Txn at {merchant}",
                            CreatedAt = DateTime.Now
                        });
                    }

                    db.SrcTxns.AddRange(rows);
                    await db.SaveChangesAsync(stoppingToken);

                    _logger.LogInformation("{n} rows inserted into SrcTxn ({nulls} with NULL ResponseCode).",
                        rows.Count, nullCount);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "InsertWorker: error while inserting.");
                }

                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
            }
        }
    }
}