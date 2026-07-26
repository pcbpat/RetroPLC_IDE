using Dock.Model.Mvvm.Controls;

namespace RetroPLC.Shell.ViewModels.Docking;

public sealed class AppearanceViewModel(MainWindowViewModel settings) : Tool
{
    public MainWindowViewModel Settings { get; } = settings;
}
