// -----------------------------------------------------------------------------
// Fatura.Core — FaturaEngine.cs
// Multimodal receipt/invoice extraction via the Gemini API (Gemma 4 by default)
// with deterministic arithmetic validation and an automated self-correction loop.
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fatura.Core;

/// <summary>
/// Extracts structured <see cref="ReceiptData"/> from receipt/invoice images (Turkish, Arabic, English),
/// validates the arithmetic deterministically, and asks the model to re-examine the image when the
/// numbers do not add up.
/// </summary>
/// <remarks>
/// Thread-safe and intended to be long-lived: create one instance and reuse it, or inject an
/// <see cref="HttpClient"/> obtained from <c>IHttpClientFactory</c>.
/// </remarks>
public sealed class FaturaEngine : IDisposable
{
    /// <summary>Default model used when none is supplied.</summary>
    public const string DefaultModelName = "gemma-4-31b-it";

    /// <summary>Maximum allowed absolute difference between calculated and extracted totals.</summary>
    public const decimal Tolerance = 0.05m;

    /// <summary>The Gemini inline request limit is ~20 MB; base64 adds ~33 %, so cap raw bytes below that.</summary>
    public const int MaxImageBytes = 14 * 1024 * 1024;

    private const int MaxTransientRetries = 3;
    private const double Temperature = 0.1;

    /// <summary>Default Gemini endpoint. Override by giving the injected <see cref="HttpClient"/> a BaseAddress.</summary>
    public static readonly Uri DefaultBaseAddress = new("https://generativelanguage.googleapis.com/v1beta/");

