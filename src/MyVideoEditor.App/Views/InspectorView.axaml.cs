using Avalonia.Controls;
using MyVideoEditor.App.ViewModels;

namespace MyVideoEditor.App.Views;

public partial class InspectorView : UserControl
{
    public InspectorView()
    {
        InitializeComponent();
        AddEffectButton.Click += (_, _) =>
        {
            if (DataContext is not ClipInspectorViewModel vm)
                return;
            var menu = new MenuFlyout();
            foreach (var group in vm.AvailableEffects.GroupBy(e => e.IsAudio))
            {
                if (menu.Items.Count > 0)
                    menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = group.Key ? "Sound" : "Picture", IsEnabled = false });
                foreach (var def in group)
                {
                    var item = new MenuItem { Header = def.Label };
                    ToolTip.SetTip(item, def.Description);
                    item.Click += (_, _) => vm.AddEffectCommand.Execute(def);
                    menu.Items.Add(item);
                }
            }
            menu.ShowAt(AddEffectButton);
        };
    }
}
