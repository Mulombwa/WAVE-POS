# WAVE POS

Point-of-sale system. C# Windows desktop app first, then a .NET MAUI mobile app and an ASP.NET Core web app, all sharing the same core code.

## Projects

| Project | What it does |
| --- | --- |
| `src/WavePOS.Core` | Products, cart, VAT, checkout, money (Kwacha). Shared by all apps. |
| `src/WavePOS.Data` | Local SQLite database (works offline). Products and sales. |
| `src/WavePOS.Desktop` | Windows till app (WPF). |
| `tests/WavePOS.Core.Tests` | Unit tests for the core logic. |

## Run it

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) (or Visual Studio 2022+ with ".NET desktop development").

```bash
dotnet run --project src/WavePOS.Desktop
dotnet test
```

The database is created at `%LOCALAPPDATA%\WavePOS\wavepos.db` with sample products on first run.

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
