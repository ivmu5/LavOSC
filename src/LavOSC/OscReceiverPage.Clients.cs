using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Laveyana.LavOSC.Core;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Laveyana.LavOSC;

internal sealed partial class OscReceiverPage
{
    private const string ClientsKey = "OscFeedbackClients";
    private const int DefaultFeedbackPort = 9000;
    private readonly Label feedbackStatus = Caption();
    private readonly ObservableCollection<FeedbackClient> feedbackClients = [];
    private readonly HashSet<string> feedbackAddresses = new(StringComparer.Ordinal);
    private readonly Stopwatch clientDiscovery = Stopwatch.StartNew();
    private IPEndPoint[] feedbackRecipients = [];
    private Grid clientsView = null!;
    private IDispatcherTimer feedbackTimer = null!;
    private UdpClient? feedbackSocket;
    private bool publishingFeedback;

    private Grid MakeClientsView()
    {
        LoadClients();
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
                state.SetBinding(Label.TextProperty, nameof(FeedbackClient.State));
                return new VerticalStackLayout
                {
                    BackgroundColor = Panel, Spacing = 2, Padding = new Thickness(12, 8),
                    Margin = new Thickness(0, 0, 0, 2), Children = { address, state }
                };
            })
        };

        var details = new VerticalStackLayout { Spacing = 12 };
        list.SelectionChanged += (_, _) =>
        {
            details.Children.Clear();
            if (list.SelectedItem is FeedbackClient client)
                details.Children.Add(MakeClientDetails(client));
        };
        var left = new Grid
        {
            RowSpacing = 12,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
        };
        left.Add(Heading("Клиенты", 27));
        left.Add(list, 0, 1);
        var view = MakeColumns();
        view.Add(left);
        view.Add(new ScrollView
        {
            Content = new VerticalStackLayout { Spacing = 12, Children = { feedbackStatus, details } }
        }, 1);
        UpdateFeedbackStatus();
        return view;
    }

    private View MakeClientDetails(FeedbackClient client)
    {
        var incomingPort = MakeEntry(Keyboard.Numeric);
        incomingPort.Text = client.Port.ToString(CultureInfo.InvariantCulture);
        var enabled = new Switch { IsToggled = client.Enabled, OnColor = Accent };
        var save = MakeButton("Сохранить", primary: true);
        var status = Caption();
        status.IsVisible = false;
        save.Clicked += (_, _) =>
        {
            if (!TryNumber(incomingPort.Text, 1, 65535, out int port))
            {
                status.Text = "Входящий UDP-порт должен быть в диапазоне 1–65535.";
                status.TextColor = Warning;
                status.IsVisible = true;
                return;
            }

            client.Configure(port, enabled.IsToggled);
            SaveClients();
            UpdateFeedbackStatus();
            status.Text = "Настройки сохранены.";
            status.TextColor = Accent;
            status.IsVisible = true;
        };
        return Card(new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Heading(client.Address, 18), Field("Входящий порт клиента", incomingPort),
                Row(Caption("Отправлять состояние"), enabled), save, status
            }
        });
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
        UpdateRecipients();
    }

    private bool AddClient(string address, int port, bool enabled)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            port is < 1 or > 65535 || !feedbackAddresses.Add(ip.ToString()))
            return false;

        feedbackClients.Add(new FeedbackClient(ip, port, enabled));
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
        UpdateRecipients();
    }

    private void UpdateRecipients() => feedbackRecipients = feedbackClients.Where(client => client.Enabled)
        .Select(client => client.EndPoint).ToArray();

    private void UpdateFeedbackStatus(string? failure = null)
    {
        feedbackStatus.TextColor = failure is null ? Muted : Warning;
        feedbackStatus.Text = failure ?? (feedbackRecipients.Length == 0 ? "Нет получателей обратной связи."
            : $"Получателей обратной связи · {feedbackRecipients.Length}");
    }

    private void StartFeedbackTimer()
    {
        feedbackTimer = Dispatcher.CreateTimer();
        feedbackTimer.Interval = TimeSpan.FromMilliseconds(100);
        feedbackTimer.Tick += async (_, _) => await PublishFeedbackAsync();
        feedbackTimer.Start();
    }

    private async Task PublishFeedbackAsync()
    {
        if (closed)
            return;
        if (clientDiscovery.Elapsed >= TimeSpan.FromSeconds(1))
        {
            clientDiscovery.Restart();
            DiscoverClients();
        }

        if (publishingFeedback || feedbackRecipients.Length == 0)
            return;

        IPEndPoint[] recipients = feedbackRecipients;
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
        if (sources.Count == 0)
            return;

        publishingFeedback = true;
        CancellationToken token = lifetime.Token;
        try
        {
            var udp = feedbackSocket ??= new UdpClient(AddressFamily.InterNetwork);
            string? failure = await Task.Run(() => FeedbackDelivery.SendAsync(udp, recipients, sources, token), token);
            if (!closed)
                UpdateFeedbackStatus(failure);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (!closed)
                UpdateFeedbackStatus(exception.Message);
        }
        finally
        {
            publishingFeedback = false;
        }
    }

    private sealed record ClientSettings(string Address, int Port, bool Enabled);
}
