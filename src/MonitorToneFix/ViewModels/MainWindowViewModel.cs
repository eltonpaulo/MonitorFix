using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitorToneFix.Core.Compositor;
using MonitorToneFix.Core.Monitors;
using MonitorToneFix.Core.Recolor;

namespace MonitorToneFix.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly HashSet<string> NonApplyProperties = new()
    {
        nameof(Monitors), nameof(StatusText), nameof(IsBusy),
    };

    private readonly MonitorService _monitorService = new();
    private readonly PicomController _picomController = new();
    private CancellationTokenSource? _debounceCts;

    public ObservableCollection<MonitorInfo> Monitors { get; } = new();

    [ObservableProperty] private MonitorInfo? selectedMonitor;
    [ObservableProperty] private bool filterEnabled;
    [ObservableProperty] private string statusText = "Selecione um monitor para comecar.";
    [ObservableProperty] private bool isBusy;

    // Faixa A — brancos neutros
    [ObservableProperty] private int aLowerLuminance = 240;
    [ObservableProperty] private int aNeutralityTolerance = 8;
    [ObservableProperty] private int aIntensityPercent = 70;
    [ObservableProperty] private int aFeatherPercent = 20;
    [ObservableProperty] private byte aTargetR = 224;
    [ObservableProperty] private byte aTargetG = 224;
    [ObservableProperty] private byte aTargetB = 224;

    // Faixa B — brancos avermelhados
    [ObservableProperty] private int bLowerLuminance = 229;
    [ObservableProperty] private int bRedSensitivity = 26;
    [ObservableProperty] private int bIntensityPercent = 70;
    [ObservableProperty] private int bFeatherPercent = 20;
    [ObservableProperty] private byte bTargetR = 224;
    [ObservableProperty] private byte bTargetG = 214;
    [ObservableProperty] private byte bTargetB = 212;

    // Ajustes globais
    [ObservableProperty] private int maxHighlightBrightness = 220;
    [ObservableProperty] private int luminancePreservationPercent = 65;
    [ObservableProperty] private int preserveHighlightDifferencesPercent = 80;

    public MainWindowViewModel()
    {
        RefreshMonitors();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not null && !NonApplyProperties.Contains(e.PropertyName))
        {
            ScheduleApply();
        }
    }

    [RelayCommand]
    private void RefreshMonitors()
    {
        var previousId = SelectedMonitor?.PersistentId;
        Monitors.Clear();
        foreach (var m in _monitorService.GetMonitors())
        {
            Monitors.Add(m);
        }
        SelectedMonitor = Monitors.FirstOrDefault(m => m.PersistentId == previousId) ?? Monitors.FirstOrDefault();
    }

    [RelayCommand]
    private void ActivateFilter() => FilterEnabled = true;

    [RelayCommand]
    private void DeactivateFilter() => FilterEnabled = false;

    /// <summary>"Restaurar imagem imediatamente" — bypasses the debounce for an instant revert.</summary>
    [RelayCommand]
    private async Task RestoreImmediatelyAsync()
    {
        _debounceCts?.Cancel();
        FilterEnabled = false;
        await ApplyNowAsync();
    }

    private void ScheduleApply()
    {
        _debounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        _ = DebounceAndApplyAsync(cts.Token);
    }

    private async Task DebounceAndApplyAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(150, token);
            await ApplyNowAsync();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer change; ignore.
        }
    }

    private async Task ApplyNowAsync()
    {
        if (SelectedMonitor is null)
        {
            StatusText = FilterEnabled
                ? "Selecione um monitor antes de ativar o filtro."
                : "Selecione um monitor para comecar.";
            return;
        }

        IsBusy = true;
        try
        {
            var parameters = BuildParameters();
            await _picomController.ApplyAsync(parameters, SelectedMonitor);
            StatusText = FilterEnabled
                ? $"Filtro ativo em {SelectedMonitor.DisplayLabel}"
                : $"Filtro desativado — {SelectedMonitor.DisplayLabel} sem alteracoes";
        }
        catch (Exception ex)
        {
            StatusText = $"Falha ao aplicar o filtro: {ex.Message}";
            FilterEnabled = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private RecolorParameters BuildParameters() => new()
    {
        FilterEnabled = FilterEnabled,
        BandA = new NeutralWhiteBand
        {
            LowerLuminance = ALowerLuminance,
            NeutralityTolerance = ANeutralityTolerance,
            IntensityPercent = AIntensityPercent,
            FeatherPercent = AFeatherPercent,
            TargetColor = new RgbColor(ATargetR, ATargetG, ATargetB),
        },
        BandB = new ReddishWhiteBand
        {
            LowerLuminance = BLowerLuminance,
            RedSensitivity = BRedSensitivity,
            IntensityPercent = BIntensityPercent,
            FeatherPercent = BFeatherPercent,
            TargetColor = new RgbColor(BTargetR, BTargetG, BTargetB),
        },
        Global = new GlobalAdjustments
        {
            MaxHighlightBrightness = MaxHighlightBrightness,
            LuminancePreservationPercent = LuminancePreservationPercent,
            PreserveHighlightDifferencesPercent = PreserveHighlightDifferencesPercent,
        },
    };

    public void Dispose()
    {
        _debounceCts?.Cancel();
        _picomController.Dispose();
    }
}
