using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json;
using Laveyana.LavOSC.Core;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Layouts;
using Microsoft.Maui.Storage;

namespace Laveyana.LavOSC;

internal sealed partial class OscReceiverPage
{
    private const string ClientsKey = "OscClientAccess";
    private const string LegacyClientsKey = "OscFeedbackClients";
    private readonly ClientAccessPolicy clientAccess = new();
    private readonly ObservableCollection<OscClientEntry> clients = [];
    private readonly Dictionary<string, OscClientEntry> clientsByAddress = new(StringComparer.Ordinal);
    private readonly ClientTrafficLog clientTraffic = new();
    private readonly Label activeClient = Heading("", 19);
    private readonly Label trafficCounts = Caption();
    private readonly Label trafficHistory = Caption();
    private Label? feedbackStatus;
    private FeedbackDelivery? feedbackDelivery;
    private long syncedClientVersion = -1;
    private Grid clientsView = null!;
    private CollectionView clientsList = null!;
    private Action<OscClientEntry> openClient = null!;
    private IDispatcherTimer clientTimer = null!;

    private Grid MakeClientsView()
    {
        clientsList = MakeClientsList(compact: false);
        var selectionPrompt = Card(Caption("Выбери клиента, чтобы настроить отправку OSC."));
        var feedback = new ContentView { Content = selectionPrompt };
        trafficHistory.FontFamily = "Consolas";
        trafficHistory.FontSize = 13;
        var statistics = Card(new VerticalStackLayout
        {
            Spacing = 12,
            Children = { Row(activeClient, trafficCounts), Heading("Журнал OSC", 15), trafficHistory }
        });
        statistics.IsVisible = false;
        clientsList.SelectionChanged += (_, _) =>
        {
            var client = clientsList.SelectedItem as OscClientEntry;
            feedbackStatus = null;
            feedback.Content = client is null ? selectionPrompt : MakeClientFeedback(client);
            statistics.IsVisible = client is not null;
            RefreshClientTraffic();
        };
        var panes = MakeColumns();
        var listPanel = Card(clientsList);
        listPanel.BackgroundColor = PulseVisuals.Surface;
        listPanel.Padding = new Thickness(10);
        panes.Add(listPanel);
        panes.Add(new ScrollView
        {
            Content = new VerticalStackLayout { Spacing = 12, Children = { feedback, MakeClientAccessSettings(), statistics } }
        }, 1);
        return panes;
    }

    private View MakeClientFeedback(OscClientEntry client)
    {
        var toggle = new Switch { OnColor = Accent };
        toggle.SetBinding(Switch.IsToggledProperty, nameof(OscClientEntry.FeedbackEnabled), BindingMode.OneWay);
        SemanticProperties.SetDescription(toggle, "Отправлять OSC этому клиенту");
        toggle.Toggled += (_, args) =>
        {
            if (client.FeedbackEnabled == args.Value)
                return;
            clientAccess.SetFeedback(client.Address, client.FeedbackPort, args.Value);
            SyncClients();
        };

        var port = MakeEntry(Keyboard.Numeric);
        port.SetBinding(Entry.TextProperty, new Binding(nameof(OscClientEntry.FeedbackPort), BindingMode.OneWay, stringFormat: "{0}"));
        var result = Caption();
        result.IsVisible = false;
        var save = MakeButton("Сохранить порт");
        save.Clicked += (_, _) =>
        {
            if (!TryNumber(port.Text, 1, 65535, out int value))
            {
                result.Text = "Укажи порт от 1 до 65535.";
                result.TextColor = Warning;
            }
            else
            {
                clientAccess.SetFeedback(client.Address, value, client.FeedbackEnabled);
                SyncClients();
                result.Text = "Порт сохранён.";
                result.TextColor = PulseVisuals.Success;
            }
            result.IsVisible = true;
        };
        port.TextChanged += (_, _) => result.IsVisible = false;
        var state = Caption();
        state.SetBinding(Label.TextProperty, nameof(OscClientEntry.FeedbackState));
        var delivery = feedbackStatus = Caption();
        RefreshFeedbackStatus();
        return Card(new VerticalStackLayout
        {
            BindingContext = client, Spacing = 9,
            Children =
            {
                Heading($"Отправка OSC · {client.Address}", 18),
                Row(Caption("Отправлять данные модулей"), toggle),
                Row(Field("UDP-порт приёма клиента", port), save),
                result, state, delivery
            }
        });
    }

    private View MakeDashboardClients()
    {
        var showAll = MakeButton("Все клиенты");
        showAll.Clicked += (_, _) => openClients();
        var panel = new Grid
        {
            RowSpacing = 10, Padding = new Thickness(16, 12, 16, 16), BackgroundColor = PulseVisuals.Surface,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
        };
        panel.Add(Row(Heading("Клиенты", 18), showAll));
        panel.Add(MakeClientsList(compact: true), 0, 1);
        return panel;
    }

