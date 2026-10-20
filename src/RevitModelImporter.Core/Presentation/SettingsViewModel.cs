using System.Windows.Input;
using RevitModelImporter.Core.Settings;
using RevitModelImporter.Core.Statistics;

namespace RevitModelImporter.Core.Presentation;

/// <summary>Settings dialog: recommendation thresholds and switch, texture folder, build chunk size.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ImporterSettings _settings;
    private readonly Func<string?>? _pickFolder;
    private bool _showRecommendation;
    private long _threshold;
    private long _strongThreshold;
    private string _fallbackTextureFolder;
    private bool _alwaysUseFallback;
    private int _maxTrianglesPerBuild;

    public SettingsViewModel(ImporterSettings settings, string settingsFilePath, Func<string?>? pickFolder = null)
    {
        _settings = settings;
        _pickFolder = pickFolder;
        SettingsFilePath = settingsFilePath;
        _showRecommendation = settings.ShowSimplifyRecommendation;
        _threshold = settings.RecommendThreshold;
        _strongThreshold = settings.StrongRecommendThreshold;
        _fallbackTextureFolder = settings.FallbackTextureFolder ?? SettingsStore.DefaultFallbackTextureFolder;
        _alwaysUseFallback = settings.AlwaysUseFallbackTextureFolder;
        _maxTrianglesPerBuild = settings.MaxTrianglesPerBuild;
        SaveCommand = new RelayCommand(Save, () => IsValid);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
        BrowseCommand = new RelayCommand(Browse, () => _pickFolder is not null);
        ResetCommand = new RelayCommand(Reset);
    }

    public event EventHandler<bool>? CloseRequested;

    public string SettingsFilePath { get; }

    public bool ShowRecommendation
    {
        get => _showRecommendation;
        set => Set(ref _showRecommendation, value);
    }

    public long Threshold
    {
        get => _threshold;
        set
        {
            if (Set(ref _threshold, value))
            {
                OnValidityChanged();
            }
        }
    }

    public long StrongThreshold
    {
        get => _strongThreshold;
        set
        {
            if (Set(ref _strongThreshold, value))
            {
                OnValidityChanged();
            }
        }
    }

    public string FallbackTextureFolder
    {
        get => _fallbackTextureFolder;
        set => Set(ref _fallbackTextureFolder, value ?? "");
    }

    public bool AlwaysUseFallbackTextureFolder
    {
        get => _alwaysUseFallback;
        set => Set(ref _alwaysUseFallback, value);
    }

    public int MaxTrianglesPerBuild
    {
        get => _maxTrianglesPerBuild;
        set
        {
            if (Set(ref _maxTrianglesPerBuild, value))
            {
                OnValidityChanged();
            }
        }
    }

    public bool IsValid => ValidationMessage is null;

    public string? ValidationMessage =>
        Threshold < 1 ? "The threshold must be at least 1 face."
        : StrongThreshold < Threshold ? "The strong threshold must not be lower than the threshold."
        : MaxTrianglesPerBuild is < 100 or > 1_000_000 ? "Triangles per build must be between 100 and 1,000,000."
        : null;

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand BrowseCommand { get; }

    public ICommand ResetCommand { get; }

    private void Browse()
    {
        if (_pickFolder?.Invoke() is { Length: > 0 } folder)
        {
            FallbackTextureFolder = folder;
        }
    }

    private void Reset()
    {
        ShowRecommendation = true;
        Threshold = SimplificationAdvisor.DefaultThreshold;
        StrongThreshold = SimplificationAdvisor.DefaultStrongThreshold;
        FallbackTextureFolder = SettingsStore.DefaultFallbackTextureFolder;
        AlwaysUseFallbackTextureFolder = false;
        MaxTrianglesPerBuild = new ImporterSettings().MaxTrianglesPerBuild;
    }

    private void Save()
    {
        if (!IsValid)
        {
            return;
        }

        _settings.ShowSimplifyRecommendation = ShowRecommendation;
        _settings.RecommendThreshold = Threshold;
        _settings.StrongRecommendThreshold = StrongThreshold;
        _settings.FallbackTextureFolder = string.Equals(FallbackTextureFolder, SettingsStore.DefaultFallbackTextureFolder, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(FallbackTextureFolder)
            ? null
            : FallbackTextureFolder;
        _settings.AlwaysUseFallbackTextureFolder = AlwaysUseFallbackTextureFolder;
        _settings.MaxTrianglesPerBuild = MaxTrianglesPerBuild;
        CloseRequested?.Invoke(this, true);
    }

    private void OnValidityChanged()
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ValidationMessage));
        ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
    }
}
