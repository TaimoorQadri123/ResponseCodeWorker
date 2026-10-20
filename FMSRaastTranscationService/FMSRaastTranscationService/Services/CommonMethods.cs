
using FMSISSTransactionService.Helpers;
using FMSRaastService.Models;
using FMSRaastTranscationService.Helpers;
using FMSRaastTranscationService.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;

namespace FMSRaastService
{
    public class CommonMethods
    {

        public static string DefaultConnection = string.Empty;
        public static string DataSourceConnection = string.Empty;
        public static string LocalSchema = string.Empty;
        public static string LocalHistorySchema = string.Empty;
        public static string LocalFailedSchema = string.Empty;
        public static bool ApplyTrim = true;

        private static TransactionValidator? _validator;
        private static readonly object _validatorLock = new();

        public CommonMethods(IConfiguration configuration, IOptions<RaastSettings> options)
        {
            // Connection strings in appsettings.json are AES-encrypted
            DefaultConnection = Decryption.DecryptString(configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("DefaultConnection is missing."));

            DataSourceConnection = Decryption.DecryptString(configuration.GetConnectionString("DataSourceConnection")
                ?? throw new InvalidOperationException("DataSourceConnection is missing."));

            var settings = options.Value;
            LocalSchema = settings.LocalSchema;
            LocalHistorySchema = settings.LocalHistorySchema;
            LocalFailedSchema = settings.LocalFailedSchema;
            ApplyTrim = settings.ApplyTrim;
        }

