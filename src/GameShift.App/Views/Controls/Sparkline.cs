using System;
using System.Windows;
using System.Windows.Media;
using GameShift.Core.Presentation;

namespace GameShift.App.Views.Controls;

/// <summary>
/// A light line chart of a <see cref="SparklineBuffer"/>. It draws one frozen geometry and rebuilds it
/// only when a sample arrives while it is visible; scaling follows <see cref="SparklineScale"/>.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty BufferProperty = DependencyProperty.Register(
        nameof(Buffer), typeof(SparklineBuffer), typeof(Sparkline),
        new PropertyMetadata(null, OnBufferChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    /// <summary>The top of the scale unless a higher sample is shown.</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    private Geometry? _geometry;
    private Pen? _pen;
    private bool _dirty = true;
    private bool _attached;

    public Sparkline()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public SparklineBuffer? Buffer
    {
        get => (SparklineBuffer?)GetValue(BufferProperty);
        set => SetValue(BufferProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    private static void OnBufferChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (Sparkline)d;
        if (chart._attached && e.OldValue is SparklineBuffer old) old.Changed -= chart.OnSamplesChanged;
        if (chart._attached && e.NewValue is SparklineBuffer now) now.Changed += chart.OnSamplesChanged;
        chart.MarkDirty();
    }

    private static void OnLookChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (Sparkline)d;
        chart._pen = null;
        chart._dirty = true;
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        if (Buffer != null) Buffer.Changed += OnSamplesChanged;
        MarkDirty();
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        if (Buffer != null) Buffer.Changed -= OnSamplesChanged;
    }

    private void OnSamplesChanged(object? sender, EventArgs e) => MarkDirty();

    private void MarkDirty()
    {
        _dirty = true;
        if (IsVisible) InvalidateVisual();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && _dirty) InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _dirty = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_dirty)
        {
            _geometry = BuildGeometry();
            _dirty = false;
        }
        if (_geometry == null || Stroke == null) return;

        if (_pen == null)
        {
            _pen = new Pen(Stroke, StrokeThickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            if (_pen.CanFreeze) _pen.Freeze();
        }
        drawingContext.DrawGeometry(null, _pen, _geometry);
    }

    private Geometry? BuildGeometry()
    {
        var buffer = Buffer;
        if (buffer == null || buffer.Count == 0) return null;

        double inset = StrokeThickness / 2;
        var segments = SparklineScale.Segments(
            buffer.Snapshot(), buffer.Capacity, ActualWidth, ActualHeight, Minimum, Maximum, inset);
        if (segments.Count == 0) return null;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var run in segments)
            {
                var first = run[0];
                if (run.Count == 1)
                {
                    // A lone reading between gaps: a short stroke, which the round caps draw as a dot.
                    context.BeginFigure(new Point(first.X - inset, first.Y), false, false);
                    context.LineTo(new Point(first.X + inset, first.Y), true, false);
                    continue;
                }

                context.BeginFigure(new Point(first.X, first.Y), false, false);
                for (int i = 1; i < run.Count; i++)
                    context.LineTo(new Point(run[i].X, run[i].Y), true, true);
            }
        }
        geometry.Freeze();
        return geometry;
    }
}
