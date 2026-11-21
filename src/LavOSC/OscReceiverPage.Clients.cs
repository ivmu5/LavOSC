using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Laveyana.LavOSC.Core;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;

namespace Laveyana.LavOSC;

internal sealed partial class OscReceiverPage
{
    private const string ClientsKey = "OscFeedbackClients";
    private const int DefaultFeedbackPort = 9000;
    private readonly Label feedbackStatus = Caption();
    private readonly Label dashboardFeedbackStatus = Caption();
    private readonly ObservableCollection<FeedbackClient> feedbackClients = [];
    private readonly HashSet<string> feedbackAddresses = new(StringComparer.Ordinal);
    private readonly ClientTrafficLog clientTraffic = new();
    private readonly Label trafficCounts = Caption();
    private readonly Label trafficHistory = Caption();
    private Grid clientsView = null!;
    private CollectionView? dashboardClientList;
    private CollectionView clientsList = null!;
    private Action<FeedbackClient> openClient = null!;
    private IDispatcherTimer clientTimer = null!;
    private FeedbackDelivery? feedbackDelivery;

    private Grid MakeClientsView()
    {
        var details = new ContentView();
        clientsList = MakeClientsList(details, compact: false);
        var statistics = Card(new VerticalStackLayout
        {
            Spacing = 8,
            Children = { Heading("Статистика за сеанс", 16), trafficCounts, Heading("Последние OSC-сообщения", 14), trafficHistory }
        });
        statistics.IsVisible = false;
        clientsList.SelectionChanged += (_, _) =>
        {
            statistics.IsVisible = clientsList.SelectedItem is FeedbackClient;
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
            Content = new VerticalStackLayout { Spacing = 12, Children = { feedbackStatus, details, statistics } }
        }, 1);
        UpdateFeedbackStatus();
        return view;
    }

    private View MakeDashboardClients()
    {
        var details = new ContentView();
        var list = MakeClientsList(details, compact: true);
        dashboardClientList = list;
        list.SelectedItem = feedbackClients.FirstOrDefault();
        var panel = new Grid
        {
            RowSpacing = 10,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) }
        };
        panel.Add(Heading("Клиенты", 18));
        panel.Add(list, 0, 1);
        panel.Add(new VerticalStackLayout
        {
            Spacing = 8, Children = { details, dashboardFeedbackStatus }
        }, 0, 2);
        return Card(panel);
    }

    private CollectionView MakeClientsList(ContentView details, bool compact)
    {
        var list = new CollectionView
        {
            ItemsSource = feedbackClients,
            SelectionMode = SelectionMode.Single,
            ItemSizingStrategy = ItemSizingStrategy.MeasureFirstItem,
            EmptyView = Caption("Клиенты OSC не обнаружены."),
            ItemTemplate = new DataTemplate(() =>
            {
                var address = Heading("", 16);
                address.SetBinding(Label.TextProperty, nameof(FeedbackClient.Address));
                var state = Caption();
                state.SetBinding(Label.TextProperty, compact ? nameof(FeedbackClient.PortLabel) : nameof(FeedbackClient.State));
                var info = new VerticalStackLayout { Spacing = 2, Children = { address, state } };
                Layout row = info;
                if (compact)
                {
                    var toggle = new Switch { OnColor = Accent, VerticalOptions = LayoutOptions.Center };
                    toggle.SetBinding(Switch.IsToggledProperty, nameof(FeedbackClient.Enabled), BindingMode.OneWay);
                    SemanticProperties.SetDescription(toggle, "Отправлять обратную связь клиенту");
                    toggle.Toggled += (_, args) =>
                    {
                        if (toggle.BindingContext is FeedbackClient client && client.Enabled != args.Value)
                        {
                            client.Configure(client.Port, args.Value);
                            SaveClients();
                        }
                    };
                    var doubleClick = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
                    doubleClick.Tapped += (_, _) =>
                    {
                        if (info.BindingContext is FeedbackClient client)
                            openClient(client);
                    };
                    info.GestureRecognizers.Add(doubleClick);
                    row = Row(info, toggle);
                }
                row.Padding = new Thickness(12, 8);
                row.BackgroundColor = Panel;
                row.Margin = new Thickness(0, 0, 0, 2);
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
                                Setters = { new Setter { Property = BackgroundColorProperty, Value = PulseVisuals.Selected } }
                            }
                        }
                    }
                });
                return row;
            })
        };

        list.SelectionChanged += (_, _) =>
        {
            details.Content = list.SelectedItem is FeedbackClient client ? MakeClientDetails(client, compact) : null;
        };
        return list;
    }

    private View MakeClientDetails(FeedbackClient client, bool compact)
    {
        var incomingPort = MakeEntry(Keyboard.Numeric);
        incomingPort.SetBinding(Entry.TextProperty,
            new Binding(nameof(FeedbackClient.Port), BindingMode.OneWay, stringFormat: "{0}"));
        var enabled = new Switch { OnColor = Accent };
        enabled.SetBinding(Switch.IsToggledProperty, nameof(FeedbackClient.Enabled), BindingMode.OneWay);
        var enabledRow = Row(Caption("Отправлять состояние"), enabled);
        enabledRow.IsVisible = !compact;
        var save = MakeButton("Сохранить", primary: true);
        var status = Caption();
        status.IsVisible = false;
        incomingPort.TextChanged += (_, _) => status.IsVisible = false;
        enabled.Toggled += (_, _) => status.IsVisible = false;
        save.Clicked += (_, _) =>
        {
            if (!TryNumber(incomingPort.Text, 1, 65535, out int port))
            {
                status.Text = "Входящий UDP-порт должен быть в диапазоне 1–65535.";
                status.TextColor = Warning;
                status.IsVisible = true;
                return;
            }

            client.Configure(port, compact ? client.Enabled : enabled.IsToggled);
            SaveClients();
            status.Text = "Настройки сохранены.";
            status.TextColor = Accent;
            status.IsVisible = true;
        };
        var form = new VerticalStackLayout
        {
            BindingContext = client, Spacing = 8,
            Children =
            {
                Heading(client.Address, compact ? 14 : 18), Field("Входящий UDP-порт", incomingPort),
                enabledRow, save, status
            }
        };
        return compact ? form : Card(form);
    }

    private void LoadClients()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<ClientSettings[]>(Preferences.Default.Get(ClientsKey, "[]")) ?? [];
            foreach (ClientSettings entry in saved)
                AddClient(entry.Address, entry.Port, entry.Enabled);
        }
        catch (JsonException)
        {
            // The list can be reconstructed from new OSC traffic.
        }
    }

    private bool AddClient(string address, int port, bool enabled)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            port is < 1 or > 65535 || !feedbackAddresses.Add(ip.ToString()))
            return false;

        var client = new FeedbackClient(ip, port, enabled);
        feedbackClients.Add(client);
        if (dashboardClientList is { SelectedItem: null } dashboardList)
            dashboardList.SelectedItem = client;
        return true;
    }

    private void DiscoverClients()
    {
        if (receiver is null)
            return;

        bool changed = false;
        foreach (string address in receiver.GetClientAddresses())
            if (!feedbackAddresses.Contains(address))
                changed |= AddClient(address, DefaultFeedbackPort, enabled: false);
        if (changed)
            SaveClients();
    }

    private void SaveClients()
    {
        var settings = feedbackClients.Select(client => new ClientSettings(client.Address, client.Port, client.Enabled)).ToArray();
        Preferences.Default.Set(ClientsKey, JsonSerializer.Serialize(settings));
        ConfigureFeedback();
    }

    private void UpdateFeedbackStatus()
    {
        var state = feedbackDelivery?.Snapshot();
        feedbackStatus.TextColor = state?.Failure is not null ? Warning : Muted;
        feedbackStatus.Text = state switch
        {
            { Failure: { } failure } => failure,
            null or { Recipients: 0 } => "Нет получателей обратной связи.",
            { HasSources: false } => "Обратная связь модулей неактивна.",
            _ => $"Получателей · {state.Recipients} · Отправлено UDP-пакетов за сеанс · {state.PacketsSent:N0}"
        };
        dashboardFeedbackStatus.TextColor = feedbackStatus.TextColor;
        dashboardFeedbackStatus.Text = feedbackStatus.Text;
        dashboardFeedbackStatus.IsVisible = state is { Recipients: > 0 };
    }

    private void StartClientMonitoring()
    {
        feedbackDelivery = new(clientTraffic.Sent);
        ConfigureFeedback();
        clientTimer = Dispatcher.CreateTimer();
        clientTimer.Interval = TimeSpan.FromMilliseconds(500);
        clientTimer.Tick += (_, _) =>
        {
            DiscoverClients();
            UpdateFeedbackStatus();
            RefreshClientTraffic();
        };
        clientTimer.Start();
    }

    private void RefreshClientTraffic()
    {
        if (clientsList.SelectedItem is not FeedbackClient client || activeSection != clientsView)
            return;

        var snapshot = clientTraffic.Snapshot(client.Address);
        trafficCounts.Text = $"Принято OSC · {snapshot.Received:N0}    Отправлено OSC · {snapshot.Sent:N0}";
        trafficHistory.Text = snapshot.Recent.Length == 0 ? "Сообщений пока нет." : string.Join("\n\n",
            snapshot.Recent.Reverse().Select(entry =>
            {
                string remote = $"{client.Address}:{entry.RemotePort}";
                string local = entry.LocalPort is int port ? $"LavOSC:{port}" : "LavOSC";
                string route = entry.Sent ? $"{local} → {remote}" : $"{remote} → {local}";
                return $"{entry.At:HH:mm:ss.fff} · {route}\n{entry.Address} {entry.Arguments}";
            }));
    }

    private void ConfigureFeedback()
    {
        if (closed || feedbackDelivery is null)
            return;

        var sources = new List<(string Name, IOscFeedbackSource Source)>();
        foreach (ModuleEntry module in moduleEntries)
        {
            if (!module.IsEnabled || !module.FeedbackEnabled)
                continue;
            IOscFeedbackSource? source = module == wheelModule ? session : module == pedalsModule ? pedals
                : module.Extension as IOscFeedbackSource;
            if (source is not null)
                sources.Add((module.Name, source));
        }
        feedbackDelivery.Configure(feedbackClients.Where(client => client.Enabled).Select(client => client.EndPoint).ToArray(),
            sources.ToArray());
    }

    private sealed record ClientSettings(string Address, int Port, bool Enabled);
}
