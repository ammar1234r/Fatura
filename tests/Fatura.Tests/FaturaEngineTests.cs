// -----------------------------------------------------------------------------
// Fatura.Tests — FaturaEngineTests.cs
// Fast, offline unit tests: the rules engine, the JSON parser, and the
// self-correction loop (driven by a fake HTTP handler — no API key needed).
// -----------------------------------------------------------------------------

using System.Net;
using System.Text;
using System.Text.Json;
using Fatura.Core;
using Xunit;

namespace Fatura.Tests;

public class ValidateReceiptTests
{
    private static ReceiptData Receipt(decimal total, decimal tax, params decimal[] prices) =>
        new("Store", "2026-10-01", "TRY", "Groceries",
            prices.Select((p, i) => new ReceiptItem($"Item {i + 1}", null, 1, p)).ToArray(),
            tax, total);

    [Fact]
    public void ExactMatch_IsValid()
    {
        var result = FaturaEngine.ValidateReceipt(Receipt(30.00m, 0m, 10.00m, 20.00m));

        Assert.True(result.IsValid);
        Assert.Equal(30.00m, result.CalculatedTotal);
        Assert.Null(result.ErrorMessage);
    }

    [Theory]
    [InlineData(30.05)]  // on the upper boundary
    [InlineData(29.95)]  // on the lower boundary
    [InlineData(30.03)]
    public void DifferenceWithinTolerance_IsValid(double total)
    {
        Assert.True(FaturaEngine.ValidateReceipt(Receipt((decimal)total, 0m, 10m, 20m)).IsValid);
    }

    [Theory]
    [InlineData(30.06)]
    [InlineData(29.94)]
    public void DifferenceOutsideTolerance_IsInvalid(double total)
    {
        Assert.False(FaturaEngine.ValidateReceipt(Receipt((decimal)total, 0m, 10m, 20m)).IsValid);
    }

    [Fact]
    public void TaxExclusiveInvoice_ItemsPlusTaxEqualsTotal_IsValid()
    {
        var result = FaturaEngine.ValidateReceipt(Receipt(total: 118m, tax: 18m, 100m));

        Assert.True(result.IsValid);
        Assert.Equal(118m, result.CalculatedTotal);
    }

    [Fact]
    public void DiscountLine_WithNegativePrice_IsSummedCorrectly()
    {
        Assert.True(FaturaEngine.ValidateReceipt(Receipt(45m, 0m, 50m, -5m)).IsValid);
    }

    [Fact]
    public void Mismatch_ProducesDetailedErrorForTheModel()
    {
        var result = FaturaEngine.ValidateReceipt(Receipt(25.50m, 0m, 10m));

        Assert.False(result.IsValid);
        Assert.Equal(10m, result.CalculatedTotal);
        Assert.Equal(25.50m, result.ExtractedTotal);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("10.00", result.ErrorMessage);
        Assert.Contains("25.50", result.ErrorMessage);
        Assert.Contains("LOWER", result.ErrorMessage);
    }

    [Theory]
    [InlineData(0, 0)]     // masked/unreadable amounts: 0 == 0 must NOT pass
    [InlineData(-5, -5)]
    public void ZeroOrNegativeTotal_IsInvalid(double total, double price)
    {
        var result = FaturaEngine.ValidateReceipt(Receipt((decimal)total, 0m, (decimal)price));

        Assert.False(result.IsValid);
        Assert.Contains("not a valid grand total", result.ErrorMessage);
    }

    [Fact]
    public void NoItems_IsInvalid()
    {
        var result = FaturaEngine.ValidateReceipt(Receipt(10m, 0m));

        Assert.False(result.IsValid);
        Assert.Contains("No line items", result.ErrorMessage);
    }
}

public class ParsingTests
{
    [Fact]
    public void ParsesJsonWrappedInMarkdownFences_AndStringNumbers()
    {
        const string raw = """
            ```json
            {"store_name":"Migros","currency":"try","items":[{"name_original":"Ekmek","quantity":"1","price":"12.50"}],
             "tax_amount":0,"total_amount":12.5}
            ```
            """;

        Assert.True(FaturaEngine.TryParseReceipt(raw, out var data, out _));
        Assert.NotNull(data);
        Assert.Equal("TRY", data.Currency);           // normalized to upper case
        Assert.Equal(12.50m, data.Items.Single().Price);
    }

    [Fact]
    public void MissingItemsArray_BecomesEmptyList()
    {
        Assert.True(FaturaEngine.TryParseReceipt("""{"total_amount": 5}""", out var data, out _));
        Assert.NotNull(data);
        Assert.Empty(data.Items);
    }

