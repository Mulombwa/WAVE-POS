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

## Businesses, roles and modules

One WAVE POS server hosts many businesses, from a market stall to a large organisation.

- **Sign-up** (`/welcome`): a public intro page where a business enters its name, town, phone, optional TPIN and the owner's sign-in, and picks a size. It gets its own private database in its own folder (`businesses/{id}/`), so its data, logo and product pictures can never be reached from another business's account. The owner becomes the **super admin** and lands on a *Getting started* checklist (business details, products with pictures, staff, modules, first sale).
- **Signing in**: business code + username + PIN. The code (e.g. `mwila-salon`) is made from the business name and shown after sign-up; each device remembers it. Sign-up and sign-in attempts are rate-limited per address.
- **Upgrading**: the database from a single-business install is adopted automatically as the first business, with every module on. Managers become admins, supervisors become operations managers, and the longest-standing admin becomes super admin.
- **Modules**, chosen by size at sign-up and changed by the super admin under *Modules*. Menus and pages for switched-off modules disappear for everyone; switching a module back on brings its data back. The till and stock are always on; purchasing, sales orders and HR need the books.

| Size | Modules |
| --- | --- |
| Informal | Till, inventory |
| Small | + customers, returns & refunds, accounting |
| Large | + purchasing, sales orders, HR & payroll |

- **Roles** (what each can do, within the modules that are on):

| Role | Can do |
| --- | --- |
| Super admin | Everything, including modules and managing admins. At least one must stay active. |
| Admin | Everything except modules and admin accounts |
| Operations manager | Stock, buying and selling day to day; approves purchase orders and refunds; attendance; audit trail |
| Accountant / bookkeeper | The books, bills, invoices, payments, payroll |
| Sales | Till, customers, quotes and sales orders |
| Warehousing | Receive goods, deliver orders, adjust and count stock |
| Cashier | Till and customer look-up |

  Only a super admin can create, change or reset the PIN of an admin or super admin.
- **Product pictures**: PNG or JPG up to 2 MB on each product's page (checked by file contents, not name). One picture covers every size and colour. Pictures show on the till and in the stock list, and are only served to people signed in to that business.

## Retail back office (web portal)

| Section | Who | What |
| --- | --- | --- |
| **Inventory** | Everyone can view; supervisors adjust; managers edit | Products with variants (e.g. Size × Colour, each with its own SKU and stock), cost and margin, reorder levels, low/out-of-stock alerts, stock value. Receive stock (weighted-average cost), adjust (damaged, expired, theft) and stock counts. Every change is in the stock ledger. |
| **Customers** | Everyone can look up; supervisors edit; managers handle data requests | Customer profiles, purchase history, visits and spend. Marketing consent is recorded with a date. Attach a customer to a sale at the till. |
| **Returns** | Operations managers and admins | Refund all or part of a sale by receipt number, choose whether items go back in stock, print a refund slip. Can't refund more than was sold. Exchanges = refund + new sale. |
| **Staff** | Super admins, admins | Unlimited users with the roles above. Employee details, disable accounts, reset PINs. |
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

## Supply chain & orders (web portal)

WAVE POS is growing into an ERP. Buying and selling on account sit alongside the till; every step updates stock, posts to the books and is audited.

