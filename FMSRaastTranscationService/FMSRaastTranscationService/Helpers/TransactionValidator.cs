#nullable enable
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace FMSRaastTranscationService.Helpers
{
    /// <summary>
    /// Dynamic, high-performance transaction validator.
    /// Fetches target table schema dynamically from SQL Server INFORMATION_SCHEMA,
    /// matches source DataTable columns with local table columns, and validates every column dynamically.
    /// Eliminates hardcoded model property validation.
    /// </summary>
    public sealed class TransactionValidator
    {
        #region Schema Model

        /// <summary>
        /// Represents a single column's metadata from the database schema.
        /// </summary>
        public sealed class ColumnSchema
        {
            public string ColumnName { get; init; } = string.Empty;
            public string DataType { get; init; } = string.Empty;       // e.g. "nvarchar", "decimal", "datetime", "bigint"
            public int? MaxLength { get; init; }                        // CHARACTER_MAXIMUM_LENGTH (null for non-string types)
            public byte? NumericPrecision { get; init; }
            public int? NumericScale { get; init; }
            public bool IsNullable { get; init; }
            public bool IsIdentity { get; init; }
            public bool IsComputed { get; init; }
        }

        #endregion

        #region Fields

        private readonly Dictionary<string, ColumnSchema> _schemaCache;

        /// <summary>
        /// Gets or sets whether to automatically trim string and decimal values that exceed the database schema's maximum length or scale.
        /// Defaults to true.
        /// </summary>
        public bool ApplyTrim { get; set; } = true;

        /// <summary>
        /// Source amount columns that are stored in minor currency units (cents/paisa)
        /// and need to be validated as numbers and divided by 100 before saving.
        /// Intentionally empty for Raast: ServiceRun.GetSelectParams already applies
        /// NVL(TO_NUMBER(col)/100, '0') in the Oracle SELECT, so dividing here would divide twice.
        /// </summary>
        public static readonly HashSet<string> AmountColumnsDividedBy100 = new(StringComparer.OrdinalIgnoreCase);

        #endregion

        #region Constructor & Schema Fetch

        private TransactionValidator(Dictionary<string, ColumnSchema> schema)
        {
            _schemaCache = schema;
        }

        /// <summary>
        /// Factory: Queries INFORMATION_SCHEMA.COLUMNS for the given table and builds a cached schema.
        /// Handles table names passed with or without schema prefix (e.g. "dbo.RaastTransaction" or "RaastTransaction").
        /// </summary>
        public static async Task<TransactionValidator> CreateAsync(string connectionString, string tableName = "RaastTransaction", string schemaName = "dbo", bool applyTrim = true)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                tableName = "RaastTransaction";

            if (tableName.Contains('.'))
            {
                var parts = tableName.Split('.');
                schemaName = parts[0].Trim('[', ']', ' ');
                tableName = parts[1].Trim('[', ']', ' ');
            }
            else
            {
                tableName = tableName.Trim('[', ']', ' ');
                schemaName = string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName.Trim('[', ']', ' ');
            }

            var schema = await FetchSchemaFromDbAsync(connectionString, tableName, schemaName);
            return new TransactionValidator(schema) { ApplyTrim = applyTrim };
        }

        private static async Task<Dictionary<string, ColumnSchema>> FetchSchemaFromDbAsync(string connectionString, string tableName, string schemaName)
        {
            const string query = @"
                SELECT 
                    c.COLUMN_NAME,
                    c.DATA_TYPE,
                    c.CHARACTER_MAXIMUM_LENGTH,
                    c.NUMERIC_PRECISION,
                    c.NUMERIC_SCALE,
                    c.IS_NULLABLE,
                    ISNULL(COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsIdentity'), 0) AS IS_IDENTITY,
                    ISNULL(COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsComputed'), 0) AS IS_COMPUTED
                FROM INFORMATION_SCHEMA.COLUMNS c
                WHERE c.TABLE_NAME = @TableName AND c.TABLE_SCHEMA = @SchemaName
                ORDER BY c.ORDINAL_POSITION";

            var result = new Dictionary<string, ColumnSchema>(StringComparer.OrdinalIgnoreCase);

            using var con = new SqlConnection(connectionString);
            await con.OpenAsync();

            using var cmd = new SqlCommand(query, con);
            cmd.Parameters.AddWithValue("@TableName", tableName);
            cmd.Parameters.AddWithValue("@SchemaName", schemaName);
            cmd.CommandTimeout = 30;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string colName = reader.GetString(0);
                result[colName] = new ColumnSchema
                {
                    ColumnName = colName,
                    DataType = reader.GetString(1).ToLowerInvariant(),
                    MaxLength = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    NumericPrecision = reader.IsDBNull(3) ? null : reader.GetByte(3),
                    NumericScale = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    IsNullable = reader.GetString(5).Equals("YES", StringComparison.OrdinalIgnoreCase),
                    IsIdentity = !reader.IsDBNull(6) && reader.GetInt32(6) == 1,
                    IsComputed = !reader.IsDBNull(7) && reader.GetInt32(7) == 1
                };
            }

            if (result.Count == 0)
                throw new InvalidOperationException($"No columns found for [{schemaName}].[{tableName}]. Verify the table exists.");

            return result;
        }

        /// <summary>
        /// Returns a copy of the cached schema for diagnostics/logging.
        /// </summary>
        public IReadOnlyDictionary<string, ColumnSchema> GetCachedSchema() => _schemaCache;

        #endregion

        #region Core Validation — Single-Pass Split

        /// <summary>
        /// Result of the validation pass: two DataTables ready for SqlBulkCopy.
        /// </summary>
        /// <summary>
        /// Result of the validation pass: two DataTables ready for SqlBulkCopy,
        /// along with the lists of updated valid and failed source DataRows.
        /// </summary>
        public sealed class ValidationResult
        {
            public DataTable ValidTable { get; init; } = null!;
            public DataTable FailedTable { get; init; } = null!;
            public List<DataRow> ValidRows { get; init; } = new();
            public List<DataRow> FailedRows { get; init; } = new();
            public int ValidCount { get; init; }
            public int FailedCount { get; init; }
        }

        /// <summary>
        /// Safely sets a value on a DataRow column, handling read-only columns and type conversions cleanly.
        /// </summary>
        public static void SetRowValueSafely(DataRow row, DataColumn col, object? value)
        {
            if (row == null || col == null) return;

            bool wasReadOnly = col.ReadOnly;
            if (wasReadOnly)
            {
                col.ReadOnly = false;
            }

            try
            {
                if (value == null || value == DBNull.Value)
                {
                    if (col.AllowDBNull)
                    {
                        row[col] = DBNull.Value;
                    }
                    return;
                }

                if (col.DataType == typeof(string))
                {
                    row[col] = value is decimal dec
                        ? dec.ToString(CultureInfo.InvariantCulture)
                        : (value is IFormattable formattable
                            ? formattable.ToString(null, CultureInfo.InvariantCulture)
                            : value.ToString() ?? string.Empty);
                }
                else if (col.DataType.IsInstanceOfType(value))
                {
                    row[col] = value;
                }
                else if (col.DataType == typeof(decimal))
                {
                    row[col] = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                }
                else if (col.DataType == typeof(double))
                {
                    row[col] = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                else if (col.DataType == typeof(float))
                {
                    row[col] = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                }
                else if (col.DataType == typeof(long))
                {
                    row[col] = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                }
                else if (col.DataType == typeof(int))
                {
                    row[col] = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }
                else if (col.DataType == typeof(DateTime))
                {
                    row[col] = Convert.ToDateTime(value, CultureInfo.InvariantCulture);
                }
                else
                {
                    Type targetType = Nullable.GetUnderlyingType(col.DataType) ?? col.DataType;
                    row[col] = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                try
                {
                    row[col] = value?.ToString() ?? (object)DBNull.Value;
                }
                catch
                {
                    // Ignore if target DataColumn cannot accept representation
                }
            }
            finally
            {
                if (wasReadOnly)
                {
                    col.ReadOnly = true;
                }
            }
        }

        /// <summary>
        /// Safely sets a value on a DataRow by column name.
        /// </summary>
        public static void SetRowValueSafely(DataRow row, string columnName, object? value)
        {
            if (row?.Table?.Columns.Contains(columnName) == true)
            {
                SetRowValueSafely(row, row.Table.Columns[columnName]!, value);
            }
        }

        /// <summary>
        /// Dynamically validates an enumerable collection of DataRows against the cached database table schema.
        /// Matches row columns with database columns dynamically, validates data types, lengths,
        /// precision, scale, and nullability, updates valid rows in-place (including amount/100),
        /// and routes rows into ValidTable/ValidRows and FailedTable/FailedRows.
        /// </summary>
        public ValidationResult ValidateAndSplit(IEnumerable<DataRow> sourceRows)
        {
            DataTable validTable = BuildTypedDataTable();
            DataTable failedTable = BuildFailedDataTable();
            var validRowsList = new List<DataRow>();
            var failedRowsList = new List<DataRow>();

            int validCount = 0;
            int failedCount = 0;

            if (sourceRows == null)
            {
                return new ValidationResult
                {
                    ValidTable = validTable,
                    FailedTable = failedTable,
                    ValidRows = validRowsList,
                    FailedRows = failedRowsList,
                    ValidCount = 0,
                    FailedCount = 0
                };
            }

            // Target columns to validate and populate (non-identity and non-computed)
            var targetSchemas = _schemaCache.Values
                .Where(s => !s.IsIdentity && !s.IsComputed)
                .ToList();

            var typedValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var failedValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var rowUpdates = new List<(DataColumn Col, object? Value)>();

            DataTable? currentTable = null;
            Dictionary<string, DataColumn>? sourceColumnMap = null;

            foreach (DataRow row in sourceRows)
            {
                if (row == null) continue;

                // Refresh or cache source column map if table reference changes
                if (sourceColumnMap == null || row.Table != currentTable)
                {
                    currentTable = row.Table;
                    sourceColumnMap = new Dictionary<string, DataColumn>(StringComparer.OrdinalIgnoreCase);
                    if (currentTable != null)
                    {
                        foreach (DataColumn col in currentTable.Columns)
                        {
                            sourceColumnMap[col.ColumnName] = col;
                        }
                    }
                }

                var errors = new StringBuilder();
                typedValues.Clear();
                failedValues.Clear();
                rowUpdates.Clear();

                // 1. Validate against target DB schema columns
                foreach (var schema in targetSchemas)
                {
                    object? rawValue;
                    DataColumn? srcCol = null;

                    if (sourceColumnMap.TryGetValue(schema.ColumnName, out srcCol))
                    {
                        rawValue = row[srcCol];
                    }
                    else
                    {
                        rawValue = DBNull.Value;
                    }

                    // Dynamically validate and convert the column value against DB schema
                    ValidateAndConvertColumn(schema, rawValue, out object? typedVal, out object? failedVal, errors);

                    typedValues[schema.ColumnName] = typedVal;
                    failedValues[schema.ColumnName] = failedVal;

                    if (srcCol != null && typedVal != DBNull.Value)
                    {
                        // Queue in-place update for valid column (including amount/100)
                        rowUpdates.Add((srcCol, typedVal));
                    }
                }

                // 2. Also check any source columns in AmountColumnsDividedBy100 that were not in targetSchemas
                foreach (var amountColName in AmountColumnsDividedBy100)
                {
                    if (typedValues.ContainsKey(amountColName))
                        continue; // Already processed above

                    if (sourceColumnMap != null && sourceColumnMap.TryGetValue(amountColName, out var extraCol))
                    {
                        object? rawVal = row[extraCol];
                        if (rawVal != null && rawVal != DBNull.Value && !string.IsNullOrWhiteSpace(rawVal.ToString()))
                        {
                            string sVal = rawVal.ToString()!.Trim();
                            if (!decimal.TryParse(sVal, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out decimal rawAmount))
                            {
                                errors.Append($"[{amountColName}] value '{sVal}' is not a valid number | ");
                            }
                            else
                            {
                                decimal decVal = rawAmount / 100m;
                                if (ApplyTrim && TryTrimDecimalScale(decVal, 2, out decimal trimmedDec))
                                {
                                    decVal = trimmedDec;
                                }
                                rowUpdates.Add((extraCol, decVal));
                            }
                        }
                    }
                }

                if (errors.Length == 0)
                {
                    // VALID — update the source DataRow in-place with validated values (including amount/100)
                    foreach (var (col, val) in rowUpdates)
                    {
                        SetRowValueSafely(row, col, val);
                    }
                    try { row.AcceptChanges(); } catch { }

                    validRowsList.Add(row);

                    // Add typed row to validTable for bulk copy
                    DataRow validRow = validTable.NewRow();
                    foreach (DataColumn col in validTable.Columns)
                    {
                        validRow[col.ColumnName] = typedValues.TryGetValue(col.ColumnName, out var tv) ? (tv ?? DBNull.Value) : DBNull.Value;
                    }
                    validTable.Rows.Add(validRow);
                    validCount++;
                }
                else
                {
                    // FAILED — add to failed list and failedTable
                    failedRowsList.Add(row);

                    DataRow failedRow = failedTable.NewRow();
                    foreach (DataColumn col in failedTable.Columns)
                    {
                        if (col.ColumnName.Equals("FailedReason", StringComparison.OrdinalIgnoreCase))
                        {
                            failedRow[col.ColumnName] = errors.ToString().TrimEnd(' ', '|', ' ');
                        }
                        else if (col.ColumnName.Equals("FailedOn", StringComparison.OrdinalIgnoreCase))
                        {
                            failedRow[col.ColumnName] = DateTime.Now;
                        }
                        else
                        {
                            failedRow[col.ColumnName] = failedValues.TryGetValue(col.ColumnName, out var fv) ? (fv ?? DBNull.Value) : DBNull.Value;
                        }
                    }
                    failedTable.Rows.Add(failedRow);
                    failedCount++;
                }
            }

            return new ValidationResult
            {
                ValidTable = validTable,
                FailedTable = failedTable,
                ValidRows = validRowsList,
                FailedRows = failedRowsList,
                ValidCount = validCount,
                FailedCount = failedCount
            };
        }

        /// <summary>
        /// Validates a single DataRow.
        /// </summary>
        public ValidationResult ValidateAndSplit(DataRow singleRow)
        {
            if (singleRow == null)
            {
                return new ValidationResult
                {
                    ValidTable = BuildTypedDataTable(),
                    FailedTable = BuildFailedDataTable(),
                    ValidCount = 0,
                    FailedCount = 0
                };
            }
            return ValidateAndSplit(new[] { singleRow });
        }

        /// <summary>
        /// Validates all DataRows in an input DataTable.
        /// </summary>
        public ValidationResult ValidateAndSplit(DataTable sourceTable)
        {
            if (sourceTable == null || sourceTable.Rows.Count == 0)
            {
                return new ValidationResult
                {
                    ValidTable = BuildTypedDataTable(),
                    FailedTable = BuildFailedDataTable(),
                    ValidCount = 0,
                    FailedCount = 0
                };
            }

            return ValidateAndSplit(sourceTable.Rows.Cast<DataRow>());
        }

        #region Public Individual & Row Validation Methods

        /// <summary>
        /// Validates a single column value dynamically against the database schema.
        /// If valid and the column is in AmountColumnsDividedBy100, divides by 100 and returns the divided value.
        /// </summary>
        public bool ValidateColumnValue(string columnName, object? rawValue, out object? validatedValue, out string? error)
        {
            var errors = new StringBuilder();

            if (_schemaCache.TryGetValue(columnName, out var schema))
            {
                ValidateAndConvertColumn(schema, rawValue, out object? typedVal, out _, errors);
                if (errors.Length == 0)
                {
                    validatedValue = typedVal;
                    error = null;
                    return true;
                }

                validatedValue = null;
                error = errors.ToString().TrimEnd(' ', '|', ' ');
                return false;
            }

            // Fallback for amount columns not explicitly in schema cache
            if (AmountColumnsDividedBy100.Contains(columnName))
            {
                if (rawValue == null || rawValue == DBNull.Value || string.IsNullOrWhiteSpace(rawValue.ToString()))
                {
                    validatedValue = 0m;
                    error = null;
                    return true;
                }

                string strVal = rawValue.ToString()!.Trim();
                if (decimal.TryParse(strVal, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out decimal rawAmount))
                {
                    decimal decVal = rawAmount / 100m;
                    if (ApplyTrim && TryTrimDecimalScale(decVal, 2, out decimal trimmedDec))
                    {
                        decVal = trimmedDec;
                    }
                    validatedValue = decVal;
                    error = null;
                    return true;
                }

                validatedValue = null;
                error = $"[{columnName}] value '{strVal}' is not a valid number";
                return false;
            }

            // Unknown column not in schema: accept raw value
            validatedValue = rawValue;
            error = null;
            return true;
        }

        /// <summary>
        /// Validates a single column value and returns a tuple (IsValid, ValidatedValue, Error).
        /// If the column is an amount column and valid, ValidatedValue is divided by 100.
        /// </summary>
        public (bool IsValid, object? ValidatedValue, string? Error) ValidateValue(string columnName, object? rawValue)
        {
            bool isValid = ValidateColumnValue(columnName, rawValue, out var val, out var err);
            return (isValid, val, err);
        }

        /// <summary>
        /// Validates a specific column on a DataRow.
        /// If valid, updates the DataRow in-place with the validated value (e.g. amount/100) and returns the updated value.
        /// </summary>
        public bool ValidateAndUpdateColumn(DataRow row, string columnName, out object? updatedValue, out string? error)
        {
            if (row == null || !row.Table.Columns.Contains(columnName))
            {
                updatedValue = null;
                error = $"Column '{columnName}' does not exist in DataRow.";
                return false;
            }

            object? rawValue = row[columnName];
            bool isValid = ValidateColumnValue(columnName, rawValue, out updatedValue, out error);
            if (isValid)
            {
                SetRowValueSafely(row, columnName, updatedValue);
                try { row.AcceptChanges(); } catch { }
            }
            return isValid;
        }

        /// <summary>
        /// Validates a specific column on a DataRow and returns a tuple (IsValid, UpdatedValue, Error).
        /// If valid, updates the DataRow in-place with the validated value (e.g. amount/100).
        /// </summary>
        public (bool IsValid, object? UpdatedValue, string? Error) ValidateAndUpdateColumn(DataRow row, string columnName)
        {
            bool isValid = ValidateAndUpdateColumn(row, columnName, out var val, out var err);
            return (isValid, val, err);
        }

        /// <summary>
        /// Validates an entire DataRow against the cached database schema.
        /// If valid, updates all matched columns on the DataRow in-place (e.g. amount/100 for amount columns).
        /// Returns true if valid, false if any validation rule failed.
        /// </summary>
        public bool ValidateAndUpdateRow(DataRow row, out string? error)
        {
            if (row == null)
            {
                error = "DataRow is null";
                return false;
            }

            var result = ValidateAndSplit(new[] { row });
            if (result.ValidCount > 0)
            {
                error = null;
                return true;
            }

            error = result.FailedTable.Rows.Count > 0
                ? result.FailedTable.Rows[0]["FailedReason"]?.ToString()
                : "Validation failed";
            return false;
        }

        /// <summary>
        /// Validates a DataRow and returns a tuple (IsValid, Row, Error).
        /// If valid, the DataRow is updated in-place with validated values (including amount/100).
        /// </summary>
        public (bool IsValid, DataRow Row, string? Error) ValidateRow(DataRow row)
        {
            bool isValid = ValidateAndUpdateRow(row, out string? error);
            return (isValid, row, error);
        }

        #endregion

        ///// <summary>
        ///// Backward-compatible overload: converts model list to DataTable and delegates to dynamic validation.
        ///// </summary>
        //public ValidationResult ValidateAndSplit(List<TransactionVIewModel> data)
        //{
        //    if (data == null || data.Count == 0)
        //    {
        //        return new ValidationResult
        //        {
        //            ValidTable = BuildTypedDataTable(),
        //            FailedTable = BuildFailedDataTable(),
        //            ValidCount = 0,
        //            FailedCount = 0
        //        };
        //    }

        //    DataTable dt = ToDataTable(data);
        //    return ValidateAndSplit(dt);
        //}

        /// <summary>
        /// Converts an enumerable of objects into a DataTable dynamically using reflection.
        /// </summary>
        public static DataTable ToDataTable<T>(IEnumerable<T> items)
        {
            var dt = new DataTable(typeof(T).Name);
            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var prop in properties)
            {
                var propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                dt.Columns.Add(prop.Name, propType);
            }

            foreach (var item in items)
            {
                var row = dt.NewRow();
                foreach (var prop in properties)
                {
                    row[prop.Name] = prop.GetValue(item) ?? DBNull.Value;
                }
                dt.Rows.Add(row);
            }

            return dt;
        }

        #endregion

        #region Dynamic Column Validation

        /// <summary>
        /// Validates a single value against the target column's schema and produces typed and failed output values.
        /// </summary>
        private void ValidateAndConvertColumn(ColumnSchema schema,object? rawValue,out object? typedVal,out object? failedVal,StringBuilder errors)
        {
            bool isNullOrDbNull = rawValue == null || rawValue == DBNull.Value;
            bool isStringEmpty = !isNullOrDbNull && rawValue is string s && string.IsNullOrWhiteSpace(s);

            // Null / Empty validation
            if (isNullOrDbNull || isStringEmpty)
            {
                // Auto-fill common audit columns if missing
                if (schema.ColumnName.Equals("AddedOn", StringComparison.OrdinalIgnoreCase))
                {
                    typedVal = DateTime.Now;
                    failedVal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    return;
                }
                if (schema.ColumnName.Equals("IsDeleted", StringComparison.OrdinalIgnoreCase))
                {
                    typedVal = false;
                    failedVal = "0";
                    return;
                }

                // If it's one of the amount columns, replicate NVL(..., '0')
                if (AmountColumnsDividedBy100.Contains(schema.ColumnName))
                {
                    if (schema.IsNullable)
                    {
                        typedVal = DBNull.Value;
                        failedVal = DBNull.Value;
                    }
                    else
                    {
                        typedVal = IsStringDataType(schema.DataType) ? (object)"0" : 0m;
                        failedVal = "0";
                    }
                    return;
                }

                if (!schema.IsNullable)
                {
                    errors.Append($"[{schema.ColumnName}] is required but was NULL or empty | ");
                }

                typedVal = DBNull.Value;
                failedVal = isNullOrDbNull ? DBNull.Value : rawValue?.ToString();
                return;
            }

            string strVal = rawValue!.ToString()!.Trim();

            // ── Amount columns stored in minor currency units (cents/paisa) divided by 100 ──
            if (AmountColumnsDividedBy100.Contains(schema.ColumnName))
            {
                if (!decimal.TryParse(strVal, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out decimal rawAmount))
                {
                    errors.Append($"[{schema.ColumnName}] value '{strVal}' is not a valid number | ");
                    typedVal = DBNull.Value;
                    failedVal = strVal;
                    return;
                }

                decimal decVal = rawAmount / 100m;

                if (schema.NumericPrecision.HasValue && schema.NumericScale.HasValue)
                {
                    ValidatePrecisionAndScale(schema.ColumnName, ref decVal, schema.NumericPrecision.Value, schema.NumericScale.Value, errors);
                }

                typedVal = IsStringDataType(schema.DataType) ? (object)decVal.ToString(CultureInfo.InvariantCulture) : decVal;
                failedVal = strVal;
                return;
            }

            switch (schema.DataType)
            {
                // ── String Types ──
                case "varchar":
                case "nvarchar":
                case "char":
                case "nchar":
                case "text":
                case "ntext":
                    if (schema.MaxLength.HasValue && schema.MaxLength.Value > 0 && strVal.Length > schema.MaxLength.Value)
                    {
                        if (ApplyTrim)
                        {
                            strVal = strVal.Substring(0, schema.MaxLength.Value);
                        }
                        else
                        {
                            errors.Append($"[{schema.ColumnName}] exceeds max length {schema.MaxLength.Value} (actual: {strVal.Length}) | ");
                        }
                    }
                    typedVal = strVal;
                    failedVal = strVal;
                    break;

                // ── Decimal & Numeric Types ──
                case "decimal":
                case "numeric":
                case "money":
                case "smallmoney":
                    if (!decimal.TryParse(strVal, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out decimal decVal))
                    {
                        errors.Append($"[{schema.ColumnName}] value '{strVal}' is not a valid decimal | ");
                        typedVal = DBNull.Value;
                        failedVal = strVal;
                    }
                    else
                    {
                        if (schema.NumericPrecision.HasValue && schema.NumericScale.HasValue)
                        {
                            ValidatePrecisionAndScale(schema.ColumnName, ref decVal, schema.NumericPrecision.Value, schema.NumericScale.Value, errors);
                        }
                        typedVal = decVal;
                        failedVal = strVal;
                    }
                    break;

                // ── Integer Types ──
                case "bigint":
                    if (!long.TryParse(strVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lVal))
                    {
                        errors.Append($"[{schema.ColumnName}] value '{strVal}' is not a valid bigint | ");
                        typedVal = DBNull.Value;
                        failedVal = strVal;
                    }
                    else
                    {
                        typedVal = lVal;
                        failedVal = strVal;
                    }
                    break;

                case "int":
                    if (!int.TryParse(strVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iVal))
                    {
                        errors.Append($"[{schema.ColumnName}] value '{strVal}' is not a valid int | ");
                        typedVal = DBNull.Value;
                        failedVal = strVal;
                    }
                    else
                    {
                        typedVal = iVal;
                        failedVal = strVal;
                    }
                    break;

                case "smallint":
                    if (!short.TryParse(strVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out short sVal))
                    {
                        errors.Append($"[{schema.ColumnName}] value '{strVal}' is not a valid smallint | ");
                        typedVal = DBNull.Value;
                        failedVal = strVal;
                    }
                    else
                    {
                        typedVal = sVal;
                        failedVal = strVal;
                    }
                    break;

                case "tinyint":
                    if (!byte.TryParse(strVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte bVal))
                    {
                        errors.Append($"[{schema.ColumnName}] value '{strVal}' is not a valid tinyint | ");
                        typedVal = DBNull.Value;
                        failedVal = strVal;
                    }
                    else
                    {
                        typedVal = bVal;
                        failedVal = strVal;
                    }
                    break;

                // ── Floating Point Types ──
                case "float":
                case "real":
                    if (!double.TryParse(strVal, NumberStyles.Float, CultureInfo.InvariantCulture, out double dVal))
                    {
                        errors.Append($"[{schema.ColumnName}] value '{strVal}' is not a valid float | ");
                        typedVal = DBNull.Value;
                        failedVal = strVal;
                    }
                    else
                    {
                        typedVal = schema.DataType == "real" ? (float)dVal : dVal;
                        failedVal = strVal;
                    }
                    break;

                // ── DateTime Types ──
                case "datetime":
                case "datetime2":
                case "smalldatetime":
                case "date":
                    DateTime dtVal;
                    bool parsedDate = false;

                    if (rawValue is DateTime dt)
                    {
                        dtVal = dt;
                        parsedDate = true;
                    }
                    else if (DateTime.TryParse(strVal, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dtVal) ||
                             DateTime.TryParse(strVal, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out dtVal))
                    {
                        parsedDate = true;
                    }

                    if (!parsedDate)
                    {
                        errors.Append($"[{schema.ColumnName}] value '{strVal}' is not a valid datetime | ");
                        typedVal = DBNull.Value;
                        failedVal = strVal;
                    }
                    else
                    {
                        if (schema.DataType == "datetime" || schema.DataType == "smalldatetime")
                        {
                            DateTime minDate = schema.DataType == "smalldatetime" ? new DateTime(1900, 1, 1) : new DateTime(1753, 1, 1);
                            DateTime maxDate = schema.DataType == "smalldatetime" ? new DateTime(2079, 6, 6) : new DateTime(9999, 12, 31, 23, 59, 59, 997);
                            if (dtVal < minDate || dtVal > maxDate)
                            {
                                errors.Append($"[{schema.ColumnName}] value '{dtVal:o}' is outside SQL Server {schema.DataType} range | ");
                            }
                        }
                        typedVal = dtVal;
                        failedVal = dtVal.ToString("o");
                    }
                    break;

                // ── Boolean / Bit Types ──
                case "bit":
                    if (rawValue is bool b)
                    {
                        typedVal = b;
                        failedVal = b ? "1" : "0";
                    }
                    else if (strVal == "1" || strVal.Equals("true", StringComparison.OrdinalIgnoreCase) || strVal.Equals("yes", StringComparison.OrdinalIgnoreCase))
                    {
                        typedVal = true;
                        failedVal = "1";
                    }
                    else if (strVal == "0" || strVal.Equals("false", StringComparison.OrdinalIgnoreCase) || strVal.Equals("no", StringComparison.OrdinalIgnoreCase))
                    {
                        typedVal = false;
                        failedVal = "0";
                    }
                    else
                    {
                        errors.Append($"[{schema.ColumnName}] value '{strVal}' is not a valid boolean/bit | ");
                        typedVal = DBNull.Value;
                        failedVal = strVal;
                    }
                    break;

                // ── Unique Identifier / GUID ──
                case "uniqueidentifier":
                    if (rawValue is Guid g)
                    {
                        typedVal = g;
                        failedVal = g.ToString();
                    }
                    else if (Guid.TryParse(strVal, out Guid guidVal))
                    {
                        typedVal = guidVal;
                        failedVal = strVal;
                    }
                    else
                    {
                        errors.Append($"[{schema.ColumnName}] value '{strVal}' is not a valid GUID | ");
                        typedVal = DBNull.Value;
                        failedVal = strVal;
                    }
                    break;

                // ── Default Fallback ──
                default:
                    typedVal = rawValue;
                    failedVal = strVal;
                    break;
            }
        }

        #endregion

        #region DataTable Builders

        /// <summary>
        /// Builds a typed DataTable dynamically based on the local table schema.
        /// Non-identity and non-computed columns are created with their corresponding .NET data types.
        /// </summary>
        private DataTable BuildTypedDataTable()
        {
            var dt = new DataTable("ValidTransactions");

            foreach (var schema in _schemaCache.Values)
            {
                if (schema.IsIdentity || schema.IsComputed)
                    continue;

                var col = dt.Columns.Add(schema.ColumnName, GetDotNetType(schema.DataType));
                col.AllowDBNull = schema.IsNullable;
            }

            return dt;
        }

        /// <summary>
        /// Builds an all-string DataTable matching the local table schema plus diagnostics columns (FailedReason, FailedOn).
        /// </summary>
        private DataTable BuildFailedDataTable()
        {
            var dt = new DataTable("FailedTransactions");

            foreach (var schema in _schemaCache.Values)
            {
                if (schema.IsIdentity || schema.IsComputed)
                    continue;

                dt.Columns.Add(schema.ColumnName, typeof(string));
            }

            // Extra columns for failure diagnostics
            dt.Columns.Add("FailedReason", typeof(string));
            dt.Columns.Add("FailedOn", typeof(DateTime));

            return dt;
        }

        /// <summary>
        /// Maps SQL Server data types to CLR .NET types.
        /// </summary>
        private static Type GetDotNetType(string sqlDataType)
        {
            return sqlDataType.ToLowerInvariant() switch
            {
                "bigint" => typeof(long),
                "int" => typeof(int),
                "smallint" => typeof(short),
                "tinyint" => typeof(byte),
                "bit" => typeof(bool),
                "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
                "float" => typeof(double),
                "real" => typeof(float),
                "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
                "time" => typeof(TimeSpan),
                "uniqueidentifier" => typeof(Guid),
                "binary" or "varbinary" or "image" => typeof(byte[]),
                _ => typeof(string)
            };
        }

        /// <summary>
        /// Attempts to trim the scale (decimal places) of a decimal value to maxScale by truncating excess digits.
        /// Returns true if trimming succeeded and produced a valid decimal within maxScale, otherwise false.
        /// </summary>
        public static bool TryTrimDecimalScale(decimal value, int maxScale, out decimal trimmedValue)
        {
            trimmedValue = value;
            if (maxScale < 0) return false;

            try
            {
                string strVal = value.ToString(CultureInfo.InvariantCulture);
                int dotIndex = strVal.IndexOf('.');
                if (dotIndex < 0)
                {
                    trimmedValue = value;
                    return true;
                }

                string trimmedStr;
                if (maxScale == 0)
                {
                    trimmedStr = strVal.Substring(0, dotIndex);
                    if (string.IsNullOrEmpty(trimmedStr) || trimmedStr == "-" || trimmedStr == "+")
                    {
                        trimmedStr = "0";
                    }
                }
                else
                {
                    int targetLength = dotIndex + 1 + maxScale;
                    if (targetLength >= strVal.Length)
                    {
                        trimmedValue = value;
                        return true;
                    }

                    trimmedStr = strVal.Substring(0, targetLength);
                }

                if (decimal.TryParse(trimmedStr, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out decimal parsed))
                {
                    string normalized = Math.Abs(parsed).ToString(CultureInfo.InvariantCulture);
                    string[] parts = normalized.Split('.');
                    int scaleDigits = parts.Length > 1 ? parts[1].TrimEnd('0').Length : 0;
                    if (scaleDigits <= maxScale)
                    {
                        trimmedValue = parsed;
                        return true;
                    }
                }
            }
            catch
            {
                // Fallback to failure
            }

            return false;
        }

        private void ValidatePrecisionAndScale(string columnName, ref decimal decVal, int precision, int scale, StringBuilder errors)
        {
            int maxIntegerDigits = precision - scale;
            string normalized = Math.Abs(decVal).ToString(CultureInfo.InvariantCulture);
            string[] parts = normalized.Split('.');
            string integerPart = parts[0].TrimStart('0');
            int integerDigits = string.IsNullOrEmpty(integerPart) ? 0 : integerPart.Length;
            int scaleDigits = parts.Length > 1 ? parts[1].TrimEnd('0').Length : 0;

            if (scaleDigits > scale)
            {
                bool trimSuccess = false;
                if (ApplyTrim && TryTrimDecimalScale(decVal, scale, out decimal trimmedVal))
                {
                    decVal = trimmedVal;
                    normalized = Math.Abs(decVal).ToString(CultureInfo.InvariantCulture);
                    parts = normalized.Split('.');
                    integerPart = parts[0].TrimStart('0');
                    integerDigits = string.IsNullOrEmpty(integerPart) ? 0 : integerPart.Length;
                    scaleDigits = parts.Length > 1 ? parts[1].TrimEnd('0').Length : 0;
                    trimSuccess = scaleDigits <= scale;
                }

                if (!trimSuccess)
                {
                    errors.Append($"[{columnName}] scale ({scaleDigits} decimals) exceeds max scale {scale} | ");
                }
            }

            if (integerDigits > maxIntegerDigits)
            {
                errors.Append($"[{columnName}] integer part ({integerDigits} digits) exceeds precision ({precision},{scale}) | ");
            }
        }

        private static bool IsStringDataType(string dataType)
        {
            return dataType.ToLowerInvariant() switch
            {
                "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext" => true,
                _ => false
            };
        }

        #endregion
    }
}
