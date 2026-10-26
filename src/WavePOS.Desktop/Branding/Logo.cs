using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WavePOS.Core.Models;

namespace WavePOS.Desktop.Branding;

/// <summary>Loads the business's logo, falling back to the Wave logo.</summary>
public static class Logo
{
    /// <summary>Image types a business can upload as its logo.</summary>
    public const string FileFilter = "Logo images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp";

    public static ImageSource WaveLogo => (ImageSource)Application.Current.FindResource("WaveLogo");

    /// <summary>The business's own logo, or null if it hasn't set one (or the file can't be read).</summary>
    public static ImageSource? LoadCustom(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // read fully so the file isn't locked
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static ImageSource For(BusinessProfile profile) => LoadCustom(profile.LogoPath) ?? WaveLogo;

    /// <summary>
    /// Copies a chosen logo into the app's data folder so it keeps working if the original is moved.
    /// A new file name each time avoids clashing with the logo currently on screen.
    /// </summary>
    public static string Import(string sourceFile, string dataDirectory)
    {
        if (LoadCustom(sourceFile) is null) throw new InvalidOperationException("That file isn't a picture WAVE POS can read. Use PNG or JPG.");

        var dir = Path.Combine(dataDirectory, "branding");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"logo-{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(sourceFile).ToLowerInvariant()}");
        File.Copy(sourceFile, target, overwrite: true);
        return target;
    }
}