    [Theory]
    [InlineData("I could not read the receipt.")]
    [InlineData("{ not valid json ")]
    [InlineData("")]
    public void InvalidOutput_ReturnsFalseWithError(string raw)
    {
        Assert.False(FaturaEngine.TryParseReceipt(raw, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}

public class SelfCorrectionLoopTests
{
    private static readonly byte[] FakeImage = [0xFF, 0xD8, 0xFF];

    private const string WrongJson =
        """{"store_name":"Migros","items":[{"name_original":"Ekmek","quantity":1,"price":10.00}],"tax_amount":0,"total_amount":25.50}""";

    private const string CorrectJson =
        """{"store_name":"Migros","items":[{"name_original":"Ekmek","quantity":1,"price":10.00},{"name_original":"Süt","quantity":1,"price":15.50}],"tax_amount":0,"total_amount":25.50}""";

    [Fact]
    public async Task WrongFirstAnswer_IsCorrectedOnSecondAttempt()
    {
        var handler = new FakeGeminiHandler(WrongJson, CorrectJson);
        using var engine = new FaturaEngine("test-key", "gemma-4-31b-it", new HttpClient(handler));

        var audit = await engine.ProcessReceiptAsync(FakeImage, "image/jpeg", maxRetries: 2);

        Assert.True(audit.IsSuccessful);
        Assert.Equal(2, audit.Attempts);
        Assert.Equal(1, audit.CorrectionsMade);
        Assert.False(audit.History[0].Validation!.IsValid);
        Assert.True(audit.History[1].Validation!.IsValid);

        // The 2nd request must contain the exact arithmetic error and the full conversation (user, model, user).
        var secondRequest = handler.RequestBodies[1];
        Assert.Contains("VALIDATION FAILED", secondRequest);
        Assert.Contains("Arithmetic mismatch", secondRequest);
        Assert.Equal(3, CountTurns(secondRequest));
    }

    [Fact]
    public async Task AlwaysWrong_StopsAfterMaxRetries()
    {
        var handler = new FakeGeminiHandler(WrongJson, WrongJson, WrongJson, WrongJson);
        using var engine = new FaturaEngine("test-key", "gemma-4-31b-it", new HttpClient(handler));

        var audit = await engine.ProcessReceiptAsync(FakeImage, "image/jpeg", maxRetries: 2);

        Assert.False(audit.IsSuccessful);
        Assert.Equal(3, audit.Attempts);          // 1 initial + 2 corrections
        Assert.Equal(2, audit.CorrectionsMade);
        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.NotNull(audit.FinalData);
    }

    [Fact]
    public async Task InvalidJson_TriggersFormatCorrection()
    {
        var handler = new FakeGeminiHandler("Sorry, here is the data: total 25.50", CorrectJson);
        using var engine = new FaturaEngine("test-key", "gemma-4-31b-it", new HttpClient(handler));

        var audit = await engine.ProcessReceiptAsync(FakeImage);

        Assert.True(audit.IsSuccessful);
        Assert.NotNull(audit.History[0].Error);
        Assert.Contains("could not be parsed", handler.RequestBodies[1]);
    }

    [Fact]
    public async Task JsonModeRejected_FallsBackToPromptOnlyJson()
    {
        var handler = new FakeGeminiHandler(CorrectJson) { RejectJsonMode = true };
        using var engine = new FaturaEngine("test-key", "gemini-2.5-flash", new HttpClient(handler));

        var audit = await engine.ProcessReceiptAsync(FakeImage);

        Assert.True(audit.IsSuccessful);
        Assert.Contains("responseSchema", handler.RequestBodies[0]);
        Assert.DoesNotContain("responseSchema", handler.RequestBodies[^1]);
    }

    [Fact]
    public async Task JsonModeCausesServer500_FallsBackToPromptOnlyJson()
    {
        // Real-world behavior seen with gemma-4-31b-it: structured output → persistent 500 INTERNAL.
        var handler = new FakeGeminiHandler(CorrectJson) { CrashOnJsonMode = true };
        using var engine = new FaturaEngine("test-key", "gemini-2.5-flash", new HttpClient(handler));

        var audit = await engine.ProcessReceiptAsync(FakeImage);

        Assert.True(audit.IsSuccessful);
        Assert.Equal(2, handler.RequestBodies.Count);   // no wasted transient retries on the JSON-mode 500
        Assert.Contains("responseSchema", handler.RequestBodies[0]);
        Assert.DoesNotContain("responseSchema", handler.RequestBodies[1]);
    }

    [Fact]
    public async Task GemmaModels_SkipNativeJsonMode_FromTheStart()
    {
        var handler = new FakeGeminiHandler(CorrectJson) { CrashOnJsonMode = true };
        using var engine = new FaturaEngine("test-key", "gemma-4-31b-it", new HttpClient(handler));

        var audit = await engine.ProcessReceiptAsync(FakeImage);

        Assert.True(audit.IsSuccessful);
        Assert.Single(handler.RequestBodies);
        Assert.DoesNotContain("responseSchema", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Progress_IsReportedForEveryAttempt()
    {
        var handler = new FakeGeminiHandler(WrongJson, CorrectJson);
        using var engine = new FaturaEngine("test-key", "gemma-4-31b-it", new HttpClient(handler));
        var reported = new List<AttemptRecord>();

        await engine.ProcessReceiptAsync(FakeImage, "image/jpeg", 2, CancellationToken.None, new SyncProgress(reported.Add));

        Assert.Equal(2, reported.Count);
        Assert.False(reported[0].Validation!.IsValid);
        Assert.True(reported[1].Validation!.IsValid);
    }

    private sealed class SyncProgress(Action<AttemptRecord> onReport) : IProgress<AttemptRecord>
    {
        public void Report(AttemptRecord value) => onReport(value);
    }

    [Fact]
    public async Task SlowModel_TimesOutOnce_WithClearError_InsteadOfRetrying()
    {
        var handler = new FakeGeminiHandler(CorrectJson) { Delay = TimeSpan.FromSeconds(10) };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(200) };
        using var engine = new FaturaEngine("test-key", "gemma-4-31b-it", http);

        var ex = await Assert.ThrowsAsync<FaturaApiException>(() => engine.ProcessReceiptAsync(FakeImage));

        Assert.Equal(HttpStatusCode.RequestTimeout, ex.StatusCode);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public async Task TranslationLanguage_IsInjectedIntoThePrompt()
    {
        var handler = new FakeGeminiHandler(CorrectJson);
        using var engine = new FaturaEngine("test-key", "gemma-4-31b-it", new HttpClient(handler))
        {
            TranslationLanguage = "Arabic",
        };

        await engine.ProcessReceiptAsync(FakeImage);

        Assert.Contains("translated to Arabic", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task HttpClientBaseAddress_OverridesTheGeminiEndpoint()
    {
        var handler = new FakeGeminiHandler(CorrectJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:9999/v1beta/") };
        using var engine = new FaturaEngine("test-key", "gemma-4-31b-it", http);

        await engine.ProcessReceiptAsync(FakeImage);

        Assert.Equal("http://localhost:9999/v1beta/models/gemma-4-31b-it:generateContent", handler.RequestUris[0]);
    }

    [Fact]
    public async Task PrimaryModelDown_FallsBackToBackupModel_AndReportsIt()
    {
        var handler = new FakeGeminiHandler(CorrectJson) { FailingModel = "gemma-4-31b-it" };
        using var engine = new FaturaEngine("test-key", "gemma-4-31b-it", new HttpClient(handler))
        {
            FallbackModelName = "gemini-3.8-flash",
        };

        var audit = await engine.ProcessReceiptAsync(FakeImage);

        Assert.True(audit.IsSuccessful);
        Assert.True(audit.UsedFallbackModel);
        Assert.Equal("gemini-3.8-flash", audit.ModelName);
        Assert.Equal(3, handler.RequestUris.Count);   // gemma + 1 retry on 500, then the backup
        Assert.Contains("gemini-3.8-flash", handler.RequestUris[^1]);
    }

    [Fact]
    public async Task PrimaryModelDown_WithoutFallback_ThrowsAfterOneRetry()
    {
        var handler = new FakeGeminiHandler(CorrectJson) { FailingModel = "gemma-4-31b-it" };
        using var engine = new FaturaEngine("test-key", "gemma-4-31b-it", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<FaturaApiException>(() => engine.ProcessReceiptAsync(FakeImage));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task ThinkingConfigRejected_RetriesWithoutIt()
    {
        var handler = new FakeGeminiHandler(CorrectJson) { RejectThinking = true };
        using var engine = new FaturaEngine("test-key", "gemini-3.8-flash", new HttpClient(handler));

        var audit = await engine.ProcessReceiptAsync(FakeImage);

        Assert.True(audit.IsSuccessful);
        Assert.Contains("\"thinkingLevel\":\"low\"", handler.RequestBodies[0]);
        Assert.DoesNotContain("thinkingConfig", handler.RequestBodies[1]);
        Assert.Contains("responseSchema", handler.RequestBodies[1]);   // JSON mode is kept
    }

    [Fact]
    public async Task GemmaModels_DoNotSendThinkingConfig()
    {
        var handler = new FakeGeminiHandler(CorrectJson);
        using var engine = new FaturaEngine("test-key", "gemma-4-31b-it", new HttpClient(handler));

        await engine.ProcessReceiptAsync(FakeImage);

        Assert.DoesNotContain("thinkingConfig", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task CorrectionTimeBudget_StopsFurtherCorrections()
    {
        var handler = new FakeGeminiHandler(WrongJson, WrongJson, WrongJson);
        using var engine = new FaturaEngine("test-key", "gemma-4-31b-it", new HttpClient(handler))
        {
            CorrectionTimeBudget = TimeSpan.Zero,
        };

        var audit = await engine.ProcessReceiptAsync(FakeImage, "image/jpeg", maxRetries: 2);

        Assert.Equal(1, audit.Attempts);
        Assert.False(audit.IsSuccessful);
    }

    [Fact]
    public async Task NetworkDown_ThrowsTypedApiException()
    {
        var handler = new FakeGeminiHandler(CorrectJson) { NetworkDown = true };
        using var engine = new FaturaEngine("test-key", "gemma-4-31b-it", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<FaturaApiException>(() => engine.ProcessReceiptAsync(FakeImage));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
    }

    [Fact]
    public async Task InvalidApiKey_ThrowsApiException()
    {
        var handler = new FakeGeminiHandler { FixedError = (HttpStatusCode.Forbidden, """{"error":"PERMISSION_DENIED"}""") };
        using var engine = new FaturaEngine("bad-key", "gemma-4-31b-it", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<FaturaApiException>(() => engine.ProcessReceiptAsync(FakeImage));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Fact]
    public async Task InvalidArguments_AreRejectedBeforeAnyHttpCall()
    {
        var handler = new FakeGeminiHandler(CorrectJson);
        using var engine = new FaturaEngine("test-key", "gemma-4-31b-it", new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentException>(() => engine.ProcessReceiptAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.ProcessReceiptAsync(FakeImage, "application/pdf"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.ProcessReceiptAsync(FakeImage, "image/jpeg", -1));
        Assert.Empty(handler.RequestBodies);
    }

    private static int CountTurns(string requestJson)
    {
        using var doc = JsonDocument.Parse(requestJson);
        return doc.RootElement.GetProperty("contents").GetArrayLength();
    }
}

/// <summary>
/// Fake HTTP handler that impersonates the Gemini generateContent endpoint.
/// Returns the queued model answers in order and records every request body.
/// </summary>
internal sealed class FakeGeminiHandler(params string[] modelAnswers) : HttpMessageHandler
{
    private readonly Queue<string> _answers = new(modelAnswers);

    public List<string> RequestBodies { get; } = [];
    public List<string> RequestUris { get; } = [];
    public bool RejectJsonMode { get; init; }
    public bool CrashOnJsonMode { get; init; }
    public bool RejectThinking { get; init; }
    public bool NetworkDown { get; init; }
    public string? FailingModel { get; init; }
    public TimeSpan Delay { get; init; }
    public (HttpStatusCode Status, string Body)? FixedError { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        RequestBodies.Add(body);
        RequestUris.Add(request.RequestUri!.ToString());
        if (NetworkDown)
            throw new HttpRequestException("Connection refused");

        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);

        if (FixedError is { } err)
            return new HttpResponseMessage(err.Status) { Content = new StringContent(err.Body) };

        if (FailingModel is not null && request.RequestUri!.ToString().Contains(FailingModel))
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"error":{"code":500,"message":"Internal error encountered.","status":"INTERNAL"}}"""),
            };

        if (RejectThinking && body.Contains("thinkingConfig"))
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"code":400,"message":"Invalid JSON payload received. Unknown name \"thinkingConfig\""}}"""),
            };

        if (CrashOnJsonMode && body.Contains("responseSchema"))
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"error":{"code":500,"message":"Internal error encountered.","status":"INTERNAL"}}"""),
            };

        if (RejectJsonMode && body.Contains("responseSchema"))
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"code":400,"message":"JSON mode is not enabled for this model"}}"""),
            };

        var answer = _answers.Dequeue();
        var payload = new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        role = "model",
                        parts = new object[] { new { text = "thinking...", thought = true }, new { text = answer } },
                    },
                    finishReason = "STOP",
                },
            },
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }
}
