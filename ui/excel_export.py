"""
Fatura — Excel export.

Builds formatted .xlsx workbooks from Fatura audit logs (the JSON returned by
Fatura.Api). Totals use live Excel formulas, so if the user corrects a price in
Excel the sum and the difference update by themselves.
"""

from __future__ import annotations

from io import BytesIO
from typing import Any

from openpyxl import Workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter
from openpyxl.worksheet.worksheet import Worksheet

# ---------------------------------------------------------------------------------------------
# Labels per results language (en / ar / tr). Arabic workbooks are also laid out right-to-left.
# ---------------------------------------------------------------------------------------------
LABELS: dict[str, dict[str, str]] = {
    "en": {
        "invoice": "Invoice", "summary": "Summary", "items": "Items", "audit": "Audit",
        "store": "Store", "date": "Date", "category": "Category", "currency": "Currency",
        "tax": "Tax", "total": "Total", "status": "Status", "file": "File",
        "verified": "Verified", "review": "Needs review",
        "no": "#", "name_original": "Item (original)", "name_translated": "Item (translated)",
        "quantity": "Qty", "price": "Price", "items_sum": "Items sum", "difference": "Difference",
        "attempt": "Attempt", "correction": "Correction", "result": "Result", "seconds": "Seconds",
        "model": "Model", "yes": "Yes", "no_": "No", "error": "Error", "receipt": "Receipt",
        "valid": "Valid", "invalid": "Invalid",
    },
    "ar": {
        "invoice": "الفاتورة", "summary": "الملخص", "items": "البنود", "audit": "سجل التحقق",
        "store": "المتجر", "date": "التاريخ", "category": "التصنيف", "currency": "العملة",
        "tax": "الضريبة", "total": "الإجمالي", "status": "الحالة", "file": "الملف",
        "verified": "متطابقة", "review": "تحتاج مراجعة",
        "no": "#", "name_original": "البند (الأصلي)", "name_translated": "البند (مترجم)",
        "quantity": "الكمية", "price": "المبلغ", "items_sum": "مجموع البنود", "difference": "الفرق",
        "attempt": "المحاولة", "correction": "تصحيح", "result": "النتيجة", "seconds": "الثواني",
        "model": "النموذج", "yes": "نعم", "no_": "لا", "error": "خطأ", "receipt": "الإيصال",
        "valid": "صحيحة", "invalid": "غير متطابقة",
    },
    "tr": {
        "invoice": "Fatura", "summary": "Özet", "items": "Kalemler", "audit": "Denetim",
        "store": "Mağaza", "date": "Tarih", "category": "Kategori", "currency": "Para birimi",
        "tax": "Vergi", "total": "Toplam", "status": "Durum", "file": "Dosya",
        "verified": "Doğrulandı", "review": "İnceleme gerekli",
        "no": "#", "name_original": "Kalem (orijinal)", "name_translated": "Kalem (çeviri)",
        "quantity": "Miktar", "price": "Tutar", "items_sum": "Kalemler toplamı", "difference": "Fark",
        "attempt": "Deneme", "correction": "Düzeltme", "result": "Sonuç", "seconds": "Saniye",
        "model": "Model", "yes": "Evet", "no_": "Hayır", "error": "Hata", "receipt": "Fiş",
        "valid": "Geçerli", "invalid": "Geçersiz",
    },
}

CATEGORIES: dict[str, dict[str, str]] = {
    "ar": {"Groceries": "بقالة", "Restaurant": "مطعم", "Cafe": "مقهى", "Fuel": "وقود", "Transport": "مواصلات",
           "Pharmacy": "صيدلية", "Healthcare": "صحة", "Electronics": "إلكترونيات", "Clothing": "ملابس",
           "Utilities": "فواتير وخدمات", "Hotel": "فندق", "Entertainment": "ترفيه", "Services": "خدمات",
           "Other": "أخرى"},
    "tr": {"Groceries": "Market", "Restaurant": "Restoran", "Cafe": "Kafe", "Fuel": "Yakıt", "Transport": "Ulaşım",
           "Pharmacy": "Eczane", "Healthcare": "Sağlık", "Electronics": "Elektronik", "Clothing": "Giyim",
           "Utilities": "Faturalar", "Hotel": "Otel", "Entertainment": "Eğlence", "Services": "Hizmetler",
           "Other": "Diğer"},
}

TEAL = "6D28D9"
TEAL_LIGHT = "F1ECFE"
GREEN_LIGHT = "E8F5E9"
AMBER_LIGHT = "FFF4E5"
MONEY = "#,##0.00"
QTY = "#,##0.###"

_thin = Side(style="thin", color="D0D7D5")
_border = Border(left=_thin, right=_thin, top=_thin, bottom=_thin)
_header_font = Font(bold=True, color="FFFFFF")
_header_fill = PatternFill("solid", fgColor=TEAL)


