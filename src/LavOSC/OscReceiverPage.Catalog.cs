using System.Runtime.InteropServices;
using Laveyana.LavOSC.Core;
using Laveyana.LavOSC.Modules.Pedals;
using Laveyana.LavOSC.Modules.Wheel;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace Laveyana.LavOSC;

internal sealed partial class OscReceiverPage
{
    private readonly List<ModuleEntry> moduleEntries = [];
    private readonly VerticalStackLayout moduleDetails = new() { Spacing = 12 };
    private readonly ScrollView moduleDetailsScroll = new();
    private readonly ScrollView oscReferenceScroll = new();
    private readonly SearchBar moduleSearch = new()
    {
        Placeholder = "Название или OSC-адрес", TextColor = Text, PlaceholderColor = Muted,
        FontFamily = PulseVisuals.Font, FontSize = 13
    };
    private readonly Switch pinnedOnly = new() { OnColor = Accent };
    private readonly CollectionView moduleList = new() { SelectionMode = SelectionMode.Single };
    private ModuleEntry wheelModule = null!;
    private ModuleEntry pedalsModule = null!;
    private Grid catalog = null!;
    private bool updatingModuleList;

    private static Grid MakeColumns()
    {
        var left = new ColumnDefinition(new GridLength(320));
        var columns = new Grid
        {
            ColumnSpacing = 16,
            ColumnDefinitions = { left, new ColumnDefinition(GridLength.Star) }
        };
        columns.SizeChanged += (_, _) =>
        {
            if (columns.Width > 0)
                left.Width = new GridLength(Math.Min(340, Math.Floor(columns.Width * .44)));
        };
        return columns;
    }

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
            RowSpacing = 9,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
        };
        listColumn.Add(new VerticalStackLayout
        {
            Spacing = 7,
            Children = { moduleSearch, Row(Caption("Только закреплённые"), pinnedOnly) }
        });
        listColumn.Add(moduleList, 0, 1);
        var listWidth = new ColumnDefinition(264);
        var referenceWidth = new ColumnDefinition(314);
        var lowerRow = new RowDefinition(0);
        var panes = new Grid
        {
            ColumnSpacing = 14,
            ColumnDefinitions = { listWidth, new ColumnDefinition(GridLength.Star), referenceWidth },
            RowDefinitions = { new RowDefinition(GridLength.Star), lowerRow }
        };
        var listPanel = Card(listColumn);
        listPanel.BackgroundColor = PulseVisuals.Surface;
        listPanel.Padding = new Thickness(10);
        panes.Add(listPanel);
        panes.Add(moduleDetailsScroll, 1);
        panes.Add(oscReferenceScroll, 2);
        bool wide = true;
        panes.SizeChanged += (_, _) =>
        {
            if (panes.Width <= 0)
                return;
            bool next = panes.Width >= 850;
            listWidth.Width = next ? Math.Clamp(Math.Floor(panes.Width * .27), 238, 290) : 236;
            referenceWidth.Width = next ? Math.Clamp(Math.Floor(panes.Width * .32), 270, 338) : 0;
            if (wide == next)
                return;
            wide = next;
            panes.ColumnSpacing = 14;
            panes.RowSpacing = next ? 0 : 12;
            lowerRow.Height = next ? new GridLength(0) : GridLength.Star;
            Grid.SetRowSpan(listPanel, next ? 1 : 2);
            Grid.SetColumn(oscReferenceScroll, next ? 2 : 1);
            Grid.SetRow(oscReferenceScroll, next ? 0 : 1);
        };
        moduleDetailsScroll.Content = moduleDetails;
        var view = new Grid
        {
            RowSpacing = 16,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
        };
        view.Add(PageHeading("Модули"));
        view.Add(panes, 0, 1);
        return view;
    }

    private View MakeCatalogRow()
    {
        var title = Heading("", 15);
        title.LineBreakMode = LineBreakMode.TailTruncation;
        title.SetBinding(Label.TextProperty, nameof(ModuleEntry.Title));
        var address = Caption();
        address.FontFamily = "Consolas";
        address.LineBreakMode = LineBreakMode.TailTruncation;
        address.SetBinding(Label.TextProperty, nameof(ModuleEntry.Address));
        var state = new Switch { OnColor = Accent, VerticalOptions = LayoutOptions.Center };
        state.SetBinding(IsEnabledProperty, nameof(ModuleEntry.Available));
        state.SetBinding(Switch.IsToggledProperty, nameof(ModuleEntry.IsEnabled), BindingMode.OneWay);
        SemanticProperties.SetDescription(state, "Включить или отключить модуль");
        state.Toggled += (_, args) =>
        {
            if (state.BindingContext is ModuleEntry entry && entry.IsEnabled != args.Value)
            {
                ToggleModule(entry);
                if (state.IsToggled != entry.IsEnabled)
                    state.IsToggled = entry.IsEnabled;
            }
        };
        var pinned = MakeButton("");
        pinned.FontSize = 17;
        pinned.WidthRequest = 32;
        pinned.MinimumHeightRequest = 32;
        pinned.Padding = 0;
        pinned.SetBinding(IsEnabledProperty, nameof(ModuleEntry.Available));
        pinned.SetBinding(Button.TextProperty, nameof(ModuleEntry.PinGlyph));
        pinned.SetBinding(ToolTipProperties.TextProperty, nameof(ModuleEntry.PinAction));
        pinned.SetBinding(SemanticProperties.DescriptionProperty, nameof(ModuleEntry.PinAction));
        pinned.Triggers.Add(new DataTrigger(typeof(Button))
        {
            Binding = new Binding(nameof(ModuleEntry.IsPinned)), Value = true,
            Setters =
            {
                new Setter { Property = Button.TextColorProperty, Value = Accent },
                new Setter { Property = BackgroundColorProperty, Value = PulseVisuals.Selected }
            }
        });
        pinned.Clicked += (_, _) =>
        {
            if (pinned.BindingContext is ModuleEntry entry && entry.Available)
            {
                SetPinned(entry, !entry.IsPinned);
                if (pinnedOnly.IsToggled)
                    FilterModules();
            }
        };

        var row = Row(new VerticalStackLayout { Spacing = 2, Children = { title, address } },
            new HorizontalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Center, Children = { state, pinned } });
        row.ColumnSpacing = 6;
        row.BackgroundColor = Panel;
        row.Margin = new Thickness(0, 0, 0, 4);
        row.Padding = new Thickness(8, 6);
        row.HeightRequest = 60;
        ApplyListItemStates(row);
        return row;
    }

    private static void ApplyListItemStates(View row) =>
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
                        Name = "PointerOver",
                        Setters = { new Setter { Property = BackgroundColorProperty, Value = PulseVisuals.Button } }
                    },
                    new VisualState
                    {
                        Name = "Selected",
                        Setters = { new Setter { Property = BackgroundColorProperty, Value = PulseVisuals.Selected } }
                    }
                }
            }
        });
    private void FilterModules(ModuleEntry? target = null)
    {
        if (catalog?.IsVisible != true || updatingModuleList)
            return;

        var selected = target ?? moduleList.SelectedItem as ModuleEntry;

        updatingModuleList = true;
        try
        {
            string query = moduleSearch.Text?.Trim() ?? "";
            if (target is not null && !MatchesModule(target, query, false))
                moduleSearch.Text = query = "";
            if (target is { IsPinned: false })
                pinnedOnly.IsToggled = false;

            var filtered = moduleEntries.Where(module => MatchesModule(module, query, pinnedOnly.IsToggled)).ToArray();
            moduleList.ItemsSource = filtered;
            moduleList.SelectedItem = selected is not null && filtered.Contains(selected) ? selected : filtered.FirstOrDefault();
        }
        finally
        {
            updatingModuleList = false;
        }
        ShowModuleDetails();
        if (target is not null && ReferenceEquals(moduleList.SelectedItem, target))
            moduleList.ScrollTo(target, position: ScrollToPosition.MakeVisible, animate: false);
    }

    private bool MatchesModule(ModuleEntry entry, string query, bool onlyPinned)
    {
        if (entry.Matches(query, onlyPinned))
            return true;
        if (onlyPinned && !entry.IsPinned)
            return false;

        try
        {
            return GetModuleInputs(entry).Any(endpoint => endpoint.Address.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (entry == wheelModule && "/whell/calibrate".Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private IEnumerable<OscEndpoint> GetModuleInputs(ModuleEntry entry) =>
        (entry == wheelModule ? WheelSession.OscEndpoints
            : entry == pedalsModule ? PedalSession.OscEndpoints
            : (entry.Extension as IOscEndpointDocumentation)?.Endpoints ?? [])
        .Where(endpoint => endpoint.Direction == OscEndpointDirection.Receive);

    private void ShowModuleDetails()
    {
        if (detailsSlot is not null)
            detailsSlot.Content = null;
        detailsSlot = null;
        moduleDetails.Children.Clear();
        oscReferenceScroll.Content = null;
        if (moduleList.SelectedItem is not ModuleEntry entry)
            return;

        moduleDetails.Children.Add(MakeModuleSummary(entry));
        oscReferenceScroll.Content = MakeOscReference(entry);
        if (entry == wheelModule)
        {
            moduleDetails.Children.Add(Card(detailsSlot = new ContentView { Content = wheelBody }));
            moduleDetails.Children.Add(wheelSettings);
        }
        else if (entry == pedalsModule)
        {
            moduleDetails.Children.Add(Card(detailsSlot = new ContentView { Content = pedalsBody }));
        }
        if (moduleDetailsScroll.ScrollY > 0)
            _ = moduleDetailsScroll.ScrollToAsync(0, 0, animated: false);
        if (oscReferenceScroll.ScrollY > 0)
            _ = oscReferenceScroll.ScrollToAsync(0, 0, animated: false);
    }

    private View MakeOscReference(ModuleEntry entry)
    {
        var reference = new VerticalStackLayout { Spacing = 12, Children = { Heading("OSC-адреса", 19) } };
        OscEndpoint[] inputs;
        try
        {
            inputs = GetModuleInputs(entry).ToArray();
        }
        catch (Exception error)
        {
            reference.Children.Add(Caption($"Описание OSC-адресов недоступно. {error.Message}"));
            return Card(reference);
        }

        if (inputs.Length == 0)
            reference.Children.Add(Caption("Модуль не предоставил описание OSC-адресов."));

        foreach (OscEndpoint endpoint in inputs)
        {
            var details = new VerticalStackLayout { Spacing = 4, Children = { MakeOscAddressRow(endpoint.Address) } };
            if (entry == wheelModule && endpoint.Address.StartsWith(entry.Address + "/", StringComparison.Ordinal))
            {
                details.Children.Add(Caption("Псевдоним"));
                details.Children.Add(MakeOscAddressRow($"/{WheelAlias}{endpoint.Address[entry.Address.Length..]}"));
            }
            details.Children.Add(Caption(endpoint.Arguments));
            if (endpoint.Example is { } example)
            {
                var sample = Caption(example);
                sample.FontFamily = "Consolas";
                sample.TextColor = Accent;
                details.Children.Add(sample);
            }
            if (endpoint.Notes is { } notes)
                details.Children.Add(Caption(notes));
            var block = Card(details);
            block.BackgroundColor = PulseVisuals.Recessed;
            block.Padding = new Thickness(10);
            reference.Children.Add(block);
        }
        return Card(reference);
    }

    private static View MakeOscAddressRow(string address)
    {
        var field = MakeEntry(Keyboard.Default);
        field.Text = address;
        field.IsReadOnly = true;
        field.FontFamily = "Consolas";
        field.FontSize = 13;
        field.BackgroundColor = PulseVisuals.Surface;
        SemanticProperties.SetDescription(field, "OSC-адрес");

        var copy = MakeButton("⧉");
        copy.FontFamily = "Segoe UI Symbol";
        copy.FontSize = 16;
        copy.WidthRequest = 36;
        copy.Padding = 0;
        ToolTipProperties.SetText(copy, "Копировать адрес");
        SemanticProperties.SetDescription(copy, $"Копировать OSC-адрес {address}");
        copy.Clicked += async (_, _) =>
        {
            try
            {
                await Clipboard.Default.SetTextAsync(address);
                copy.Text = "✓";
                ToolTipProperties.SetText(copy, "Скопировано");
            }
            catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
            {
                copy.Text = "!";
                ToolTipProperties.SetText(copy, "Ошибка копирования");
            }
        };
        var row = Row(field, copy);
        row.ColumnSpacing = 6;
        return row;
    }

    private static View MakeModuleSummary(ModuleEntry entry)
    {
        var title = Heading(entry.Title, 22);
        title.LineBreakMode = LineBreakMode.TailTruncation;
        var state = Caption();
        state.SetBinding(Label.TextProperty, nameof(ModuleEntry.State));
        var body = new VerticalStackLayout
        {
            BindingContext = entry, Spacing = 8, Children = { title, state, MakeModuleError() }
        };
        return Card(body);
    }

    private static Label MakeModuleError()
    {
        var status = Caption();
        status.TextColor = Warning;
        status.SetBinding(Label.TextProperty, nameof(ModuleEntry.Error));
        status.SetBinding(IsVisibleProperty, nameof(ModuleEntry.HasError));
        return status;
    }
}
