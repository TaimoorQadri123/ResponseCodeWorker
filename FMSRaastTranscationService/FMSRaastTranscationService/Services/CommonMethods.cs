
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
    }
}