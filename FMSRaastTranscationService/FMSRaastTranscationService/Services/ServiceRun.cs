using FMSISSTransactionService.Helpers;
using FMSRaastService;
using FMSRaastService.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace FMSRaastTranscationService.Services
{
    public  class ServiceRun
    {
        public static string Schema = string.Empty;
        public static string Tran_Count = string.Empty;
        public static int CycleDelay;
        public static string LogPath = string.Empty;
        private readonly CommonMethods _commonMethods;
        public  string DefaultConnection = string.Empty;
        public  string DataSourceConnection = string.Empty;

        public DataTable completeTrans = new DataTable();
        public DataTable latestTrans = new DataTable();
        public DataTable rules = new DataTable();
        public DataTable criterias = new DataTable();
        public string previous_datetime;
        public string previous_trxlogid;
        public static int suspect_count = 0;
        public List<RuleViewModel> RaastRules = new List<RuleViewModel>();
        public List<Parameter> Parameters = new List<Parameter>();

        //new code 
        public static bool EnableRecheck { get; set; } = false;

        // Last row pulled from Oracle (before validation), so failed rows are not re-fetched every cycle
        private string? last_fetched_trxlogid;
        private string? last_fetched_time;

        // CommonMethods is injected so its static connection strings are set before the first cycle
        public ServiceRun(IOptions<RaastSettings> options, CommonMethods commonMethods)
        {
            var settings = options.Value;
            Schema = settings.TransactionSchema;
            Tran_Count = settings.TranCount.ToString();  
            CycleDelay = settings.CycleDelay;
            LogPath = settings.LogPath;
            _commonMethods = commonMethods;            
            DefaultConnection =  CommonMethods.DefaultConnection;
            DataSourceConnection = CommonMethods.DataSourceConnection;

            Worker.is_running = true;
        }

        internal void StartServices()
        {
            // ServiceRun is a singleton now, so clear per-cycle state from the previous run
            this.completeTrans = new DataTable();
            this.latestTrans = new DataTable();
            this.last_fetched_trxlogid = null;
            this.last_fetched_time = null;
            ServiceRun.suspect_count = 0;

            // Whole cycle goes into one log file, even if it runs past midnight
            Logger.BeginCycle();
            try
            {
                Logger.Simple($"================================ Cycle Started ==================================");

                Logger.Simple("Fetching Rules");
                this.rules = _commonMethods.GetRules();
                this.criterias = _commonMethods.GetRulesCriterias();
                this.RaastRules = _commonMethods.DataTable_To_List_Rules(this.rules, this.criterias);
                if (this.RaastRules.Any())
                    Logger.Simple($"Rules Fetched : {this.RaastRules.Count}");
                else
                    Logger.Simple("No Rules Found.");

                this.Parameters = _commonMethods.Get_Parameters();
                Logger.Simple($"Parameters Fetched : {this.Parameters.Count}");

                Logger.Simple("Fetching Modify Points...");
                ModifyPoints modifiedPoints = _commonMethods.GetModifiedPoints();
                this.previous_datetime =DateTime.Now.ToString();
                //this.previous_datetime = modifiedPoints.ModifiedOn.ToString("yyyy-MM-dd HH:mm:ss");
                this.previous_trxlogid = 10.ToString();
                //this.previous_trxlogid = modifiedPoints.TrxLogId2;

                //end
                Logger.Simple($"Previous Modify Point Found | DateTime : {this.previous_datetime} | TrxLogId2 : {this.previous_trxlogid}");

                if (!modifiedPoints.IsSeeded)
                {
                    Logger.Simple("System Not Seeded. Loading Transactions...");
                    //Logger.Simple($"ModifiedOn: {modifiedPoints.ModifiedOn:O}");
                    //Logger.Simple($"Previous DateTime: {this.previous_datetime}");
                    completeTrans = GetDataSetFullTrans();
                    Logger.Simple($"Transactions Fetched : {completeTrans.Rows.Count}");
                }

                Logger.Simple($"Loading Latest Raast Transactions after {this.previous_trxlogid}.");
                latestTrans = GetDataSetLatestTrans();
                Logger.Simple($"Latest Transactions Fetched : {latestTrans.Rows.Count}");

               
                if (this.completeTrans.Rows.Count >= 1)
                {
                    UpdateModifyPointsFromLastFetched();
                }
                else if (this.latestTrans.Rows.Count >= 1)
                {
                    if (this.RaastRules.Any<RuleViewModel>())
                    {
                        Logger.Simple("Transaction Processing Started...");
                        ServiceRun.suspect_count = _commonMethods.GenerateFrequencySuspect(this.previous_trxlogid);
                        Logger.Simple($"Total Transactions Processed : {this.latestTrans.Rows.Count}");
                        Logger.Simple($"Total Suspects Added : {ServiceRun.suspect_count}");
                    }
                    else
                        Logger.Simple("No Active Raast Rules Found For Processing.");

                    UpdateModifyPointsFromLastFetched();
                }
                else if (this.last_fetched_trxlogid != null)
                {
                    // Every fetched row failed validation; still move past them
                    Logger.Simple("No Valid Transactions Found In This Batch.");
                    UpdateModifyPointsFromLastFetched();
                }
                else
                {
                    Logger.Simple("No New Transactions Found.");
                }

                Logger.Simple($"=============== Cycle Completed At : {DateTime.Now} ===========================");
                Logger.Simple("");
            }
            //yahan error araha hy 
            catch (Exception ex)
            {
                Logger.LogException(ex, "StartServices");
                Logger.Simple($"=============== Cycle Failed At : {DateTime.Now} ==============================");
                Logger.Simple("");
            }
            finally
            {
                Worker.is_running = false;
                Logger.EndCycle();
            }
        }
    
        private void UpdateModifyPointsFromLastFetched()
        {
            if (this.last_fetched_trxlogid == null || this.last_fetched_trxlogid == previous_trxlogid)
                return;

            _commonMethods.UpdateModifyPoints("Raast", this.last_fetched_time ?? string.Empty, this.last_fetched_trxlogid);
            Logger.Simple($"Modify Points Updated Successfully | DateTime : {this.last_fetched_time} | TrxLogId : {this.last_fetched_trxlogid}");
        }

        // Validates and inserts the pulled rows, then returns only the valid ones
        private  DataTable InsertValidRows(DataTable fetched)
        {
            var result = _commonMethods.InsertIntoTempTable(fetched);
            return result.ValidRows.Count > 0 ? result.ValidRows.CopyToDataTable() : fetched.Clone();
        }

        private DataTable GetDataSetFullTrans()
        {
            string predatetime = Convert.ToDateTime(this.previous_datetime).AddHours(-24).ToString("yyyy-MM-dd HH:mm:ss");
            DataSet dataSet = new DataSet();
            try
            {
                using (SqlConnection connection = new SqlConnection(DefaultConnection))
                {
                    string query = "DELETE FROM RaastTransaction WHERE TRANSACTION_LOG_ID <= " + this.previous_trxlogid + "; " +
                        " DELETE FROM RaastTransactionHistory WHERE TRANSACTION_LOG_ID <= " + this.previous_trxlogid;
                    connection.Open();
                    using (SqlCommand sqlCommand = new SqlCommand(query, connection))
                    {
                        sqlCommand.CommandTimeout = 0;
                        var a = sqlCommand.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
            try
            {
                using (OracleConnection conn = new OracleConnection(DataSourceConnection))
                {
                    Logger.Simple($"Fetching Transactions From Raast Oracle Source Schema | TrxID <= {this.previous_trxlogid} | DateTime >= {predatetime}");
                    string cmdText = "SELECT " + this.GetSelectParams() + " FROM " + Schema +
                        " WHERE TRANSACTION_LOG_ID <= " + this.previous_trxlogid + " AND TIME_LOC_TRAN_DT IS NOT NULL " +
                        "AND ACCT_ID_1 IS NOT NULL AND DEBITOR_BANK_IDENTIFICATION_CODE = 'BPUNPKKA' " +
                        "AND TO_CHAR(TIME_LOC_TRAN_DT, 'YYYY-MM-DD HH24:MI:SS') >= '" + predatetime + "' " +
                        //"WHERE TRANSACTION_LOG_ID <= " + this.previous_trxlogid + " AND PAN IS NOT NULL " +
                        //"AND TO_CHAR(TIME_LOC_TRAN_DT, 'YYYY-MM-DD HH24:MI:SS') >= '" + predatetime + "' " +
                        //"AND TIME_LOC_TRAN_DT IS NOT NULL AND PAN NOT IN ('0000000000000', '0000000000000000') " +
                        "ORDER BY TRANSACTION_LOG_ID ASC ";
                    conn.Open();
                    using (OracleCommand selectCommand = new OracleCommand(cmdText, conn))
                    {
                        selectCommand.CommandTimeout = 300;
                        selectCommand.CommandText = cmdText;
                        using (OracleDataAdapter oracleDataAdapter = new OracleDataAdapter(selectCommand))
                            oracleDataAdapter.Fill(dataSet, "Transactions");
                    }
                    Logger.Simple($"Transactions Pulled From Oracle : {dataSet.Tables[0].Rows.Count}");
                }
                if (dataSet.Tables[0].Rows.Count > 0)
                    return InsertValidRows(dataSet.Tables[0]);
                return dataSet.Tables[0];
            }
            catch (Exception e)
            {
                throw e;
            }
        }
        //---

        private DataTable GetDataSetLatestTrans()
        {
            DataSet dataSet = new DataSet();
            int TranCount = Convert.ToInt32(Tran_Count);
            try
            {
                using (SqlConnection connection = new SqlConnection(DefaultConnection))
                {
                    string query = "DELETE FROM RaastTransaction WHERE TRANSACTION_LOG_ID > " + this.previous_trxlogid + "; " +
                        " DELETE FROM RaastTransactionHistory WHERE TRANSACTION_LOG_ID > " + this.previous_trxlogid;
                    connection.Open();
                    using (SqlCommand sqlCommand = new SqlCommand(query, connection))
                    {
                        sqlCommand.CommandTimeout = 0;
                        var a = sqlCommand.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }

            int retryCount = 3; 
            bool success = false; 

            while (retryCount > 0 && !success)
            {
                try
                {
                    using (OracleConnection conn = new OracleConnection(DataSourceConnection))
                    {
                        Logger.Simple($"Fetching Transactions From Raast Oracle Source Schema | Oracle Query Parameter TrxID > {this.previous_trxlogid} | TranCount : {TranCount}");
                        string cmdText = "SELECT " + this.GetSelectParams() + " FROM " + Schema +
                            " WHERE TRANSACTION_LOG_ID > " + this.previous_trxlogid + " AND TIME_LOC_TRAN_DT IS NOT NULL " +
                            "AND ACCT_ID_1 IS NOT NULL AND DEBITOR_BANK_IDENTIFICATION_CODE = 'BPUNPKKA' " +
                            //"WHERE TRANSACTION_LOG_ID > " + this.previous_trxlogid + " AND PAN IS NOT NULL " +
                            //"AND TIME_LOC_TRAN_DT IS NOT NULL AND PAN NOT IN ('0000000000000', '0000000000000000') " +
                            //"ORDER BY TRANSACTION_LOG_ID ASC ";
                            "ORDER BY TRANSACTION_LOG_ID ASC FETCH FIRST " + TranCount + " ROWS ONLY";
                        conn.Open();
                        using (OracleCommand selectCommand = new OracleCommand(cmdText, conn))
                        {
                            selectCommand.CommandTimeout = 300;
                            selectCommand.CommandText = cmdText;
                            using (OracleDataAdapter oracleDataAdapter = new OracleDataAdapter(selectCommand))
                                oracleDataAdapter.Fill(dataSet, "Transactions");
                        }
                        Logger.Simple($"Transactions Pulled From Oracle : {dataSet.Tables[0].Rows.Count}");
                        if (dataSet.Tables[0].Rows.Count > TranCount)
                        {
                            for (int index = dataSet.Tables[0].Rows.Count - 1; index >= TranCount; --index)
                                dataSet.Tables[0].Rows[index].Delete();
                            dataSet.AcceptChanges();
                            Logger.Simple($"Transactions Reduced To TranCount : {dataSet.Tables[0].Rows.Count}");
                        }
                    }
                    DataTable fetched = dataSet.Tables[0];
                    success = true;
                    if (fetched.Rows.Count == 0)
                        return fetched;

                    // Capture before validation: the validator rewrites valid rows in place
                    DataRow lastFetched = fetched.Rows[fetched.Rows.Count - 1];
                    this.last_fetched_trxlogid = lastFetched["TRANSACTION_LOG_ID"].ToString();
                    this.last_fetched_time = lastFetched["TIME_LOC_TRAN_DT"].ToString();

                    return InsertValidRows(fetched);
                }
                catch (OracleException e)
                {
                    // Retry on certain errors (e.g., network timeout)
                    retryCount--;
                    if (retryCount == 0)
                    {
                        // Logged once (with the Oracle error as inner exception) by StartServices
                        throw new Exception("Failed to pull the latest transactions after 3 attempts.", e);
                    }
                    else
                    {
                        Logger.Simple($"Oracle Exception : {e.Message.Trim()} | Retrying in 2 seconds... ({retryCount} attempts left)");
                        Thread.Sleep(2000);  // Wait before retrying
                    }
                }
                // Other exceptions bubble up and are logged once by StartServices

            }
            throw new Exception("Failed to pull the latest transactions after multiple attempts.");
        }

        public string GetSelectParams()
        {
            string str = "";
            foreach (Parameter parameter in this.Parameters)
            {
                if (parameter.ParameterName == "AMT_TRAN_ISS")
                    str += "NVL(To_Number(AMT_TRAN_ISS)/100,'0') AS AMT_TRAN_ISS, ";
                else if (parameter.ParameterName == "AMT_TRAN_ACQ")
                    str += "NVL(To_Number(AMT_TRAN_ACQ)/100,'0') AS AMT_TRAN_ACQ, ";
                else if (parameter.ParameterName == "SETT_AMOUNT_ACQ")
                    str += "NVL(To_Number(SETT_AMOUNT_ACQ)/100,'0') AS SETT_AMOUNT_ACQ, ";
                else if (parameter.ParameterName == "CARD_HOLD_BILL_AMOUNT_ACQ")
                    str += "NVL(To_Number(CARD_HOLD_BILL_AMOUNT_ACQ)/100,'0') AS CARD_HOLD_BILL_AMOUNT_ACQ, ";
                else if (parameter.ParameterName == "AMOUNT_FEE_ACCOUNT")
                    str += "NVL(To_Number(AMOUNT_FEE_ACCOUNT)/100,'0') AS AMOUNT_FEE_ACCOUNT, ";
                else if (parameter.ParameterName == "AMOUNT_TRANSACTION_BASE")
                    str += "NVL(To_Number(AMOUNT_TRANSACTION_BASE)/100,'0') AS AMOUNT_TRANSACTION_BASE, ";
                else if (parameter.ParameterName == "AMT_FEE_ACQ")
                    str += "NVL(To_Number(AMT_FEE_ACQ)/100,'0') AS AMT_FEE_ACQ, ";
                else if (parameter.ParameterName == "AMT_FEE_ISS")
                    str += "NVL(To_Number(AMT_FEE_ISS)/100,'0') AS AMT_FEE_ISS, ";
                else if (parameter.ParameterName == "CARD_HOLD_BILL_AMOUNT_ISS")
                    str += "NVL(To_Number(CARD_HOLD_BILL_AMOUNT_ISS)/100,'0') AS CARD_HOLD_BILL_AMOUNT_ISS, ";
                else if (parameter.ParameterName == "CARD_HOLD_BILL_FEE_ISS")
                    str += "NVL(To_Number(CARD_HOLD_BILL_FEE_ISS)/100,'0') AS CARD_HOLD_BILL_FEE_ISS, ";
                else if (parameter.ParameterName == "CARD_HOLD_BILL_FEE_ACQ")
                    str += "NVL(To_Number(CARD_HOLD_BILL_FEE_ACQ)/100,'0') AS CARD_HOLD_BILL_FEE_ACQ, ";
                else if (parameter.ParameterName == "SETT_AMOUNT_ISS")
                    str += "NVL(To_Number(SETT_AMOUNT_ISS)/100,'0') AS SETT_AMOUNT_ISS, ";
                else if (parameter.ParameterName == "TIME_LOC_TRAN_DT")
                    str += "TO_CHAR(TIME_LOC_TRAN_DT, 'YYYY-MM-DD HH24:MI:SS') AS TIME_LOC_TRAN_DT, ";
                else if (parameter.ParameterName == "DB_DATE_TIME")
                    str += "TO_CHAR(DB_DATE_TIME, 'YYYY-MM-DD HH24:MI:SS') AS DB_DATE_TIME, ";
                else if (parameter.ParameterName == "DB_DATE_TIME_UPDATE")
                    str += "TO_CHAR(DB_DATE_TIME_UPDATE, 'YYYY-MM-DD HH24:MI:SS') AS DB_DATE_TIME_UPDATE, ";
                else if (parameter.ParameterName == "TRANSM_DATE_TIME_DT")
                    str += "TO_CHAR(TRANSM_DATE_TIME_DT, 'YYYY-MM-DD HH24:MI:SS') AS TRANSM_DATE_TIME_DT, ";
                else if (parameter.ParameterName == "FORWARD_INSTITUTION_ID_CODE")
                    str += "SUBSTR(FORWARD_INSTITUTION_ID_CODE, 0, 6) AS FORWARD_INSTITUTION_ID_CODE, ";
                else if (parameter.ParameterName == "DB_DATE_TIME_UPDATE2")
                    str += "TO_CHAR(DB_DATE_TIME_UPDATE2, 'YYYY-MM-DD HH24:MI:SS') AS DB_DATE_TIME_UPDATE2, ";
                else if (parameter.ParameterName == "HOST_TRANSMISSION_DATE_TIME")
                    str += "TO_CHAR(TO_DATE(HOST_TRANSMISSION_DATE_TIME, 'YYYY-MM-DD HH24:MI:SS'), 'YYYY-MM-DD HH24:MI:SS') AS HOST_TRANSMISSION_DATE_TIME, ";
                else
                    str += parameter.ParameterName + ", ";
            }
            if (str.Length > 0)
                str = str.Remove(str.Length - 2);   
            return str;
        }
    }
    
}
