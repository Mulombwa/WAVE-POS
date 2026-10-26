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

## Till shortcuts

- Scan a barcode (or type a code/SKU) and press **Enter** to add it.
- **F12** completes the sale.
