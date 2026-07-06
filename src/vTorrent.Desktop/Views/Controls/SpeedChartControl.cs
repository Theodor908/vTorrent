using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using vTorrent.Desktop.ViewModels;

namespace vTorrent.Desktop.Views;

/// <summary>
/// High-performance custom control for rendering smooth speed charts.
/// Features:
/// - Catmull-Rom spline interpolation for smooth curves
/// - Animated Y-axis scaling for smooth transitions
/// - EMA-smoothed data from ViewModel
/// </summary>
public class SpeedChartControl : Control, Avalonia.Rendering.ICustomHitTest
{
    #region Styled Properties

    public static readonly StyledProperty<ObservableCollection<SpeedDataPoint>?> DownloadDataProperty =
        AvaloniaProperty.Register<SpeedChartControl, ObservableCollection<SpeedDataPoint>?>(nameof(DownloadData));

    public static readonly StyledProperty<ObservableCollection<SpeedDataPoint>?> UploadDataProperty =
        AvaloniaProperty.Register<SpeedChartControl, ObservableCollection<SpeedDataPoint>?>(nameof(UploadData));

    public static readonly StyledProperty<bool> ShowDownloadProperty =
        AvaloniaProperty.Register<SpeedChartControl, bool>(nameof(ShowDownload), true);

    public static readonly StyledProperty<bool> ShowUploadProperty =
        AvaloniaProperty.Register<SpeedChartControl, bool>(nameof(ShowUpload), true);

    public static readonly StyledProperty<bool> IsDarkThemeProperty =
        AvaloniaProperty.Register<SpeedChartControl, bool>(nameof(IsDarkTheme), true);

    public ObservableCollection<SpeedDataPoint>? DownloadData
    {
        get => GetValue(DownloadDataProperty);
        set => SetValue(DownloadDataProperty, value);
    }

    public ObservableCollection<SpeedDataPoint>? UploadData
    {
        get => GetValue(UploadDataProperty);
        set => SetValue(UploadDataProperty, value);
    }

    public bool ShowDownload
    {
        get => GetValue(ShowDownloadProperty);
        set => SetValue(ShowDownloadProperty, value);
    }

    public bool ShowUpload
    {
        get => GetValue(ShowUploadProperty);
        set => SetValue(ShowUploadProperty, value);
    }

    public bool IsDarkTheme
    {
        get => GetValue(IsDarkThemeProperty);
        set => SetValue(IsDarkThemeProperty, value);
    }

    #endregion

    #region Brushes and Drawing

    private IBrush _downloadBrush = new SolidColorBrush(Color.Parse("#F5A623"));
    private IBrush _uploadBrush = new SolidColorBrush(Color.Parse("#94A3B8"));
    private IBrush _gridBrush = new SolidColorBrush(Color.Parse("#1F2937"));
    private IBrush _labelBrush = new SolidColorBrush(Color.Parse("#6B7280"));
    private IPen _downloadPen = default!;
    private IPen _uploadPen = default!;
    private IPen _gridPen = default!;
    private IPen _crosshairVerticalPen = default!;
    private IPen _crosshairDownloadPen = default!;
    private IPen _crosshairUploadPen = default!;
    private IBrush _tooltipBackgroundBrush = new SolidColorBrush(Color.Parse("#1E293B"));
    private IPen _tooltipBorderPen = new Pen(new SolidColorBrush(Color.Parse("#334155")), 1);

    private const double LeftMargin = 55;
    private const double BottomMargin = 20;
    private const int InterpolationSegments = 6; // Points between each data point for smooth curves
    private const int VisiblePoints = 60; // Only render the last 60 points (buffer may hold more)
    #endregion

    #region Animation & Render State

    private DispatcherTimer? _renderTimer;
    private DateTime _lastDataArrivalTime = DateTime.Now;

    // Y-axis scale animation (computed each frame, no separate timer)
    private double _currentMaxValue = 1000000;
    private double _targetMaxValue = 1000000;
    private DateTime _animationStartTime;
    private double _animationStartValue;
    private bool _isScaleAnimating;

    // Peak-hold state - prevents graph collapse during speed fluctuations
    private double _peakMaxValue = 1000000;
    private DateTime _peakHoldUntil = DateTime.MinValue;
    private const double PeakHoldDurationSeconds = 6.0;
    private const double ScaleUpThreshold = 0.03;
    private const double ScaleDownThreshold = 0.25;
    private const double ScaleAnimationDuration = 0.25;

    // Smooth leading-edge interpolation: lerp from previous to current speed each frame
    private long _prevDownloadSpeed;
    private long _prevUploadSpeed;

    // Crosshair state
    private Point? _pointerPosition;

