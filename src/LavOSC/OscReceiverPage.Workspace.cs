using Laveyana.LavOSC.Core;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace Laveyana.LavOSC;

internal enum WorkspaceSection { Dashboard, Modules, Clients, Settings }

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

    internal static IReadOnlyList<(WorkspaceSection Section, string Title)> WorkspaceTabs { get; } =
        Array.AsReadOnly<(WorkspaceSection, string)>([
            (WorkspaceSection.Dashboard, "Главная"), (WorkspaceSection.Modules, "Модули"),
            (WorkspaceSection.Clients, "Клиенты"), (WorkspaceSection.Settings, "Настройки")
        ]);
    internal event Action<WorkspaceSection>? SectionChanged;
    internal WorkspaceSection CurrentSection { get; private set; }

    private Action<WorkspaceSection, ModuleEntry?> showSection = null!;
    private Grid fallbackNavigation = null!;
    private Grid dashboardContent = null!;
    private ContentView dashboardClientSlot = null!;
    private View[] workspacePages = [];

    internal void Navigate(WorkspaceSection section) => showSection(section, null);

    internal void UseNativeCaption() => fallbackNavigation.IsVisible = false;

    internal void SetCaptionInset(double height)
    {
        var contentMargin = new Thickness(16, height + 10, 16, 12);
        dashboardContent.Margin = contentMargin;
        dashboardClientSlot.Margin = new Thickness(0, height, 0, 0);
        foreach (View page in workspacePages.Skip(1))
            page.Margin = contentMargin;
    }

    private Grid MakeWorkspace()
    {
        wheelBody = new VerticalStackLayout
        {
            Spacing = 12, VerticalOptions = LayoutOptions.Center,
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
        workspacePages = [dashboard, catalog, clientsView, settings];
        var pages = new Grid();
        foreach (View page in workspacePages)
            pages.Add(page);

        var navItems = new HorizontalStackLayout
        {
            Padding = new Thickness(14, 6), Spacing = 8, BackgroundColor = PulseVisuals.Surface,
            Children = { Heading("LavOSC", 16) }
        };
        var buttons = new Dictionary<WorkspaceSection, Button>();
        foreach (var (section, title) in WorkspaceTabs)
        {
            var button = MakeButton(title);
            button.MinimumHeightRequest = 32;
            button.Clicked += (_, _) => Navigate(section);
            buttons.Add(section, button);
            navItems.Children.Add(button);
        }
        fallbackNavigation = new Grid { Children = { navItems } };

        showSection = Show;
        openCatalogModule = entry => Show(WorkspaceSection.Modules, entry);
        openModules = () => Navigate(WorkspaceSection.Modules);
        openClients = () => Navigate(WorkspaceSection.Clients);
        openClient = client =>
        {
            Navigate(WorkspaceSection.Clients);
            clientsList.SelectedItem = client;
            clientsList.ScrollTo(client, position: ScrollToPosition.MakeVisible, animate: false);
        };

        void Show(WorkspaceSection section, ModuleEntry? selectedModule)
        {
            View page = workspacePages[(int)section];
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
            CurrentSection = section;
            for (int index = 0; index < workspacePages.Length; index++)
                workspacePages[index].IsVisible = index == (int)section;
            foreach (var (id, button) in buttons)
            {
                button.BackgroundColor = id == section ? PulseVisuals.Selected : Colors.Transparent;
                button.TextColor = id == section ? Accent : Text;
            }
            if (section == WorkspaceSection.Modules)
                FilterModules(selectedModule);
            else if (section == WorkspaceSection.Dashboard)
                ShowPinnedModules();
            else if (section == WorkspaceSection.Clients)
                RefreshClientTraffic();
            SectionChanged?.Invoke(section);
        }

        var layout = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
        };
        layout.Add(fallbackNavigation);
        layout.Add(pages, 0, 1);
        SetCaptionInset(0);
        Navigate(WorkspaceSection.Dashboard);
        return layout;
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
        var moduleHeading = Row(Heading("Закреплённые модули", 20), catalogButton);
        var mainBody = new VerticalStackLayout
        {
            Spacing = 12,
            Children = { summaries, moduleHeading, pinnedCards, emptyDashboard, inlineClients }
        };
        var content = new ScrollView { Content = mainBody };
        var clientsPanel = MakeDashboardClients();
        dashboardClientSlot = new ContentView { Content = clientsPanel };
        var sidebar = new Grid { BackgroundColor = PulseVisuals.Surface, Children = { dashboardClientSlot } };
        var right = new ColumnDefinition(280);
        var dashboard = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), right }
        };

        var status = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto)
            }
        };
        status.Add(StatusMetric("Приём OSC", oscOverview, oscIndicator));
        status.Add(StatusMetric("Выход vJoy", vJoyOverview, vJoyIndicator), 1);
        status.Add(listen, 2);
        status.Add(diagnosticsToggle, 3);

        dashboardContent = new Grid
        {
            RowSpacing = 12,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
        };
        dashboardContent.Add(Card(status));
        dashboardContent.Add(content, 0, 1);
        dashboard.Add(dashboardContent);
        dashboard.Add(sidebar, 1);

        bool sideBySide = true;
        void UpdateCardHeight() => pinnedCards.MinimumSingleRowHeight = sideBySide && !summaries.IsVisible
            ? Math.Max(0, content.Height - moduleHeading.Height - mainBody.Spacing)
            : 0;

        content.SizeChanged += (_, _) => UpdateCardHeight();
        moduleHeading.SizeChanged += (_, _) => UpdateCardHeight();
        diagnosticsToggle.Clicked += async (_, _) =>
        {
            summaries.IsVisible = !summaries.IsVisible;
            diagnosticsToggle.Text = summaries.IsVisible ? "Скрыть диагностику" : "IP и диагностика";
            UpdateCardHeight();
            if (summaries.IsVisible && content.ScrollY > 0)
                await content.ScrollToAsync(0, 0, animated: false);
        };
        dashboard.SizeChanged += (_, _) =>
        {
            if (dashboard.Width <= 0)
                return;
            bool next = dashboard.Width >= 900;
            right.Width = next ? Math.Clamp(Math.Floor(dashboard.Width * .27), 260, 330) : 0;
            if (sideBySide == next)
                return;

            sideBySide = next;
            dashboardClientSlot.Content = null;
            inlineClients.Content = null;
            sidebar.IsVisible = next;
            inlineClients.IsVisible = !next;
            if (next)
                dashboardClientSlot.Content = clientsPanel;
            else
                inlineClients.Content = clientsPanel;
            UpdateCardHeight();
        };
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

    private static View Metric(string label, Label value) =>
        new VerticalStackLayout { Spacing = 2, Children = { Caption(label), value } };

    private static BoxView StatusIndicator() => new()
    {
        WidthRequest = 7, HeightRequest = 7, CornerRadius = 4, VerticalOptions = LayoutOptions.Center
    };

    private static View StatusMetric(string title, Label value, BoxView indicator)
    {
        value.LineBreakMode = LineBreakMode.TailTruncation;
        value.MaxLines = 1;
        var metric = new Grid
        {
            ColumnSpacing = 10, VerticalOptions = LayoutOptions.Center,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }
        };
        metric.Add(indicator);
        metric.Add(Metric(title, value), 1);
        return metric;
    }

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
        return new ScrollView { Content = cards };
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
        var body = new Grid
        {
            BindingContext = entry, RowSpacing = 8,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
        };
        body.Add(header);
        body.Add(MakeModuleError(), 0, 1);
        if (entry == wheelModule)
            body.Add(wheelSlot = new ContentView { Content = wheelBody }, 0, 2);
        else if (entry == pedalsModule)
            body.Add(pedalsSlot = new ContentView { Content = pedalsBody }, 0, 2);
        else
            body.Add(Caption(entry.Address), 0, 2);
        return Card(body);
    }

    private static void SetPinned(ModuleEntry entry, bool value)
    {
        entry.SetPinned(value);
        Preferences.Default.Set(entry.PinnedKey, value);
    }
}
