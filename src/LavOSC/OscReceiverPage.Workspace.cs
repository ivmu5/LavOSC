using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace Laveyana.LavOSC;

internal sealed partial class OscReceiverPage
{
    private readonly DashboardGrid pinnedCards = new(3);
    private readonly Label emptyDashboard = Caption("Нет закреплённых модулей.");
    private readonly Button settingsListen = MakeButton("Начать приём");
    private readonly Label settingsNetworkStatus = Caption();
    private ContentView? wheelSlot;
    private ContentView? pedalsSlot;
    private ContentView? detailsSlot;
    private View wheelBody = null!;
    private View pedalsBody = null!;
    private View wheelSettings = null!;
    private Grid workspace = null!;
    private View? activeSection;
    private Action<ModuleEntry> openCatalogModule = null!;

    internal void SetCaptionInset(double height) => workspace.Margin = new Thickness(0, height, 0, 0);

    private Grid MakeWorkspace()
    {
        wheelBody = new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                Row(angle, axisValue), gaugeView, sensorValues, sampleStatus, notice,
                Row(calibrate, drive)
            }
        };
        wheelSettings = Card(new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Heading("Настройки руля", 16),
                SliderField("Угол от центра до упора", lockDegrees, "°"),
                SliderField("Мёртвая зона", deadZone, "°"),
                SliderField("Сглаживание", smoothing, " мс"),
                Row(Caption("Инверсия руля"), invert)
            }
        });
        pedalsBody = MakePedalBody();

        LoadClients();
        var dashboard = MakeDashboard();
        catalog = MakeCatalog();
        clientsView = MakeClientsView();
        var settings = MakeSettings();
        var sections = new[]
        {
            (Page: (View)dashboard, Button: NavButton("osc_dashboard.png", "Главная")),
            (Page: catalog, Button: NavButton("osc_modules.png", "Модули")),
            (Page: clientsView, Button: NavButton("osc_clients.png", "Клиенты")),
            (Page: settings, Button: NavButton("osc_settings.png", "Настройки"))
        };

        var navigation = new Grid
        {
            BackgroundColor = PulseVisuals.Surface,
            Padding = new Thickness(9, 12), RowSpacing = 8,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)
            }
        };
        var pages = new Grid();
        openCatalogModule = entry => Show(catalog, entry);
        for (int index = 0; index < sections.Length; index++)
        {
            var (page, button) = sections[index];
            page.Margin = new Thickness(18, 16, 18, 16);
            pages.Add(page);
            navigation.Add(button, 0, index == 3 ? 4 : index);
            button.Clicked += (_, _) => Show(page);
        }

        void Show(View page, ModuleEntry? selectedModule = null)
        {
            if (activeSection == page)
                return;

            ClearPinnedBodies();
            if (detailsSlot is not null)
                detailsSlot.Content = null;
            detailsSlot = null;
            moduleDetails.Children.Clear();
            activeSection = page;
            foreach (var (panel, button) in sections)
            {
                panel.IsVisible = panel == page;
                button.BackgroundColor = panel.IsVisible ? PulseVisuals.Selected : Colors.Transparent;
            }
            if (page == catalog)
                FilterModules(selectedModule);
            else if (page == dashboard)
                ShowPinnedModules();
        }

        var layout = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(new GridLength(62)), new ColumnDefinition(GridLength.Star) }
        };
        layout.Add(navigation);
        layout.Add(pages, 1);
        Show(dashboard);
        return layout;
    }

    private static ImageButton NavButton(string icon, string title)
    {
        var button = new ImageButton
        {
            Source = icon, Aspect = Aspect.AspectFit, Padding = 10, WidthRequest = 44,
            HeightRequest = 44, CornerRadius = 9, BackgroundColor = Colors.Transparent
        };
        ToolTipProperties.SetText(button, title);
        SemanticProperties.SetDescription(button, title);
        return button;
    }

    private Grid MakeDashboard()
    {
        var reload = MakeButton("Обновить IP");
        reload.Clicked += (_, _) => ShowAddresses();
        var summaries = new DashboardGrid(2);
        summaries.SetCards([
            Card(new VerticalStackLayout
            {
                Spacing = 7,
                Children =
                {
                    Heading("Приём OSC", 16), networkStatus, localAddresses, discoveryStatus, routingStatus,
                    Row(listen, reload)
                }
            }),
            Card(new VerticalStackLayout
            {
                Spacing = 7,
                Children = { Heading("vJoy", 16), vJoyStatus, vJoyDevices, vJoyLibrary }
            })
        ]);
        var content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Children = { Heading("Главная", 26), summaries, Heading("Закреплённые модули", 18), pinnedCards, emptyDashboard }
            }
        };
        var clients = MakeDashboardClients();
        var right = new ColumnDefinition(new GridLength(268));
        var dashboard = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), right }
        };
        dashboard.Add(content);
        dashboard.Add(clients, 1);
        dashboard.SizeChanged += (_, _) =>
        {
            if (dashboard.Width > 0)
                right.Width = new GridLength(Math.Clamp(Math.Floor(dashboard.Width * .28), 250, 310));
        };
        return dashboard;
    }

    private ScrollView MakeSettings()
    {
        settingsListen.Clicked += (_, _) => ToggleListening();
        var cards = new DashboardGrid(2);
        cards.SetCards([
            Card(new VerticalStackLayout
            {
                Spacing = 8,
                Children = { Heading("Приём OSC", 16), Field("UDP-порт", port), settingsListen, settingsNetworkStatus }
            }),
            Card(new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    Heading("vJoy", 16), Row(Caption("Автоподготовка при запуске"), autoSetup),
                    Field("Устройство vJoy", device), Field("Путь к vJoyInterface.dll", dllPath), prepare
                }
            })
        ]);
        return new ScrollView { Content = new VerticalStackLayout { Spacing = 12, Children = { Heading("Настройки", 26), cards } } };
    }

    private void ShowPinnedModules()
    {
        ClearPinnedBodies();
        var cards = new List<View>();
        foreach (ModuleEntry entry in moduleEntries.Where(entry => entry.IsPinned))
            cards.Add(MakePinnedCard(entry));
        pinnedCards.SetCards(cards);
        emptyDashboard.IsVisible = cards.Count == 0;
    }

    private void ClearPinnedBodies()
    {
        if (wheelSlot is not null)
            wheelSlot.Content = null;
        if (pedalsSlot is not null)
            pedalsSlot.Content = null;
        wheelSlot = pedalsSlot = null;
    }

    private View MakePinnedCard(ModuleEntry entry)
    {
        var title = MakeButton(entry.Title);
        title.FontSize = 16;
        title.FontAttributes = FontAttributes.Bold;
        title.TextColor = Accent;
        title.BackgroundColor = Colors.Transparent;
        title.Padding = new Thickness(0, 4);
        title.HorizontalOptions = LayoutOptions.Start;
        title.LineBreakMode = LineBreakMode.TailTruncation;
        title.VerticalOptions = LayoutOptions.Center;
        title.Clicked += (_, _) => openCatalogModule(entry);
        SemanticProperties.SetDescription(title, $"Открыть модуль «{entry.Title}»");
        var state = MakeButton("");
        state.SetBinding(Button.TextProperty, nameof(ModuleEntry.State));
        state.TextColor = Muted;
        state.MinimumHeightRequest = 28;
        state.Padding = new Thickness(8, 3);
        state.Triggers.Add(new DataTrigger(typeof(Button))
        {
            Binding = new Binding(nameof(ModuleEntry.IsEnabled)), Value = true,
            Setters =
            {
                new Setter { Property = Button.TextColorProperty, Value = Accent },
                new Setter { Property = BackgroundColorProperty, Value = PulseVisuals.Selected }
            }
        });
        state.Clicked += async (_, _) => await ToggleModuleAsync(entry);
        ToolTipProperties.SetText(state, "Включить или отключить модуль");
        var unpin = MakeButton("−");
        unpin.FontSize = 18;
        unpin.WidthRequest = 30;
        unpin.HeightRequest = 30;
        unpin.MinimumHeightRequest = 30;
        unpin.MinimumWidthRequest = 30;
        unpin.Padding = 0;
        unpin.BackgroundColor = PulseVisuals.Danger;
        ToolTipProperties.SetText(unpin, "Убрать с главной");
        SemanticProperties.SetDescription(unpin, $"Убрать «{entry.Title}» с главной");
        unpin.Clicked += (_, _) =>
        {
            SetPinned(entry, false);
            ShowPinnedModules();
        };

        var header = new Grid
        {
            ColumnSpacing = 6,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) }
        };
        header.Add(title);
        header.Add(state, 1);
        header.Add(unpin, 2);
        var body = new VerticalStackLayout
        {
            BindingContext = entry, Spacing = 8, Children = { header, Caption(entry.Address), MakeModuleError() }
        };
        if (entry == wheelModule)
            body.Children.Add(wheelSlot = new ContentView { Content = wheelBody });
        else if (entry == pedalsModule)
            body.Children.Add(pedalsSlot = new ContentView { Content = pedalsBody });
        return Card(body);
    }

    private static void SetPinned(ModuleEntry entry, bool value)
    {
        entry.SetPinned(value);
        Preferences.Default.Set(entry.PinnedKey, value);
    }
}
