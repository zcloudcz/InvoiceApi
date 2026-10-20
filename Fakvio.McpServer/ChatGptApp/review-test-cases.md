# ChatGPT app review: test cases

Paste these into **Review details** on platform.openai.com/plugins. OpenAI asks for
five positive and three negative cases per MCP plugin. Every case assumes the reviewer
account from the credentials section is connected with **read and write** consent and
that the test company is populated (clients, issued and received invoices in every
status, bank payments, templates, number sequences, VAT settings).

Tool names below are the exact `tools/list` names of `https://mcp.fakvio.cz/mcp`.

## Positive cases

### P1. Dashboard and recent invoices (read)
- Prompt: "Show me my Fakvio dashboard and my five latest invoices."
- Expected tools: `get_dashboard`, then `list_invoices` (pageSize 5).
- Expected result: unpaid total, overdue count, top clients, and a list of the five
  newest issued documents with number, client, amount, status. No write tool is called.

### P2. Issue an invoice and send it by e-mail (write, two confirmations)
- Prompt: "Create an invoice for client Novak s.r.o.: 10 hours of consulting at
  1500 CZK plus 21 % VAT, due in 14 days. Issue it and e-mail it to the client."
- Expected tools: `list_clients` (search "Novak") → `create_invoice` → `complete_invoice`
  → `get_invoice_email_options` → `send_invoice_email`.
- Expected result: a Draft is created, becomes Completed with a document number from
  the default number sequence, the assistant shows the rendered subject/body before
  sending, and the e-mail goes to the client's stored contact (a mailbox we control).
  `send_invoice_email` is annotated destructive, so ChatGPT asks for confirmation.

### P3. Overdue invoices and unmatched bank payments (read)
- Prompt: "Which invoices are overdue, and which incoming bank payments from the last
  30 days are still unmatched?"
- Expected tools: `get_overdue_invoices`, `list_payments` (status unmatched,
  direction incoming, from = today-30d).
- Expected result: two lists; the overdue list is sorted by due date ascending.

### P4. Received supplier invoice with attachment, then approve it (write)
- Prompt: "Record a received invoice from supplier ABC s.r.o., number 2026-0042,
  500 EUR without VAT, received today, due 2026-11-15, then approve it."
- Expected tools: `list_clients` or `create_client` (supplier) → `create_invoice`
  (documentType `ReceivedInvoice`, currency `EUR`) → `complete_invoice`.
- Expected result: the received invoice exists in status Approved (= Completed) with the
  supplier's number kept; `list_invoices` with documentType `ReceivedInvoice` lists it.

### P5. VAT report and EPO export (read, download link)
- Prompt: "Prepare my VAT summary for Q3 2026 and give me the EPO XML for the VAT
  return."
- Expected tools: `get_vat_report` (2026-07-01 .. 2026-09-30), `export_vat_epo`
  (kind `return`, year 2026, period Q3 / month as the company files).
- Expected result: output VAT, input VAT and liability figures, plus a short-lived
  `downloadUrl` that the assistant hands to the user as a link (it must not fetch it).

## Negative cases

### N1. Write tool with read-only consent (authorization)
- Setup: connect a second time and choose **Read only** on the Fakvio consent screen.
- Prompt: "Mark invoice FAK2026001 as paid."
- Expected: `find_invoice_by_number` succeeds; `mark_invoice_paid` returns a structured
  `forbidden` error ("write scope required"). Nothing changes; the assistant explains
  that the connection is read-only and how to reconnect with write access.

### N2. Document that does not exist (not found)
- Prompt: "Show me invoice number XYZ-DOES-NOT-EXIST."
- Expected: `find_invoice_by_number` returns `not_found` with a clear message. The
  assistant reports that no such document exists and suggests `list_invoices` with a
  search instead of guessing or hallucinating an invoice.

### N3. Invalid input on a write tool (validation)
- Prompt: "Create an invoice for client Novak s.r.o. with one line: quantity -3,
  unit price 1000 CZK, VAT rate 99 %."
- Expected: `create_invoice` returns `validation_error` naming the offending fields
  (quantity must be positive, VAT rate 99 % is not an active rate on the issue date).
  No draft is created; the assistant asks the user to correct the values.

## Optional extra negative case

### N4. Deleting a completed invoice (state machine)
- Prompt: "Delete invoice FAK2026001." (an issued, completed invoice that is not the
  last in its number series)
- Expected: `delete_invoice` is refused with a message that only Draft documents can be
  deleted and that `revert_invoice_to_draft` is required first; the assistant asks for
  explicit confirmation before attempting the revert.