    private View MakeClientAccessSettings()
    {
        var current = clientAccess.Snapshot();
        var blacklist = new RadioButton
        {
            Content = "Чёрный список", GroupName = "OscClientAccessMode", TextColor = Text,
            IsChecked = current.Mode == ClientAccessMode.Blacklist
        };
        var whitelist = new RadioButton
        {
            Content = "Белый список", GroupName = "OscClientAccessMode", TextColor = Text,
            IsChecked = current.Mode == ClientAccessMode.Whitelist
        };
        blacklist.Margin = new Thickness(0, 0, 12, 0);
        var modes = new FlexLayout
        {
            Direction = FlexDirection.Row, Wrap = FlexWrap.Wrap, AlignItems = FlexAlignItems.Center,
            Children = { blacklist, whitelist }
        };
        var blockNew = new Switch { OnColor = Accent, IsToggled = current.BlockNew };
        var explanation = Caption(DescribeClientMode(current.Mode));
        SemanticProperties.SetDescription(blockNew, "Добавлять новые IP в чёрный список");
        blacklist.CheckedChanged += (_, args) =>
        {
            if (args.Value)
            {
                SetClientMode(ClientAccessMode.Blacklist, blockNew.IsToggled);
                explanation.Text = DescribeClientMode(ClientAccessMode.Blacklist);
            }
        };
        whitelist.CheckedChanged += (_, args) =>
        {
            if (args.Value)
            {
                SetClientMode(ClientAccessMode.Whitelist, blockNew.IsToggled);
                explanation.Text = DescribeClientMode(ClientAccessMode.Whitelist);
            }
        };
        blockNew.Toggled += (_, args) =>
            SetClientMode(clientAccess.Snapshot().Mode, args.Value);

        var address = MakeEntry(Keyboard.Default);
        address.Placeholder = "IPv4";
        SemanticProperties.SetDescription(address, "IPv4 клиента");
        var add = MakeButton("Добавить IP");
        var warning = Caption();
        warning.TextColor = Warning;
        warning.IsVisible = false;
        add.Clicked += (_, _) =>
        {
            // Adding an explicit IP assigns it to the currently selected list.
            var mode = clientAccess.Snapshot().Mode;
            warning.IsVisible = !clientAccess.SetAllowed(address.Text?.Trim() ?? "", mode == ClientAccessMode.Whitelist);
            if (warning.IsVisible)
            {
                warning.Text = "Введите IPv4-адрес.";
                return;
            }
            ApplyClientAccess();
            address.Text = "";
        };

        return Card(new VerticalStackLayout
        {
            Spacing = 12,
            Children =
            {
                Heading("Доступ к OSC", 19),
                modes,
                explanation, Row(Caption("Добавлять новые IP в чёрный список"), blockNew),
                Row(address, add), warning
            }
        });
    }

    private static string DescribeClientMode(ClientAccessMode mode) => mode == ClientAccessMode.Whitelist
        ? "Команды принимаются только от разрешённых IP."
        : "Команды принимаются от всех IP, кроме заблокированных.";

    private CollectionView MakeClientsList(bool compact) => new()
    {
        ItemsSource = clients,
        SelectionMode = compact ? SelectionMode.None : SelectionMode.Single,
        ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems,
        EmptyView = Caption("Клиенты OSC не обнаружены."),
        ItemTemplate = new DataTemplate(() =>
        {
            var address = MetricValue(15);
            address.SetBinding(Label.TextProperty, nameof(OscClientEntry.Address));
            var state = Caption();
            state.SetBinding(Label.TextProperty, nameof(OscClientEntry.State));
            state.TextColor = PulseVisuals.DangerText;
            state.Triggers.Add(new DataTrigger(typeof(Label))
            {
                Binding = new Binding(nameof(OscClientEntry.Allowed)), Value = true,
                Setters = { new Setter { Property = Label.TextColorProperty, Value = PulseVisuals.Success } }
            });
            var outgoing = Caption();
            outgoing.SetBinding(Label.TextProperty, nameof(OscClientEntry.FeedbackState));
            outgoing.SetBinding(IsVisibleProperty, nameof(OscClientEntry.FeedbackEnabled));
            var info = new VerticalStackLayout { Spacing = 2, Children = { address, state, outgoing } };

            View access;
            if (compact)
            {
                var toggle = new Switch { OnColor = Accent, VerticalOptions = LayoutOptions.Center };
                toggle.SetBinding(Switch.IsToggledProperty, nameof(OscClientEntry.Allowed), BindingMode.OneWay);
                toggle.SetBinding(ToolTipProperties.TextProperty, nameof(OscClientEntry.Action));
                SemanticProperties.SetDescription(toggle, "Доступ клиента к OSC");
                toggle.Toggled += (_, args) =>
                {
                    if (toggle.BindingContext is OscClientEntry client && client.Allowed != args.Value)
                        ChangeClientAccess(client, args.Value);
                };
                var doubleClick = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
                doubleClick.Tapped += (_, _) =>
                {
                    if (info.BindingContext is OscClientEntry client)
                        openClient(client);
                };
                info.GestureRecognizers.Add(doubleClick);
                access = toggle;
            }
            else
            {
                var button = MakeButton("");
                button.SetBinding(Button.TextProperty, nameof(OscClientEntry.Action));
                button.Clicked += (_, _) =>
                {
                    if (button.BindingContext is OscClientEntry client)
                        ChangeClientAccess(client, !client.Allowed);
                };
                access = button;
            }
            var row = Row(info, access);
            row.ColumnSpacing = 6;
            row.Padding = new Thickness(10, 7);
            row.BackgroundColor = Panel;
            row.Margin = new Thickness(0, 0, 0, 2);
            ApplyListItemStates(row);
            return row;
        })
    };

