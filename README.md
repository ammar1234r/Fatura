# فاتورة · Fatura

**Turn receipt and invoice photos into verified, structured data and a ready-to-use Excel file.**

Fatura reads receipts and invoices in **Turkish, Arabic, or English** using Google's multimodal models (Gemini / Gemma 4). What sets it apart is that it **checks the math**. A deterministic rules engine verifies that the line items add up to the total. If they don't, it runs a **self-correction loop**: the exact arithmetic error goes back to the model, which re-examines the image.

> ارفع صورة فاتورتك، فيستخرج Fatura بنودها، ويتحقق من أن مجموع البنود يساوي الإجمالي. وإذا لم يتطابق الحساب يطلب من النموذج إعادة فحص الصورة تلقائياً، ثم يعطيك النتيجة وملف Excel جاهز.

## Features

- **Multilingual extraction:** store, date, currency, category, line items (original + translated name), tax, and total.
- **Arithmetic validation:** sum of items equals the total within ±0.05. Tax-exclusive invoices (items + tax = total) are also handled.
- **Self-correction loop:** up to 2 re-examinations with the exact mismatch fed back to the model.
- **Resilient by design:** automatic fallback model, JSON-mode and thinking-level auto-detection, retries with backoff, time budgets.
- **Excel export:** one workbook per invoice, plus a combined workbook. Totals are live formulas, and Arabic workbooks are right-to-left.
- **Public-site ready:** the API key stays on the server, each visitor's session is isolated, images are never stored, and requests are rate-limited per visitor.
- **Full audit trail:** every attempt, correction, model, and duration is recorded.

## Architecture

```
src/
  Fatura.Core/         .NET class library: models, rules engine, Gemini client, self-correction loop
  Fatura.Api/          ASP.NET Core minimal API (POST /api/receipts/process)
  Fatura.ConsoleTest/  Console demo
tests/
  Fatura.Tests/        xUnit tests (offline, fake Gemini handler)
ui/                    Streamlit web front end + Excel export
```

```
Browser ──► Streamlit UI ──► Fatura.Api ──► Fatura.Core ──► Gemini API
                 ▲                                │
                 └──── result card + Excel ◄──────┘  (validate → self-correct)
```

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Python 3.10+](https://www.python.org/downloads/)
- A Gemini API key from [Google AI Studio](https://aistudio.google.com/apikey)

## Quick start (Windows PowerShell)

```powershell
git clone https://github.com/<your-username>/fatura.git
cd fatura
py -m pip install -r ui\requirements.txt
$env:GEMINI_API_KEY="your-key"
powershell -ExecutionPolicy Bypass -File .\run.ps1
```

Then open **http://localhost:8501**.

`run.ps1` starts the API on `http://localhost:5080` in a second window and the website in the current one.

### Run the pieces manually

```powershell
dotnet run --project src/Fatura.Api        # API
cd ui; py -m streamlit run app.py           # website (second terminal)
dotnet test                                 # tests
dotnet run --project src/Fatura.ConsoleTest -- path\to\receipt.jpg
```

## Configuration (`src/Fatura.Api/appsettings.json`)

| Setting | Default | Meaning |
|---|---|---|
| `Gemini:Models` | `["gemini-3.8-flash", "gemma-4-31b-it"]` | Offered models. The first is the default; the other is the automatic fallback. |
| `Gemini:TimeoutSeconds` | `120` | Max wait per model call |
| `RateLimit:ReceiptsPerMinute` | `10` | Per-visitor limit |
| `GEMINI_API_KEY` (env var) | — | Server-side key. Never commit it. |

## Using the library

```csharp
using Fatura.Core;

using var engine = new FaturaEngine(apiKey, "gemini-3.8-flash")
{
    FallbackModelName = "gemma-4-31b-it",
    TranslationLanguage = "Arabic",
};

ProcessingAuditLog audit = await engine.ProcessReceiptAsync(imageBytes, "image/jpeg", maxRetries: 2);

if (audit.IsSuccessful)
    Console.WriteLine($"{audit.FinalData!.StoreName}: {audit.FinalData.TotalAmount} {audit.FinalData.Currency}");
else
    Console.WriteLine(audit.Validation?.ErrorMessage); // needs human review
```

## Limitations

The model can misread blurry or unusual documents. Arithmetic validation catches most of these errors, but not all: a receipt can add up and still have a misread item name. Results marked **needs review** should be checked by a person. Latency depends on Google's servers, typically 10–60 s per receipt.

## License

[MIT](LICENSE)
