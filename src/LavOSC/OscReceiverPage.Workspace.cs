using Laveyana.LavOSC.Core;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace Laveyana.LavOSC;

internal sealed partial class OscReceiverPage
{
    private readonly DashboardGrid pinnedCards = new(3);
    private readonly Label emptyDashboard = Caption("Закрепи нужные модули в каталоге.");
    private readonly Label oscOverview = Heading("", 16);
    private readonly Label vJoyOverview = Heading("", 16);
    private readonly BoxView oscIndicator = StatusIndicator();
    private readonly BoxView vJoyIndicator = StatusIndicator();
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
    private Action openModules = null!;
    private Action openClients = null!;

    internal void SetCaptionInset(double height) => workspace.Margin = new Thickness(0, height, 0, 0);

    private Grid MakeWorkspace()
    {
        wheelBody = new VerticalStackLayout
        {
            Spacing = 9,
            Children =
            {
                Row(Metric("Угол", angle), Metric("Ось X", axisValue)), gaugeView, sampleStatus, sensorValues, notice,
                calibrate
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
            (Page: (View)dashboard, Button: NavButton("osc_dashboard.png", "Главная"), Title: "Главная"),
            (Page: catalog, Button: NavButton("osc_modules.png", "Модули"), Title: "Модули"),
            (Page: clientsView, Button: NavButton("osc_clients.png", "Клиенты"), Title: "Клиенты"),
            (Page: settings, Button: NavButton("osc_settings.png", "Настройки"), Title: "Настройки")
        };

        var navigation = new Grid
        {
            BackgroundColor = PulseVisuals.Surface,
            Padding = new Thickness(9, 18), RowSpacing = 7,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)
            }
        };
        var pages = new Grid();
        openCatalogModule = entry => Show(catalog, entry);
        openModules = () => Show(catalog);
        openClients = () => Show(clientsView);
        openClient = client =>
        {
            Show(clientsView);
            clientsList.SelectedItem = client;
            clientsList.ScrollTo(client, position: ScrollToPosition.MakeVisible, animate: false);
        };
        for (int index = 0; index < sections.Length; index++)
        {
            var (page, button, _) = sections[index];
            page.Margin = new Thickness(20, 18, 20, 18);
            pages.Add(page);
            navigation.Add(button, 0, index == 3 ? 4 : index);
            button.Clicked += (_, _) => Show(page);
        }

        void Show(View page, ModuleEntry? selectedModule = null)
        {
            if (activeSection == page)
            {
                if (selectedModule is not null)
                    FilterModules(selectedModule);
                return;
            }

            ClearPinnedBodies();
            if (detailsSlot is not null)
                detailsSlot.Content = null;
            detailsSlot = null;
            moduleDetails.Children.Clear();
            oscReferenceScroll.Content = null;
            activeSection = page;
            foreach (var (panel, button, _) in sections)
            {
                panel.IsVisible = panel == page;
                button.BackgroundColor = panel.IsVisible ? PulseVisuals.Selected : Colors.Transparent;
                button.BorderColor = panel.IsVisible ? Accent.WithAlpha(.40f) : Colors.Transparent;
                button.TextColor = panel.IsVisible ? Accent : Text;
            }
            if (page == catalog)
                FilterModules(selectedModule);
            else if (page == dashboard)
                ShowPinnedModules();
            else if (page == clientsView)
                RefreshClientTraffic();
        }

