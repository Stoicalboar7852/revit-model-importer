using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace RevitModelImporter.Core.Presentation;

/// <summary>Minimal INotifyPropertyChanged base (no MVVM library, to keep the add-in dependency-free).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>ICommand from delegates. ICommand lives in System.ObjectModel, so this is cross-platform.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _execute(parameter);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A value with a label for combo boxes.</summary>
public sealed record Option<T>(T Value, string Label, string? Description = null)
{
    public override string ToString() => Label;
}

/// <summary>A Revit category that DirectShape accepts.</summary>
public sealed record CategoryOption(long Id, string Name, string BuiltInName)
{
    public override string ToString() => Name;
}
