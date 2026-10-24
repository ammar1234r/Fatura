"""
Fatura (فاتورة) — Streamlit front end for the Fatura engine.

Public website: any company opens the site, adds its invoice images, and gets back a
verified result card plus a formatted Excel file (one per invoice, and a combined file
when it uploads several). No database: each visitor's results live only in their own
browser session and disappear when they leave. The site owner's Gemini key lives on the
server (GEMINI_API_KEY), so visitors never need one.

Run (from the solution folder, with the API already running):
    streamlit run ui/app.py
"""

from __future__ import annotations

import hashlib
import json
import os
import uuid
from dataclasses import dataclass, field
from typing import Any

import pandas as pd
import requests
import streamlit as st

from excel_export import build_combined_workbook, build_receipt_workbook, category_label, duration_seconds

DEFAULT_API_URL = os.environ.get("FATURA_API_URL", "http://localhost:5080")
MODELS = ["gemini-3.8-flash", "gemma-4-31b-it"]  # the other one is used automatically as a fallback
LANGUAGES = {"English": "en", "العربية": "ar", "Türkçe": "tr"}
MAX_IMAGES_PER_RUN = 10   # keeps one visitor from monopolizing the shared quota
REQUEST_TIMEOUT_S = 420  # engine: ≤120 s per model call and stops correcting after 150 s
XLSX_MIME = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"


@dataclass
class ReceiptResult:
    """One processed image kept in the session."""
    file_name: str
    image: bytes
    audit: dict[str, Any] | None = None
    error: str | None = None
    lang: str = "en"
    extra: dict[str, Any] = field(default_factory=dict)

    @property
    def ok(self) -> bool:
        return bool(self.audit and (self.audit.get("validation") or {}).get("is_valid"))

    @property
    def data(self) -> dict[str, Any]:
        return (self.audit or {}).get("final_data") or {}


# ---------------------------------------------------------------------------------------------
# Page setup and styling
# ---------------------------------------------------------------------------------------------
st.set_page_config(page_title="فاتورة · Fatura", page_icon="🧾", layout="wide")

st.markdown(
    """
    <style>
      .sj-hero{position:relative;overflow:hidden;background:#111827;border:1px solid #273244;border-radius:22px;padding:30px 34px;display:flex;gap:22px;align-items:center;margin-bottom:1.5rem}
      .sj-hero:before{content:"";position:absolute;inset:-40% auto auto -10%;width:420px;height:420px;background:radial-gradient(circle,rgba(139,92,246,.35),transparent 65%)}
      .sj-hero:after{content:"";position:absolute;inset:auto -8% -60% auto;width:380px;height:380px;background:radial-gradient(circle,rgba(34,197,94,.22),transparent 65%)}
      .sj-hero > *{position:relative;z-index:1}
      .sj-logo{width:66px;height:66px;border-radius:16px;background:linear-gradient(135deg,#8B5CF6,#22C55E);display:flex;align-items:center;justify-content:center;font-size:34px}
      .sj-title{font-size:42px;font-weight:800;margin:0;line-height:1.1;color:#F3F4F6}
      .sj-title small{font-size:20px;font-weight:600;margin-inline-start:10px;background:linear-gradient(90deg,#A78BFA,#4ADE80);-webkit-background-clip:text;background-clip:text;color:transparent}
      .sj-sub{margin:8px 0 12px;font-size:16px;color:#C7CDD8}
      .sj-pill{display:inline-block;border:1px solid #3B4558;background:#1A2232;color:#C4B5FD;border-radius:999px;padding:4px 12px;font-size:12px}
      .sj-step{display:flex;align-items:center;gap:10px;margin:1.8rem 0 .7rem}
      .sj-num{width:30px;height:30px;border-radius:9px;background:#8B5CF6;color:#fff;display:flex;align-items:center;justify-content:center;font-weight:700}
      .sj-step h3{margin:0;font-size:20px;color:#F3F4F6}
      .sj-rtl{direction:rtl;text-align:right}
      .sj-total{font-size:30px;font-weight:800;margin:0;direction:ltr;text-align:end;color:#4ADE80}
      .sj-muted{color:#9AA4B2;font-size:13px;margin:0}
      [data-testid="stVerticalBlockBorderWrapper"]{border-radius:16px}
    </style>
    """,
    unsafe_allow_html=True,
)


def step(number: int, title: str) -> None:
    st.markdown(f'<div class="sj-step"><div class="sj-num">{number}</div><h3>{title}</h3></div>',
                unsafe_allow_html=True)


