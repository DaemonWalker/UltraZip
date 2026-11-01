using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UltraZip.Views;

public sealed class ImePasswordBox : TextBox
{
    private string _password = string.Empty;
    private bool _updating;

    public ImePasswordBox()
    {
        InputMethod.SetIsInputMethodEnabled(this, true);
        InputMethod.SetPreferredImeState(this, InputMethodState.On);
    }

    public string Password
    {
        get => _password;
        set
        {
            value ??= string.Empty;
            if (_password == value) return;
            _password = value;
            ShowMaskedPassword();
            OnPasswordChanged();
        }
    }

    public char PasswordChar { get; set; } = '●';

    public event RoutedEventHandler? PasswordChanged;

    public new void Clear() => Password = string.Empty;

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        if (_updating) return;

        string password = _password;
        foreach (TextChange change in e.Changes)
        {
            if (change.RemovedLength > 0)
                password = password.Remove(change.Offset, change.RemovedLength);
            if (change.AddedLength > 0)
                password = password.Insert(change.Offset, Text.Substring(change.Offset, change.AddedLength));
        }

        int caret = CaretIndex;
        _password = password;
        ShowMaskedPassword();
        CaretIndex = Math.Min(caret, Text.Length);
        OnPasswordChanged();
    }

    private void ShowMaskedPassword()
    {
        _updating = true;
        Text = new string(PasswordChar, _password.Length);
        CaretIndex = Text.Length;
        _updating = false;
    }

    private void OnPasswordChanged() =>
        PasswordChanged?.Invoke(this, new RoutedEventArgs());
}
