using System.ComponentModel;

namespace Laveyana.LavOSC;

internal sealed class OscClientEntry(ClientAccessRule rule, bool allowed) : INotifyPropertyChanged
{
    public string Address { get; } = rule.Address;
    public bool Allowed { get; private set; } = allowed;
    public int FeedbackPort { get; private set; } = rule.FeedbackPort;
    public bool FeedbackEnabled { get; private set; } = rule.FeedbackEnabled;
    public string FeedbackState => FeedbackEnabled
        ? Allowed ? $"Отправка · UDP {FeedbackPort}" : "Отправка приостановлена"
        : "Отправка выключена";
    public string State => Allowed ? "Доступ разрешён" : "Доступ закрыт";
    public string Action => Allowed ? "Заблокировать" : "Разрешить";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(ClientAccessRule settings, bool allowed)
    {
        bool accessChanged = Allowed != allowed;
        bool portChanged = FeedbackPort != settings.FeedbackPort;
        bool enabledChanged = FeedbackEnabled != settings.FeedbackEnabled;
        Allowed = allowed;
        FeedbackPort = settings.FeedbackPort;
        FeedbackEnabled = settings.FeedbackEnabled;
        if (accessChanged)
        {
            PropertyChanged?.Invoke(this, new(nameof(Allowed)));
            PropertyChanged?.Invoke(this, new(nameof(State)));
            PropertyChanged?.Invoke(this, new(nameof(Action)));
        }
        if (portChanged)
            PropertyChanged?.Invoke(this, new(nameof(FeedbackPort)));
        if (enabledChanged)
            PropertyChanged?.Invoke(this, new(nameof(FeedbackEnabled)));
        if (accessChanged || portChanged || enabledChanged)
            PropertyChanged?.Invoke(this, new(nameof(FeedbackState)));
    }
}