def _labels(lang: str) -> dict[str, str]:
    return LABELS.get(lang, LABELS["en"])


def category_label(category: str | None, lang: str) -> str | None:
    """Translates the engine's English category name for display."""
    return CATEGORIES.get(lang, {}).get(category or "", category)


def _setup_sheet(ws: Worksheet, lang: str) -> None:
    ws.sheet_view.rightToLeft = lang == "ar"
    ws.sheet_view.showGridLines = False
    # Print / PDF: all columns on one page width, as many pages tall as needed.
    ws.sheet_properties.pageSetUpPr.fitToPage = True
    ws.page_setup.fitToWidth = 1
    ws.page_setup.fitToHeight = 0
    ws.page_setup.orientation = "portrait"


def _header_row(ws: Worksheet, row: int, values: list[str]) -> None:
    for col, value in enumerate(values, start=1):
        cell = ws.cell(row=row, column=col, value=value)
        cell.font = _header_font
        cell.fill = _header_fill
        cell.border = _border
        cell.alignment = Alignment(horizontal="center", vertical="center", wrap_text=True)


def _widths(ws: Worksheet, widths: list[int]) -> None:
    for i, width in enumerate(widths, start=1):
        ws.column_dimensions[get_column_letter(i)].width = width


def _status(audit: dict[str, Any], L: dict[str, str]) -> str:
    return L["verified"] if (audit.get("validation") or {}).get("is_valid") else L["review"]


def _write_audit_sheet(ws: Worksheet, audits: list[tuple[str, dict[str, Any]]], L: dict[str, str]) -> None:
    _header_row(ws, 1, [L["file"], L["attempt"], L["correction"], L["result"], L["seconds"], L["model"]])
    r = 2
    for file_name, audit in audits:
        for h in audit.get("history", []):
            v = h.get("validation")
            result = L["error"] + ": " + h["error"] if h.get("error") else (
                L["valid"] if v and v.get("is_valid") else f'{L["invalid"]}: {(v or {}).get("error_message", "")}')
            row = [file_name, h.get("attempt_number"), L["yes"] if h.get("is_correction") else L["no_"],
                   result, round(duration_seconds(h.get("duration")), 1), audit.get("model_name")]
            for c, val in enumerate(row, start=1):
                cell = ws.cell(row=r, column=c, value=val)
                cell.border = _border
                cell.alignment = Alignment(vertical="top", wrap_text=(c == 4))
            r += 1
    _widths(ws, [24, 10, 12, 70, 10, 20])
    ws.freeze_panes = "A2"


def duration_seconds(value: Any) -> float:
    """Parses a .NET TimeSpan string such as '00:00:48.4142408' into seconds."""
    if not value:
        return 0.0
    try:
        days = 0
        text = str(value)
        if "." in text.split(":")[0]:  # 'd.hh:mm:ss'
            d, text = text.split(".", 1)
            days = int(d)
        h, m, s = text.split(":")
        return days * 86400 + int(h) * 3600 + int(m) * 60 + float(s)
    except (ValueError, TypeError):
        return 0.0


# ---------------------------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------------------------

