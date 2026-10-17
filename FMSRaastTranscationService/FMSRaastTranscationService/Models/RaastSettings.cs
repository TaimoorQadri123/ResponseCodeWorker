using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using FMSISSTransactionService.Helpers;

namespace FMSRaastService.Models;

public sealed class RaastSettings
{
    public const string SectionName = "RaastSettings";

    // Schema is concatenated into SQL, so allow only SCHEMA.TABLE identifiers
    [Required, RegularExpression(@"^[A-Za-z0-9_$#]+(\.[A-Za-z0-9_$#]+)?$")]
    public string TransactionSchema { get; set; } = string.Empty;

    [Range(1, 100000)]
    public int TranCount { get; set; } = 1000;

    [Required]
    public string LogPath { get; set; } = string.Empty;

    [Range(0, int.MaxValue)]
    public int CycleDelay { get; set; } = 1000;

    // Local SQL Server tables; also concatenated into SQL, so same identifier rule applies
    [Required, RegularExpression(@"^[A-Za-z0-9_$#]+(\.[A-Za-z0-9_$#]+)?$")]
    public string LocalSchema { get; set; } = "dbo.RaastTransaction";

    [Required, RegularExpression(@"^[A-Za-z0-9_$#]+(\.[A-Za-z0-9_$#]+)?$")]
    public string LocalHistorySchema { get; set; } = "dbo.RaastTransactionHistory";

    [Required, RegularExpression(@"^[A-Za-z0-9_$#]+(\.[A-Za-z0-9_$#]+)?$")]
    public string LocalFailedSchema { get; set; } = "dbo.RaastTransactionFailed";

    // Trim over-length strings / extra decimals instead of failing the row
    public bool ApplyTrim { get; set; } = true;

    /// <summary>
    /// Fills settings from the RaastSettings section, decrypting the AES-encrypted values.
    /// Numeric values may be plain (e.g. "1000") or encrypted.
    /// </summary>
    public void LoadFrom(IConfigurationSection section)
    {
        TransactionSchema = DecryptRequired(section, nameof(TransactionSchema));
        LogPath = DecryptRequired(section, nameof(LogPath));
        LocalSchema = DecryptRequired(section, nameof(LocalSchema));
        LocalHistorySchema = DecryptRequired(section, nameof(LocalHistorySchema));
        LocalFailedSchema = DecryptRequired(section, nameof(LocalFailedSchema));

        TranCount = ReadInt(section, nameof(TranCount), TranCount);
        CycleDelay = ReadInt(section, nameof(CycleDelay), CycleDelay);
        ApplyTrim = section.GetValue(nameof(ApplyTrim), ApplyTrim);
    }

    private static string DecryptRequired(IConfigurationSection section, string key)
    {
        string value = section[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{SectionName}:{key} is missing.");

        try
        {
            return Decryption.DecryptString(value).Trim();
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidOperationException($"{SectionName}:{key} could not be decrypted.", ex);
        }
    }

    private static int ReadInt(IConfigurationSection section, string key, int defaultValue)
    {
        string value = section[key];
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (int.TryParse(value, out int plain))
            return plain;

        string decrypted = DecryptRequired(section, key);
        return int.TryParse(decrypted, out int parsed)
            ? parsed
            : throw new InvalidOperationException($"{SectionName}:{key} is not a valid number.");
    }
}