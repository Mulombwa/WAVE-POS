using System.Windows.Media;
using WavePOS.Core.Models;
using WavePOS.Core.Security;
using WavePOS.Data;

namespace WavePOS.Desktop.ViewModels;

/// <summary>
/// Login screen. On a brand-new install (no users yet) it switches to
/// first-time setup, where the owner creates the first manager account.
/// </summary>
public class LoginViewModel : ObservableObject
{
    private readonly UserRepository _users;
    private string _username = "";
    private string _displayName = "";
    private string _errorMessage = "";
    private bool _isBusy;
    private bool _isSetupMode;

    public LoginViewModel(UserRepository users, BusinessProfile business)
    {
        _users = users;
        _isSetupMode = !users.AnyUsers();
        BusinessName = business.Name;
        LogoImage = Branding.Logo.For(business);
    }

    /// <summary>The business's logo (Wave logo until the business uploads its own).</summary>
    public ImageSource LogoImage { get; }
    public string BusinessName { get; }

    /// <summary>Raised when someone has logged in successfully.</summary>
    public event Action<User>? LoggedIn;

    public bool IsSetupMode
    {
        get => _isSetupMode;
        private set
        {
            if (SetProperty(ref _isSetupMode, value))
            {
                OnPropertyChanged(nameof(Heading));
                OnPropertyChanged(nameof(SubHeading));
                OnPropertyChanged(nameof(SubmitText));
            }
        }
    }

    public string Heading => IsSetupMode ? "Welcome to WAVE POS" : "Sign in";
    public string SubHeading => IsSetupMode
        ? "Create the manager account. You'll use it to add cashiers and manage the shop."
        : "Enter your username and PIN or password.";
    public string SubmitText => IsSetupMode ? "Create account & start" : "Sign in";

    public string Username { get => _username; set { if (SetProperty(ref _username, value)) ErrorMessage = ""; } }
    public string DisplayName { get => _displayName; set { if (SetProperty(ref _displayName, value)) ErrorMessage = ""; } }

    public string ErrorMessage
    {
        get => _errorMessage;
        set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => ErrorMessage.Length > 0;

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    /// <summary>Called by the window with the PasswordBox contents (PasswordBox can't be data-bound).</summary>
    public async Task SubmitAsync(string password, string confirmPassword)
    {
        if (IsBusy) return;

        if (string.IsNullOrWhiteSpace(Username)) { ErrorMessage = "Enter your username."; return; }
        if (string.IsNullOrEmpty(password)) { ErrorMessage = "Enter your PIN or password."; return; }

        IsBusy = true;
        try
        {
            if (IsSetupMode)
                await CreateFirstManagerAsync(password, confirmPassword);
            else
                await LoginAsync(password);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoginAsync(string password)
    {
        // Password hashing is deliberately slow, so keep it off the UI thread.
        var result = await Task.Run(() => _users.Authenticate(Username, password));

        switch (result.Status)
        {
            case LoginStatus.Success when !result.User!.Can(Permission.Sell):
                ErrorMessage = "This account can't use the till. Accountants sign in to the web portal.";
                break;
            case LoginStatus.Success:
                ErrorMessage = "";
                LoggedIn?.Invoke(result.User!);
                break;
            case LoginStatus.LockedOut:
                ErrorMessage = $"Too many wrong attempts. Try again after {result.LockedUntil:HH:mm}.";
                break;
            case LoginStatus.Disabled:
                ErrorMessage = "This account has been disabled. Ask a manager.";
                break;
            default:
                ErrorMessage = "Wrong username or password.";
                break;
        }
    }

    private async Task CreateFirstManagerAsync(string password, string confirmPassword)
    {
        if (string.IsNullOrWhiteSpace(DisplayName)) { ErrorMessage = "Enter your full name."; return; }
        if (PasswordHasher.Validate(password) is { } problem) { ErrorMessage = problem; return; }
        if (password != confirmPassword) { ErrorMessage = "The two passwords don't match."; return; }

        try
        {
            var user = await Task.Run(() => _users.Create(Username, DisplayName, password, UserRole.Manager));
            IsSetupMode = false;
            LoggedIn?.Invoke(user);
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
