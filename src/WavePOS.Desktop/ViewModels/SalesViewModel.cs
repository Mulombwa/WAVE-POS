using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using WavePOS.Core;
using WavePOS.Core.Models;
using WavePOS.Core.Sales;
using WavePOS.Data;

namespace WavePOS.Desktop.ViewModels;

/// <summary>One row in the cart list on screen.</summary>
public record CartLineItem(long ProductId, string Name, int Quantity, string UnitPrice, string LineTotal);

/// <summary>Drives the main sales (till) screen.</summary>
public class SalesViewModel : ObservableObject
{
    private readonly ProductRepository _products;
    private readonly SaleRepository _sales;
    private readonly Cart _cart = new();

    private string _searchText = "";
    private string _tenderedText = "";
    private string _statusMessage;
    private bool _statusIsError;
    private PaymentMethod _paymentMethod = PaymentMethod.Cash;

    public SalesViewModel(ProductRepository products, SaleRepository sales, User cashier)
    {
        _products = products;
        _sales = sales;
        Cashier = cashier;
        _statusMessage = $"Welcome, {cashier.DisplayName}. Scan a barcode or tap a product.";

        LogoutCommand = new RelayCommand(Logout);
        AddProductCommand = new RelayCommand(p => { if (p is Product product) AddToCart(product); });
        IncreaseCommand = new RelayCommand(p => ChangeQuantity(p, +1));
        DecreaseCommand = new RelayCommand(p => ChangeQuantity(p, -1));
        RemoveCommand = new RelayCommand(p => { if (p is CartLineItem l) { _cart.Remove(l.ProductId); RefreshCart(); } });
        ClearCartCommand = new RelayCommand(_ => { _cart.Clear(); RefreshCart(); SetStatus("Sale cleared."); },
                                            _ => !_cart.IsEmpty);
        ScanCommand = new RelayCommand(ScanOrSearch);
        QuickCashCommand = new RelayCommand(p => TenderedText = Convert.ToString(p, CultureInfo.InvariantCulture) ?? "");
        ExactCashCommand = new RelayCommand(_ => TenderedText = _cart.Total.ToString("0.00", CultureInfo.InvariantCulture));
        CompleteSaleCommand = new RelayCommand(_ => CompleteSale(), _ => !_cart.IsEmpty);

        LoadProducts();
        RefreshCart();
    }

    /// <summary>Raised when the cashier logs out; the app then returns to the login screen.</summary>
    public event Action? LogoutRequested;

    public User Cashier { get; }
    public string CashierText => $"{Cashier.DisplayName} · {Cashier.Role}";

    public ICommand LogoutCommand { get; }

    private void Logout()
    {
        if (!_cart.IsEmpty)
        {
            SetStatus("Finish or clear the current sale before logging out.", isError: true);
            return;
        }
        LogoutRequested?.Invoke();
    }

    public ObservableCollection<Product> Products { get; } = [];
    public ObservableCollection<CartLineItem> CartLines { get; } = [];

    public ICommand AddProductCommand { get; }
    public ICommand IncreaseCommand { get; }
    public ICommand DecreaseCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ClearCartCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand QuickCashCommand { get; }
    public ICommand ExactCashCommand { get; }
    public ICommand CompleteSaleCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) LoadProducts(); }
    }

    public string TenderedText
    {
        get => _tenderedText;
        set { if (SetProperty(ref _tenderedText, value)) OnPropertyChanged(nameof(ChangeText)); }
    }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }

    public string TotalText => Money.Format(_cart.Total);
    public string VatText => Money.Format(_cart.VatAmount);
    public string ItemCountText => _cart.ItemCount == 1 ? "1 item" : $"{_cart.ItemCount} items";

    public string ChangeText
    {
        get
        {
            if (_paymentMethod != PaymentMethod.Cash || !TryParseTendered(out var tendered)) return "—";
            var change = tendered - _cart.Total;
            return change >= 0 ? Money.Format(change) : $"Short {Money.Format(-change)}";
        }
    }

    // Payment method radio buttons bind to these.
    public bool IsCash { get => _paymentMethod == PaymentMethod.Cash; set { if (value) SetPayment(PaymentMethod.Cash); } }
    public bool IsCard { get => _paymentMethod == PaymentMethod.Card; set { if (value) SetPayment(PaymentMethod.Card); } }
    public bool IsMobileMoney { get => _paymentMethod == PaymentMethod.MobileMoney; set { if (value) SetPayment(PaymentMethod.MobileMoney); } }

    private void SetPayment(PaymentMethod method)
    {
        _paymentMethod = method;
        OnPropertyChanged(nameof(IsCash));
        OnPropertyChanged(nameof(IsCard));
        OnPropertyChanged(nameof(IsMobileMoney));
        OnPropertyChanged(nameof(ChangeText));
    }

    private void LoadProducts()
    {
        Products.Clear();
        foreach (var p in _products.Search(SearchText)) Products.Add(p);
    }

    private void AddToCart(Product product)
    {
        _cart.Add(product);
        RefreshCart();
        SetStatus($"Added {product.Name}.");
    }

    private void ChangeQuantity(object? parameter, int delta)
    {
        if (parameter is not CartLineItem line) return;
        _cart.SetQuantity(line.ProductId, line.Quantity + delta);
        RefreshCart();
    }

    /// <summary>
    /// Enter in the search box. Barcode scanners type the code then press Enter,
    /// so an exact code match goes straight into the cart.
    /// </summary>
    private void ScanOrSearch(object? _)
    {
        var text = SearchText.Trim();
        if (text.Length == 0) return;

        var product = _products.FindByCode(text) ?? (Products.Count == 1 ? Products[0] : null);
        if (product is null)
        {
            SetStatus(Products.Count == 0 ? $"No product matches \"{text}\"." : "Several matches — tap the product.", isError: Products.Count == 0);
            return;
        }

        AddToCart(product);
        SearchText = "";
    }

    private void CompleteSale()
    {
        decimal tendered = 0;
        if (_paymentMethod == PaymentMethod.Cash && !TryParseTendered(out tendered))
        {
            SetStatus("Enter the cash amount received.", isError: true);
            return;
        }

        try
        {
            var sale = _cart.Checkout(_paymentMethod, tendered);
            sale.CashierId = Cashier.Id;
            _sales.Save(sale);

            _cart.Clear();
            RefreshCart();
            TenderedText = "";
            SetPayment(PaymentMethod.Cash);
            LoadProducts(); // stock levels changed

            var change = sale.PaymentMethod == PaymentMethod.Cash ? $" Change: {Money.Format(sale.Change)}." : "";
            SetStatus($"Sale #{sale.Id} complete — {Money.Format(sale.Total)} by {Describe(sale.PaymentMethod)}.{change}");
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void RefreshCart()
    {
        CartLines.Clear();
        foreach (var l in _cart.Lines)
            CartLines.Add(new CartLineItem(l.Product.Id, l.Product.Name, l.Quantity,
                Money.Format(l.Product.Price), Money.Format(l.LineTotal)));

        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(VatText));
        OnPropertyChanged(nameof(ItemCountText));
        OnPropertyChanged(nameof(ChangeText));
    }

    private bool TryParseTendered(out decimal value) =>
        decimal.TryParse(TenderedText.Replace("K", "").Replace(",", "").Trim(),
            NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private void SetStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    private static string Describe(PaymentMethod m) => m switch
    {
        PaymentMethod.MobileMoney => "mobile money",
        _ => m.ToString().ToLowerInvariant(),
    };
}
