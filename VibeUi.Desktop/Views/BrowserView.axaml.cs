using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VibeUi.Desktop.ViewModels;

namespace VibeUi.Desktop.Views;

/// <summary>Hosts the native web view. Created lazily so the window still opens where no web engine exists.</summary>
public partial class BrowserView : UserControl
{
    private NativeWebView? _web;
    private BrowserViewModel? _model;

    public BrowserView()
    {
        InitializeComponent();
        AddressBox.AddHandler(KeyDownEvent, AddressKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Bind(DataContext as BrowserViewModel);
        AttachedToVisualTree += (_, _) => EnsureWebView();
    }

    private void Bind(BrowserViewModel? model)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.ActionRequested -= OnAction;
        }
        _model = model;
        if (_model is null)
            return;
        _model.PropertyChanged += OnModelChanged;
        _model.ActionRequested += OnAction;
        if (_web is not null)
            _web.Source = _model.Source;
    }

    private void EnsureWebView()
    {
        if (_web is not null)
            return;
        try
        {
            _web = new NativeWebView();
            _web.NavigationStarted += (_, _) => _model?.ReportNavigationStarted();
            _web.NavigationCompleted += (_, args) => _model?.ReportNavigation(args.Request, args.IsSuccess, _web.CanGoBack, _web.CanGoForward);
            _web.NewWindowRequested += (_, args) =>
            {
                // Keep pop-ups in the pane instead of spawning windows.
                if (args.Request is { } target)
                    _web.Navigate(target);
                args.Handled = true;
            };
            if (_model is not null)
                _web.Source = _model.Source;
            WebHost.Children.Add(_web);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or InvalidOperationException or TypeInitializationException or DllNotFoundException)
        {
            _web = null;
            if (_model is not null)
                _model.Error = $"No web engine is available here: {exception.Message}";
        }
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(BrowserViewModel.Source) && _web is not null && _model is not null && _web.Source != _model.Source)
            _web.Navigate(_model.Source);
    }

    private void OnAction(object? sender, string action)
    {
        if (_web is null)
            return;
        switch (action)
        {
            case "back": _web.GoBack(); break;
            case "forward": _web.GoForward(); break;
            case "refresh": _web.Refresh(); break;
            case "stop": _web.Stop(); break;
        }
    }

    private void AddressKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && _model is not null)
        {
            _model.Go();
            args.Handled = true;
        }
    }
}