**Buying (procure-to-pay), under Purchasing**
- **Suppliers** with ZRA TPIN, contact details and payment terms.
- **Purchase orders**: raise a draft, then place it. Orders within the **approval limit** (default K5,000, set by a manager) are approved straight away; bigger ones wait for a manager. Nobody can approve their own order unless they're the only person who can approve, and that is flagged in the audit trail.
- **Goods received**: receive all or part of an order. Stock goes up at the ordered cost (averaged into the product's cost) and the books record it as *Goods received not yet billed*.
- **Supplier bills**: entered for exactly what was received, at the ordered price (three-way match: order, receipt, bill). The same supplier invoice number can't be entered twice. VAT on the bill becomes claimable input VAT.
- **Payments** from cash, bank or mobile money, in full or in part. They appear on the bank reconciliation page for matching.
- **Bills to pay** with ageing (not yet due, 1–30, 31–60, 61–90, over 90 days) and **reorder suggestions** that count stock already on order and turn into a draft purchase order in one click.

**Selling on account (order-to-cash), under Sales orders**
- **Quotes** with a validity date, printable as a quotation. Prices include VAT, like the till.
- **Confirming** reserves the stock and runs a **credit check**: what the customer owes plus open orders plus this order must stay within their credit limit. Going over needs a manager and a reason, recorded in the audit trail.
- **Deliveries** take stock out (it must be on hand) and post cost of sales; each prints a **delivery note** with signature lines.
- **Invoices** for what was delivered, due according to the customer's payment terms, then **customer payments** into cash, bank or mobile money.
- **Invoices & money owed** with ageing. Each customer's profile shows their account: TPIN, credit limit, terms, balance and orders.

**Who does what (separation of duties)**

| Role | Purchasing & sales orders |
| --- | --- |
| Operations manager | Raise and approve purchase orders, receive goods, raise quotes, confirm orders (within credit limits) and deliver |
| Sales | Raise quotes and confirm orders (within credit limits) |
| Warehousing | Receive goods and deliver orders |
| Accountant / bookkeeper | See everything; enter supplier bills, raise invoices, record payments |
| Admin / super admin | Everything, including approving big orders, setting the approval limit and credit limits, and credit-limit overrides |

Printed invoices are titled *Invoice*. ZRA requires VAT-registered businesses to issue tax invoices through **ZRA Smart Invoice**; connecting to it is on the roadmap.

## HR & payroll (web portal)

Under **HR & payroll**.

- **Employees**: job, department, contact details, NRC, TPIN, NAPSA and NHIMA numbers, and how they're paid (bank, mobile money or cash). Personal and pay details are only shown to HR and payroll staff; every time someone opens a record it's written to the audit trail, and the audit trail records which fields changed, never the personal values.
- **Contracts**: permanent, fixed-term or casual, with basic pay, housing, transport and other allowances, normal hours and annual leave (at least 24 days a year, per the Employment Code Act 2019). A new contract (a pay rise or renewal) ends the previous one. Contracts ending within 30 days are flagged.
- **Leave**: annual, sick, maternity, paternity, compassionate, family responsibility and unpaid. Annual leave builds up at 2 days per completed month and can't be over-booked; overlapping requests are refused; a manager approves or turns down (with a reason). Weekends aren't counted; public holidays aren't taken off yet.
- **Attendance**: a daily register (present, late, absent, on leave, day off) with hours and overtime. Approved leave is filled in automatically.
- **Payroll**, one run per month:
  1. *Prepare*: worked out for everyone with a contract that month, pro rata for people who joined or left part-way. Overtime is paid at 1.5 × the hourly rate (basic × 12 ÷ (52 × weekly hours)); unpaid leave and absences not covered by leave are deducted at basic ÷ working days in the month.
  2. *Adjust* while in draft: bonuses, and deductions after tax such as salary advance recovery. *Recalculate* picks up late attendance, leave or contract changes and keeps the adjustments.
  3. *Approve*: by a manager other than whoever prepared it (unless they're the only one, which is flagged). Posts to the books, dated the last day of the month: gross pay and the employer's NAPSA, NHIMA and skills levy as costs; PAYE, NAPSA, NHIMA and skills levy payable; advances recovered; and net wages payable.
  4. *Pay*: records net pay out of the bank, cash or mobile money. It then shows up for matching on the bank reconciliation page.
- **Payslips** (A4, print or PDF, one per page for the whole payroll), a CSV list for making the payments, and a summary of PAYE, NAPSA, NHIMA and skills levy due by the 10th of the next month.
- **Statutory rates** are kept as dated tables. Built in: PAYE 0% up to K5,100 a month, 20% to K7,100, 30% to K9,200 and 37% above; NAPSA 5% + 5% up to an earnings ceiling of K34,164 a month (2025) and K37,236 (from January 2026); NHIMA 1% + 1% of basic pay; skills development levy 0.5% of gross (employer). Whether NHIMA is on basic or gross pay, and whether employee NAPSA is taken off before PAYE, are settings. **Check these figures against the latest ZRA practice note and NAPSA notice before running a real payroll**, and add a new table whenever they change.

| Role | HR & payroll |
| --- | --- |
| Operations manager | Staff directory, attendance register, request leave |
| Accountant / bookkeeper | Staff directory, prepare payroll, payslips, record wages paid |
| Admin / super admin | Everything, including employee records and contracts, approving leave and payroll, and changing rates |

## Roadmap

1. ✅ Retail core: staff & roles, audit trail, inventory & variants, customers, returns.
2. ✅ Accounting: chart of accounts, double-entry books from sales/refunds/stock, accountant & bookkeeper views, bank statement import and reconciliation.
3. ✅ Supply chain & orders: suppliers, purchase orders with approvals, goods received, supplier bills and payments; quotes, sales orders with credit control, deliveries, invoices and customer payments.
4. ✅ HR & payroll: employee records, contracts, leave and attendance, Zambian payroll (PAYE, NAPSA, NHIMA, skills levy), payslips, posted to the books. Next for HR: public holidays, employee self-service, statutory return files (ZRA PAYE, NAPSA and NHIMA schedules).
5. Central server sync: desktop tills share the web portal's data and work offline.
6. Integrations & platform: ZRA Smart Invoice, a REST API, modules that can be switched on or off, encryption of sensitive fields and security monitoring.
7. Reports & analytics: sales by day/product/staff/customer, margins, seasonal trends, stock turnover.
8. Hardware: ESC/POS receipt printers and cash drawers, barcode scanners, card terminals and mobile money (provider to be chosen).
9. Later: manufacturing (bills of materials and production orders).

## Logging in

- **Web portal:** a new business signs up on `/welcome`; after that, staff sign in with the business code, their username and PIN/password (at least 4 characters).
- **Desktop till:** on first start there are no accounts, so it asks you to create the **super admin** account. There's an on-screen keypad for touch screens.
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
