using Laveyana.LavOSC.Core;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace Laveyana.LavOSC;

internal sealed partial class OscReceiverPage
{
    private readonly List<ModuleEntry> moduleEntries = [];
    private readonly VerticalStackLayout pinnedControls = new() { Spacing = 12 };
    private readonly VerticalStackLayout pinnedDiagnostics = new() { Spacing = 12 };
    private readonly VerticalStackLayout moduleDetails = new() { Spacing = 12 };
    private readonly Button dashboardTab = MakeButton("Главная");
    private readonly Button catalogTab = MakeButton("Модули");
    private readonly SearchBar moduleSearch = new() { Placeholder = "Название или OSC-адрес", TextColor = Text, PlaceholderColor = Muted };
    private readonly Switch pinnedOnly = new() { OnColor = Accent };
    private readonly CollectionView moduleList = new() { SelectionMode = SelectionMode.Single };
    private ModuleEntry wheelModule = null!;
    private ModuleEntry pedalsModule = null!;
    private Grid dashboard = null!;
    private Grid catalog = null!;
    private Grid? activeSection;
    private (View Controls, View Diagnostics) wheelPanel;
    private (View Controls, View Diagnostics) pedalsPanel;
    private bool updatingModuleList;

    private View MakeWorkspace()
    {
        wheelPanel = (MakeModuleControls(wheelModule,
            Row(calibrate, drive),
            SliderField("Угол от центра до упора", lockDegrees, "°"),
            SliderField("Мёртвая зона", deadZone, "°"),
            SliderField("Сглаживание", smoothing, " мс"),
            Row(Caption("Инверсия руля"), invert)),
            Card(new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    Heading("Ось руля", 18), Row(angle, axisValue), gaugeView,
                    Row(Caption("Влево"), Caption("Вправо")), sensorValues, sampleStatus, notice
                }
            }));
        pedalsPanel = (MakeModuleControls(pedalsModule, pedalDrive), MakePedalDiagnostics());

        dashboard = MakeColumns();
        dashboard.Add(new ScrollView { Content = MakeControls() });
        dashboard.Add(new ScrollView { Content = MakeDiagnostics() }, 1);
        catalog = MakeCatalog();
        clientsView = MakeClientsView();

        var navigation = new HorizontalStackLayout { Spacing = 8, Children = { dashboardTab, catalogTab, clientsTab } };
        dashboardTab.Clicked += (_, _) => ShowSection(dashboard);
        catalogTab.Clicked += (_, _) => ShowSection(catalog);
        clientsTab.Clicked += (_, _) => ShowSection(clientsView);
        var layout = new Grid
        {
            Padding = new Thickness(18),
            RowSpacing = 16,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
        };
        layout.Add(navigation);
        layout.Add(dashboard, 0, 1);
        layout.Add(catalog, 0, 1);
        layout.Add(clientsView, 0, 1);
        ShowSection(dashboard);
        return layout;
    }

    private static Grid MakeColumns() => new()
    {
        ColumnSpacing = 16,
        ColumnDefinitions = { new ColumnDefinition(new GridLength(360)), new ColumnDefinition(GridLength.Star) }
    };

    private Grid MakeCatalog()
    {
        moduleList.ItemTemplate = new DataTemplate(MakeCatalogRow);
        moduleList.ItemSizingStrategy = ItemSizingStrategy.MeasureFirstItem;
        moduleList.EmptyView = Caption("Модули не найдены.");
        moduleList.SelectionChanged += (_, _) =>
        {
            if (!updatingModuleList && catalog?.IsVisible == true)
                ShowModuleDetails();
        };
        moduleSearch.TextChanged += (_, _) => FilterModules();
        pinnedOnly.Toggled += (_, _) => FilterModules();

        var listColumn = new Grid
        {
            RowSpacing = 12,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
        };
        listColumn.Add(new VerticalStackLayout
        {
            Spacing = 8,
            Children = { Heading("Модули", 27), moduleSearch, Row(Caption("Только на главной"), pinnedOnly) }
        });
        listColumn.Add(moduleList, 0, 1);
        var view = MakeColumns();
        view.Add(listColumn);
        view.Add(new ScrollView { Content = moduleDetails }, 1);
        return view;
    }

    private static View MakeCatalogRow()
    {
        var title = Heading("", 16);
        title.LineBreakMode = LineBreakMode.TailTruncation;
        title.SetBinding(Label.TextProperty, nameof(ModuleEntry.Title));
        var address = Caption();
        address.LineBreakMode = LineBreakMode.TailTruncation;
        address.SetBinding(Label.TextProperty, nameof(ModuleEntry.Address));
        var state = Caption();
        state.HorizontalOptions = LayoutOptions.End;
        state.SetBinding(Label.TextProperty, nameof(ModuleEntry.State));
        var pinned = Caption("На главной");
        pinned.TextColor = Accent;
        pinned.HorizontalOptions = LayoutOptions.End;
        pinned.SetBinding(IsVisibleProperty, nameof(ModuleEntry.IsPinned));

        var row = Row(new VerticalStackLayout { Spacing = 2, Children = { title, address } },
            new VerticalStackLayout { Spacing = 2, Children = { state, pinned } });
        row.BackgroundColor = Panel;
        row.Margin = new Thickness(0, 0, 0, 2);
        row.Padding = new Thickness(12, 8);
        row.HeightRequest = 64;
        VisualStateManager.SetVisualStateGroups(row, new VisualStateGroupList
        {
            new VisualStateGroup
            {
                Name = "CommonStates",
                States =
                {
                    new VisualState { Name = "Normal" },
                    new VisualState
                    {
                        Name = "Selected",
                        Setters = { new Setter { Property = BackgroundColorProperty, Value = Outline } }
                    }
                }
            }
        });
        return row;
    }

    private void ShowSection(Grid section)
    {
        if (activeSection == section)
            return;
        activeSection = section;
        dashboard.IsVisible = section == dashboard;
        catalog.IsVisible = section == catalog;
        clientsView.IsVisible = section == clientsView;
        dashboardTab.BackgroundColor = dashboard.IsVisible ? Outline : Panel;
        catalogTab.BackgroundColor = catalog.IsVisible ? Outline : Panel;
        clientsTab.BackgroundColor = clientsView.IsVisible ? Outline : Panel;
        pinnedControls.Children.Clear();
        pinnedDiagnostics.Children.Clear();
        moduleDetails.Children.Clear();
        if (dashboard.IsVisible)
            ShowPinnedModules();
        else if (catalog.IsVisible)
            FilterModules();
    }

    private void FilterModules()
    {
        var selected = moduleList.SelectedItem as ModuleEntry;
        string query = moduleSearch.Text?.Trim() ?? "";
        var filtered = moduleEntries.Where(module => module.Matches(query, pinnedOnly.IsToggled)).ToArray();

        updatingModuleList = true;
        try
        {
            moduleList.ItemsSource = filtered;
            moduleList.SelectedItem = selected is not null && filtered.Contains(selected) ? selected : filtered.FirstOrDefault();
        }
        finally
        {
            updatingModuleList = false;
        }
        ShowModuleDetails();
    }

    private void ShowModuleDetails()
    {
        moduleDetails.Children.Clear();
        if (moduleList.SelectedItem is ModuleEntry entry)
            AddModulePanels(entry, moduleDetails, moduleDetails);
    }

    private void ShowPinnedModules()
    {
        pinnedControls.Children.Clear();
        pinnedDiagnostics.Children.Clear();
        foreach (ModuleEntry entry in moduleEntries)
            if (entry.IsPinned)
                AddModulePanels(entry, pinnedControls, pinnedDiagnostics);

        if (pinnedControls.Children.Count == 0)
            pinnedControls.Children.Add(Caption("Закрепи нужные модули в каталоге."));
    }

    private void AddModulePanels(ModuleEntry entry, VerticalStackLayout controls, VerticalStackLayout diagnostics)
    {
        if (entry == wheelModule || entry == pedalsModule)
        {
            var panel = entry == wheelModule ? wheelPanel : pedalsPanel;
            controls.Children.Add(panel.Controls);
            diagnostics.Children.Add(panel.Diagnostics);
        }
        else
        {
            controls.Children.Add(MakeModuleControls(entry));
        }
    }

    private View MakeModuleControls(ModuleEntry entry, params View[] moduleControls)
    {
        var title = Heading(entry.Title, 18);
        title.LineBreakMode = LineBreakMode.TailTruncation;
        var pin = MakeButton("");
        pin.IsVisible = entry.Available;
        pin.SetBinding(Button.TextProperty, nameof(ModuleEntry.PinAction));
        pin.Clicked += (_, _) =>
        {
            entry.SetPinned(!entry.IsPinned);
            Preferences.Default.Set(entry.PinnedKey, entry.IsPinned);
            if (dashboard.IsVisible)
                ShowPinnedModules();
            else if (pinnedOnly.IsToggled)
                FilterModules();
        };

        var toggle = MakeButton("");
        toggle.IsVisible = entry.Available;
        toggle.SetBinding(Button.TextProperty, nameof(ModuleEntry.EnableAction));
        toggle.Clicked += async (_, _) => await ToggleModuleAsync(entry);
        var status = Caption();
        status.TextColor = Warning;
        status.SetBinding(Label.TextProperty, nameof(ModuleEntry.Error));
        status.SetBinding(IsVisibleProperty, nameof(ModuleEntry.HasError));

        var address = Caption(entry.Address);
        address.LineBreakMode = LineBreakMode.TailTruncation;
        var body = new VerticalStackLayout
        {
            BindingContext = entry, Spacing = 8, Children = { Row(title, pin), Row(address, toggle), status }
        };
        if (entry.SupportsFeedback)
        {
            var feedback = MakeButton("");
            feedback.SetBinding(Button.TextProperty, nameof(ModuleEntry.FeedbackAction));
            feedback.Clicked += (_, _) =>
            {
                entry.SetFeedback(!entry.FeedbackEnabled);
                Preferences.Default.Set(entry.FeedbackKey, entry.FeedbackEnabled);
            };
            body.Children.Add(feedback);
        }
        foreach (View control in moduleControls)
            body.Children.Add(control);
        return Card(body);
    }
}
