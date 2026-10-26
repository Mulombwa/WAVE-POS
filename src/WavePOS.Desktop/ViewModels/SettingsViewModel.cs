using System.IO;
using System.Windows.Media;
using WavePOS.Core.Models;
using WavePOS.Data;
using WavePOS.Desktop.Branding;
using WavePOS.Desktop.Printing;

namespace WavePOS.Desktop.ViewModels;

public record PaperOption(PaperSize Value, string Label);

/// <summary>Business details, logo and printing options. Managers only.</summary>
public class SettingsViewModel : ObservableObject
{
    public const string AskEachTime = "Ask each time (show print dialog)";

    private readonly SettingsRepository _settings;
    private readonly string _dataDirectory;
    private readonly BusinessProfile _profile;

    private string? _pendingLogoFile;  // chosen but not yet saved
    private bool _useWaveLogo;
    private ImageSource _logoPreview;
    private string _errorMessage = "";

    public SettingsViewModel(SettingsRepository settings, string dataDirectory)
    {
        _settings = settings;
        _dataDirectory = dataDirectory;
        _profile = settings.GetBusinessProfile();
        var print = settings.GetPrintSettings();

        Name = _profile.Name;
        Address = _profile.Address;
        Phone = _profile.Phone;
        Email = _profile.Email;
        Tpin = _profile.Tpin;
        ReceiptFooter = _profile.ReceiptFooter;
        _useWaveLogo = !_profile.HasCustomLogo;
        _logoPreview = Logo.For(_profile);

        Printers = [AskEachTime, .. PrintService.InstalledPrinters()];
        SelectedPrinter = print.PrinterName is { } p && Printers.Contains(p) ? p : AskEachTime;
        ReceiptPaper = PaperOptions.First(o => o.Value == print.ReceiptPaper);
        InvoicePaper = PaperOptions.First(o => o.Value == print.InvoicePaper);
        AutoPrintReceipt = print.AutoPrintReceipt;
    }

    public string Name { get; set; }
    public string Address { get; set; }
    public string Phone { get; set; }
    public string Email { get; set; }
    public string Tpin { get; set; }
    public string ReceiptFooter { get; set; }

    public ImageSource LogoPreview { get => _logoPreview; private set => SetProperty(ref _logoPreview, value); }
    public string LogoDescription => _useWaveLogo ? "Using the Wave logo" : "Using your logo";
    public bool HasCustomLogo => !_useWaveLogo;

    public List<string> Printers { get; }
    public string SelectedPrinter { get; set; }

    public static IReadOnlyList<PaperOption> PaperOptions { get; } =
    [
        new(PaperSize.Receipt80mm, "80mm receipt roll"),
        new(PaperSize.A4, "A4 page"),
    ];

    public PaperOption ReceiptPaper { get; set; }
    public PaperOption InvoicePaper { get; set; }
    public bool AutoPrintReceipt { get; set; }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); }
    }
    public bool HasError => ErrorMessage.Length > 0;

    public void ChooseLogo(string file)
    {
        var image = Logo.LoadCustom(file);
        if (image is null)
        {
            ErrorMessage = "That file isn't a picture WAVE POS can read. Use a PNG or JPG.";
            return;
        }
        _pendingLogoFile = file;
        _useWaveLogo = false;
        LogoPreview = image;
        ErrorMessage = "";
        OnLogoChanged();
    }

    public void UseWaveLogo()
    {
        _pendingLogoFile = null;
        _useWaveLogo = true;
        LogoPreview = Logo.WaveLogo;
        OnLogoChanged();
    }

    private void OnLogoChanged()
    {
        OnPropertyChanged(nameof(LogoDescription));
        OnPropertyChanged(nameof(HasCustomLogo));
    }

    /// <summary>Saves everything. Returns false (with <see cref="ErrorMessage"/>) if something is wrong.</summary>
    public bool Save()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = "Enter the business name.";
            return false;
        }

        try
        {
            if (_useWaveLogo) _profile.LogoPath = null;
            else if (_pendingLogoFile is not null) _profile.LogoPath = Logo.Import(_pendingLogoFile, _dataDirectory);

            _profile.Name = Name;
            _profile.Address = Address;
            _profile.Phone = Phone;
            _profile.Email = Email;
            _profile.Tpin = Tpin;
            _profile.ReceiptFooter = ReceiptFooter;
            _settings.SaveBusinessProfile(_profile);

            _settings.SavePrintSettings(new PrintSettings
            {
                PrinterName = SelectedPrinter == AskEachTime ? null : SelectedPrinter,
                ReceiptPaper = ReceiptPaper.Value,
                InvoicePaper = InvoicePaper.Value,
                AutoPrintReceipt = AutoPrintReceipt,
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorMessage = $"Couldn't save: {ex.Message}";
            return false;
        }
    }
}
