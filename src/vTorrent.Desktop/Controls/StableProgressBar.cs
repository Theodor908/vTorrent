using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace vTorrent.Desktop.Controls;

public class StableProgressBar : ProgressBar
{
    private bool _isAttached;
    
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        
        if (_isAttached)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var currentValue = Value;
                var transitions = Transitions;
                
                Transitions = null;
                
                Value = 0;
                Value = currentValue;
                
                Dispatcher.UIThread.Post(() =>
                {
                    Transitions = transitions;
                }, DispatcherPriority.Background);
            }, DispatcherPriority.Loaded);
        }
        
        _isAttached = true;
    }
    
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Keep _isAttached = true so we know we've been attached before
    }
}