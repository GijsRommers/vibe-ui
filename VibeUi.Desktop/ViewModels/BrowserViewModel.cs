using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VibeUi.Desktop.ViewModels;

/// <summary>
/// The built-in browser pane: an address bar over a native web view (WKWebView, WebView2). Handy
/// for the dev server a thread just started. The view binds to <see cref="Source"/> and reports
/// navigation back through <see cref="ReportNavigation"/>.
/// </summary>
public sealed partial class BrowserViewModel : ObservableObject
{
    public const string DefaultUrl = "http://localhost:3000/";

    private readonly Action<string> _openExternally;

    public BrowserViewModel(Action<string> openExternally, string? lastUrl = null)
    {
        _openExternally = openExternally;
        _addressText = lastUrl ?? DefaultUrl;
        _source = new Uri(_addressText);
    }

    /// <summary>What the web view shows. Set by Go, by agents, and by the view when the page navigates itself.</summary>
    [ObservableProperty] private Uri _source;

    [ObservableProperty] private string _addressText;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;
    [ObservableProperty] private string? _error;

    /// <summary>Requests the view perform a browser action (back, forward, refresh, stop).</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>Raised when the current URL changed, so the shell can remember it per project.</summary>
    public event EventHandler<string>? Navigated;

    [RelayCommand]
    public void Go() => Navigate(AddressText);

    /// <summary>Loads a URL. Bare hosts get http://; a port alone means localhost.</summary>
    public void Navigate(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;
        if (int.TryParse(text, out var port))
            text = $"http://localhost:{port}/";
        else if (!text.Contains("://", StringComparison.Ordinal))
            text = text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || text.StartsWith("127.0.0.1", StringComparison.Ordinal) || text.Contains(":") && !text.Contains('.')
                ? "http://" + text
                : "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Error = "That is not a web address.";
            return;
        }
        Error = null;
        AddressText = uri.ToString();
        Source = uri;
    }

    [RelayCommand] private void Back() => ActionRequested?.Invoke(this, "back");
    [RelayCommand] private void Forward() => ActionRequested?.Invoke(this, "forward");
    [RelayCommand] private void Refresh() => ActionRequested?.Invoke(this, "refresh");
    [RelayCommand] private void Stop() => ActionRequested?.Invoke(this, "stop");

    [RelayCommand]
    private void OpenExternally() => _openExternally(Source.ToString());

    /// <summary>Called by the view after a navigation: keeps the address bar and history buttons honest.</summary>
    public void ReportNavigation(Uri? url, bool success, bool canGoBack, bool canGoForward, string? error = null)
    {
        IsLoading = false;
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
        Error = success ? null : error ?? "The page could not be loaded.";
        if (url is not null)
        {
            AddressText = url.ToString();
            Navigated?.Invoke(this, url.ToString());
        }
    }

    public void ReportNavigationStarted() => IsLoading = true;
}
