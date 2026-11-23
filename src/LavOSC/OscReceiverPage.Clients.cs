using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
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
    private readonly Label trafficCounts = Caption();
    private readonly Label trafficHistory = Caption();
    private long syncedClientVersion = -1;
    private Grid clientsView = null!;
    private CollectionView clientsList = null!;
    private Action<OscClientEntry> openClient = null!;
    private IDispatcherTimer clientTimer = null!;

    private Grid MakeClientsView()
    {
        clientsList = MakeClientsList(compact: false);
        var statistics = Card(new VerticalStackLayout
        {
            Spacing = 8,
            Children = { Heading("Статистика за сеанс", 16), trafficCounts, Heading("Входящие OSC-сообщения", 14), trafficHistory }
        });
        statistics.IsVisible = false;
        clientsList.SelectionChanged += (_, _) =>
        {
            statistics.IsVisible = clientsList.SelectedItem is OscClientEntry;
            RefreshClientTraffic();
        };
        var left = new Grid
        {
            RowSpacing = 12,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
        };
        left.Add(Heading("Клиенты", 27));
        left.Add(clientsList, 0, 1);
        var view = MakeColumns();
        view.Add(left);
        view.Add(new ScrollView
        {
            Content = new VerticalStackLayout { Spacing = 12, Children = { MakeClientAccessSettings(), statistics } }
        }, 1);
        return view;
    }

    private View MakeDashboardClients()
    {
        var panel = new Grid
        {
            RowSpacing = 10,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
        };
        panel.Add(Heading("Клиенты", 18));
        panel.Add(MakeClientsList(compact: true), 0, 1);
        return Card(panel);
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
        var blockNew = new Switch { OnColor = Accent, IsToggled = current.BlockNew };
        SemanticProperties.SetDescription(blockNew, "Добавлять новые IP в чёрный список");
        blacklist.CheckedChanged += (_, args) =>
        {
            if (args.Value)
                SetClientMode(ClientAccessMode.Blacklist, blockNew.IsToggled);
        };
        whitelist.CheckedChanged += (_, args) =>
        {
            if (args.Value)
                SetClientMode(ClientAccessMode.Whitelist, blockNew.IsToggled);
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
            Spacing = 8,
            Children =
            {
                Heading("Доступ к OSC", 16), blacklist, whitelist,
                Row(Caption("Добавлять новые IP в чёрный список"), blockNew),
                Row(address, add), warning
            }
        });
    }

    private CollectionView MakeClientsList(bool compact) => new()
    {
        ItemsSource = clients,
        SelectionMode = compact ? SelectionMode.None : SelectionMode.Single,
        ItemSizingStrategy = ItemSizingStrategy.MeasureFirstItem,
        EmptyView = Caption("Клиенты OSC не обнаружены."),
        ItemTemplate = new DataTemplate(() =>
        {
            var address = Heading("", 16);
            address.SetBinding(Label.TextProperty, nameof(OscClientEntry.Address));
            var state = Caption();
            state.SetBinding(Label.TextProperty, nameof(OscClientEntry.State));
            var info = new VerticalStackLayout { Spacing = 2, Children = { address, state } };
            var access = MakeButton("");
            access.SetBinding(Button.TextProperty, nameof(OscClientEntry.Action));
            access.Clicked += (_, _) =>
            {
                if (access.BindingContext is OscClientEntry client && clientAccess.SetAllowed(client.Address, !client.Allowed))
                    ApplyClientAccess();
            };

            if (compact)
            {
                var doubleClick = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
                doubleClick.Tapped += (_, _) =>
                {
                    if (info.BindingContext is OscClientEntry client)
                        openClient(client);
                };
                info.GestureRecognizers.Add(doubleClick);
            }
            var row = Row(info, access);
            row.Padding = new Thickness(8, 6);
            row.BackgroundColor = Panel;
            row.Margin = new Thickness(0, 0, 0, 2);
            ApplyListItemStates(row);
            return row;
        })
    };

    private void SetClientMode(ClientAccessMode mode, bool blockNew)
    {
        clientAccess.Configure(mode, blockNew);
        ApplyClientAccess();
    }

    private void ApplyClientAccess()
    {
        ResetBlockedSources();
        SyncClients();
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
            bool allowed = state.Mode == ClientAccessMode.Whitelist ? rule.Allowed : !rule.Blocked;
            if (clientsByAddress.TryGetValue(rule.Address, out var existing))
            {
                existing.SetAllowed(allowed);
            }
            else
            {
                var client = new OscClientEntry(rule.Address, allowed);
                clientsByAddress.Add(rule.Address, client);
                clients.Add(client);
            }
        }

        Preferences.Default.Set(ClientsKey, JsonSerializer.Serialize(state));
        syncedClientVersion = version;
    }

    private void StartClientMonitoring()
    {
        clientTimer = Dispatcher.CreateTimer();
        clientTimer.Interval = TimeSpan.FromMilliseconds(500);
        clientTimer.Tick += (_, _) =>
        {
            SyncClients();
            RefreshClientTraffic();
        };
        clientTimer.Start();
    }

    private void RefreshClientTraffic()
    {
        if (clientsList.SelectedItem is not OscClientEntry client || activeSection != clientsView)
            return;

        var snapshot = clientTraffic.Snapshot(client.Address);
        trafficCounts.Text = $"Получено OSC · {snapshot.Received:N0}";
        trafficHistory.Text = snapshot.Recent.Length == 0 ? "Сообщений пока нет." : string.Join("\n\n",
            snapshot.Recent.Reverse().Select(entry =>
                $"{entry.At:HH:mm:ss.fff} · {client.Address}:{entry.RemotePort} → LavOSC:{entry.LocalPort}\n{entry.Address} {entry.Arguments}"));
    }
}
