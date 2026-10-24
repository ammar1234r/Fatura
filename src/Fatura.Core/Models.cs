// -----------------------------------------------------------------------------
// Fatura.Core — Models.cs
// Immutable data contracts for receipt extraction, validation and auditing.
// JSON names are snake_case (via JsonPropertyName) so the same records can be
// deserialized directly from the model's structured output.
// -----------------------------------------------------------------------------

using System.Text.Json.Serialization;

namespace Fatura.Core;

/// <summary>A single line item printed on a receipt or invoice.</summary>
/// <param name="NameOriginal">Item name exactly as printed (Turkish, Arabic, English, ...).</param>
/// <param name="NameTranslated">English translation of the item name.</param>
/// <param name="Quantity">Quantity purchased (may be fractional, e.g. 0.750 kg).</param>
/// <param name="Price">Line total for this item (quantity × unit price) as printed. Discounts are negative.</param>
public sealed record ReceiptItem(
    [property: JsonPropertyName("name_original")] string NameOriginal,
    [property: JsonPropertyName("name_translated")] string? NameTranslated,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("price")] decimal Price);

/// <summary>Structured data extracted from a receipt or invoice image.</summary>
/// <param name="StoreName">Merchant / store name.</param>
/// <param name="Date">Transaction date in ISO-8601 (yyyy-MM-dd) when readable.</param>
/// <param name="Currency">ISO-4217 currency code (TRY, SAR, AED, USD, EUR, ...).</param>
/// <param name="Category">High-level spending category (Groceries, Restaurant, Fuel, ...).</param>
/// <param name="Items">Purchased line items.</param>
/// <param name="TaxAmount">Total tax (VAT / KDV) printed on the receipt; 0 if absent.</param>
/// <param name="TotalAmount">Grand total actually payable.</param>
public sealed record ReceiptData(
    [property: JsonPropertyName("store_name")] string? StoreName,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("items")] IReadOnlyList<ReceiptItem> Items,
    [property: JsonPropertyName("tax_amount")] decimal TaxAmount,
    [property: JsonPropertyName("total_amount")] decimal TotalAmount);

/// <summary>Outcome of the deterministic arithmetic check.</summary>
/// <param name="IsValid">True when the item sum matches the total within tolerance.</param>
/// <param name="CalculatedTotal">Sum of item prices computed locally (plus tax for tax-exclusive receipts).</param>
/// <param name="ExtractedTotal">Total amount reported by the model.</param>
/// <param name="ErrorMessage">Model-readable description of the mismatch; null when valid.</param>
public sealed record ValidationResult(
    [property: JsonPropertyName("is_valid")] bool IsValid,
    [property: JsonPropertyName("calculated_total")] decimal CalculatedTotal,
    [property: JsonPropertyName("extracted_total")] decimal ExtractedTotal,
    [property: JsonPropertyName("error_message")] string? ErrorMessage);

/// <summary>Snapshot of one model call (the initial extraction or a self-correction).</summary>
/// <param name="AttemptNumber">1-based attempt index.</param>
/// <param name="IsCorrection">True if this attempt was a self-correction pass.</param>
/// <param name="Validation">Validation for this attempt; null if the response could not be parsed.</param>
/// <param name="Error">Parsing error for this attempt, if any.</param>
/// <param name="Duration">Wall-clock duration of the attempt.</param>
public sealed record AttemptRecord(
    [property: JsonPropertyName("attempt_number")] int AttemptNumber,
    [property: JsonPropertyName("is_correction")] bool IsCorrection,
    [property: JsonPropertyName("validation")] ValidationResult? Validation,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("duration")] TimeSpan Duration);

/// <summary>Full audit trail of a <see cref="FaturaEngine.ProcessReceiptAsync"/> run.</summary>
/// <param name="Attempts">Total number of model calls made.</param>
/// <param name="CorrectionsMade">Number of self-correction passes triggered.</param>
/// <param name="FinalData">Last successfully parsed receipt data (may still be invalid).</param>
/// <param name="Validation">Validation result of <paramref name="FinalData"/>.</param>
/// <param name="History">Per-attempt details, in order.</param>
/// <param name="ModelName">Model used for extraction.</param>
/// <param name="TotalDuration">End-to-end processing time.</param>
/// <param name="UsedFallbackModel">True if the primary model failed and the fallback model produced the result.</param>
public sealed record ProcessingAuditLog(
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("corrections_made")] int CorrectionsMade,
    [property: JsonPropertyName("final_data")] ReceiptData? FinalData,
    [property: JsonPropertyName("validation")] ValidationResult? Validation,
    [property: JsonPropertyName("history")] IReadOnlyList<AttemptRecord> History,
    [property: JsonPropertyName("model_name")] string ModelName,
    [property: JsonPropertyName("total_duration")] TimeSpan TotalDuration,
    [property: JsonPropertyName("used_fallback_model")] bool UsedFallbackModel = false)
{
    /// <summary>True when the final data passed arithmetic validation.</summary>
    [JsonIgnore]
    public bool IsSuccessful => Validation?.IsValid == true;
}