    private void ChangeClientAccess(OscClientEntry client, bool allowed)
    {
        if (clientAccess.SetAllowed(client.Address, allowed))
            ApplyClientAccess();
    }

    private void SetClientMode(ClientAccessMode mode, bool blockNew)
    {
        clientAccess.Configure(mode, blockNew);
        ApplyClientAccess();
    }

    private void ApplyClientAccess()
    {
        SyncClients();
        ResetBlockedSources();
    }

    private void LoadClients()
    {
        ClientAccessSettings? saved = null;
        try
        {
            saved = JsonSerializer.Deserialize<ClientAccessSettings>(Preferences.Default.Get(ClientsKey, "null"));
            if (saved is null)
            {
                var old = JsonSerializer.Deserialize<ClientAccessRule[]>(Preferences.Default.Get(LegacyClientsKey, "[]")) ?? [];
                saved = new(ClientAccessMode.Blacklist, false, old);
            }
        }
        catch (JsonException)
        {
            // Input discovery can rebuild the list.
        }
        clientAccess.Load(saved ?? new(ClientAccessMode.Blacklist, false, []));
        SyncClients();
    }

    private void SyncClients()
    {
        long version = clientAccess.Version;
        if (version == syncedClientVersion)
            return;

        var state = clientAccess.Snapshot();
        foreach (var rule in state.Clients)
        {
            bool allowed = rule.IsAllowed(state.Mode);
            if (clientsByAddress.TryGetValue(rule.Address, out var existing))
            {
                existing.Update(rule, allowed);
            }
            else
            {
                var client = new OscClientEntry(rule, allowed);
                clientsByAddress.Add(rule.Address, client);
                clients.Add(client);
            }
        }

        Preferences.Default.Set(ClientsKey, JsonSerializer.Serialize(state));
        syncedClientVersion = version;
        ConfigureFeedback();
    }

    private void StartClientMonitoring()
    {
        feedbackDelivery = new(clientTraffic.Sent);
        clientTimer = Dispatcher.CreateTimer();
        clientTimer.Interval = TimeSpan.FromMilliseconds(500);
        clientTimer.Tick += (_, _) =>
        {
            SyncClients();
            RefreshFeedbackStatus();
            RefreshClientTraffic();
        };
        clientTimer.Start();
    }

    private void ConfigureFeedback()
    {
        if (closed || feedbackDelivery is null)
            return;

        var recipients = clients.Where(client => client.Allowed && client.FeedbackEnabled)
            .Select(client => new IPEndPoint(IPAddress.Parse(client.Address), client.FeedbackPort)).ToArray();
        var sources = new List<(string, IOscFeedbackSource)>();
        if (receiver?.Snapshot().Listening == true && recipients.Length > 0)
        {
            foreach (ModuleEntry module in moduleEntries.Where(entry => entry.IsEnabled && entry.FeedbackEnabled))
            {
                IOscFeedbackSource? source = module == wheelModule ? session : module == pedalsModule ? pedals
                    : module.Extension as IOscFeedbackSource;
                if (source is not null)
                    sources.Add((module.Name, source));
            }
        }
        feedbackDelivery.Configure(recipients, sources.ToArray());
    }

    private void RefreshFeedbackStatus()
    {
        if (feedbackStatus is null)
            return;
        var status = feedbackDelivery?.Snapshot();
        feedbackStatus.IsVisible = clientsList.SelectedItem is OscClientEntry { Allowed: true, FeedbackEnabled: true } &&
            status is { Recipients: > 0 };
        feedbackStatus.TextColor = status?.Failure is not null ? Warning : Muted;
        feedbackStatus.Text = status?.Failure ?? (status?.HasSources == true
            ? $"Отправлено за сеанс · {status.PacketsSent:N0}"
            : "Ожидание приёма OSC или включённого источника данных.");
    }

    private void RefreshClientTraffic()
    {
        if (clientsList.SelectedItem is not OscClientEntry client || activeSection != clientsView)
            return;

        var snapshot = clientTraffic.Snapshot(client.Address);
        activeClient.Text = client.Address;
        trafficCounts.Text = $"Входящие · {snapshot.Received:N0}   Исходящие · {snapshot.Sent:N0}";
        trafficHistory.Text = snapshot.Recent.Length == 0 ? "Сообщений пока нет." : string.Join("\n\n",
            snapshot.Recent.Reverse().Select(entry =>
                $"{entry.At:HH:mm:ss.fff} · {(entry.Sent ? "Исходящее" : "Входящее")} · UDP {entry.RemotePort}\n{entry.Address} {entry.Arguments}"));
    }
}
