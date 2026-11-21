using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;
using NativeColor = Windows.UI.Color;
using NativeWindow = Microsoft.UI.Xaml.Window;
using MauiWindow = Microsoft.Maui.Controls.Window;
using AutomationProperties = Microsoft.UI.Xaml.Automation.AutomationProperties;
using Border = Microsoft.UI.Xaml.Controls.Border;
using Button = Microsoft.UI.Xaml.Controls.Button;
using CornerRadius = Microsoft.UI.Xaml.CornerRadius;
using Grid = Microsoft.UI.Xaml.Controls.Grid;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using Image = Microsoft.UI.Xaml.Controls.Image;
using KeyboardAccelerator = Microsoft.UI.Xaml.Input.KeyboardAccelerator;
using SolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
using Stretch = Microsoft.UI.Xaml.Media.Stretch;
using Thickness = Microsoft.UI.Xaml.Thickness;
using VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;
using Visibility = Microsoft.UI.Xaml.Visibility;

namespace Laveyana.LavOSC;

// Adapted from LavPulse's Windows caption overlay.
internal sealed class WindowsWindowChrome
{
    private const int CaptionHeight = 44;
    private const int ToolbarWidth = 170;
    private const double AnimationMilliseconds = 180;
    private readonly NativeWindow window;
    private readonly Panel root;
    private readonly AppWindow appWindow;
    private readonly OverlappedPresenter presenter;
    private readonly OscReceiverPage page;
    private readonly Grid caption;
    private readonly Grid dragRegion;
    private readonly Button maximize;
    private readonly Button fullscreen;
    private readonly TranslateTransform translation = new();
    private readonly UISettings system = new();
    private readonly KeyboardAccelerator fullscreenKey;
    private readonly KeyboardAccelerator escapeKey;
    private RectInt32 restoreBounds;
    private bool restoreMaximized;
    private bool captionVisible = true;
    private long animationStarted;
    private double animationFrom;
    private bool IsFullscreen => appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    public static void Prepare(NativeWindow window)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        window.ExtendsContentIntoTitleBar = true;
        window.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
    }

    public static void Attach(MauiWindow window, OscReceiverPage page)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported() || window.Handler?.PlatformView is not NativeWindow native)
            return;

        if (native.Content is not Panel root || native.AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            native.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            native.ExtendsContentIntoTitleBar = false;
            return;
        }

        _ = new WindowsWindowChrome(native, root, presenter, page);
    }

    private WindowsWindowChrome(NativeWindow window, Panel root, OverlappedPresenter presenter, OscReceiverPage page)
    {
        this.window = window;
        this.root = root;
        this.presenter = presenter;
        this.page = page;
        appWindow = window.AppWindow;
        root.RequestedTheme = ElementTheme.Dark;
        root.Background ??= Brush(PulseVisuals.Background);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        buttons.Children.Add(CreateButton("\uE921", "Свернуть", () =>
        {
            if (IsFullscreen)
                ExitFullscreen();
            presenter.Minimize();
        }));
        maximize = CreateButton("\uE922", "Развернуть", ToggleMaximize);
        fullscreen = CreateButton("\uE92D", "На весь экран (F11)", ToggleFullscreen);
        buttons.Children.Add(maximize);
        buttons.Children.Add(fullscreen);
        var close = CreateButton("\uE8BB", "Закрыть", window.Close);
        close.Resources["ButtonBackgroundPointerOver"] = Brush(PulseVisuals.Danger);
        buttons.Children.Add(close);
        var toolbar = new Border
        {
            Child = buttons, Background = Brush(PulseVisuals.Card, 230), CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(3)
        };

        dragRegion = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Margin = new Thickness(0, 0, ToolbarWidth, 0)
        };
        dragRegion.Children.Add(CreateBrand());
        caption = new Grid
        {
            Height = CaptionHeight, VerticalAlignment = VerticalAlignment.Top,
            Background = Brush(PulseVisuals.Background, 160), RenderTransform = translation
        };
        caption.Children.Add(dragRegion);
        caption.Children.Add(toolbar);
        Canvas.SetZIndex(caption, 1000);
        dragRegion.Loaded += OnDragRegionLoaded;
        root.Children.Add(caption);

        fullscreenKey = new KeyboardAccelerator { Key = VirtualKey.F11 };
        fullscreenKey.Invoked += (_, args) => { ToggleFullscreen(); args.Handled = true; };
        escapeKey = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escapeKey.Invoked += (_, args) =>
        {
            if (IsFullscreen)
            {
                ExitFullscreen();
                args.Handled = true;
            }
        };
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        root.KeyboardAccelerators.Add(fullscreenKey);
        root.KeyboardAccelerators.Add(escapeKey);
        root.AddHandler(UIElement.PointerMovedEvent, (PointerEventHandler)OnPointer, handledEventsToo: true);
        root.PointerExited += OnPointer;
        window.Activated += OnActivated;
        window.Closed += OnClosed;
        appWindow.Changed += OnWindowChanged;
        page.SetCaptionInset(CaptionHeight);
        Prepare(window);
        UpdateButtons();
    }

    private static StackPanel CreateBrand()
    {
        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0)
        };
        string path = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcon", "lavosc.png");
        if (File.Exists(path))
            brand.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri(path)), Width = 26, Height = 26, Stretch = Stretch.Uniform
            });
        brand.Children.Add(new TextBlock
        {
            Text = "LavOSC", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(PulseVisuals.Text), VerticalAlignment = VerticalAlignment.Center
        });
        return brand;
    }

    private void OnDragRegionLoaded(object sender, RoutedEventArgs args)
    {
        if (!IsFullscreen)
            window.SetTitleBar(dragRegion);
    }

    private void ToggleMaximize()
    {
        if (IsFullscreen)
        {
            ExitFullscreen();
            presenter.Maximize();
        }
        else if (presenter.State == OverlappedPresenterState.Maximized)
        {
            presenter.Restore();
        }
        else
        {
            presenter.Maximize();
        }
        UpdateButtons();
    }

    private void ToggleFullscreen()
    {
        if (IsFullscreen)
        {
            ExitFullscreen();
            return;
        }

        restoreMaximized = presenter.State == OverlappedPresenterState.Maximized;
        var position = appWindow.Position;
        var size = appWindow.Size;
        restoreBounds = new RectInt32(position.X, position.Y, size.Width, size.Height);
        window.SetTitleBar(null);
        appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        page.SetCaptionInset(0);
        SetCaptionVisible(false, animate: true);
        UpdateButtons();
    }

    private void ExitFullscreen()
    {
        appWindow.SetPresenter(presenter);
        if (restoreMaximized)
            presenter.Maximize();
        else
        {
            presenter.Restore();
            appWindow.MoveAndResize(restoreBounds);
        }
        Prepare(window);
        if (dragRegion.IsLoaded)
            window.SetTitleBar(dragRegion);
        page.SetCaptionInset(CaptionHeight);
        SetCaptionVisible(true, animate: false);
        UpdateButtons();
    }

    private void OnPointer(object sender, PointerRoutedEventArgs args)
    {
        if (!IsFullscreen || args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
            return;

        var point = args.GetCurrentPoint(root);
        var position = point.Position;
        bool inside = position.X >= 0 && position.X < root.ActualWidth && position.Y >= 0 && position.Y < root.ActualHeight;
        if (captionVisible && (!inside || position.Y >= CaptionHeight + 8))
            SetCaptionVisible(false, animate: true);
        else if (!captionVisible && inside && position.Y <= 8 && !point.Properties.IsLeftButtonPressed)
            SetCaptionVisible(true, animate: true);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (IsFullscreen && args.WindowActivationState == WindowActivationState.Deactivated)
            SetCaptionVisible(false, animate: false);
    }

    private void SetCaptionVisible(bool visible, bool animate)
    {
        if (animate && captionVisible == visible)
            return;

        captionVisible = visible;
        CompositionTarget.Rendering -= OnAnimationFrame;
        if (visible)
            caption.Visibility = Visibility.Visible;
        double target = visible ? 0 : -CaptionHeight;
        if (!animate || !system.AnimationsEnabled || translation.Y == target)
        {
            FinishAnimation(target);
            return;
        }

        animationFrom = translation.Y;
        animationStarted = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnAnimationFrame;
    }

    private void OnAnimationFrame(object? sender, object args)
    {
        double target = captionVisible ? 0 : -CaptionHeight;
        double progress = Stopwatch.GetElapsedTime(animationStarted).TotalMilliseconds / AnimationMilliseconds;
        if (progress >= 1)
            FinishAnimation(target);
        else
            translation.Y = target + (animationFrom - target) * Math.Pow(1 - progress, 3);
    }

    private void FinishAnimation(double target)
    {
        CompositionTarget.Rendering -= OnAnimationFrame;
        translation.Y = target;
        caption.Visibility = captionVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange || args.DidSizeChange)
            UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool maximized = !IsFullscreen && presenter.State == OverlappedPresenterState.Maximized;
        UpdateButton(maximize, maximized ? "\uE923" : "\uE922", maximized ? "Восстановить размер окна" : "Развернуть");
        UpdateButton(fullscreen, IsFullscreen ? "\uE92C" : "\uE92D",
            IsFullscreen ? "Выйти из полноэкранного режима (F11 / Esc)" : "На весь экран (F11)");
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        CompositionTarget.Rendering -= OnAnimationFrame;
        root.KeyboardAccelerators.Remove(fullscreenKey);
        root.KeyboardAccelerators.Remove(escapeKey);
        root.RemoveHandler(UIElement.PointerMovedEvent, (PointerEventHandler)OnPointer);
        root.PointerExited -= OnPointer;
        window.Activated -= OnActivated;
        window.Closed -= OnClosed;
        appWindow.Changed -= OnWindowChanged;
        dragRegion.Loaded -= OnDragRegionLoaded;
    }

    private static Button CreateButton(string glyph, string description, Action action)
    {
        var button = new Button
        {
            Width = 36, Height = 32, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = Brush(PulseVisuals.Text), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Content = new FontIcon { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13 }
        };
        button.Click += (_, _) => action();
        UpdateButton(button, glyph, description);
        return button;
    }

    private static void UpdateButton(Button button, string glyph, string description)
    {
        ((FontIcon)button.Content).Glyph = glyph;
        AutomationProperties.SetName(button, description);
        ToolTipService.SetToolTip(button, description);
    }

    private static SolidColorBrush Brush(Microsoft.Maui.Graphics.Color color, byte alpha = 255)
    {
        color.ToRgb(out byte red, out byte green, out byte blue);
        return new SolidColorBrush(NativeColor.FromArgb(alpha, red, green, blue));
    }
}