    private static readonly HashSet<string> SupportedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/heic", "image/heif",
    };

    /// <summary>Serializer options for the Gemini REST API (camelCase, nulls omitted).</summary>
    private static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Lenient options for parsing the model's JSON answer.</summary>
    private static readonly JsonSerializerOptions ModelOutputJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _baseAddress;

    /// <summary>Gets the model name used for extraction.</summary>
    public string ModelName { get; }

    /// <summary>
    /// Language for <see cref="ReceiptItem.NameTranslated"/> (e.g. "English", "Arabic", "Turkish").
    /// Defaults to English. Callers exposing this to end users should whitelist the values.
    /// </summary>
    public string TranslationLanguage
    {
        get => _translationLanguage;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _translationLanguage = value.Trim();
        }
    }

    private readonly string _translationLanguage = "English";

    /// <summary>
    /// No new self-correction pass is started once this much time has elapsed for a receipt, so a slow or
    /// overloaded model can't keep a user waiting for many minutes. Defaults to 150 seconds.
    /// </summary>
    public TimeSpan CorrectionTimeBudget { get; init; } = TimeSpan.FromSeconds(150);

    /// <summary>
    /// Optional backup model (e.g. <c>gemma-4-31b-it</c>). If the primary model fails with a server error,
    /// timeout, quota or "model not found" error, the call is retried once with this model. The audit log's
    /// <see cref="ProcessingAuditLog.ModelName"/> always reports the model that actually produced the result.
    /// </summary>
    public string? FallbackModelName
    {
        get => _fallbackModelName;
        init => _fallbackModelName = string.IsNullOrWhiteSpace(value) ? null : NormalizeModelName(value);
    }

    private readonly string? _fallbackModelName;

    /// <summary>Creates an engine that owns its own <see cref="HttpClient"/>.</summary>
    /// <param name="apiKey">Gemini API key (load it from an environment variable or a secret store — never hard-code it).</param>
    /// <param name="modelName">Model id, e.g. <c>gemma-4-31b-it</c> or <c>gemini-2.5-flash</c>.</param>
    public FaturaEngine(string apiKey, string modelName = DefaultModelName)
        : this(apiKey, modelName, new HttpClient { Timeout = TimeSpan.FromMinutes(3) }, ownsHttpClient: true)
    {
    }

    /// <summary>Creates an engine that uses an externally managed <see cref="HttpClient"/> (DI / IHttpClientFactory / tests).</summary>
    public FaturaEngine(string apiKey, string modelName, HttpClient httpClient)
        : this(apiKey, modelName, httpClient, ownsHttpClient: false)
    {
    }

    private FaturaEngine(string apiKey, string modelName, HttpClient httpClient, bool ownsHttpClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(httpClient);

        _apiKey = apiKey;
        ModelName = NormalizeModelName(modelName);
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _baseAddress = httpClient.BaseAddress ?? DefaultBaseAddress;
    }

    private static string NormalizeModelName(string name)
    {
        name = name.Trim();
        return name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name;
    }

    /// <summary>Per-call state: which model is being used and whether it accepts native JSON mode.</summary>
    private sealed class ModelTarget(string name, Uri uri)
    {
        public string Name { get; } = name;
        public Uri Uri { get; } = uri;

        // Gemma models on the Gemini API fail on native structured output (observed: 500 INTERNAL after a
        // long wait), so skip it up-front for them and rely on the prompt-embedded schema instead.
        public bool NativeJsonMode { get; set; } = !name.StartsWith("gemma", StringComparison.OrdinalIgnoreCase);

        // Gemini "thinking" models spend most of their latency reasoning. Receipt reading doesn't need deep
        // reasoning, so ask for a low thinking level; dropped automatically if the model rejects the setting.
        public bool LowThinking { get; set; } = !name.StartsWith("gemma", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CallState(ModelTarget primary)
    {
        public ModelTarget Current { get; set; } = primary;
        public bool UsedFallback { get; set; }
    }

    private ModelTarget CreateTarget(string modelName) =>
        new(modelName, new Uri(_baseAddress, $"models/{Uri.EscapeDataString(modelName)}:generateContent"));

    // ==========================================================================================
    // 1) Deterministic rules engine
    // ==========================================================================================

    /// <summary>
    /// Checks that the sum of item prices equals <see cref="ReceiptData.TotalAmount"/> within
    /// <see cref="Tolerance"/>. Tax-exclusive receipts (items + tax = total) are also accepted.
    /// </summary>
    /// <remarks>Pure and deterministic: no I/O and no model calls, so it is trivially unit-testable.</remarks>
    public static ValidationResult ValidateReceipt(ReceiptData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var items = data.Items ?? [];
        var total = data.TotalAmount;

        if (items.Count == 0)
        {
            return new ValidationResult(false, 0m, total,
                "No line items were extracted. Every purchased item printed on the receipt must be listed in \"items\".");
        }

        var itemSum = decimal.Round(items.Sum(i => i.Price), 2, MidpointRounding.AwayFromZero);

        // A zero/negative grand total means the total was not read (or the amounts are masked/illegible).
        // Without this rule an all-zero extraction would trivially "pass" (0 == 0).
        if (total <= 0)
        {
            return new ValidationResult(false, itemSum, total,
                $"total_amount is {total.ToString("0.00", CultureInfo.InvariantCulture)}, which is not a valid grand total. " +
                "Locate the grand-total line (TOPLAM / GENEL TOPLAM / الإجمالي / TOTAL) and read its amount. " +
                "If the amounts on the document are genuinely masked or illegible, keep 0 — do not guess.");
        }

        // Case 1: tax-inclusive prices (Turkish KDV and most retail receipts).
        if (Math.Abs(itemSum - total) <= Tolerance)
        {
            return new ValidationResult(true, itemSum, total, null);
        }

        // Case 2: tax-exclusive prices (typical B2B invoices): items + tax = total.
        var itemSumPlusTax = decimal.Round(itemSum + data.TaxAmount, 2, MidpointRounding.AwayFromZero);
        if (data.TaxAmount > 0 && Math.Abs(itemSumPlusTax - total) <= Tolerance)
        {
            return new ValidationResult(true, itemSumPlusTax, total, null);
        }

        return new ValidationResult(false, itemSum, total, BuildMismatchMessage(items, itemSum, data.TaxAmount, total));
    }

    private static string BuildMismatchMessage(IReadOnlyList<ReceiptItem> items, decimal itemSum, decimal tax, decimal total)
    {
        var inv = CultureInfo.InvariantCulture;
        var diff = itemSum - total;
        var sb = new StringBuilder();

        sb.Append(inv, $"Arithmetic mismatch: the sum of the {items.Count} item prices is {itemSum:0.00}, ")
          .Append(inv, $"but total_amount is {total:0.00} (difference {diff:+0.00;-0.00}, tolerance ±{Tolerance:0.00}).");

        if (tax > 0)
        {
            sb.Append(inv, $" Adding tax_amount ({tax:0.00}) gives {itemSum + tax:0.00}, which does not match either.");
        }

        sb.AppendLine().AppendLine("Items you extracted:");
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            sb.AppendLine(inv, $"  {i + 1}. \"{it.NameOriginal}\" qty={it.Quantity:0.###} price={it.Price:0.00}");
        }

        sb.Append(diff < 0
            ? "The items sum is LOWER than the total: an item may be missing or a price under-read."
            : "The items sum is HIGHER than the total: an item may be duplicated, a subtotal/discount line mis-handled, or a price over-read.");

        return sb.ToString();
    }

    // ==========================================================================================
    // 2) Core pipeline + 3) self-correction loop
    // ==========================================================================================

    /// <summary>
    /// Extracts structured data from a receipt image, validates it, and runs up to
    /// <paramref name="maxRetries"/> self-correction passes when validation fails.
    /// </summary>
    /// <param name="imageBytes">Raw image bytes.</param>
    /// <param name="imageMimeType">image/jpeg, image/png, image/webp, image/heic or image/heif.</param>
    /// <param name="maxRetries">Correction passes after the initial attempt (0–5). 2 ⇒ at most 3 model calls.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="progress">Optional callback invoked after each attempt (useful for live console/UI updates).</param>
    /// <returns>An audit log with the final data, its validation, and per-attempt history.</returns>
    /// <exception cref="ArgumentException">Invalid image, MIME type or retry count.</exception>
    /// <exception cref="FaturaApiException">The Gemini API returned a non-recoverable error.</exception>
    public async Task<ProcessingAuditLog> ProcessReceiptAsync(
        byte[] imageBytes,
        string imageMimeType = "image/jpeg",
        int maxRetries = 2,
        CancellationToken cancellationToken = default,
        IProgress<AttemptRecord>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
            throw new ArgumentException("Image is empty.", nameof(imageBytes));
        if (imageBytes.Length > MaxImageBytes)
            throw new ArgumentException($"Image exceeds the {MaxImageBytes / (1024 * 1024)} MB inline limit.", nameof(imageBytes));
        if (string.IsNullOrWhiteSpace(imageMimeType) || !SupportedMimeTypes.Contains(imageMimeType))
            throw new ArgumentException($"Unsupported MIME type '{imageMimeType}'.", nameof(imageMimeType));
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRetries, 5);

        var totalTimer = Stopwatch.StartNew();

        // Multi-turn conversation: the image is sent once in the first user turn. Each correction
        // appends (model's previous answer, user feedback) so the model sees exactly what it got wrong.
        var conversation = new List<GeminiContent>
        {
            new("user",
            [
                new GeminiPart { InlineData = new GeminiInlineData(imageMimeType.ToLowerInvariant(), Convert.ToBase64String(imageBytes)) },
                new GeminiPart { Text = Prompts.Extraction(TranslationLanguage) },
            ]),
        };

        var call = new CallState(CreateTarget(ModelName));
        var history = new List<AttemptRecord>(maxRetries + 1);
        ReceiptData? finalData = null;
        ValidationResult? finalValidation = null;
        var corrections = 0;

        void Record(AttemptRecord record)
        {
            history.Add(record);
            progress?.Report(record);
        }

        for (var attempt = 1; attempt <= maxRetries + 1; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attemptTimer = Stopwatch.StartNew();
            var isCorrection = attempt > 1;
            var hasRetriesLeft = attempt <= maxRetries && totalTimer.Elapsed < CorrectionTimeBudget;

            var rawText = await GenerateAsync(conversation, call, cancellationToken).ConfigureAwait(false);

            // (a) The answer isn't valid JSON → ask the model to fix the format.
            if (!TryParseReceipt(rawText, out var data, out var parseError))
            {
                Record(new AttemptRecord(attempt, isCorrection, null, parseError, attemptTimer.Elapsed));
                if (!hasRetriesLeft) break;

                corrections++;
                AppendCorrectionTurn(conversation, rawText, Prompts.InvalidJson(parseError));
                continue;
            }

            // (b) Valid JSON → run the deterministic arithmetic check.
            var validation = ValidateReceipt(data);
            finalData = data;
            finalValidation = validation;
            Record(new AttemptRecord(attempt, isCorrection, validation, null, attemptTimer.Elapsed));

            if (validation.IsValid || !hasRetriesLeft) break;

            // (c) Arithmetic failed → feed the exact error back and ask for a re-examination.
            corrections++;
            AppendCorrectionTurn(conversation, rawText, Prompts.ArithmeticCorrection(validation.ErrorMessage!, attempt));
        }

        return new ProcessingAuditLog(
            Attempts: history.Count,
            CorrectionsMade: corrections,
            FinalData: finalData,
            Validation: finalValidation,
            History: history,
            ModelName: call.Current.Name,
            TotalDuration: totalTimer.Elapsed,
            UsedFallbackModel: call.UsedFallback);
    }

    private static void AppendCorrectionTurn(List<GeminiContent> conversation, string previousAnswer, string feedback)
    {
        conversation.Add(new GeminiContent("model", [new GeminiPart { Text = previousAnswer }]));
        conversation.Add(new GeminiContent("user", [new GeminiPart { Text = feedback }]));
    }

    // ==========================================================================================
    // Model output parsing
    // ==========================================================================================

    /// <summary>Parses the model's raw text into <see cref="ReceiptData"/>, tolerating Markdown fences and prose.</summary>
    internal static bool TryParseReceipt(
        string rawText,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ReceiptData? data,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        data = null;
        error = null;

        var json = ExtractJsonObject(rawText);
        if (json is null)
        {
            error = "Response did not contain a JSON object.";
            return false;
        }

        ReceiptData? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ReceiptData>(json, ModelOutputJsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = "JSON deserialized to null.";
            return false;
        }

        // Normalize: guarantee a non-null item list, drop nameless items, upper-case the currency code.
        var items = (parsed.Items ?? [])
            .Where(i => i is not null && !string.IsNullOrWhiteSpace(i.NameOriginal))
            .ToArray();

        data = parsed with { Items = items, Currency = parsed.Currency?.Trim().ToUpperInvariant() };
        return true;
    }

    /// <summary>Returns the outermost <c>{ ... }</c> block of the text, or null.</summary>
    private static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    // ==========================================================================================
    // Gemini REST transport
    // ==========================================================================================

    private async Task<string> GenerateAsync(List<GeminiContent> conversation, CallState call, CancellationToken ct)
    {
        try
        {
            return await GenerateWithTargetAsync(conversation, call.Current, ct).ConfigureAwait(false);
        }
        catch (FaturaApiException ex) when (!call.UsedFallback && _fallbackModelName is not null &&
                                                 _fallbackModelName != call.Current.Name && IsModelFailure(ex))
        {
            // Primary model is down/overloaded/unavailable → switch to the backup for the rest of this call.
            call.Current = CreateTarget(_fallbackModelName);
            call.UsedFallback = true;
            return await GenerateWithTargetAsync(conversation, call.Current, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> GenerateWithTargetAsync(List<GeminiContent> conversation, ModelTarget target, CancellationToken ct)
    {
        // Start with the fastest settings and drop any optional setting the model rejects.
        // Each catch disables one flag, so this loop runs at most three times.
        while (true)
        {
            try
            {
                return await SendWithRetryAsync(conversation, target, ct).ConfigureAwait(false);
            }
            catch (FaturaApiException ex) when (target.LowThinking && ex.StatusCode == HttpStatusCode.BadRequest)
            {
                // The model doesn't support thinkingConfig → retry without it.
                target.LowThinking = false;
            }
            catch (FaturaApiException ex) when (target.NativeJsonMode && ShouldFallBackFromJsonMode(ex))
            {
                // The model rejected (or crashed on) responseMimeType/responseSchema.
                // The prompt already contains the full schema, so plain-text JSON works too.
                target.NativeJsonMode = false;
            }
        }
    }

    /// <summary>Errors where trying a different model can help (not bad keys or bad input).</summary>
    private static bool IsModelFailure(FaturaApiException ex) =>
        ex.StatusCode is HttpStatusCode.InternalServerError
                      or HttpStatusCode.BadGateway
                      or HttpStatusCode.ServiceUnavailable
                      or HttpStatusCode.GatewayTimeout
                      or HttpStatusCode.RequestTimeout
                      or HttpStatusCode.TooManyRequests
                      or HttpStatusCode.NotFound;

    /// <summary>
    /// Gemma models on the Gemini API either reject structured output with a 400 mentioning JSON/schema,
    /// or fail with a persistent 500 INTERNAL. In both cases we retry once without native JSON mode.
    /// </summary>
    private static bool ShouldFallBackFromJsonMode(FaturaApiException ex) =>
        ex.StatusCode == HttpStatusCode.InternalServerError ||
        (ex.StatusCode == HttpStatusCode.BadRequest &&
         ex.ResponseBody is { } body &&
         (body.Contains("json", StringComparison.OrdinalIgnoreCase) ||
          body.Contains("schema", StringComparison.OrdinalIgnoreCase) ||
          body.Contains("mime", StringComparison.OrdinalIgnoreCase)));

    private async Task<string> SendWithRetryAsync(List<GeminiContent> conversation, ModelTarget target, CancellationToken ct)
    {
        var uri = target.Uri;
        var useNativeJsonMode = target.NativeJsonMode;
        var request = new GeminiRequest(
            Contents: conversation,
            GenerationConfig: new GeminiGenerationConfig(
                Temperature: Temperature,
                ResponseMimeType: useNativeJsonMode ? "application/json" : null,
                ResponseSchema: useNativeJsonMode ? ResponseSchema.Value : null,
                ThinkingConfig: target.LowThinking ? new GeminiThinkingConfig("low") : null));

        for (var attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(request, options: ApiJsonOptions),
            };
            message.Headers.Add("x-goog-api-key", _apiKey); // header, not query string → key never appears in URLs/logs

            HttpResponseMessage response;
            try
            {
                response = await _httpClient
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaxTransientRetries && !ct.IsCancellationRequested)
            {
                // Network blip → back off and retry.
                await Task.Delay(GetBackoffDelay(null, attempt), ct).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
            {
                // Still unreachable after retries → a clear, typed error instead of a raw network exception.
                throw new FaturaApiException(HttpStatusCode.ServiceUnavailable,
                    $"Could not reach the Gemini API: {ex.Message}", innerException: ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // HttpClient.Timeout elapsed (not a user cancellation). Retrying a multi-minute call
                // would only multiply the wait, so fail fast with a clear message instead.
                throw new FaturaApiException(HttpStatusCode.RequestTimeout,
                    $"The model did not answer within {_httpClient.Timeout.TotalSeconds:0} s. " +
                    "It may be overloaded — try again later or use a faster model.",
                    innerException: ex);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadFromJsonAsync<GeminiResponse>(ApiJsonOptions, ct).ConfigureAwait(false);
                    return ExtractText(body);
                }

                // A 500 while native JSON mode is on is most likely caused by JSON mode itself:
                // don't burn transient retries on it — let GenerateAsync fall back immediately.
                var likelyJsonModeFailure = useNativeJsonMode && response.StatusCode == HttpStatusCode.InternalServerError;

                // A plain 500 rarely clears on its own: retry it once, then fail (and let the fallback model take over).
                var retryBudget = response.StatusCode == HttpStatusCode.InternalServerError ? 1 : MaxTransientRetries;

                if (IsTransient(response.StatusCode) && !likelyJsonModeFailure && attempt < retryBudget)
                {
                    await Task.Delay(GetBackoffDelay(response, attempt), ct).ConfigureAwait(false);
                    continue;
                }

                var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new FaturaApiException(
                    response.StatusCode,
                    $"Gemini API returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(errorBody, 2000)}",
                    errorBody);
            }
        }
    }

    private static string ExtractText(GeminiResponse? response)
    {
        if (response?.PromptFeedback?.BlockReason is { } blockReason)
            throw new FaturaApiException(HttpStatusCode.OK, $"Prompt was blocked by the API: {blockReason}.");

        var candidate = response?.Candidates?.FirstOrDefault()
            ?? throw new FaturaApiException(HttpStatusCode.OK, "Gemini API returned no candidates.");

        // Join the answer parts, skipping "thinking" parts that some models emit.
        var text = string.Concat(candidate.Content?.Parts?
            .Where(p => p.Thought != true && p.Text is not null)
            .Select(p => p.Text) ?? []);

        if (string.IsNullOrWhiteSpace(text))
            throw new FaturaApiException(HttpStatusCode.OK,
                $"Gemini API returned an empty answer (finishReason: {candidate.FinishReason ?? "unknown"}).");

        return text;
    }

    private static bool IsTransient(HttpStatusCode code) =>
        code is HttpStatusCode.TooManyRequests
             or HttpStatusCode.InternalServerError
             or HttpStatusCode.BadGateway
             or HttpStatusCode.ServiceUnavailable
             or HttpStatusCode.GatewayTimeout;

    private static TimeSpan GetBackoffDelay(HttpResponseMessage? response, int attempt)
    {
        if (response?.Headers.RetryAfter?.Delta is { } delta && delta < TimeSpan.FromSeconds(60))
            return delta;

        var baseMs = 1000 * Math.Pow(2, attempt); // 1s, 2s, 4s
        return TimeSpan.FromMilliseconds(baseMs + Random.Shared.Next(0, 250));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    // ==========================================================================================
    // Prompts
    // ==========================================================================================

    private static class Prompts
    {
        public static string Extraction(string translationLanguage) => $$"""
            You are Fatura, a precise receipt and invoice data-extraction engine.
            The attached image is a receipt or invoice that may be written in Turkish, Arabic, or English
            (or a mix). Read it carefully and extract the data below.

            RULES
            1. items: list EVERY purchased line item exactly once.
               - name_original: the item name exactly as printed, in its original language and script.
               - name_translated: the item name translated to {{translationLanguage}}.
               - quantity: numeric quantity (use 1 if not printed; weights like 0.750 kg are allowed).
               - price: the LINE TOTAL for that item (quantity × unit price) exactly as printed on the receipt.
               - Discounts (indirim / خصم / discount) printed as separate lines are items with a NEGATIVE price.
               - Do NOT include subtotal, total, tax, payment method, cash tendered, change, or loyalty lines as items.
            2. tax_amount: total tax printed (VAT, KDV / TOPKDV, ضريبة القيمة المضافة). Use 0 if none is printed.
            3. total_amount: the grand total actually payable (TOPLAM, GENEL TOPLAM, الإجمالي, المجموع, TOTAL).
            4. Numbers: plain JSON numbers, '.' as decimal separator, no thousands separators, no currency symbols.
               Turkish "1.234,56" → 1234.56. Convert Arabic-Indic digits (٠١٢٣٤٥٦٧٨٩) to Western digits.
            5. date: ISO-8601 "yyyy-MM-dd"; null if not readable.
            6. currency: ISO-4217 code (TL/₺ → TRY, ر.س/SR → SAR, د.إ → AED, $ → USD, € → EUR, ج.م → EGP).
            7. category: one of Groceries, Restaurant, Cafe, Fuel, Transport, Pharmacy, Healthcare, Electronics,
               Clothing, Utilities, Hotel, Entertainment, Services, Other.
            8. Never invent data. If a value is unreadable use null (strings) or 0 (numbers).

            OUTPUT
            Return ONLY a single JSON object, no Markdown and no commentary, with exactly this shape:
            {
              "store_name": string | null,
              "date": string | null,
              "currency": string | null,
              "category": string,
              "items": [ { "name_original": string, "name_translated": string, "quantity": number, "price": number } ],
              "tax_amount": number,
              "total_amount": number
            }
            """;

        public static string ArithmeticCorrection(string validationError, int attempt) => $"""
            VALIDATION FAILED (after attempt {attempt}). A deterministic arithmetic check rejected your extraction:

            {validationError}

            Re-examine the ORIGINAL IMAGE line by line. Common causes:
            - a line item was skipped, or one was listed twice;
            - a digit was misread (e.g. 1↔7, 3↔8, 5↔6) or the decimal separator was misinterpreted;
            - a unit price was used instead of the line total for an item with quantity > 1;
            - a discount line was omitted or not entered as a negative price;
            - a subtotal, tax, or payment line was wrongly included as an item;
            - the wrong line was chosen as the grand total.

            Report only what is actually printed; do NOT invent or adjust values just to make the numbers balance.
            Return the COMPLETE corrected JSON object only, in the same shape as before.
            """;

        public static string InvalidJson(string parseError) => $"""
            Your previous response could not be parsed: {parseError}
            Return ONLY one valid JSON object in the required shape, with no Markdown fences and no extra text.
            """;
    }

    // ==========================================================================================
    // Structured-output schema (OpenAPI subset accepted by the Gemini API)
    // ==========================================================================================

    private static readonly Lazy<JsonElement> ResponseSchema = new(() => JsonSerializer.SerializeToElement(new
    {
        type = "OBJECT",
        properties = new Dictionary<string, object>
        {
            ["store_name"] = new { type = "STRING", nullable = true },
            ["date"] = new { type = "STRING", nullable = true, description = "ISO-8601 yyyy-MM-dd" },
            ["currency"] = new { type = "STRING", nullable = true, description = "ISO-4217 code" },
            ["category"] = new { type = "STRING" },
            ["items"] = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new Dictionary<string, object>
                    {
                        ["name_original"] = new { type = "STRING" },
                        ["name_translated"] = new { type = "STRING" },
                        ["quantity"] = new { type = "NUMBER" },
                        ["price"] = new { type = "NUMBER", description = "Line total (quantity x unit price)" },
                    },
                    required = new[] { "name_original", "name_translated", "quantity", "price" },
                    propertyOrdering = new[] { "name_original", "name_translated", "quantity", "price" },
                },
            },
            ["tax_amount"] = new { type = "NUMBER" },
            ["total_amount"] = new { type = "NUMBER" },
        },
        required = new[] { "store_name", "date", "currency", "category", "items", "tax_amount", "total_amount" },
        propertyOrdering = new[] { "store_name", "date", "currency", "category", "items", "tax_amount", "total_amount" },
    }));

    // ==========================================================================================
    // Gemini REST DTOs (private — never leak transport types into the public API)
    // ==========================================================================================

    private sealed record GeminiRequest(List<GeminiContent> Contents, GeminiGenerationConfig GenerationConfig);

    private sealed record GeminiContent(string Role, List<GeminiPart> Parts);

    private sealed record GeminiPart
    {
        public string? Text { get; init; }
        public GeminiInlineData? InlineData { get; init; }
        public bool? Thought { get; init; }
    }

    private sealed record GeminiInlineData(string MimeType, string Data);

    private sealed record GeminiGenerationConfig(
        double Temperature, string? ResponseMimeType, JsonElement? ResponseSchema, GeminiThinkingConfig? ThinkingConfig);

    private sealed record GeminiThinkingConfig(string ThinkingLevel);

    private sealed record GeminiResponse(List<GeminiCandidate>? Candidates, GeminiPromptFeedback? PromptFeedback);

    private sealed record GeminiCandidate(GeminiContentResponse? Content, string? FinishReason);

    private sealed record GeminiContentResponse(List<GeminiPart>? Parts, string? Role);

    private sealed record GeminiPromptFeedback(string? BlockReason);
}

/// <summary>Thrown when the Gemini API returns a non-recoverable error or an unusable response.</summary>
public sealed class FaturaApiException(
    HttpStatusCode statusCode, string message, string? responseBody = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>HTTP status code (200 for semantic failures such as a blocked prompt or empty answer).</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>Raw error body returned by the API, if any.</summary>
    public string? ResponseBody { get; } = responseBody;
}
