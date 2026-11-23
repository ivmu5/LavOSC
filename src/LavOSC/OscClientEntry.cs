using System.ComponentModel;

namespace Laveyana.LavOSC;

internal sealed class OscClientEntry(string address, bool allowed) : INotifyPropertyChanged
{
    public string Address { get; } = address;
    public bool Allowed { get; private set; } = allowed;
    public string State => Allowed ? "Доступ разрешён" : "Доступ закрыт";
    public string Action => Allowed ? "Заблокировать" : "Разрешить";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetAllowed(bool value)
    {
        if (Allowed == value)
            return;
        Allowed = value;
        PropertyChanged?.Invoke(this, new(nameof(Allowed)));
        PropertyChanged?.Invoke(this, new(nameof(State)));
        PropertyChanged?.Invoke(this, new(nameof(Action)));
    }
}