        var navigationWidth = new ColumnDefinition(new GridLength(62));
        var layout = new Grid
        {
            ColumnDefinitions = { navigationWidth, new ColumnDefinition(GridLength.Star) }
        };
        layout.Add(navigation);
        layout.Add(pages, 1);
        bool expanded = false;
        layout.SizeChanged += (_, _) =>
        {
            bool next = layout.Width >= 1180;
            if (next == expanded)
                return;
            expanded = next;
            navigationWidth.Width = next ? 160 : 62;
            foreach (var (_, button, title) in sections)
                button.Text = next ? title : "";
        };
        Show(dashboard);
        return layout;
    }

    private static Button NavButton(string icon, string title)
    {
        var button = MakeButton("");
        button.ImageSource = icon;
        button.ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left, 8);
        button.HeightRequest = 44;
        button.Padding = new Thickness(7, 5);
        button.BackgroundColor = Colors.Transparent;
        button.BorderWidth = 1;
        ToolTipProperties.SetText(button, title);
        SemanticProperties.SetDescription(button, title);
        return button;
    }

    private Grid MakeDashboard()
    {
        var diagnosticsToggle = MakeButton("IP и диагностика");
        var reload = MakeButton("Обновить IP");
        reload.Clicked += (_, _) => ShowAddresses();
        var summaries = new DashboardGrid(2);
        summaries.SetCards([
            Card(new VerticalStackLayout
            {
                Spacing = 7,
                Children =
                {
                    Row(Heading("Приём OSC", 17), reload), networkStatus, localAddresses, discoveryStatus, routingStatus
                }
            }),
            Card(new VerticalStackLayout
            {
                Spacing = 7,
                Children = { Heading("vJoy", 17), vJoyStatus, vJoyDevices, vJoyLibrary }
            })
        ]);
        summaries.IsVisible = false;
        var catalogButton = MakeButton("Каталог");
        catalogButton.Clicked += (_, _) => openModules();
        var inlineClients = new ContentView { HeightRequest = 260, IsVisible = false };
        var mainBody = new VerticalStackLayout
        {
            Spacing = 14,
            Children = { summaries, Row(Heading("Закреплённые модули", 20), catalogButton), pinnedCards, emptyDashboard, inlineClients }
        };
        var content = new ScrollView { Content = mainBody };
        diagnosticsToggle.Clicked += async (_, _) =>
        {
            summaries.IsVisible = !summaries.IsVisible;
            diagnosticsToggle.Text = summaries.IsVisible ? "Скрыть диагностику" : "IP и диагностика";
            if (summaries.IsVisible && content.ScrollY > 0)
                await content.ScrollToAsync(0, 0, animated: false);
        };
        View clientsPanel = MakeDashboardClients();
        var sidebar = new ContentView();
        var right = new ColumnDefinition(new GridLength(280));
        var body = new Grid
        {
            ColumnSpacing = 14,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), right }
        };
        body.Add(content);
        body.Add(sidebar, 1);
        sidebar.Content = clientsPanel;
        bool sideBySide = true;
        body.SizeChanged += (_, _) =>
        {
            if (body.Width <= 0)
                return;
            bool next = body.Width >= 890;
            right.Width = next ? Math.Clamp(Math.Floor(body.Width * .28), 260, 310) : 0;
            if (sideBySide == next)
                return;
            sideBySide = next;
            sidebar.Content = null;
            inlineClients.Content = null;
            sidebar.IsVisible = next;
            inlineClients.IsVisible = !next;
            if (next)
                sidebar.Content = clientsPanel;
            else
                inlineClients.Content = clientsPanel;
        };
        var status = new Grid
        {
            ColumnSpacing = 18,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }
        };
        status.Add(StatusMetric("Приём OSC", oscOverview, oscIndicator));
        status.Add(StatusMetric("Выход vJoy", vJoyOverview, vJoyIndicator), 1);
        status.Add(listen, 2);
        var dashboard = new Grid
        {
            RowSpacing = 16,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
        };
        dashboard.Add(Row(PageHeading("Главная"), diagnosticsToggle));
        dashboard.Add(Card(status), 0, 1);
        dashboard.Add(body, 0, 2);
        return dashboard;
    }

    private void UpdateDashboardStatus(OscReceiverSnapshot? input, bool wheelActive, bool pedalsActive)
    {
        bool listening = input?.Listening == true;
        bool receiverError = error is not null || input?.Error is not null;
        oscOverview.Text = listening ? $"UDP {input!.Port}" : receiverError ? "Ошибка приёма" : "Остановлен";
        oscIndicator.Color = listening ? PulseVisuals.Success : receiverError ? Warning : Muted;
        vJoyOverview.Text = vJoyError is not null ? "Нужна настройка" : (wheelActive, pedalsActive) switch
        {
            (true, true) => "Руль и педали",
            (true, false) => "Руль подключён",
            (false, true) => "Педали подключены",
            _ => working ? "Подключение…" : vJoyScan?.DriverEnabled == true ? "Готов" : "Ожидание"
        };
        vJoyIndicator.Color = vJoyError is not null ? Warning : wheelActive || pedalsActive ? PulseVisuals.Success : Muted;
        listen.BackgroundColor = listening ? PulseVisuals.Button : Accent;
        listen.TextColor = listening ? Text : Canvas;
    }

    private static Label PageHeading(string text)
    {
        var heading = Heading(text, 32);
        heading.VerticalOptions = LayoutOptions.Center;
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level1);
        return heading;
    }

    private static View Metric(string label, Label value) =>
        new VerticalStackLayout { Spacing = 2, Children = { Caption(label), value } };

    private static BoxView StatusIndicator() => new()
    {
        WidthRequest = 7, HeightRequest = 7, CornerRadius = 4, VerticalOptions = LayoutOptions.Center
    };

    private static View StatusMetric(string title, Label value, BoxView indicator) =>
        new HorizontalStackLayout
        {
            Spacing = 10, VerticalOptions = LayoutOptions.Center, Children = { indicator, Metric(title, value) }
        };

    private ScrollView MakeSettings()
    {
        settingsListen.Clicked += (_, _) => ToggleListening();
        var cards = new DashboardGrid(2);
        cards.SetCards([
            Card(new VerticalStackLayout
            {
                Spacing = 12,
                Children = { Heading("Приём OSC", 19), Field("UDP-порт", port), settingsListen, settingsNetworkStatus }
            }),
            Card(new VerticalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    Heading("vJoy", 19), Row(Caption("Автоматически подготавливать vJoy"), autoSetup),
                    Field("Устройство vJoy", device), Field("Путь к vJoyInterface.dll", dllPath), prepare
                }
            })
        ]);
        return new ScrollView { Content = new VerticalStackLayout { Spacing = 18, Children = { PageHeading("Настройки"), cards } } };
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
        title.FontSize = 17;
        title.FontAttributes = FontAttributes.Bold;
        title.TextColor = Text;
        title.BackgroundColor = Colors.Transparent;
        title.Padding = new Thickness(0);
        title.HorizontalOptions = LayoutOptions.Start;
        title.MinimumWidthRequest = 0;
        title.MinimumHeightRequest = 32;
        title.HeightRequest = 32;
        title.LineBreakMode = LineBreakMode.TailTruncation;
        title.VerticalOptions = LayoutOptions.Center;
        title.Clicked += (_, _) => openCatalogModule(entry);
        SemanticProperties.SetDescription(title, $"Открыть модуль «{entry.Title}»");
        var state = MakeButton("");
        state.SetBinding(Button.TextProperty, nameof(ModuleEntry.State));
        state.SetBinding(IsEnabledProperty, nameof(ModuleEntry.Available));
        state.TextColor = Muted;
        state.BackgroundColor = PulseVisuals.Recessed;
        state.MinimumHeightRequest = 28;
        state.Padding = new Thickness(8, 3);
        state.Triggers.Add(new DataTrigger(typeof(Button))
        {
            Binding = new Binding(nameof(ModuleEntry.IsEnabled)), Value = true,
            Setters =
            {
                new Setter { Property = Button.TextColorProperty, Value = PulseVisuals.Success },
                new Setter { Property = BackgroundColorProperty, Value = PulseVisuals.Selected }
            }
        });
        state.Triggers.Add(new DataTrigger(typeof(Button))
        {
            Binding = new Binding(nameof(ModuleEntry.HasError)), Value = true,
            Setters = { new Setter { Property = Button.TextColorProperty, Value = Warning } }
        });
        state.Clicked += (_, _) => ToggleModule(entry);
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
            BindingContext = entry, Spacing = 12, Children = { header, MakeModuleError() }
        };
        if (entry == wheelModule)
            body.Children.Add(wheelSlot = new ContentView { Content = wheelBody });
        else if (entry == pedalsModule)
            body.Children.Add(pedalsSlot = new ContentView { Content = pedalsBody });
        else
            body.Children.Add(Caption(entry.Address));
        return Card(body);
    }

    private static void SetPinned(ModuleEntry entry, bool value)
    {
        entry.SetPinned(value);
        Preferences.Default.Set(entry.PinnedKey, value);
    }
}