    #endregion

    public SpeedChartControl()
    {
        UpdateBrushesFromTheme();
        UpdateIdleBrushes();

        this.GetObservable(DownloadDataProperty).Subscribe(OnDownloadDataChanged);
        this.GetObservable(UploadDataProperty).Subscribe(OnUploadDataChanged);
        this.GetObservable(ShowDownloadProperty).Subscribe(_ => InvalidateVisual());
        this.GetObservable(ShowUploadProperty).Subscribe(_ => InvalidateVisual());
        this.GetObservable(IsDarkThemeProperty).Subscribe(_ => InvalidateVisual());

        if (Application.Current != null)
        {
            Application.Current.ActualThemeVariantChanged += (s, e) =>
            {
                UpdateBrushesFromTheme();
                UpdateIdleBrushes();
                InvalidateVisual();
            };
        }

        // Continuous 60fps render timer — created once, started/stopped on attach/detach
        _renderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _renderTimer.Tick += (_, _) => InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _renderTimer?.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _renderTimer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    // ICustomHitTest: enable hit testing across entire control surface for crosshair pointer events
    public bool HitTest(Point point) => true;

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _pointerPosition = e.GetPosition(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointerPosition = e.GetPosition(this);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointerPosition = null;
    }

    private void StartScaleAnimation(double newTarget)
    {
        var isScalingUp = newTarget > _targetMaxValue;
        var threshold = isScalingUp ? ScaleUpThreshold : ScaleDownThreshold;

        if (Math.Abs(newTarget - _targetMaxValue) < _targetMaxValue * threshold)
            return;

        _targetMaxValue = newTarget;
        _animationStartValue = _currentMaxValue;
        _animationStartTime = DateTime.Now;
        _isScaleAnimating = true;
    }

    #region Collection Change Handling

    private ObservableCollection<SpeedDataPoint>? _currentDownloadData;
    private ObservableCollection<SpeedDataPoint>? _currentUploadData;

    private void OnDownloadDataChanged(ObservableCollection<SpeedDataPoint>? newCollection)
    {
        if (_currentDownloadData != null)
            _currentDownloadData.CollectionChanged -= OnCollectionChanged;

        _currentDownloadData = newCollection;

        if (_currentDownloadData != null)
            _currentDownloadData.CollectionChanged += OnCollectionChanged;

        CheckMaxValueChange();
        UpdateIdleState();
    }

    private void OnUploadDataChanged(ObservableCollection<SpeedDataPoint>? newCollection)
    {
        if (_currentUploadData != null)
            _currentUploadData.CollectionChanged -= OnCollectionChanged;

        _currentUploadData = newCollection;

        if (_currentUploadData != null)
            _currentUploadData.CollectionChanged += OnCollectionChanged;

        CheckMaxValueChange();
        UpdateIdleState();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Capture previous speed for smooth leading-edge interpolation
        if (sender == _currentDownloadData && _currentDownloadData?.Count >= 2)
            _prevDownloadSpeed = _currentDownloadData[^2].Speed;
        else if (sender == _currentUploadData && _currentUploadData?.Count >= 2)
            _prevUploadSpeed = _currentUploadData[^2].Speed;

        _lastDataArrivalTime = DateTime.Now;
        CheckMaxValueChange();
        UpdateIdleState();
    }

    private void CheckMaxValueChange()
    {
        var newDataMax = CalculateMaxValue();
        var now = DateTime.Now;

        // Peak-hold logic to prevent graph collapse during speed fluctuations
        if (newDataMax > _peakMaxValue)
        {
            // New peak - update and extend hold period
            _peakMaxValue = newDataMax;
            _peakHoldUntil = now.AddSeconds(PeakHoldDurationSeconds);
            StartScaleAnimation(_peakMaxValue);
        }
        else if (now > _peakHoldUntil)
        {
            // Hold period expired - allow scale to shrink if data is significantly lower
            if (newDataMax < _peakMaxValue * (1 - ScaleDownThreshold))
            {
                // Significant drop after hold period - update peak and animate down
                _peakMaxValue = newDataMax;
                StartScaleAnimation(_peakMaxValue);
            }
        }
        // Otherwise, keep the current peak (prevents yo-yo effect during hold period)
    }

    // Cached idle state — updated on collection change, not per-frame
    private bool _isIdle = true;

    private void UpdateIdleState()
    {
        var dlData = DownloadData;
        var ulData = UploadData;

        var hasDownloadData = dlData != null && dlData.Count >= 2;
        var hasUploadData = ulData != null && ulData.Count >= 2;

        if (!hasDownloadData && !hasUploadData)
        {
            _isIdle = true;
            return;
        }

        if (hasDownloadData && dlData!.Any(p => p.Speed > 0))
        {
            _isIdle = false;
            return;
        }
        if (hasUploadData && ulData!.Any(p => p.Speed > 0))
        {
            _isIdle = false;
            return;
        }

        _isIdle = true;
    }

    // Pre-cached idle state brushes/pens (rebuilt on theme change)
    private IPen _idleGridPen = default!;
    private IBrush _idleDashBrush = default!;
    private IBrush _idleZeroBrush = default!;
    private Color _idleGlowColor;
    private IPen _idleGlowPen = default!;
    private IBrush _idleCenterBrush = default!;
    private static readonly Typeface IdleTypeface = new(Typeface.Default.FontFamily);

    private void UpdateIdleBrushes()
    {
        var dark = IsDarkTheme;
        _idleGridPen = new Pen(new SolidColorBrush(Color.FromArgb(50, 148, 163, 184)), 1);
        _idleDashBrush = new SolidColorBrush(Color.Parse(dark ? "#374151" : "#D1D5DB"));
        _idleZeroBrush = new SolidColorBrush(Color.Parse(dark ? "#4B5563" : "#9CA3AF"));
        _idleGlowColor = dark ? Color.Parse("#00d9ff") : Color.Parse("#4A90A4");
        _idleGlowPen = new Pen(new SolidColorBrush(Color.FromArgb(64, _idleGlowColor.R, _idleGlowColor.G, _idleGlowColor.B)), 2);
        _idleCenterBrush = new SolidColorBrush(Color.Parse(dark ? "#94a3b8" : "#718096"));
    }

    private void RenderIdleState(DrawingContext context, double chartWidth, double chartHeight)
    {
        const int horizontalLines = 4;

        // Faded grid lines
        for (int i = 1; i < horizontalLines; i++)
        {
            var y = chartHeight * i / horizontalLines;
            context.DrawLine(_idleGridPen, new Point(LeftMargin, y), new Point(LeftMargin + chartWidth, y));
        }

        // Y-axis dashes instead of numeric labels
        for (int i = 0; i <= horizontalLines; i++)
        {
            var y = chartHeight * i / horizontalLines;
            var label = i == horizontalLines ? "0 B/s" : "\u2014";
            var brush = i == horizontalLines ? _idleZeroBrush : _idleDashBrush;

            var formattedText = new FormattedText(
                label,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                IdleTypeface,
                10,
                brush);

            var textX = LeftMargin - formattedText.Width - 8;
            var textY = y - formattedText.Height / 2;
            if (textY < 0) textY = 0;
            if (textY + formattedText.Height > chartHeight) textY = chartHeight - formattedText.Height;

            context.DrawText(formattedText, new Point(textX, textY));
        }

        // Baseline glow line
        var baselineY = chartHeight;
        context.DrawLine(_idleGlowPen, new Point(LeftMargin, baselineY), new Point(LeftMargin + chartWidth, baselineY));

        // Breathing dot at right edge — only this needs per-frame computation
        var elapsed = (DateTime.Now - _animationStartTime).TotalSeconds;
        var breatheT = (Math.Sin(elapsed * 2 * Math.PI / 3.0) + 1.0) / 2.0;
        var dotOpacity = (byte)(102 + (int)(153 * breatheT));
        var dotBrush = new SolidColorBrush(Color.FromArgb(dotOpacity, _idleGlowColor.R, _idleGlowColor.G, _idleGlowColor.B));

        var dotX = LeftMargin + chartWidth - 4;
        var dotY = baselineY - 3;
        context.DrawEllipse(dotBrush, null, new Point(dotX, dotY), 3, 3);

        // Glow halo around dot
        var haloBrush = new SolidColorBrush(Color.FromArgb((byte)(dotOpacity / 3), _idleGlowColor.R, _idleGlowColor.G, _idleGlowColor.B));
        context.DrawEllipse(haloBrush, null, new Point(dotX, dotY), 6, 6);

        // Center text
        var centerText = new FormattedText(
            "No active transfers",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            IdleTypeface,
            13,
            _idleCenterBrush);

        var centerX = LeftMargin + (chartWidth - centerText.Width) / 2;
        var centerY = (chartHeight - centerText.Height) / 2;
        context.DrawText(centerText, new Point(centerX, centerY));
    }

    #endregion

    private void UpdateBrushesFromTheme()
    {
        var textSecondaryColor = Color.Parse("#94A3B8");

        if (!IsDarkTheme)
        {
            _downloadBrush = new SolidColorBrush(Color.Parse("#D4A574"));
            _uploadBrush = new SolidColorBrush(Color.Parse("#94A3B8"));
        }
        else
        {
            _downloadBrush = new SolidColorBrush(Color.Parse("#00d9ff"));
            _uploadBrush = new SolidColorBrush(Color.Parse("#8b5cf6"));
        }

        _gridBrush = new SolidColorBrush(Color.FromArgb(80, textSecondaryColor.R, textSecondaryColor.G, textSecondaryColor.B));
        _labelBrush = new SolidColorBrush(textSecondaryColor);

        _downloadPen = new Pen(_downloadBrush, 2);
        _uploadPen = new Pen(_uploadBrush, 2);
        _gridPen = new Pen(_gridBrush, 1);

        // Crosshair pens (dashed, semi-translucent)
        var dashStyle = new DashStyle(new double[] { 4, 3 }, 0);
        var dlColor = ((_downloadBrush as SolidColorBrush)?.Color ?? Color.Parse("#00d9ff"));
        var ulColor = ((_uploadBrush as SolidColorBrush)?.Color ?? Color.Parse("#8b5cf6"));

        _crosshairVerticalPen = new Pen(
            new SolidColorBrush(Color.FromArgb(102, dlColor.R, dlColor.G, dlColor.B)), 1)
            { DashStyle = dashStyle };
        _crosshairDownloadPen = new Pen(
            new SolidColorBrush(Color.FromArgb(102, dlColor.R, dlColor.G, dlColor.B)), 1)
            { DashStyle = dashStyle };
        _crosshairUploadPen = new Pen(
            new SolidColorBrush(Color.FromArgb(76, ulColor.R, ulColor.G, ulColor.B)), 1)
            { DashStyle = dashStyle };

        if (!IsDarkTheme)
        {
            _tooltipBackgroundBrush = new SolidColorBrush(Color.Parse("#F1F5F9"));
            _tooltipBorderPen = new Pen(new SolidColorBrush(Color.Parse("#CBD5E1")), 1);
        }
        else
        {
            _tooltipBackgroundBrush = new SolidColorBrush(Color.Parse("#1E293B"));
            _tooltipBorderPen = new Pen(new SolidColorBrush(Color.Parse("#334155")), 1);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        UpdateBrushesFromTheme();

        var bounds = Bounds;
        var totalWidth = bounds.Width;
        var height = bounds.Height;

        if (totalWidth <= 0 || height <= 0) return;

        var chartWidth = totalWidth - LeftMargin;
        var chartHeight = height - BottomMargin;
        if (chartHeight <= 0) return;

        // Idle state: breathing baseline when no transfers active
        if (_isIdle)
        {
            RenderIdleState(context, chartWidth, chartHeight);
            return;
        }

        var maxValue = _currentMaxValue;
        if (maxValue <= 0) maxValue = 1000000;

        // Inline Y-axis scale animation (replaces old timer-based approach)
        if (_isScaleAnimating)
        {
            var elapsed = (DateTime.Now - _animationStartTime).TotalSeconds;
            var progress = Math.Min(elapsed / ScaleAnimationDuration, 1.0);
            var easedProgress = 1 - (1 - progress) * (1 - progress);
            _currentMaxValue = _animationStartValue + (_targetMaxValue - _animationStartValue) * easedProgress;
            if (progress >= 1.0)
            {
                _currentMaxValue = _targetMaxValue;
                _isScaleAnimating = false;
            }
            maxValue = _currentMaxValue;
        }

        // Smooth scroll: fraction of time elapsed since last data point (0.0 = just arrived, 1.0 = next point imminent)
        var scrollFraction = Math.Clamp(
            (DateTime.Now - _lastDataArrivalTime).TotalSeconds / 1.0,
            0.0, 1.0);

        DrawYAxisLabels(context, chartHeight, (long)maxValue);
        DrawGridLines(context, chartWidth, chartHeight);

        // Clip curve drawing to the chart area (prevents scroll-shifted points from overdrawing Y-axis labels)
        using (context.PushClip(new Rect(LeftMargin, 0, chartWidth, chartHeight)))
        {
            if (ShowDownload && DownloadData?.Count >= 2)
            {
                DrawSmoothDataLine(context, DownloadData, _downloadPen, chartWidth, chartHeight, maxValue, scrollFraction, _prevDownloadSpeed);
                DrawSmoothAreaGradient(context, DownloadData, _downloadBrush, chartWidth, chartHeight, maxValue, scrollFraction, _prevDownloadSpeed);
                DrawEndPointIndicator(context, DownloadData, _downloadBrush, chartWidth, chartHeight, maxValue, scrollFraction, _prevDownloadSpeed);
            }

            if (ShowUpload && UploadData?.Count >= 2)
            {
                DrawSmoothDataLine(context, UploadData, _uploadPen, chartWidth, chartHeight, maxValue, scrollFraction, _prevUploadSpeed);
                DrawEndPointIndicator(context, UploadData, _uploadBrush, chartWidth, chartHeight, maxValue, scrollFraction, _prevUploadSpeed);
            }
        }

        // X-axis time labels (drawn after curves so label area stays clean)
        var xAxisData = (ShowDownload && DownloadData?.Count >= 2) ? DownloadData : UploadData;
        DrawXAxisLabels(context, xAxisData, chartWidth, chartHeight, scrollFraction);

        // Crosshair overlay (drawn last, on top of everything)
        DrawCrosshair(context, chartWidth, chartHeight, maxValue, scrollFraction);
    }

    private void DrawGridLines(DrawingContext context, double width, double height)
    {
        const int horizontalLines = 4;

        for (int i = 1; i < horizontalLines; i++)
        {
            var y = height * i / horizontalLines;
            context.DrawLine(_gridPen, new Point(LeftMargin, y), new Point(LeftMargin + width, y));
        }
    }

    private void DrawYAxisLabels(DrawingContext context, double height, long maxValue)
    {
        const int labelCount = 4;
        var typeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Normal);

        for (int i = 0; i <= labelCount; i++)
        {
            var value = maxValue * (labelCount - i) / labelCount;
            var y = height * i / labelCount;
            var label = FormatSpeed(value);

            var formattedText = new FormattedText(
                label,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                10,
                _labelBrush);

            var textX = LeftMargin - formattedText.Width - 8;
            var textY = y - formattedText.Height / 2;

            if (textY < 0) textY = 0;
            if (textY + formattedText.Height > height) textY = height - formattedText.Height;

            context.DrawText(formattedText, new Point(textX, textY));
        }
    }

    private string FormatSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond >= 1_000_000_000)
            return $"{bytesPerSecond / 1_000_000_000.0:F1} GB/s";
        if (bytesPerSecond >= 1_000_000)
            return $"{bytesPerSecond / 1_000_000.0:F1} MB/s";
        if (bytesPerSecond >= 1_000)
            return $"{bytesPerSecond / 1_000.0:F0} KB/s";
        return $"{bytesPerSecond} B/s";
    }

    private void DrawXAxisLabels(DrawingContext context, ObservableCollection<SpeedDataPoint>? data,
        double chartWidth, double chartHeight, double scrollFraction)
    {
        if (data == null || data.Count < 2) return;

        var typeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Normal);
        var totalCount = data.Count;
        var renderCount = Math.Min(totalCount, VisiblePoints + 1);
        var startIndex = Math.Max(0, totalCount - renderCount);

        // Time range of visible data
        var firstTime = data[startIndex].Timestamp;
        var lastTime = data[^1].Timestamp;
        var timeSpan = (lastTime - firstTime).TotalSeconds;
        if (timeSpan <= 0) return;

        // Place labels at fixed 15-second clock boundaries (e.g. 14:32:00, 14:32:15, ...)
        const int labelIntervalSec = 15;

        // Find the first 15-second boundary at or after firstTime
        var firstBoundary = new DateTime(
            firstTime.Year, firstTime.Month, firstTime.Day,
            firstTime.Hour, firstTime.Minute, 0);
        while (firstBoundary < firstTime)
            firstBoundary = firstBoundary.AddSeconds(labelIntervalSec);

        // Interpolate fractional position within the time window, accounting for horizLerp
        var horizLerp = 1.0 - scrollFraction;
        var stepX = chartWidth / (VisiblePoints - 1);
        var indexOffset = renderCount - VisiblePoints;

        for (var t = firstBoundary; t <= lastTime; t = t.AddSeconds(labelIntervalSec))
        {
            // Map time to fractional data index
            var timeFrac = (t - firstTime).TotalSeconds / timeSpan;
            var dataIdx = timeFrac * (renderCount - 1);

            // Map data index to screen x (same formula as GetInterpolatedPoints)
            var visibleIndex = dataIdx - indexOffset;
            var x = LeftMargin + (visibleIndex + horizLerp) * stepX;

            if (x < LeftMargin - 5 || x > LeftMargin + chartWidth + 5) continue;

            var label = t.ToString("HH:mm:ss");

            var formattedText = new FormattedText(
                label,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                10,
                _labelBrush);

            var textX = x - formattedText.Width / 2;
            var textY = chartHeight + 4;

            if (textX < LeftMargin - 5) continue;
            if (textX + formattedText.Width > LeftMargin + chartWidth + 5) continue;

            context.DrawText(formattedText, new Point(textX, textY));
        }
    }

    /// <summary>
    /// Draw data line using Catmull-Rom spline for smooth curves
    /// </summary>
    private void DrawSmoothDataLine(DrawingContext context, ObservableCollection<SpeedDataPoint> data,
        IPen pen, double width, double height, double maxValue, double scrollFraction, long prevSpeed)
    {
        var points = GetInterpolatedPoints(data, width, height, maxValue, scrollFraction, prevSpeed);
        if (points.Count < 2) return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false);
            for (int i = 1; i < points.Count; i++)
            {
                ctx.LineTo(points[i]);
            }
        }

        context.DrawGeometry(null, pen, geometry);
    }

    /// <summary>
    /// Draw area gradient using smooth curve
    /// </summary>
    private void DrawSmoothAreaGradient(DrawingContext context, ObservableCollection<SpeedDataPoint> data,
        IBrush brush, double width, double height, double maxValue, double scrollFraction, long prevSpeed)
    {
        var points = GetInterpolatedPoints(data, width, height, maxValue, scrollFraction, prevSpeed);
        if (points.Count < 2) return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, height), true);
            ctx.LineTo(points[0]);

            for (int i = 1; i < points.Count; i++)
            {
                ctx.LineTo(points[i]);
            }

            ctx.LineTo(new Point(points[^1].X, height));
            ctx.EndFigure(true);
        }

        var brushColor = (brush as SolidColorBrush)?.Color ?? Color.Parse("#F5A623");
        var gradientBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromArgb(32, brushColor.R, brushColor.G, brushColor.B), 0),
                new GradientStop(Color.FromArgb(0, brushColor.R, brushColor.G, brushColor.B), 1)
            }
        };

        context.DrawGeometry(gradientBrush, null, geometry);
    }

    /// <summary>
    /// Generate interpolated points using Catmull-Rom spline
    /// </summary>
    private List<Point> GetInterpolatedPoints(ObservableCollection<SpeedDataPoint> data,
        double width, double height, double maxValue, double scrollFraction, long prevSpeed)
    {
        var result = new List<Point>();
        var totalCount = data.Count;

        if (totalCount < 2) return result;

        // Render VisiblePoints + 1 so there's always a point smoothly exiting the left edge.
        // The extra point is hidden by PushClip but keeps the curve continuous.
        var renderCount = Math.Min(totalCount, VisiblePoints + 1);
        var startIndex = Math.Max(0, totalCount - renderCount);
        var count = renderCount;

        // stepX is based on VisiblePoints so the rightmost 60 fill the chart width
        var stepX = width / (VisiblePoints - 1);

        // horizLerp: when the window shifts, all points glide left by one stepX over 1 second.
        // At scrollFraction=0 (just arrived): points are one stepX right of final position.
        // At scrollFraction=1: points are at their true position.
        // The extra buffer point ensures a point is always smoothly exiting the left edge.
        var horizLerp = 1.0 - scrollFraction;

        // The extra point (index 0) starts to the left of the visible VisiblePoints.
        // We offset so that the last point (index count-1) is anchored at the right edge.
        var indexOffset = count - VisiblePoints; // 0 when filling up, 1 when buffer has extra point

        var screenPoints = new List<Point>(count);

        for (int i = 0; i < count; i++)
        {
            // Position: map visible index to x, with horizLerp for smooth glide
            var visibleIndex = i - indexOffset;
            var x = LeftMargin + (visibleIndex + horizLerp) * stepX;
            var speed = data[startIndex + i].Speed;

            // Smooth the leading edge: lerp last point from previous value
            if (i == count - 1 && prevSpeed > 0)
            {
                speed = (long)(prevSpeed + (speed - prevSpeed) * scrollFraction);
            }

            var normalizedValue = speed / maxValue;
            var y = height - (normalizedValue * height * 0.9);
            screenPoints.Add(new Point(x, Math.Clamp(y, 0, height)));
        }

        // Generate smooth curve using Catmull-Rom spline
        for (int i = 0; i < screenPoints.Count - 1; i++)
        {
            var p0 = i > 0 ? screenPoints[i - 1] : screenPoints[i];
            var p1 = screenPoints[i];
            var p2 = screenPoints[i + 1];
            var p3 = i + 2 < screenPoints.Count ? screenPoints[i + 2] : screenPoints[i + 1];

            for (int j = 0; j < InterpolationSegments; j++)
            {
                var t = j / (double)InterpolationSegments;
                result.Add(CatmullRom(p0, p1, p2, p3, t));
            }
        }

        // Add last point
        result.Add(screenPoints[^1]);

        return result;
    }

    /// <summary>
    /// Catmull-Rom spline interpolation
    /// </summary>
    private static Point CatmullRom(Point p0, Point p1, Point p2, Point p3, double t)
    {
        var t2 = t * t;
        var t3 = t2 * t;

        var x = 0.5 * ((2 * p1.X) +
                       (-p0.X + p2.X) * t +
                       (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 +
                       (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);

        var y = 0.5 * ((2 * p1.Y) +
                       (-p0.Y + p2.Y) * t +
                       (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 +
                       (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);

        return new Point(x, y);
    }

    private (long Speed, DateTime Timestamp)? InterpolateDataAtX(
        ObservableCollection<SpeedDataPoint> data, double chartWidth, double pointerX, double scrollFraction)
    {
        if (data.Count < 2) return null;

        var totalCount = data.Count;
        var renderCount = Math.Min(totalCount, VisiblePoints + 1);
        var startIndex = Math.Max(0, totalCount - renderCount);
        var stepX = chartWidth / (VisiblePoints - 1);
        var horizLerp = 1.0 - scrollFraction;
        var indexOffset = renderCount - VisiblePoints;

        // Convert pointer X back to data index (accounting for horizLerp offset)
        var visibleIndex = (pointerX - LeftMargin) / stepX - horizLerp;
        var dataIdx = visibleIndex + indexOffset;
        if (dataIdx < 0 || dataIdx > renderCount - 1) return null;

        var i0 = (int)Math.Floor(dataIdx);
        i0 = Math.Clamp(i0, 0, renderCount - 1);
        var i1 = Math.Min(i0 + 1, renderCount - 1);
        var frac = dataIdx - i0;

        var speed = (long)(data[startIndex + i0].Speed * (1 - frac) + data[startIndex + i1].Speed * frac);
        var ticks = (long)(data[startIndex + i0].Timestamp.Ticks * (1 - frac) + data[startIndex + i1].Timestamp.Ticks * frac);
        var timestamp = new DateTime(ticks);

        return (speed, timestamp);
    }

    private double SpeedToY(long speed, double chartHeight, double maxValue)
    {
        var normalizedValue = speed / maxValue;
        return Math.Clamp(chartHeight - (normalizedValue * chartHeight * 0.9), 0, chartHeight);
    }

    private void DrawEndPointIndicator(DrawingContext context, ObservableCollection<SpeedDataPoint> data,
        IBrush brush, double width, double height, double maxValue, double scrollFraction, long prevSpeed)
    {
        if (data.Count == 0) return;

        var lastPoint = data[^1];
        var displaySpeed = lastPoint.Speed;
        if (prevSpeed > 0)
            displaySpeed = (long)(prevSpeed + (displaySpeed - prevSpeed) * scrollFraction);
        var normalizedValue = displaySpeed / maxValue;
        var y = height - (normalizedValue * height * 0.9);

        // Newest point anchored at right edge
        var stepX = width / Math.Max(VisiblePoints - 1, 1);
        var x = LeftMargin + (VisiblePoints - 1) * stepX;

        y = Math.Clamp(y, 0, height);

        var color = (brush as SolidColorBrush)?.Color ?? Color.Parse("#06B6D4");
        var glowColor = Color.FromArgb(64, color.R, color.G, color.B);

        // Outer glow
        context.DrawEllipse(new SolidColorBrush(glowColor), null, new Point(x, y), 8, 8);

        // Inner point
        context.DrawEllipse(brush, null, new Point(x, y), 4, 4);
    }

    private void DrawCrosshair(DrawingContext context, double chartWidth, double chartHeight,
        double maxValue, double scrollFraction)
    {
        if (_pointerPosition is not { } pos) return;
        if (pos.X < LeftMargin || pos.X > LeftMargin + chartWidth) return;
        if (pos.Y < 0 || pos.Y > chartHeight) return;

        var typeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Normal);
        var tooltipLines = new List<(FormattedText Text, IBrush? DotBrush)>();

        (long Speed, DateTime Timestamp)? dlData = null;
        (long Speed, DateTime Timestamp)? ulData = null;
        DateTime? timestamp = null;

        if (ShowDownload && DownloadData?.Count >= 2)
        {
            dlData = InterpolateDataAtX(DownloadData, chartWidth, pos.X, scrollFraction);
            if (dlData.HasValue) timestamp = dlData.Value.Timestamp;
        }

        if (ShowUpload && UploadData?.Count >= 2)
        {
            ulData = InterpolateDataAtX(UploadData, chartWidth, pos.X, scrollFraction);
            if (ulData.HasValue && !timestamp.HasValue) timestamp = ulData.Value.Timestamp;
        }

        if (!timestamp.HasValue) return;

        // 1. Vertical dashed line
        context.DrawLine(_crosshairVerticalPen, new Point(pos.X, 0), new Point(pos.X, chartHeight));

        // 2. Horizontal dashed lines + intersection dots
        if (dlData.HasValue)
        {
            var y = SpeedToY(dlData.Value.Speed, chartHeight, maxValue);
            context.DrawLine(_crosshairDownloadPen, new Point(LeftMargin, y), new Point(LeftMargin + chartWidth, y));

            var dlColor = (_downloadBrush as SolidColorBrush)?.Color ?? Color.Parse("#00d9ff");
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(76, dlColor.R, dlColor.G, dlColor.B)),
                null, new Point(pos.X, y), 5, 5);
            context.DrawEllipse(_downloadBrush, null, new Point(pos.X, y), 3, 3);
        }

        if (ulData.HasValue)
        {
            var y = SpeedToY(ulData.Value.Speed, chartHeight, maxValue);
            context.DrawLine(_crosshairUploadPen, new Point(LeftMargin, y), new Point(LeftMargin + chartWidth, y));

            var ulColor = (_uploadBrush as SolidColorBrush)?.Color ?? Color.Parse("#8b5cf6");
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(76, ulColor.R, ulColor.G, ulColor.B)),
                null, new Point(pos.X, y), 5, 5);
            context.DrawEllipse(_uploadBrush, null, new Point(pos.X, y), 3, 3);
        }

        // 3. Build tooltip text lines
        var timeText = new FormattedText(
            timestamp.Value.ToString("HH:mm:ss"),
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, 10, _labelBrush);
        tooltipLines.Add((timeText, null));

        if (dlData.HasValue)
        {
            var dlText = new FormattedText(
                $"\u2193 {FormatSpeed(dlData.Value.Speed)}",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 10, _downloadBrush);
            tooltipLines.Add((dlText, _downloadBrush));
        }

        if (ulData.HasValue)
        {
            var ulText = new FormattedText(
                $"\u2191 {FormatSpeed(ulData.Value.Speed)}",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 10, _uploadBrush);
            tooltipLines.Add((ulText, _uploadBrush));
        }

        // 4. Measure and position tooltip
        const double tooltipPadX = 10;
        const double tooltipPadY = 6;
        const double lineSpacing = 2;
        const double dotSize = 3;
        const double dotMargin = 6;

        var maxTextWidth = 0.0;
        var totalTextHeight = 0.0;
        foreach (var (text, _) in tooltipLines)
        {
            maxTextWidth = Math.Max(maxTextWidth, text.Width);
            totalTextHeight += text.Height + lineSpacing;
        }
        totalTextHeight -= lineSpacing;

        var tooltipWidth = tooltipPadX * 2 + dotMargin + dotSize * 2 + maxTextWidth;
        var tooltipHeight = tooltipPadY * 2 + totalTextHeight;

        var tooltipX = pos.X + 8;
        var tooltipY = pos.Y - tooltipHeight - 8;

        if (tooltipX + tooltipWidth > LeftMargin + chartWidth)
            tooltipX = pos.X - tooltipWidth - 8;
        if (tooltipY < 0)
            tooltipY = pos.Y + 8;

        // Draw tooltip background
        var tooltipRect = new Rect(tooltipX, tooltipY, tooltipWidth, tooltipHeight);
        context.DrawRectangle(_tooltipBackgroundBrush, _tooltipBorderPen,
            new RoundedRect(tooltipRect, 6));

        // Draw tooltip content
        var currentY = tooltipY + tooltipPadY;
        foreach (var (text, dotBrush) in tooltipLines)
        {
            var textX = tooltipX + tooltipPadX;

            if (dotBrush != null)
            {
                var dotY = currentY + text.Height / 2;
                context.DrawEllipse(dotBrush, null, new Point(textX + dotSize, dotY), dotSize, dotSize);
                textX += dotSize * 2 + dotMargin;
            }

            context.DrawText(text, new Point(textX, currentY));
            currentY += text.Height + lineSpacing;
        }
    }

    private double CalculateMaxValue()
    {
        long max = 0;

        // Use the maximum of BOTH smoothed Speed and RawSpeed to ensure scale captures true peaks
        // This prevents graph collapse when EMA-smoothed values are lower than actual peaks
        if (ShowDownload && DownloadData != null && DownloadData.Count > 0)
        {
            max = Math.Max(max, DownloadData.Max(d => Math.Max(d.Speed, d.RawSpeed)));
        }

        if (ShowUpload && UploadData != null && UploadData.Count > 0)
        {
            max = Math.Max(max, UploadData.Max(d => Math.Max(d.Speed, d.RawSpeed)));
        }

        // 20% headroom, minimum 1 MB/s
        return Math.Max(max * 1.2, 1000000);
    }
}