# ---------------------------------------------------------------------------------------------
# Session state
# ---------------------------------------------------------------------------------------------
st.session_state.setdefault("results", {})        # sha256 -> ReceiptResult
st.session_state.setdefault("uploader_key", 0)    # bump to clear the uploader widget
st.session_state.setdefault("client_id", uuid.uuid4().hex)  # per-visitor id for API rate limiting
results: dict[str, ReceiptResult] = st.session_state.results


# ---------------------------------------------------------------------------------------------
# API helpers
# ---------------------------------------------------------------------------------------------
@st.cache_data(ttl=10, show_spinner=False)
def api_health(api_url: str) -> dict | None:
    """Returns the API's /health payload, or None if the API is unreachable."""
    try:
        response = requests.get(f"{api_url.rstrip('/')}/health", timeout=2)
        return response.json() if response.ok else None
    except (requests.RequestException, ValueError):
        return None


def process_receipt(api_url: str, api_key: str, model: str, lang: str,
                    name: str, content: bytes, mime: str) -> tuple[dict | None, str | None]:
    """Calls Fatura.Api. Returns (audit, None) on success or (None, user-friendly error)."""
    try:
        response = requests.post(
            f"{api_url.rstrip('/')}/api/receipts/process",
            headers={"X-Gemini-Api-Key": api_key, "X-Client-Id": st.session_state.client_id},
            files={"image": (name, content, mime)},
            data={"model": model, "language": lang},
            timeout=REQUEST_TIMEOUT_S,
        )
    except requests.ConnectionError:
        return None, "لا يمكن الوصول إلى المحرّك. تأكد أنه يعمل: dotnet run --project src/Fatura.Api"
    except requests.Timeout:
        return None, "انتهت مهلة الانتظار. جرّب مرة أخرى أو استخدم نموذجاً أسرع."

    if response.ok:
        return response.json(), None

    try:
        detail = response.json().get("detail") or response.text
    except ValueError:
        detail = response.text
    hints = {
        401: "مفتاح API غير موجود أو غير صالح.",
        400: "الصورة غير مدعومة. استخدم JPG أو PNG أو WEBP.",
        403: "مفتاح API مرفوض. تأكد أنه من Google AI Studio.",
        404: "النموذج غير متاح. اختر نموذجاً آخر من الإعدادات.",
        429: "وصلت إلى الحد المسموح من الطلبات. انتظر دقيقة ثم أعد المحاولة.",
        504: "النموذج تأخر في الرد. جرّب مرة أخرى أو استخدم نموذجاً أسرع.",
    }
    return None, f"{hints.get(response.status_code, 'حدث خطأ أثناء المعالجة.')} ({response.status_code})\n\n{detail}"


def money(value: Any, currency: str | None = None) -> str:
    try:
        text = f"{float(value):,.2f}"
    except (TypeError, ValueError):
        text = "—"
    return f"{text} {currency}" if currency else text


def safe_stem(name: str) -> str:
    stem = os.path.splitext(name)[0]
    return "".join(ch if ch.isalnum() or ch in "-_" else "_" for ch in stem) or "receipt"


# ---------------------------------------------------------------------------------------------
# Sidebar — settings
# ---------------------------------------------------------------------------------------------
with st.sidebar:
    st.markdown("## ⚙️ الإعدادات")
    model = st.selectbox("Model", MODELS, help="gemini-3.8-flash أسرع. إذا فشل النموذج المختار يُستخدم الآخر تلقائياً.")
    lang_label = st.selectbox("لغة النتائج", list(LANGUAGES), help="لغة ترجمة أسماء البنود ولغة ملف Excel.")
    lang = LANGUAGES[lang_label]

    with st.expander("متقدّم"):
        custom_model = st.text_input("نموذج آخر (اختياري)", placeholder="gemini-3.8-pro")
        api_url = st.text_input("عنوان المحرّك (API)", value=DEFAULT_API_URL)
    if custom_model.strip():
        model = custom_model.strip()

    health = api_health(api_url)
    # Ask for a key only when the server doesn't have one (e.g. local development).
    needs_key = not (health or {}).get("hasServerKey", False)
    api_key = ""
    if needs_key:
        api_key = st.text_input("Gemini API Key", type="password", value=os.environ.get("GEMINI_API_KEY", ""))

    if health:
        st.success("المحرّك متصل", icon="✅")
    else:
        st.error("المحرّك غير شغّال.\n\nشغّله بالأمر:\n`dotnet run --project src/Fatura.Api`", icon="🔌")

    if needs_key and not api_key:
        st.warning("أولاً أدخل مفتاح API")

    st.caption("🔒 الصور تُعالَج في الذاكرة فقط ولا تُحفظ على الخادم. تختفي نتائجك عند إغلاق الصفحة.")

    if st.button("🗑️ مسح كل الإيصالات", width="stretch"):
        st.session_state.results = {}
        st.session_state.uploader_key += 1
        st.rerun()


