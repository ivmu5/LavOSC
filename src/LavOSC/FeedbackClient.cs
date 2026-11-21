using System.ComponentModel;
using System.Net;

namespace Laveyana.LavOSC;

internal sealed class FeedbackClient(IPAddress address, int port, bool enabled) : INotifyPropertyChanged
{
    public string Address { get; } = address.ToString();
    public int Port { get; private set; } = port;
    public bool Enabled { get; private set; } = enabled;
    public string PortLabel => $"UDP {Port}";
    public string State => Enabled ? $"Отправка на UDP {Port}" : $"Отправка отключена · UDP {Port}";
    public IPEndPoint EndPoint => new(address, Port);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Configure(int port, bool enabled)
    {
        bool portChanged = Port != port;
        bool enabledChanged = Enabled != enabled;
        Port = port;
        Enabled = enabled;
        if (portChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Port)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PortLabel)));
        }
        if (enabledChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        if (portChanged || enabledChanged)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
    }
}
