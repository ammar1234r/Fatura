// -----------------------------------------------------------------------------
// Fatura.Api — Program.cs
// Thin HTTP layer over Fatura.Core so any front end (the Sijill Streamlit UI,
// a mobile app, ...) can send a receipt image and get the audited JSON back.
//
//   POST /api/receipts/process   multipart/form-data: image, model?, language?
//        header X-Gemini-Api-Key  (optional if GEMINI_API_KEY is set on the server)
//        header X-Client-Id       (visitor/session id from the UI, used for per-visitor rate limiting)
//   GET  /health
//
// Public-site design: the site owner sets GEMINI_API_KEY on the server, so visiting companies
// never need their own key. Images are processed in memory and never stored.
// -----------------------------------------------------------------------------

using System.Net;
using System.Threading.RateLimiting;
using Fatura.Core;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// One pooled HttpClient for all Gemini calls. Gemini:BaseUrl can point at a mock server for testing.
builder.Services.AddHttpClient("gemini", client =>
{
    client.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Gemini:TimeoutSeconds", 120));
    var baseUrl = builder.Configuration["Gemini:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl)) client.BaseAddress = new Uri(baseUrl);
});

builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = FaturaEngine.MaxImageBytes + 1024 * 1024);
builder.Services.AddProblemDetails();

// Protect the owner's Gemini quota on a public site: N receipts per visitor per minute.
// The UI forwards a per-session X-Client-Id; direct callers are partitioned by IP.
var permitsPerMinute = builder.Configuration.GetValue("RateLimit:ReceiptsPerMinute", 10);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("per-visitor", http =>
    {
        var clientId = http.Request.Headers["X-Client-Id"].ToString();
        var key = string.IsNullOrWhiteSpace(clientId)
            ? $"ip:{http.Connection.RemoteIpAddress}"
            : $"client:{clientId[..Math.Min(clientId.Length, 64)]}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseRateLimiter();

// hasServerKey tells the UI whether to ask visitors for their own key (false) or not (true).
app.MapGet("/health", (IConfiguration config) => Results.Ok(new
{
    status = "ok",
    models = config.GetSection("Gemini:Models").Get<string[]>(),
    hasServerKey = !string.IsNullOrWhiteSpace(config["GEMINI_API_KEY"]),
}));

app.MapPost("/api/receipts/process", async (
        IFormFile image,
        [FromForm] string? model,
        [FromForm] string? language,
        [FromHeader(Name = "X-Gemini-Api-Key")] string? apiKey,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<Program> logger,
        CancellationToken ct) =>
    {
        // The key comes from the UI per request; fall back to the server's own key if configured.
        apiKey = string.IsNullOrWhiteSpace(apiKey) ? config["GEMINI_API_KEY"] : apiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return Results.Problem("Enter a Gemini API key in the settings.", statusCode: StatusCodes.Status401Unauthorized);

        // Whitelist the translation language so user input never flows into the prompt as free text.
        var translationLanguage = (language ?? "en").Trim().ToLowerInvariant() switch
        {
            "ar" or "arabic" => "Arabic",
            "tr" or "turkish" => "Turkish",
            _ => "English",
        };

        // Models offered by the site; the first one is the default, the next different one is the fallback.
        var models = config.GetSection("Gemini:Models").Get<string[]>() ?? ["gemini-3.8-flash", FaturaEngine.DefaultModelName];
        var modelName = string.IsNullOrWhiteSpace(model) ? models[0] : model.Trim();
        var fallbackModel = models.FirstOrDefault(m => !string.Equals(m, modelName, StringComparison.OrdinalIgnoreCase));
        var mimeType = string.IsNullOrWhiteSpace(image.ContentType) ? "image/jpeg" : image.ContentType;

        byte[] bytes;
        await using (var stream = image.OpenReadStream())
        using (var ms = new MemoryStream((int)image.Length))
        {
            await stream.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        using var engine = new FaturaEngine(apiKey, modelName, httpClientFactory.CreateClient("gemini"))
        {
            TranslationLanguage = translationLanguage,
            // If the chosen model is down or overloaded, retry once with the backup model.
            FallbackModelName = fallbackModel,
        };

        try
        {
            var audit = await engine.ProcessReceiptAsync(bytes, mimeType, maxRetries: 2, ct);
            logger.LogInformation("Processed {File} with {Model} (fallback={Fallback}): valid={Valid}, attempts={Attempts}",
                image.FileName, audit.ModelName, audit.UsedFallbackModel, audit.IsSuccessful, audit.Attempts);
            return Results.Ok(audit);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (FaturaApiException ex)
        {
            logger.LogWarning(ex, "Gemini call failed for {File}", image.FileName);
            var status = ex.StatusCode switch
            {
                HttpStatusCode.OK => StatusCodes.Status502BadGateway,               // blocked / empty answer
                HttpStatusCode.RequestTimeout => StatusCodes.Status504GatewayTimeout,
                _ => (int)ex.StatusCode,
            };
            return Results.Problem(ex.Message, statusCode: status);
        }
    })
    .RequireRateLimiting("per-visitor")
    .DisableAntiforgery(); // API is called by a server-side UI with a header key, not by browser forms with cookies.

app.Run();
