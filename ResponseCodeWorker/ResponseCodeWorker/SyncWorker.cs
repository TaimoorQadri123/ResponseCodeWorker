using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ResponseCodeWorker.Data;
using ResponseCodeWorker.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ResponseCodeWorker
{
    public class SyncWorker : BackgroundService
    {
        private readonly ILogger<SyncWorker> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _config;

        public SyncWorker(ILogger<SyncWorker> logger,
                          IServiceScopeFactory scopeFactory,
                          IConfiguration config)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = _config.GetValue<int>("WorkerSettings:SyncIntervalSeconds", 10);
            var batchSize = _config.GetValue<int>("WorkerSettings:SyncBatchSize", 5000);

            _logger.LogInformation("SyncWorker started. Every {i} seconds, max {b} rows per round.", interval, batchSize);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SyncOnceAsync(batchSize, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "SyncWorker: error during sync.");
                }

                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
            }
        }

        private async Task SyncOnceAsync(int batchSize, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ResponseDbContext>();

            // 1)Read Checkpoint (modify)
            var checkpoint = await db.ModifyPoints.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);

            if (checkpoint == null)
            {
                checkpoint = new ModifyPoint { TranId = 0, ModifyOn = DateTime.Now };
                db.ModifyPoints.Add(checkpoint);
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("First run: ModifyPoint created with TranId = 0.");
            }

            var lastTranId = checkpoint.TranId;

            // 2) data from Source 
            var newRows = await db.SrcTxns
                .AsNoTracking()
                .Where(x => x.TranId > lastTranId)
                .OrderBy(x => x.TranId)
                .Take(batchSize)
                .ToListAsync(ct);

            if (newRows.Count == 0)
            {
                _logger.LogInformation("Nothing new (last TranId = {id}).", lastTranId);
                return;
            }

            // 3) divide in two partition 
            var validRows = newRows.Where(x => x.ResponseCode != null).ToList();
            var failedRows = newRows.Where(x => x.ResponseCode == null).ToList();

            var validTable = ToLocalTable(validRows);
            var failedTable = ToFailedTable(failedRows);

            // 4) Ek transaction: dono bulk copy + checkpoint
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var connection = (SqlConnection)db.Database.GetDbConnection();
            var sqlTx = (SqlTransaction)tx.GetDbTransaction();

            // ---- LocalTxn (ResponseCode NOT NULL) ----
            if (validTable.Rows.Count > 0)
            {
                using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, sqlTx)
                {
                    DestinationTableName = "LocalTxn",
                    BatchSize = 1000,
                    BulkCopyTimeout = 60
                };

                // (DataTable ka column, LocalTxn ka column)
                bulk.ColumnMappings.Add("TranId", "TransactionId");
                bulk.ColumnMappings.Add("CustomerName", "CustName");
                bulk.ColumnMappings.Add("CardNumber", "CardNo");
                bulk.ColumnMappings.Add("MerchantId", "MerchId");
                bulk.ColumnMappings.Add("Channel", "TranChannel");
                bulk.ColumnMappings.Add("Currency", "CurrencyCode");
                bulk.ColumnMappings.Add("Amount", "TranAmount");
                bulk.ColumnMappings.Add("Status", "TranStatus");
                bulk.ColumnMappings.Add("ResponseCode", "RespCode");
                bulk.ColumnMappings.Add("Description", "TranDesc");
                bulk.ColumnMappings.Add("CreatedAt", "SourceCreatedOn");

                await bulk.WriteToServerAsync(validTable, ct);
            }

            // ---- FailedTxn (ResponseCode NULL) ----
            if (failedTable.Rows.Count > 0)
            {
                using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, sqlTx)
                {
                    DestinationTableName = "FailedTxn",
                    BatchSize = 1000,
                    BulkCopyTimeout = 60
                };

                // (DataTable ka column, FailedTxn ka column)
                bulk.ColumnMappings.Add("TranId", "SrcTranId");
                bulk.ColumnMappings.Add("CustomerName", "FCustName");
                bulk.ColumnMappings.Add("CardNumber", "FCardNo");
                bulk.ColumnMappings.Add("MerchantId", "FMerchId");
                bulk.ColumnMappings.Add("Channel", "FChannel");
                bulk.ColumnMappings.Add("Currency", "FCurrency");
                bulk.ColumnMappings.Add("Amount", "FAmount");
                bulk.ColumnMappings.Add("Status", "FStatus");
                bulk.ColumnMappings.Add("ResponseCode", "FResponseCode");
                bulk.ColumnMappings.Add("Description", "FDesc");
                bulk.ColumnMappings.Add("CreatedAt", "FSourceCreatedOn");
                bulk.ColumnMappings.Add("FailureReason", "FailureReason");

                await bulk.WriteToServerAsync(failedTable, ct);
            }

            // 5) Checkpoint = fetch hui SAARI rows (valid + failed) ki sabse bari TranId
            checkpoint.TranId = newRows.Max(x => x.TranId);
            checkpoint.ModifyOn = DateTime.Now;
            await db.SaveChangesAsync(ct);

            // 6) Sab theek to commit, warna rollback
            await tx.CommitAsync(ct);

            _logger.LogInformation("Synced {n} rows: {v} to Local, {f} to Failed (TranId {from} to {to}). Checkpoint updated.",
                newRows.Count, validRows.Count, failedRows.Count, lastTranId + 1, checkpoint.TranId);
        }

        private static DataTable ToLocalTable(List<SrcTxn> rows)
        {
            var t = new DataTable();
            t.Columns.Add("TranId", typeof(int));
            t.Columns.Add("CustomerName", typeof(string));
            t.Columns.Add("CardNumber", typeof(string));
            t.Columns.Add("MerchantId", typeof(string));
            t.Columns.Add("Channel", typeof(string));
            t.Columns.Add("Currency", typeof(string));
            t.Columns.Add("Amount", typeof(decimal));
            t.Columns.Add("Status", typeof(string));
            t.Columns.Add("ResponseCode", typeof(string));
            t.Columns.Add("Description", typeof(string));
            t.Columns.Add("CreatedAt", typeof(DateTime));

            foreach (var r in rows)
            {
                t.Rows.Add(r.TranId, r.CustomerName, r.CardNumber, r.MerchantId, r.Channel,
                    r.Currency, r.Amount, r.Status, r.ResponseCode,
                    (object?)r.Description ?? DBNull.Value, r.CreatedAt);
            }
            return t;
        }

        private static DataTable ToFailedTable(List<SrcTxn> rows)
        {
            var t = new DataTable();
            t.Columns.Add("TranId", typeof(int));
            t.Columns.Add("CustomerName", typeof(string));
            t.Columns.Add("CardNumber", typeof(string));
            t.Columns.Add("MerchantId", typeof(string));
            t.Columns.Add("Channel", typeof(string));
            t.Columns.Add("Currency", typeof(string));
            t.Columns.Add("Amount", typeof(decimal));
            t.Columns.Add("Status", typeof(string));
            t.Columns.Add("ResponseCode", typeof(string));
            t.Columns.Add("Description", typeof(string));
            t.Columns.Add("CreatedAt", typeof(DateTime));
            t.Columns.Add("FailureReason", typeof(string));

            foreach (var r in rows)
            {
                t.Rows.Add(r.TranId, r.CustomerName, r.CardNumber, r.MerchantId, r.Channel,
                    r.Currency, r.Amount, r.Status,
                    DBNull.Value,                             
                    (object?)r.Description ?? DBNull.Value,
                    r.CreatedAt,
                    "ResponseCode is null");                  
            }
            return t;
        }
    }
}