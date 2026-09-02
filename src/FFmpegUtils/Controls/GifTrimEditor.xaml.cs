using System.Windows;
using System.Windows.Controls;
using FFmpegUtils.ViewModels;

namespace FFmpegUtils.Controls;

public partial class GifTrimEditor : UserControl
{
    private GifTrimViewModel? ViewModel => DataContext as GifTrimViewModel;
    public GifTrimEditor()
    {
        InitializeComponent();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is GifTrimViewModel old) old.SetActive(false);
            ViewModel?.SetActive(IsVisible);
        };
    }
    private void Editor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => ViewModel?.SetActive(IsVisible);
    private void Editor_Unloaded(object sender, RoutedEventArgs e) => ViewModel?.SetActive(false);
    private void Timeline_RangeChanged(object? sender, RangeSelectionEventArgs e) => ViewModel?.SetRange(e.Start, e.End, e.Preview);
    private async void Timeline_SeekRequested(object? sender, double seconds) { if (ViewModel is { } vm) await vm.SeekAsync(seconds); }
    private void Timeline_InteractionStarted(object? sender, bool editingRange) => ViewModel?.BeginInteraction(editingRange);
    private async void Timeline_InteractionCompleted(object? sender, EventArgs e) { if (ViewModel is { } vm) await vm.EndInteractionAsync(); }
    private async void Play_Click(object sender, RoutedEventArgs e) { if (ViewModel is { } vm) await vm.PlayAsync(); }
    private async void Restart_Click(object sender, RoutedEventArgs e) { if (ViewModel is { } vm) await vm.PlayAsync(restart: true); }
    private void Reset_Click(object sender, RoutedEventArgs e) => ViewModel?.ResetRange();
}
