# WAVE POS

Point-of-sale system. C# Windows desktop app first, then a .NET MAUI mobile app and an ASP.NET Core web app, all sharing the same core code.

## Projects

| Project | What it does |
| --- | --- |
| `src/WavePOS.Core` | Products, cart, VAT, checkout, money (Kwacha). Shared by all apps. |
| `src/WavePOS.Data` | Local SQLite database (works offline). Products and sales. |
| `src/WavePOS.Desktop` | Windows till app (WPF). |
| `src/WavePOS.Web` | Web portal (ASP.NET Core Blazor): same till, login, settings, receipts and draft invoices in the browser. |
| `tests/WavePOS.Core.Tests` | Unit tests for the core logic. |
| `tests/WavePOS.Data.Tests` | Tests for the database layer (login, settings, drafts, receipts). |

## Run it

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) (or Visual Studio 2022+ with ".NET desktop development").

```bash
dotnet run --project src/WavePOS.Desktop
dotnet test
```

The database is created at `%LOCALAPPDATA%\WavePOS\wavepos.db` with sample products on first run.

## Web portal

```bash
dotnet run --project src/WavePOS.Web
```

Then open http://localhost:5254. It works on PCs, tablets and phones.

- Same features as the desktop till: first-time manager setup, sign in, products, barcode entry, cart, Cash / Card / Mobile Money, change, draft invoices, receipt preview and printing, Settings with logo upload (managers only), and "Built by Wave".
- Printing uses the browser's print dialog. Receipts and invoices have print layouts for 80mm rolls and A4. Choose "Save as PDF" in the dialog to get a PDF.
- Data is stored in `src/WavePOS.Web/App_Data/wavepos.db` by default. Set `WavePOS:Database` in `appsettings.json` (or `WAVEPOS_DB`) to use a different file.
- The web portal and the desktop app each have their **own** database for now. Sharing one set of data across tills and the web needs a central server database (a planned next step).
- For use over the internet, host it behind HTTPS.

## Retail back office (web portal)

| Section | Who | What |
| --- | --- | --- |
| **Inventory** | Everyone can view; supervisors adjust; managers edit | Products with variants (e.g. Size × Colour, each with its own SKU and stock), cost and margin, reorder levels, low/out-of-stock alerts, stock value. Receive stock (weighted-average cost), adjust (damaged, expired, theft) and stock counts. Every change is in the stock ledger. |
| **Customers** | Everyone can look up; supervisors edit; managers handle data requests | Customer profiles, purchase history, visits and spend. Marketing consent is recorded with a date. Attach a customer to a sale at the till. |
| **Returns** | Supervisors and managers | Refund all or part of a sale by receipt number, choose whether items go back in stock, print a refund slip. Can't refund more than was sold. Exchanges = refund + new sale. |
| **Staff** | Managers | Unlimited users. Roles: Cashier, Supervisor, Manager, Accountant / bookkeeper. Employee details, disable accounts, reset PINs. At least one manager must stay active. |
| **Audit trail** | Managers, accountants | Sign-ins (incl. failed and lockouts), sales, refunds, stock and price changes, customer data access/exports, journals, bank reconciliation, staff and settings changes. Append-only and hash-chained, with an integrity check and CSV export. |
| **Accounts** | Managers, accountants | Double-entry books, see below. |

### Data protection (Zambia Data Protection Act 2021)

- Privacy notice on customer forms; marketing only with recorded consent (and consent can be withdrawn).
- Every view, change, export and anonymisation of a customer's data is in the audit trail.
- **Export** gives a customer a copy of their data (JSON). **Anonymise** removes their personal details but keeps the sales for tax records.

## Accounting (web portal)

Double-entry books that fill themselves from the shop. Accountants and bookkeepers get their own role: they see the books, reports, inventory, customers and the audit trail, but not the till or staff settings. The desktop till refuses accountant sign-ins.