# ---------------------------------------------------------------------------------------------
# Hero
# ---------------------------------------------------------------------------------------------
st.markdown(
    """
    <div class="sj-hero">
      <div class="sj-logo">🧾</div>
      <div>
        <div class="sj-title">فاتورة <small>Fatura</small></div>
        <div class="sj-sub" dir="rtl" style="text-align:left">ارفع فاتورتك، ونستخرج بياناتها ونتحقق من حسابها ونعطيك ملف Excel جاهز</div>
        <span class="sj-pill">✨ Gemini · Gemma 4 · Arithmetic-verified · Open source</span>
      </div>
    </div>
    """,
    unsafe_allow_html=True,
)

# ---------------------------------------------------------------------------------------------
# Step 1 — add receipts
# ---------------------------------------------------------------------------------------------
step(1, "Add receipts")
col_upload, col_camera = st.columns(2)
with col_upload:
    uploads = st.file_uploader("ارفع صور الإيصالات", type=["jpg", "jpeg", "png", "webp"],
                               accept_multiple_files=True, key=f"uploader_{st.session_state.uploader_key}")
with col_camera:
    use_camera = st.toggle("أو صوّر الإيصال مباشرة")
    photo = st.camera_input("صوّر الإيصال", label_visibility="collapsed") if use_camera else None

incoming: list[tuple[str, str, bytes, str]] = []  # (id, name, bytes, mime)
for f in list(uploads or []) + ([photo] if photo else []):
    content = f.getvalue()
    incoming.append((hashlib.sha256(content).hexdigest()[:16], f.name, content, f.type or "image/jpeg"))
pending = [x for x in incoming if x[0] not in results or results[x[0]].error]
if len(pending) > MAX_IMAGES_PER_RUN:
    st.warning(f"سيتم معالجة أول {MAX_IMAGES_PER_RUN} صور فقط في هذه الدفعة.")
    pending = pending[:MAX_IMAGES_PER_RUN]

# ---------------------------------------------------------------------------------------------
# Step 2 — process
# ---------------------------------------------------------------------------------------------
step(2, "Extract and verify")
if not incoming:
    st.info("أضف صورة إيصال واحدة على الأقل للبدء.", icon="📷")
elif not pending:
    st.caption("كل الصور المضافة تمت معالجتها ✅")
else:
    st.caption(f"{len(pending)} صورة جاهزة للمعالجة. كل صورة قد تستغرق حتى دقيقة أو أكثر.")
    if st.button(f"استخراج البيانات من {len(pending)} صورة", type="primary", disabled=needs_key and not api_key):
        progress = st.progress(0.0)
        for i, (rid, name, content, mime) in enumerate(pending, start=1):
            with st.status(f"جاري معالجة {name} ({i}/{len(pending)})…", expanded=False) as status:
                audit, error = process_receipt(api_url, api_key, model, lang, name, content, mime)
                results[rid] = ReceiptResult(name, content, audit, error, lang)
                status.update(label=f"{name}: {'تم' if audit else 'فشل'}",
                              state="complete" if audit else "error")
            progress.progress(i / len(pending))
        st.rerun()

# ---------------------------------------------------------------------------------------------
# Step 3 — results (one card per invoice)
# ---------------------------------------------------------------------------------------------
if results:
    step(3, "Results")

