using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace vTorrent.Desktop.Controls;

public enum ToastType
{
    Info,
    Success,
    Warning,
    Error
}

public partial class ToastNotification : UserControl
{
    private CancellationTokenSource? _autoHideCts;
    private readonly TimeSpan _defaultDuration = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _animationDuration = TimeSpan.FromMilliseconds(300);

    public ToastNotification()
    {
        InitializeComponent();
    }

    public void Show(string title, string message, ToastType type = ToastType.Info, TimeSpan? duration = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Cancel any existing auto-hide
            _autoHideCts?.Cancel();
            _autoHideCts = new CancellationTokenSource();

            // Set content
            TitleText.Text = title;
            MessageText.Text = message;
            MessageText.IsVisible = !string.IsNullOrEmpty(message);

            // Set icon and border style based on type
            SetToastType(type);

            // Show with animation
            IsVisible = true;
            AnimateIn();

            // Auto-hide after duration
            var actualDuration = duration ?? _defaultDuration;
            _ = AutoHideAsync(actualDuration, _autoHideCts.Token);
        });
    }

    private void SetToastType(ToastType type)
    {
        // Remove all type classes
        ToastContainer.Classes.Remove("toastInfo");
        ToastContainer.Classes.Remove("toastSuccess");
        ToastContainer.Classes.Remove("toastWarning");
        ToastContainer.Classes.Remove("toastError");

        ToastIcon.Classes.Remove("toastIconInfo");
        ToastIcon.Classes.Remove("toastIconSuccess");
        ToastIcon.Classes.Remove("toastIconWarning");
        ToastIcon.Classes.Remove("toastIconError");

        // Add the appropriate class
        var typeClass = type switch
        {
            ToastType.Info => "toastInfo",
            ToastType.Success => "toastSuccess",
            ToastType.Warning => "toastWarning",
            ToastType.Error => "toastError",
            _ => "toastInfo"
        };

        var iconClass = type switch
        {
            ToastType.Info => "toastIconInfo",
            ToastType.Success => "toastIconSuccess",
            ToastType.Warning => "toastIconWarning",
            ToastType.Error => "toastIconError",
            _ => "toastIconInfo"
        };

        ToastContainer.Classes.Add(typeClass);
        ToastIcon.Classes.Add(iconClass);
    }

    private async void AnimateIn()
    {
        try
        {
            if (ToastContainer.RenderTransform is TranslateTransform transform)
            {
                // Start from off-screen (right)
                transform.X = 100;

                // Animate to visible position
                var animation = new Animation
                {
                    Duration = _animationDuration,
                    Easing = new CubicEaseOut(),
                    FillMode = FillMode.Forward,
                    Children =
                    {
                        new KeyFrame
                        {
                            Cue = new Cue(0),
                            Setters = { new Setter(TranslateTransform.XProperty, 100.0) }
                        },
                        new KeyFrame
                        {
                            Cue = new Cue(1),
                            Setters = { new Setter(TranslateTransform.XProperty, 0.0) }
                        }
                    }
                };

                await animation.RunAsync(ToastContainer);

                // Explicitly set the final position after animation completes
                transform.X = 0;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in AnimateIn: {ex.Message}");
        }
    }

    private async Task AnimateOut()
    {
        if (ToastContainer.RenderTransform is TranslateTransform transform)
        {
            var animation = new Animation
            {
                Duration = _animationDuration,
                Easing = new CubicEaseIn(),
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0),
                        Setters = { new Setter(TranslateTransform.XProperty, 0.0) }
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1),
                        Setters = { new Setter(TranslateTransform.XProperty, 100.0) }
                    }
                }
            };

            await animation.RunAsync(ToastContainer);

            // Explicitly set the final position after animation completes
            transform.X = 100;
        }
    }

    private async Task AutoHideAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, cancellationToken);
            await Hide();
        }
        catch (TaskCanceledException)
        {
            // Cancelled - ignore
        }
    }

    public async Task Hide()
    {
        _autoHideCts?.Cancel();

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await AnimateOut();
            IsVisible = false;
        });
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        _ = Hide();
    }
}
