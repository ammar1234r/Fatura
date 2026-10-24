// -----------------------------------------------------------------------------
// Fatura.ConsoleTest — Program.cs
// Loads a receipt image, runs the extraction + self-correction pipeline, and
// prints the audit trail and the final structured JSON.
//
// Usage (from the solution folder):
//   dotnet run --project src/Fatura.ConsoleTest                       → uses sample.jpg
//   dotnet run --project src/Fatura.ConsoleTest -- my.png gemini-2.5-flash
// The API key is read from the GEMINI_API_KEY environment variable.
// -----------------------------------------------------------------------------

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Fatura.Core;

Console.OutputEncoding = Encoding.UTF8; // Needed to display Turkish/Arabic characters.

// ---- 1. Read configuration -------------------------------------------------------------------
var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    WriteColored("ERROR: The GEMINI_API_KEY environment variable is not set.", ConsoleColor.Red);
    return 1;
}

var imagePath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "sample.jpg");
var modelName = args.Length > 1 ? args[1] : "gemini-3.8-flash";

if (!File.Exists(imagePath))
{
    WriteColored($"ERROR: Image not found: {Path.GetFullPath(imagePath)}", ConsoleColor.Red);
    Console.WriteLine("Put a receipt photo named 'sample.jpg' in src/Fatura.ConsoleTest/ and run again.");
    return 1;
}

var mimeType = Path.GetExtension(imagePath).ToLowerInvariant() switch
{
    ".png" => "image/png",
    ".webp" => "image/webp",
    ".heic" => "image/heic",
    ".heif" => "image/heif",
    _ => "image/jpeg",
};

// Ctrl+C cancels the request gracefully instead of killing the process.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---- 2. Run the engine -----------------------------------------------------------------------
using var engine = new FaturaEngine(apiKey, modelName)
{
    // If the chosen model is down or overloaded, retry once with the other one.
    FallbackModelName = modelName == FaturaEngine.DefaultModelName ? "gemini-3.8-flash" : FaturaEngine.DefaultModelName,
};

Console.WriteLine("==================================================");
Console.WriteLine(" Fatura — Receipt Extraction Engine");
Console.WriteLine("==================================================");
Console.WriteLine($" Image : {Path.GetFileName(imagePath)} ({mimeType})");
Console.WriteLine($" Model : {engine.ModelName}");
Console.WriteLine(" Processing... (large models can take 1–3 minutes per attempt; Ctrl+C to cancel)");
Console.WriteLine();

// Heartbeat: prints a line every 15 s so it's obvious the program is alive while the model works.
var started = System.Diagnostics.Stopwatch.StartNew();
using var heartbeat = new Timer(
    _ => Console.WriteLine($" … still waiting for the model ({started.Elapsed.TotalSeconds:0}s)"),
    null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

try
{
    var imageBytes = await File.ReadAllBytesAsync(imagePath, cts.Token);
    var progress = new ConsoleProgress<AttemptRecord>(a =>
    {
        var outcome = a.Error is not null ? "unreadable JSON → asking the model to fix it..."
                    : a.Validation!.IsValid ? "arithmetic OK"
                    : "arithmetic mismatch → asking the model to re-check the image...";
        Console.WriteLine($" ⏱ Attempt {a.AttemptNumber} finished in {a.Duration.TotalSeconds:0}s: {outcome}");
    });

    var audit = await engine.ProcessReceiptAsync(imageBytes, mimeType, maxRetries: 2, cts.Token, progress);
    heartbeat.Change(Timeout.Infinite, Timeout.Infinite);
    Console.WriteLine();

    PrintAuditLog(audit);
    PrintJson(audit);

    return audit.IsSuccessful ? 0 : 2; // non-zero exit code = needs human review
}
catch (FaturaApiException ex)
{
    WriteColored($"API ERROR ({(int)ex.StatusCode}): {ex.Message}", ConsoleColor.Red);
    return 3;
}
catch (OperationCanceledException)
{
    WriteColored("Cancelled.", ConsoleColor.Yellow);
    return 130;
}

// ---- 3. Output helpers -----------------------------------------------------------------------

static void PrintAuditLog(ProcessingAuditLog audit)
{
    Console.WriteLine("---------------- Audit Log -----------------------");
    foreach (var a in audit.History)
    {
        var label = a.IsCorrection ? "self-correction" : "initial";
        Console.Write($" Attempt {a.AttemptNumber} ({label}, {a.Duration.TotalSeconds:0.00}s): ");

        if (a.Error is not null)
        {
            WriteColored($"PARSE ERROR — {a.Error}", ConsoleColor.Red);
        }
        else if (a.Validation is { IsValid: true } ok)
        {
            WriteColored($"VALID (items {ok.CalculatedTotal:0.00} = total {ok.ExtractedTotal:0.00})", ConsoleColor.Green);
        }
        else if (a.Validation is { } bad)
        {
            WriteColored($"INVALID (items {bad.CalculatedTotal:0.00} ≠ total {bad.ExtractedTotal:0.00})", ConsoleColor.Yellow);
            foreach (var line in (bad.ErrorMessage ?? string.Empty).Split('\n'))
                Console.WriteLine($"     │ {line.TrimEnd()}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($" Attempts         : {audit.Attempts}");
    Console.WriteLine($" Corrections made : {audit.CorrectionsMade}");
    Console.WriteLine($" Total duration   : {audit.TotalDuration.TotalSeconds:0.00}s");
    Console.Write(" Final status     : ");
    WriteColored(
        audit.IsSuccessful ? "PASSED arithmetic validation" : "FAILED arithmetic validation (needs human review)",
        audit.IsSuccessful ? ConsoleColor.Green : ConsoleColor.Red);
    Console.WriteLine();
}

static void PrintJson(ProcessingAuditLog audit)
{
    var options = new JsonSerializerOptions
    {
        WriteIndented = true,
        // Keep Arabic/Turkish characters readable instead of \uXXXX escapes (fine for console output).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    Console.WriteLine("---------------- Extracted Data (JSON) -----------");
    Console.WriteLine(audit.FinalData is null ? "null" : JsonSerializer.Serialize(audit.FinalData, options));
    Console.WriteLine();
    Console.WriteLine("---------------- Full Audit Log (JSON) -----------");
    Console.WriteLine(JsonSerializer.Serialize(audit, options));
}

static void WriteColored(string text, ConsoleColor color)
{
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ForegroundColor = previous;
}

/// <summary>Synchronous <see cref="IProgress{T}"/> so progress lines print immediately and in order.</summary>
sealed class ConsoleProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
