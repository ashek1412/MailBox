using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MailBox.ViewModels;

namespace MailBox.Views;

public partial class SidebarView : UserControl
{
    public SidebarView() { InitializeComponent(); }

    private Point _dragStartPoint;
    private AccountItemViewModel? _draggedItem;

    private void DragHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _draggedItem    = (sender as FrameworkElement)?.DataContext as AccountItemViewModel;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void DragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedItem == null) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        ((UIElement)sender).ReleaseMouseCapture();
        var item = _draggedItem;
        _draggedItem = null;
        DragDrop.DoDragDrop((DependencyObject)sender,
            new DataObject("AccountItem", item), DragDropEffects.Move);
    }

    private void DragHandle_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedItem != null)
        {
            ((UIElement)sender).ReleaseMouseCapture();
            _draggedItem = null;
        }
    }

    private void AccountList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("AccountItem")
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void AccountList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("AccountItem") is not AccountItemViewModel dragged) return;

        var target = GetAccountItemAtPoint(e.GetPosition(AccountList));
        if (target == null || target == dragged) return;

        if (DataContext is not SidebarViewModel vm) return;

        int fromIdx = vm.AccountItems.IndexOf(dragged);
        int toIdx   = vm.AccountItems.IndexOf(target);
        if (fromIdx < 0 || toIdx < 0) return;

        vm.MoveAccount(fromIdx, toIdx);
    }

    private AccountItemViewModel? GetAccountItemAtPoint(Point point)
    {
        var hit = VisualTreeHelper.HitTest(AccountList, point);
        if (hit == null) return null;

        DependencyObject? el = hit.VisualHit;
        while (el != null)
        {
            if (el is FrameworkElement fe && fe.DataContext is AccountItemViewModel avm)
                return avm;
            el = VisualTreeHelper.GetParent(el);
        }
        return null;
    }
}