for rid, r in reversed(list(results.items())):
    with st.container(border=True):
        if r.error:
            st.markdown(f"**{r.file_name}**")
            st.error(r.error, icon="⚠️")
            continue

        d, audit = r.data, r.audit or {}
        validation = audit.get("validation") or {}
        currency = d.get("currency")

        head, total = st.columns([3, 1])
        with head:
            category = d.get("category") or "—"
            st.markdown(f"### {d.get('store_name') or r.file_name}")
            st.markdown(f'<p class="sj-muted">{d.get("date") or "—"} · {category_label(category, "ar")} · '
                        f'{currency or "—"} · {r.file_name}</p>', unsafe_allow_html=True)
        with total:
            st.markdown('<p class="sj-muted" style="text-align:end">الإجمالي</p>', unsafe_allow_html=True)
            st.markdown(f'<div class="sj-total">{money(d.get("total_amount"), currency)}</div>', unsafe_allow_html=True)

        calc, extracted = validation.get("calculated_total"), validation.get("extracted_total")
        if r.ok:
            diff = abs(float(calc or 0) - float(extracted or 0))
            note = f" (فرق تقريب {diff:.2f})" if diff >= 0.005 else ""
            st.success(f"الحساب مطابق: مجموع البنود {money(calc)} = الإجمالي {money(extracted)}{note}", icon="✅")
        else:
            st.warning(f"الأرقام لم تتطابق بعد {audit.get('attempts', 0)} محاولات: مجموع البنود {money(calc)} "
                       f"والإجمالي {money(extracted)}. راجع الصورة وصحّح الأرقام في ملف Excel.", icon="⚠️")

        img_col, table_col = st.columns([1, 3])
        with img_col:
            st.image(r.image, width="stretch")
        with table_col:
            items = d.get("items") or []
            if items:
                table = pd.DataFrame(items).rename(columns={
                    "name_original": "البند", "name_translated": "الترجمة", "quantity": "الكمية", "price": "المبلغ"})
                st.dataframe(table, hide_index=True, width="stretch",
                             column_config={"المبلغ": st.column_config.NumberColumn(format="%.2f"),
                                            "الكمية": st.column_config.NumberColumn(format="%.3g")})
            else:
                st.caption("لم يُستخرج أي بند.")

        m1, m2, m3 = st.columns(3)
        m1.metric("الضريبة", money(d.get("tax_amount")))
        corrections = audit.get("corrections_made", 0)
        m2.metric("المحاولات", f"{audit.get('attempts', 0)}", f"{corrections} تصحيح" if corrections else None,
                  delta_color="off")
        m3.metric("المدة", f"{duration_seconds(audit.get('total_duration')):.0f} ث")
        if audit.get("used_fallback_model"):
            st.caption(f"النموذج: {audit.get('model_name', '—')} (النموذج المختار لم يستجب، فاستُخدم هذا النموذج الاحتياطي)")
        else:
            st.caption(f"النموذج: {audit.get('model_name', '—')}")

        st.download_button(
            "⬇️ تنزيل الفاتورة Excel",
            data=build_receipt_workbook(audit, r.file_name, r.lang),
            file_name=f"{safe_stem(d.get('store_name') or r.file_name)}_{d.get('date') or 'invoice'}.xlsx",
            mime=XLSX_MIME, key=f"xlsx_{rid}", type="primary",
        )
        with st.expander("JSON"):
            st.code(json.dumps(audit, ensure_ascii=False, indent=2), language="json")

# ---------------------------------------------------------------------------------------------
# Step 4 — all of this visitor's invoices together (only when there is more than one)
# ---------------------------------------------------------------------------------------------
done = [(r.file_name, r.audit) for r in results.values() if r.audit]
if len(done) >= 2:
    step(4, "All your invoices")
    rows = []
    for file_name, audit in done:
        d = audit.get("final_data") or {}
        rows.append({
            "التاريخ": d.get("date"), "المتجر": d.get("store_name"),
            "التصنيف": category_label(d.get("category"), "ar"),
            "العملة": d.get("currency"), "الضريبة": d.get("tax_amount"), "الإجمالي": d.get("total_amount"),
            "الحالة": "✅ متطابقة" if (audit.get("validation") or {}).get("is_valid") else "⚠️ تحتاج مراجعة",
            "الملف": file_name,
        })
    expenses = pd.DataFrame(rows)

    totals = expenses.groupby("العملة", dropna=False)["الإجمالي"].sum()
    cols = st.columns(min(len(totals), 4) + 1)
    cols[0].metric("عدد الفواتير", len(expenses))
    for col, (cur, value) in zip(cols[1:], totals.items()):
        col.metric(f"المجموع ({cur or '—'})", money(value))

    st.dataframe(expenses, hide_index=True, width="stretch",
                 column_config={"الضريبة": st.column_config.NumberColumn(format="%.2f"),
                                "الإجمالي": st.column_config.NumberColumn(format="%.2f")})

    workbook_lang = next(iter({r.lang for r in results.values() if r.audit}), lang)
    c1, c2 = st.columns(2)
    c1.download_button("⬇️ تنزيل كل الفواتير Excel", data=build_combined_workbook(done, workbook_lang),
                       file_name="fatura_expenses.xlsx", mime=XLSX_MIME, type="primary",
                       width="stretch")
    c2.download_button("⬇️ CSV", data=expenses.to_csv(index=False).encode("utf-8-sig"),
                       file_name="fatura_expenses.csv", mime="text/csv", width="stretch")
