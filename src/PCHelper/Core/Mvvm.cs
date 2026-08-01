using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace PCHelper.Core;

/// <summary>Basisklasse fuer ViewModels mit Aenderungsbenachrichtigung.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>Klassisches ICommand mit Delegates; unterstuetzt asynchrone Aktionen.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _running;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        : this(p => { execute(p); return Task.CompletedTask; }, canExecute) { }

    public RelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
        => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            Log.Error("Befehl fehlgeschlagen", ex);
            System.Windows.MessageBox.Show(
                $"Aktion fehlgeschlagen:\n\n{ex.Message}",
                AppInfo.Name, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher is { } d && !d.CheckAccess())
            d.BeginInvoke(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
        else
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
