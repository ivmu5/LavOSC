using System.ComponentModel;
using Laveyana.LavOSC.Core;

namespace Laveyana.LavOSC;

internal sealed class ModuleEntry(string name, string title, string enabledKey,
    bool enabled = false, bool pinned = false, IOscModule? extension = null, string? validationError = null)
    : INotifyPropertyChanged
{
    public string Name { get; } = name;
    public string Title { get; } = title;
    public string Address => $"/{Name}";
    public string EnabledKey { get; } = enabledKey;
    public string PinnedKey => $"OscModulePinned.{Name}";
    public IOscModule? Extension { get; } = extension;
    public IDisposable? Registration { get; set; }
    public bool Available { get; } = validationError is null;
    public bool IsEnabled { get; private set; } = enabled;
    public bool IsPinned { get; private set; } = pinned;
    public string PinAction => IsPinned ? "Убрать с главной" : "На главный экран";
    public string PinGlyph => IsPinned ? "★" : "☆";
    public string State => Error is not null ? "Ошибка" : IsEnabled ? "Включён" : "Выключен";
    public string? Error { get; private set; } = validationError;
    public bool HasError => Error is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Matches(string query, bool onlyPinned) => (!onlyPinned || IsPinned) &&
        (Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
         Address.Contains(query, StringComparison.OrdinalIgnoreCase));

    public void SetEnabled(bool value)
    {
        if (IsEnabled == value)
            return;

        IsEnabled = value;
        Notify(nameof(IsEnabled), nameof(State));
    }

    public void SetPinned(bool value)
    {
        if (IsPinned == value)
            return;

        IsPinned = value;
        Notify(nameof(IsPinned), nameof(PinAction), nameof(PinGlyph));
    }

    public void SetError(string? value)
    {
        if (Error == value)
            return;

        Error = value;
        Notify(nameof(Error), nameof(HasError), nameof(State));
    }

    private void Notify(params string[] properties)
    {
        foreach (string property in properties)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