def build_receipt_workbook(audit: dict[str, Any], file_name: str, lang: str = "en") -> bytes:
    """One invoice → workbook with an 'Invoice' sheet (header + items + live totals) and an 'Audit' sheet."""
    L = _labels(lang)
    data = audit.get("final_data") or {}
    items = data.get("items") or []

    wb = Workbook()
    ws = wb.active
    ws.title = L["invoice"]
    _setup_sheet(ws, lang)

    # --- Header block ------------------------------------------------------------------------
    ws["A1"] = data.get("store_name") or file_name
    ws["A1"].font = Font(bold=True, size=16, color=TEAL)
    ws["A1"].alignment = Alignment(horizontal="center", vertical="center")
    ws.merge_cells("A1:E1")
    ws.row_dimensions[1].height = 28

    status = _status(audit, L)
    info = [
        (L["date"], data.get("date")),
        (L["category"], category_label(data.get("category"), lang)),
        (L["currency"], data.get("currency")),
        (L["status"], status),
        (L["file"], file_name),
    ]
    for i, (label, value) in enumerate(info, start=3):
        ws.cell(row=i, column=1, value=label).font = Font(bold=True, color="5F6B6A")
        ws.cell(row=i, column=2, value=value).alignment = Alignment(horizontal="left", indent=1)
    status_cell = ws.cell(row=6, column=2)
    status_cell.fill = PatternFill("solid", fgColor=GREEN_LIGHT if status == L["verified"] else AMBER_LIGHT)
    status_cell.font = Font(bold=True)

    # --- Items table -------------------------------------------------------------------------
    header_row = 9
    _header_row(ws, header_row, [L["no"], L["name_original"], L["name_translated"], L["quantity"], L["price"]])
    first = header_row + 1
    for i, item in enumerate(items):
        r = first + i
        values = [i + 1, item.get("name_original"), item.get("name_translated"),
                  item.get("quantity"), item.get("price")]
        for c, val in enumerate(values, start=1):
            cell = ws.cell(row=r, column=c, value=val)
            cell.border = _border
            if c == 4:
                cell.number_format = QTY
            if c == 5:
                cell.number_format = MONEY
        if i % 2 == 1:
            for c in range(1, 6):
                ws.cell(row=r, column=c).fill = PatternFill("solid", fgColor=TEAL_LIGHT)
    last = first + max(len(items), 1) - 1

    # --- Totals (live formulas) --------------------------------------------------------------
    t = last + 2
    rows = [
        (L["items_sum"], f"=SUM(E{first}:E{last})"),
        (L["tax"], data.get("tax_amount") or 0),
        (L["total"], data.get("total_amount") or 0),
        (L["difference"], f"=E{t}-E{t + 2}"),
    ]
    for i, (label, value) in enumerate(rows):
        r = t + i
        label_cell = ws.cell(row=r, column=4, value=label)
        label_cell.font = Font(bold=True)
        label_cell.alignment = Alignment(horizontal="right" if lang != "ar" else "left")
        cell = ws.cell(row=r, column=5, value=value)
        cell.number_format = MONEY
        cell.border = _border
    ws.cell(row=t + 2, column=5).font = Font(bold=True, size=12)
    ws.cell(row=t + 2, column=5).fill = PatternFill("solid", fgColor=TEAL_LIGHT)

    _widths(ws, [6, 42, 36, 10, 16])
    ws.freeze_panes = ws.cell(row=first, column=1)

    _write_audit_sheet(wb.create_sheet(L["audit"]), [(file_name, audit)], L)
    _setup_sheet(wb[L["audit"]], lang)
    return _save(wb)


def build_combined_workbook(receipts: list[tuple[str, dict[str, Any]]], lang: str = "en") -> bytes:
    """Many invoices → 'Summary' (one row per invoice), 'Items' (all line items) and 'Audit' sheets."""
    L = _labels(lang)
    wb = Workbook()

    summary = wb.active
    summary.title = L["summary"]
    _setup_sheet(summary, lang)
    _header_row(summary, 1, [L["file"], L["store"], L["date"], L["category"], L["currency"],
                             L["tax"], L["total"], L["status"]])
    for r, (file_name, audit) in enumerate(receipts, start=2):
        d = audit.get("final_data") or {}
        status = _status(audit, L)
        values = [file_name, d.get("store_name"), d.get("date"), category_label(d.get("category"), lang), d.get("currency"),
                  d.get("tax_amount") or 0, d.get("total_amount") or 0, status]
        for c, val in enumerate(values, start=1):
            cell = summary.cell(row=r, column=c, value=val)
            cell.border = _border
            if c in (6, 7):
                cell.number_format = MONEY
        summary.cell(row=r, column=8).fill = PatternFill(
            "solid", fgColor=GREEN_LIGHT if status == L["verified"] else AMBER_LIGHT)
    _widths(summary, [24, 34, 12, 16, 10, 12, 14, 16])
    summary.freeze_panes = "A2"
    if receipts:
        summary.auto_filter.ref = f"A1:H{len(receipts) + 1}"

    items_ws = wb.create_sheet(L["items"])
    _setup_sheet(items_ws, lang)
    _header_row(items_ws, 1, [L["receipt"], L["date"], L["name_original"], L["name_translated"],
                              L["quantity"], L["price"], L["currency"]])
    r = 2
    for file_name, audit in receipts:
        d = audit.get("final_data") or {}
        for item in d.get("items") or []:
            values = [d.get("store_name") or file_name, d.get("date"), item.get("name_original"),
                      item.get("name_translated"), item.get("quantity"), item.get("price"), d.get("currency")]
            for c, val in enumerate(values, start=1):
                cell = items_ws.cell(row=r, column=c, value=val)
                cell.border = _border
                if c == 5:
                    cell.number_format = QTY
                if c == 6:
                    cell.number_format = MONEY
            r += 1
    _widths(items_ws, [30, 12, 40, 34, 8, 12, 10])
    items_ws.freeze_panes = "A2"
    if r > 2:
        items_ws.auto_filter.ref = f"A1:G{r - 1}"

    audit_ws = wb.create_sheet(L["audit"])
    _setup_sheet(audit_ws, lang)
    _write_audit_sheet(audit_ws, receipts, L)
    return _save(wb)


def _save(wb: Workbook) -> bytes:
    buffer = BytesIO()
    wb.save(buffer)
    return buffer.getvalue()