        // Target table schema is read from INFORMATION_SCHEMA once and cached for the service lifetime
        private static TransactionValidator GetValidator()
        {
            if (_validator != null) return _validator;

            lock (_validatorLock)
            {
                _validator ??= TransactionValidator.CreateAsync(DefaultConnection, LocalSchema, applyTrim: ApplyTrim)
                                                   .GetAwaiter().GetResult();
                return _validator;
            }
        }
        public  DataTable GetRules()
        {
            DataSet dataSet = new DataSet();
            try
            {
                using (SqlConnection connection = new SqlConnection(DefaultConnection))
                {
                    connection.Open();
                    string sqlQuery = "SELECT RuleId, RuleName,Description, Status, IsDeleted, Frequency, Days, Hours, Minutes, " +
                        "IsLookBack, IsCumulative, RuleType, OrderOfExecution, AutoClose, Bal_Inq, Diff_CURR " +
                        "FROM Rules WHERE RuleType = 'Raast' AND Status = 'Active' AND IsDeleted = 0 " +
                        "ORDER BY OrderOfExecution DESC ";
                    using (SqlCommand selectCommand = new SqlCommand(sqlQuery, connection))
                    {
                        selectCommand.CommandTimeout = 0;
                        using (SqlDataAdapter sqlDataAdapter = new SqlDataAdapter(selectCommand))

                            sqlDataAdapter.Fill(dataSet, "Rules");

                    }

                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
            return dataSet.Tables["Rules"];
        }

        public  DataTable GetRulesCriterias()
        {
            DataSet dataSet = new DataSet();
            try
            {
                using (SqlConnection connection = new SqlConnection(DefaultConnection))
                {
                    connection.Open();
                    string sqlQuery = "SELECT P.ParameterId, C.CriteriaId, P.FieldType, C.Operator, P.ParameterName, " +
                        "C.Value, C.RuleId, C.CriteriaType " +
                        "FROM Rules R INNER JOIN Criterias C ON R.RuleId = C.RuleId " +
                        "INNER JOIN Parameters P ON C.Parameter = P.ParameterId WHERE R.IsDeleted = 0 " +
                        "AND R.Status = 'Active' AND R.RuleType = 'Raast' AND C.IsDeleted = 0 AND P.IsDeleted = 0 ";

                    using (SqlCommand selectCommand = new SqlCommand(sqlQuery, connection))
                    {
                        selectCommand.CommandTimeout = 0;
                        using (SqlDataAdapter sqlDataAdapter = new SqlDataAdapter(selectCommand))

                            sqlDataAdapter.Fill(dataSet, "RuleCriteria");

                    }

                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
            return dataSet.Tables["RuleCriteria"];
        }

        public  List<RuleViewModel> DataTable_To_List_Rules(DataTable rules, DataTable criterias)
        {
            List<RuleViewModel> templist = new List<RuleViewModel>();
            for (int i = 0; i < rules.Rows.Count; i++)
            {
                RuleViewModel temp = new RuleViewModel();
                List<ParameterCriteriasViewModel> list = new List<ParameterCriteriasViewModel>();
                temp.RuleId = (long)Convert.ToDouble(rules.Rows[i]["RuleId"]);
                temp.RuleName = rules.Rows[i]["RuleName"].ToString();
                temp.Description = rules.Rows[i]["Description"].ToString();
                temp.Status = rules.Rows[i]["Status"].ToString();
                temp.IsDeleted = false;
                temp.Frequency = Convert.ToInt32(rules.Rows[i]["Frequency"]);
                temp.Days = Convert.ToInt32(rules.Rows[i]["Days"]);
                temp.Hours = Convert.ToInt32(rules.Rows[i]["Hours"]);
                temp.Minutes = Convert.ToInt32(rules.Rows[i]["Minutes"]);
                temp.IsLookBack = Convert.ToBoolean(rules.Rows[i]["IsLookBack"]);
                temp.IsCumulative = Convert.ToBoolean(rules.Rows[i]["IsCumulative"]);
                temp.RuleType = rules.Rows[i]["RuleType"].ToString();
                temp.OrderOfExecution = Convert.ToInt32(rules.Rows[i]["OrderOfExecution"]);
                temp.AutoClose = Convert.ToBoolean(rules.Rows[i]["AutoClose"]);
                var dt = criterias.Copy().Select("RuleId = " + temp.RuleId);
                //DataTable dt = criterias.Select("RuleId = " + temp.RuleId).CopyToDataTable();
                if (dt != null && dt.Count() > 0)
                {
                    for (int j = 0; j < dt.Count(); j++)
                    {
                        ParameterCriteriasViewModel tempcriteria = new ParameterCriteriasViewModel();
                        tempcriteria.ParameterId = Convert.ToInt32(dt[j]["ParameterId"]);
                        tempcriteria.CriteriaId = Convert.ToInt32(dt[j]["CriteriaId"]);
                        tempcriteria.FieldType = dt[j]["FieldType"].ToString();
                        tempcriteria.Operator = dt[j]["Operator"].ToString();
                        tempcriteria.ParameterName = dt[j]["ParameterName"].ToString();
                        tempcriteria.Value = dt[j]["Value"].ToString();
                        tempcriteria.RuleId = Convert.ToInt32(dt[j]["RuleId"]);
                        tempcriteria.CriteriaType = dt[j]["CriteriaType"].ToString();
                        list.Add(tempcriteria);
                    }
                }
                temp.ParameterCriteris = list;
                templist.Add(temp);
            }
            return templist;
        }

        public  List<Parameter> Get_Parameters()
        {
            DataSet dataSet = new DataSet();
            List<Parameter> parameters = new List<Parameter>();
            try
            {
                using (SqlConnection connection = new SqlConnection(DefaultConnection))
                {
                    connection.Open();
                    string sqlQuery = "SELECT ParameterId, ParameterName, FieldType, IsDeleted, DataType " +
                        "FROM Parameters WHERE DataType = 'Raast' AND IsDeleted = 0 ";

                    using (SqlCommand selectCommand = new SqlCommand(sqlQuery, connection))
                    {
                        selectCommand.CommandTimeout = 0;
                        using (SqlDataAdapter sqlDataAdapter = new SqlDataAdapter(selectCommand))

                            sqlDataAdapter.Fill(dataSet, "Parameters");

                    }

                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
            for (int index = 0; index < dataSet.Tables["Parameters"].Rows.Count; ++index)

                parameters.Add(new Parameter()
                {
                    ParameterId = (long)Convert.ToInt32(dataSet.Tables["Parameters"].Rows[index]["ParameterId"]),
                    ParameterName = dataSet.Tables["Parameters"].Rows[index]["ParameterName"].ToString(),
                    FieldType = dataSet.Tables["Parameters"].Rows[index]["FieldType"].ToString(),
                    IsDeleted = false,
                    DataType = dataSet.Tables["Parameters"].Rows[index]["DataType"].ToString()
                });

            return parameters;
        }

        public  ModifyPoints GetModifiedPoints()
        {
            ModifyPoints modifiedPoints = new ModifyPoints();
            try
            {
                using (SqlConnection connection = new SqlConnection(DefaultConnection))
                {
                    connection.Open();
                    string sqlQuery = "SELECT TrxLogId2, ModifiedOn, IsSeeded from ModifyPoints WHERE ModifyType = 'Raast' ";

                    using (SqlCommand sqlCommand = new SqlCommand(sqlQuery, connection))
                    {
                        using (SqlDataReader sqlDataReader = sqlCommand.ExecuteReader())
                        {
                            if (sqlDataReader.HasRows)
                            {
                                while (sqlDataReader.Read())
                                {
                                    modifiedPoints.TrxLogId2 = sqlDataReader.GetString(sqlDataReader.GetOrdinal("TrxLogId2"));
                                    modifiedPoints.ModifiedOn = sqlDataReader.GetDateTime(sqlDataReader.GetOrdinal("ModifiedOn"));
                                    modifiedPoints.IsSeeded = sqlDataReader.GetBoolean(sqlDataReader.GetOrdinal("IsSeeded"));
                                    modifiedPoints.ModifyType = "Raast";
                                }
                            }
                        }
                    }

                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
            return modifiedPoints;
        }

        public  TransactionValidator.ValidationResult InsertIntoTempTable(DataTable ds)
        {
            Logger.Simple("Validating transactions dynamically before insert...");
            var result = GetValidator().ValidateAndSplit(ds);
            Logger.Simple($"Dynamic Validation Completed. Valid: {result.ValidCount}, Failed: {result.FailedCount}");
            LogFailedSample(result.FailedTable);

            using (SqlConnection connection = new SqlConnection(DefaultConnection))
            {
                connection.Open();
                using (SqlTransaction externalTransaction = connection.BeginTransaction())
                {
                    try
                    {
                        if (result.ValidCount > 0)
                        {
                            BulkCopy(connection, externalTransaction, result.ValidTable, LocalSchema);
                            BulkCopy(connection, externalTransaction, result.ValidTable, LocalHistorySchema);
                        }

                        if (result.FailedCount > 0)
                            BulkCopy(connection, externalTransaction, result.FailedTable, LocalFailedSchema);

                        using (SqlCommand sqlCommand = new SqlCommand("UPDATE ModifyPoints SET IsSeeded = 1 WHERE ModifyType = 'Raast' ", connection, externalTransaction))
                        {
                            sqlCommand.CommandTimeout = 0;
                            sqlCommand.ExecuteNonQuery();
                        }

                        externalTransaction.Commit();
                    }
                    catch
                    {
                        externalTransaction.Rollback();
                        throw;
                    }
                }
            }

            if (result.ValidCount > 0)
            {
                Logger.Simple($"Inserted Valid Rows Count: {result.ValidCount} -> {LocalSchema}");
                Logger.Simple($"Inserted into History Rows Count  {result.ValidCount} -> {LocalHistorySchema}");
            }
            if (result.FailedCount > 0)
                Logger.Simple($"Inserted Failed Rows Count: {result.FailedCount} -> {LocalFailedSchema}");
            Logger.Simple("Updated ModifyPoints IsSeeded = 1");

            return result;
        }

        // Shows why rows failed without opening the failed table; capped so a bad batch does not flood the log
        private static void LogFailedSample(DataTable failedTable, int maxRows = 5)
        {
            if (failedTable.Rows.Count == 0)
                return;

            bool hasId = failedTable.Columns.Contains("TRANSACTION_LOG_ID");
            var lines = failedTable.AsEnumerable()
                .Take(maxRows)
                .Select(r => (hasId ? $"TRXLOGID {r["TRANSACTION_LOG_ID"]}: " : "") + r["FailedReason"]);

            string more = failedTable.Rows.Count > maxRows ? $"{Environment.NewLine}... and {failedTable.Rows.Count - maxRows} more" : "";
            Logger.Simple("Failed Rows Reason:" + Environment.NewLine + string.Join(Environment.NewLine, lines) + more);
        }

        private static void BulkCopy(SqlConnection connection, SqlTransaction transaction, DataTable table, string destination)
        {
            using (SqlBulkCopy sqlBulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
            {
                sqlBulkCopy.DestinationTableName = destination;
                sqlBulkCopy.BatchSize = 5000;
                sqlBulkCopy.BulkCopyTimeout = 300;

                // Map by column name; ordinal mapping breaks when the Oracle column order differs from the table
                foreach (DataColumn col in table.Columns)
                    sqlBulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);

                sqlBulkCopy.WriteToServer(table);
            }
        }

        public  Int32 GenerateFrequencySuspect(string previous_TRXLOGID)
        {
            Int32 count = 0;
            try
            {
                using (SqlConnection connection = new SqlConnection(DefaultConnection))
                {

                    SqlCommand sqlCommand = new SqlCommand("GENERATE_FREQUENCY_SUSPECTS_RAAST", connection);
                    sqlCommand.CommandType = CommandType.StoredProcedure;
                    sqlCommand.CommandTimeout = 0;
                    sqlCommand.Parameters.Add("@PREVIOUS_TRXLOGID", SqlDbType.VarChar).Value = (object)previous_TRXLOGID;
                    sqlCommand.Parameters.Add("@RECORD_COUNT", SqlDbType.Int);
                    sqlCommand.Parameters["@RECORD_COUNT"].Direction = ParameterDirection.Output;
                    connection.Open();
                    sqlCommand.ExecuteNonQuery();
                    count = Convert.ToInt32(sqlCommand.Parameters["@RECORD_COUNT"].Value);
                    ServiceRun.suspect_count = count;

                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
            return count;
        }

        public  void UpdateModifyPoints(string modifyname, string updated_datetime, string trxlogid)
        {
            try
            {
                using (SqlConnection con = new SqlConnection(DefaultConnection))
                {
                    var command = new SqlCommand("UpdatePoints", con);
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandTimeout = 0;
                    command.Parameters.Add("@modifyname", SqlDbType.VarChar).Value = modifyname;
                    command.Parameters.Add("@updated_datetime", SqlDbType.VarChar).Value = updated_datetime;
                    command.Parameters.Add("@trxlogid", SqlDbType.BigInt).Value = trxlogid;
                    command.Parameters.Add("@isseeded", SqlDbType.Bit).Value = 1;
                    con.Open();
                    int a = command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
        // New Code In RAAST
        public void DeleteFutureTransactions(long trxLogId)
        {
            try
            {
                Logger.Simple(
                    $"Deleting Raast transactions after checkpoint: {trxLogId}");

                string sqlQuery = $@"
            DELETE FROM {LocalSchema}
            WHERE TRANSACTION_LOG_ID > @TrxLogId;

            DELETE FROM {LocalHistorySchema}
            WHERE TRANSACTION_LOG_ID > @TrxLogId;";

                using (SqlConnection connection =
                       new SqlConnection(DefaultConnection))
                {
                    connection.Open();

                    using (SqlCommand command =
                           new SqlCommand(sqlQuery, connection))
                    {
                        command.CommandTimeout = 0;

                        command.Parameters.Add(
                            "@TrxLogId",
                            SqlDbType.BigInt).Value = trxLogId;

                        int deletedRows = command.ExecuteNonQuery();

                        Logger.Simple(
                            $"Raast future transactions deleted. Rows affected: {deletedRows}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "DeleteFutureTransactions");
                throw;
            }
        }
        public async Task<List<string>> GetOldRechecks()
        {
            List<string> recheckTrxLogids = new List<string>();
            DataTable dataTable = new DataTable();

            try
            {
                using (SqlConnection connection = new SqlConnection(DefaultConnection))
                {
                    await connection.OpenAsync();

                    using (SqlCommand command = new SqlCommand(
                        "SELECT DISTINCT TRXLOGID FROM Recheck_OnResponse",
                        connection))
                    {
                        command.CommandTimeout = 30;

                        using (SqlDataAdapter adapter = new SqlDataAdapter(command))
                        {
                            adapter.Fill(dataTable);
                        }
                    }
                }

                foreach (DataRow row in dataTable.Rows)
                {
                    if (row["TRXLOGID"] != DBNull.Value)
                    {
                        recheckTrxLogids.Add(row["TRXLOGID"].ToString()!);
                    }
                }

                Logger.Simple($"Old recheck transactions found: {recheckTrxLogids.Count}");

                return recheckTrxLogids;
            }
            catch (Exception ex)
            {
                Logger.Simple($"GetOldRechecks error: {ex}");
                throw;
            }
        }
        //new code
        public async Task RemoveOldRechecks(int timeoutSeconds)
        {
            try
            {
                using (SqlConnection connection =
                       new SqlConnection(DefaultConnection))
                {
                    await connection.OpenAsync();

                    const string query = @"
                DELETE FROM Recheck_OnResponse
                WHERE TRX_DATETIME <
                    DATEADD(SECOND, -@TimeoutSeconds, GETDATE());";

                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.CommandTimeout = 0;
                        command.Parameters.Add("@TimeoutSeconds", SqlDbType.Int)
                            .Value = timeoutSeconds;

                        int deleted = await command.ExecuteNonQueryAsync();

                        Logger.Simple($"Old rechecks removed: {deleted}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "RemoveOldRechecks");
                throw;
            }
        }

        public async Task<string> RecheckTransactions(
            string selectParams,
            DataTable fetchedTransactions)
        {
            try
            {
                // Purane pending transactions ka response dobara check karein.
                List<string> oldIds = await GetOldRechecks();

                string oldIdsCsv = string.Join(",", oldIds);

                await GetChangeofResponseCode(selectParams, oldIds);

                // Nayi 056 response-code transactions ko recheck queue mein daalein.
                if (fetchedTransactions.Rows.Count > 0 &&
                    fetchedTransactions.Columns.Contains("RESP_CODE") &&
                    fetchedTransactions.Columns.Contains("TRANSACTION_LOG_ID") &&
                    fetchedTransactions.Columns.Contains("TIME_LOC_TRAN_DT"))
                {
                    DataRow[] rows = fetchedTransactions.AsEnumerable()
                        .Where(row =>
                            row["RESP_CODE"] != DBNull.Value &&
                            row["RESP_CODE"].ToString() == "056")
                        .ToArray();

                    if (rows.Length > 0)
                    {
                        await InsertRecheckData(rows.CopyToDataTable());
                    }
                }

                return oldIdsCsv;
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "RecheckTransactions");
                throw;
            }
        }

        public async Task GetChangeofResponseCode(
            string selectParams,
            List<string> transactionIds)
        {
            if (transactionIds.Count == 0)
            {
                Logger.Simple("No old recheck transactions found.");
                return;
            }

            if (string.IsNullOrWhiteSpace(selectParams))
                throw new InvalidOperationException(
                    "No Oracle select parameters available for recheck.");

            try
            {
                using (OracleConnection connection =
                       new OracleConnection(DataSourceConnection))
                {
                    await connection.OpenAsync();

                    // Oracle IN clause ki 1000-item limit se neeche batches.
                    const int batchSize = 900;

                    for (int i = 0; i < transactionIds.Count; i += batchSize)
                    {
                        List<string> batch = transactionIds
                            .Skip(i)
                            .Take(batchSize)
                            .ToList();

                        string placeholders = string.Join(
                            ",",
                            Enumerable.Range(0, batch.Count)
                                .Select(index => $":id{index}"));

                        string query = $@"
                    SELECT DISTINCT {selectParams}
                    FROM {ServiceRun.Schema}
                    WHERE TRAN_CODE IN
                        ('00', '01', '44', '9017', '30', '73', '9011')
                      AND RESP_CODE IN
                        ('000', '001', '004', '014', '024',
                         '025', '060', '094', '997')
                      AND TRANSACTION_LOG_ID IN ({placeholders})
                    ORDER BY TRANSACTION_LOG_ID ASC";

                        using (OracleCommand command =
                               new OracleCommand(query, connection))
                        {
                            command.CommandTimeout = 300;

                            for (int j = 0; j < batch.Count; j++)
                            {
                                command.Parameters.Add(
                                    $"id{j}",
                                    OracleDbType.Int64
                                ).Value = Convert.ToInt64(batch[j]);
                            }

                            DataTable updatedTransactions = new DataTable();

                            using (OracleDataAdapter adapter =
                                   new OracleDataAdapter(command))
                            {
                                adapter.Fill(updatedTransactions);
                            }

                            if (updatedTransactions.Rows.Count > 0)
                            {
                                await UpdateRecheckedTransactions(
                                    updatedTransactions);
                            }
                        }

                        Logger.Simple(
                            $"Recheck batch processed: {(i / batchSize) + 1}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "GetChangeofResponseCode");
                throw;
            }
        }

        public async Task UpdateRecheckedTransactions(DataTable data)
        {
            if (!data.Columns.Contains("TRANSACTION_LOG_ID") ||
                !data.Columns.Contains("RESP_CODE"))
            {
                throw new InvalidOperationException(
                    "Oracle recheck result must contain TRANSACTION_LOG_ID and RESP_CODE.");
            }

            try
            {
                using (SqlConnection connection =
                       new SqlConnection(DefaultConnection))
                {
                    await connection.OpenAsync();

                    using (SqlTransaction transaction =
                           connection.BeginTransaction())
                    {
                        try
                        {
                            const string createQuery = @"
                        CREATE TABLE #RecheckUpdates
                        (
                            TRANSACTION_LOG_ID BIGINT NOT NULL,
                            RESP_CODE VARCHAR(50) NULL
                        );";

                            using (SqlCommand command = new SqlCommand(
                                createQuery, connection, transaction))
                            {
                                await command.ExecuteNonQueryAsync();
                            }

                            DataTable updates = new DataTable();
                            updates.Columns.Add(
                                "TRANSACTION_LOG_ID", typeof(long));
                            updates.Columns.Add("RESP_CODE", typeof(string));

                            foreach (DataRow row in data.Rows)
                            {
                                DataRow newRow = updates.NewRow();
                                newRow["TRANSACTION_LOG_ID"] =
                                    Convert.ToInt64(row["TRANSACTION_LOG_ID"]);

                                newRow["RESP_CODE"] =
                                    row["RESP_CODE"] == DBNull.Value
                                        ? DBNull.Value
                                        : row["RESP_CODE"].ToString();

                                updates.Rows.Add(newRow);
                            }

                            using (SqlBulkCopy bulkCopy = new SqlBulkCopy(
                                connection,
                                SqlBulkCopyOptions.Default,
                                transaction))
                            {
                                bulkCopy.DestinationTableName = "#RecheckUpdates";
                                bulkCopy.ColumnMappings.Add(
                                    "TRANSACTION_LOG_ID", "TRANSACTION_LOG_ID");
                                bulkCopy.ColumnMappings.Add(
                                    "RESP_CODE", "RESP_CODE");

                                await bulkCopy.WriteToServerAsync(updates);
                            }

                            string updateQuery = $@"
                        UPDATE target
                        SET target.RESP_CODE = source.RESP_CODE
                        FROM {CommonMethods.LocalSchema} target
                        INNER JOIN #RecheckUpdates source
                            ON target.TRANSACTION_LOG_ID =
                               source.TRANSACTION_LOG_ID;

                        UPDATE target
                        SET target.RESP_CODE = source.RESP_CODE
                        FROM {CommonMethods.LocalHistorySchema} target
                        INNER JOIN #RecheckUpdates source
                            ON target.TRANSACTION_LOG_ID =
                               source.TRANSACTION_LOG_ID
                        WHERE target.TIME_LOC_TRAN_DT >=
                              DATEADD(HOUR, -2, GETDATE());";

                            using (SqlCommand command = new SqlCommand(
                                updateQuery, connection, transaction))
                            {
                                command.CommandTimeout = 0;
                                await command.ExecuteNonQueryAsync();
                            }

                            const string deleteQuery = @"
                        DELETE target
                        FROM Recheck_OnResponse target
                        INNER JOIN #RecheckUpdates source
                            ON target.TRXLOGID =
                               source.TRANSACTION_LOG_ID;";

                            using (SqlCommand command = new SqlCommand(
                                deleteQuery, connection, transaction))
                            {
                                command.CommandTimeout = 0;
                                await command.ExecuteNonQueryAsync();
                            }

                            transaction.Commit();
                            Logger.Simple(
                                $"Rechecked transactions updated: {updates.Rows.Count}");
                        }
                        catch
                        {
                            transaction.Rollback();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "UpdateRecheckedTransactions");
                throw;
            }
        }

        public async Task InsertRecheckData(DataTable transactions)
        {
            DataTable table = new DataTable();
            table.Columns.Add("TRXLOGID", typeof(long));
            table.Columns.Add("TRX_DATETIME", typeof(DateTime));
            table.Columns.Add("RESP_CODE", typeof(string));

            foreach (DataRow transaction in transactions.Rows)
            {
                DataRow row = table.NewRow();

                row["TRXLOGID"] =
                    Convert.ToInt64(transaction["TRANSACTION_LOG_ID"]);

                row["TRX_DATETIME"] =
                    Convert.ToDateTime(transaction["TIME_LOC_TRAN_DT"]);

                row["RESP_CODE"] =
                    transaction["RESP_CODE"] == DBNull.Value
                        ? DBNull.Value
                        : transaction["RESP_CODE"].ToString();

                table.Rows.Add(row);
            }

            try
            {
                using (SqlConnection connection =
                       new SqlConnection(DefaultConnection))
                {
                    await connection.OpenAsync();

                    using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection))
                    {
                        bulkCopy.DestinationTableName = "Recheck_OnResponsePaysys";
                        bulkCopy.BatchSize = 5000;
                        bulkCopy.BulkCopyTimeout = 0;

                        bulkCopy.ColumnMappings.Add("TRXLOGID", "TRXLOGID");
                        bulkCopy.ColumnMappings.Add(
                            "TRX_DATETIME", "TRX_DATETIME");
                        bulkCopy.ColumnMappings.Add("RESP_CODE", "RESP_CODE");

                        await bulkCopy.WriteToServerAsync(table);
                    }
                }

                Logger.Simple(
                    $"New recheck transactions inserted: {table.Rows.Count}");
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "InsertRecheckData");
                throw;
            }
        }


    }
}