- **Chart of accounts**: a starting chart for a Zambian VAT-registered retailer (cash, bank, mobile money, card clearing, inventory, VAT input/output, payables, equity, sales, cost of sales, stock losses and common expenses). Add your own accounts and mark bank accounts or wallets. Expense accounts numbered 5000–5999 count as cost of sales.
- **Automatic postings**, in the same transaction as the shop activity:
  - Sale: cash, card or mobile money in; sales (excl. VAT) and VAT output; cost of sales out of inventory.
  - Refund: the reverse, with restocked items back into inventory at the cost they were sold at.
  - Stock received: inventory owed to the supplier. Damaged, expired, stolen and stock-count differences go to stock losses. Cost-price changes revalue stock on hand.
  - The Inventory account always equals the stock value at cost (tests check this).
  - When the books start, the stock already on hand is posted as an opening balance. Sales from before the upgrade are not posted.
- **Journals**: post manual journals (wages, loans, depreciation, opening balances). Debits must equal credits. Posted journals can't be edited or deleted (database triggers): correct them with a reversal. Till and stock entries are undone by refunding or adjusting stock instead.
- **Lock date**: after filing VAT or closing a month, lock the books so nothing can be posted on or before that date.
- **Bank reconciliation**: import a bank or mobile-money statement as CSV (upload or paste). Columns are found by their headings (Date, Description/Narration/Details, Amount or Debit + Credit, Reference, Balance) and dates are read day-first. Re-importing the same file skips lines already imported. For each line:
  - match it to an entry already in the books (suggested automatically: same amount, within a week);
  - post it to an account, optionally splitting out 16% VAT; or
  - exclude it with a reason.
  - A check shows whether the books agree with the statement balance.
- **Reports**: profit & loss, balance sheet, trial balance and a VAT summary for the ZRA return (standard-rated and zero-rated sales, output and input VAT). Each has CSV download and print. Each account has a ledger with a running balance.
- **Bank feeds**: live feeds need a bank or aggregator partner. Statement import works today, and a feed can plug into the same import (`BankRepository.ImportLines`).

## Roadmap

1. ✅ Retail core: staff & roles, audit trail, inventory & variants, customers, returns.
2. ✅ Accounting: chart of accounts, double-entry books from sales/refunds/stock, accountant & bookkeeper views, bank statement import and reconciliation.
3. Central server sync: desktop tills share the web portal's data and work offline.
4. Reports & analytics: sales by day/product/staff/customer, margins, seasonal trends, stock turnover.
5. Hardware: ESC/POS receipt printers and cash drawers, barcode scanners, card terminals and mobile money (provider to be chosen).

## Logging in

- On first start there are no accounts, so the app asks you to create the **manager** account.
- After that, everyone signs in with a username and PIN/password (at least 4 characters). There's an on-screen keypad for touch screens.
- 5 wrong attempts lock that account for 5 minutes.
- Passwords are stored as salted PBKDF2 hashes, never as plain text.
- Every sale records which user made it. **Log out** is in the top-right of the till.
- Set the `WAVEPOS_DB` environment variable to use a different database file (e.g. for training or testing).

## Branding

- Ships with the **Wave Cyber Security** brand (logo `#6E91CB`, text `#231F20`). The logo is drawn as vectors from `Branding/WaveBrand.xaml`, generated from the brand SVG.
- Each business can use its own logo: **Settings → Upload your logo** (PNG/JPG). It shows on the sign-in screen, the till header and printed receipts/invoices. "Use the Wave logo" switches back.
- Business name, address, phone, email, TPIN and the receipt message are set in **Settings** (managers only).
- "Built by Wave" always shows at the bottom of each screen.

## Printing and draft invoices

- **Draft invoice** on the till saves the cart as a numbered quotation (`DRF-00001`, valid 14 days), asks for an optional customer name, and opens a print preview. The cart is kept so the sale can still go ahead.
- **Last receipt** previews and reprints the receipt for the last sale.
- **Print receipt after each sale** prints automatically to the printer chosen in Settings.
- Paper sizes: **80mm receipt roll** or **A4**, chosen in Settings and switchable in the preview.
- To save a PDF, click **Print…** and choose **Microsoft Print to PDF**.

## Till shortcuts

- Scan a barcode (or type a code/SKU) and press **Enter** to add it.
- **F12** completes the sale.

## Licence

Proprietary software. Copyright (c) 2026 Wave Cyber Security Limited. All rights reserved.
No use, copying, modification or distribution without written permission from Wave. See [LICENSE](LICENSE).
