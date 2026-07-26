using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace RetroPLC.Shell.Controls;

/// <summary>
/// Adds Visual Studio-style hover previews to Dock's pinned tool tabs.
/// Dock provides the pinning model and overlay; this behavior supplies the
/// hover gesture, delayed dismissal, and a short slide/fade transition.
/// </summary>
public sealed class PinnedDockHoverBehavior : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<PinnedDockHoverBehavior, DockControl, bool>("IsEnabled");

    private static readonly ConditionalWeakTable<DockControl, HoverState> States = new();

    static PinnedDockHoverBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<DockControl>((control, change) =>
        {
            if (change.NewValue is true)
                States.GetValue(control, static dock => new HoverState(dock)).Attach();
            else if (States.TryGetValue(control, out var state))
                state.Detach();
        });
    }

    public static bool GetIsEnabled(AvaloniaObject element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(AvaloniaObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private sealed class HoverState
    {
        private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(350);
        private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(170);

        private readonly DockControl _dockControl;
        private readonly DispatcherTimer _closeTimer;
        private readonly DispatcherTimer _animationTimer;
        private IDockable? _previewedDockable;
        private PinnedDockControl? _flyout;
        private TranslateTransform? _translation;
        private DateTime _animationStarted;
        private double _startX;
        private double _startY;
        private bool _isAttached;
        private bool _isClosing;

        public HoverState(DockControl dockControl)
        {
            _dockControl = dockControl;
            _closeTimer = new DispatcherTimer { Interval = CloseDelay };
            _closeTimer.Tick += CloseTimer_OnTick;
            _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _animationTimer.Tick += AnimationTimer_OnTick;
        }

        public void Attach()
        {
            if (_isAttached)
                return;

            _dockControl.AddHandler(
                InputElement.PointerMovedEvent,
                DockControl_OnPointerMoved,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            _dockControl.AddHandler(
                InputElement.PointerPressedEvent,
                DockControl_OnPointerPressed,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            _dockControl.AddHandler(
                InputElement.PointerReleasedEvent,
                DockControl_OnPointerReleased,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            _dockControl.PointerExited += DockControl_OnPointerExited;
            _isAttached = true;
        }

        public void Detach()
        {
            if (!_isAttached)
                return;

            _dockControl.RemoveHandler(InputElement.PointerMovedEvent, DockControl_OnPointerMoved);
            _dockControl.RemoveHandler(InputElement.PointerPressedEvent, DockControl_OnPointerPressed);
            _dockControl.RemoveHandler(InputElement.PointerReleasedEvent, DockControl_OnPointerReleased);
            _dockControl.PointerExited -= DockControl_OnPointerExited;
            _closeTimer.Stop();
            _animationTimer.Stop();
            _isAttached = false;
        }

        private void DockControl_OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (e.Source is not Visual source)
                return;

            var ancestors = source.GetSelfAndVisualAncestors().ToList();
            var pinItem = ancestors.OfType<ToolPinItemControl>().FirstOrDefault();
            if (pinItem?.DataContext is IDockable dockable)
            {
                CancelClose();
                Preview(dockable);
                return;
            }

            if (IsPointerInsideFlyout(e))
            {
                CancelClose();
                return;
            }

            ScheduleClose();
        }

        private void DockControl_OnPointerExited(object? sender, PointerEventArgs e) => ScheduleClose();

        private void DockControl_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (IsPointerInsideFlyout(e))
            {
                // Keep the overlay and its chrome alive until the button receives
                // PointerReleased/Click. Rebuilding it mid-gesture cancels every
                // title-bar action (menu, pin, maximize, and close).
                CancelClose();
            }
        }

        private void DockControl_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (IsPointerInsideFlyout(e))
                CancelClose();
            else
                ScheduleClose();
        }

        private void Preview(IDockable dockable)
        {
            if (ReferenceEquals(_previewedDockable, dockable) && IsPreviewVisible(dockable))
            {
                CancelClose();
                return;
            }

            CancelClose();
            ClosePreviewImmediately();

            var factory = _dockControl.Layout?.Factory;
            if (factory is null)
                return;

            _flyout = _dockControl.GetVisualDescendants().OfType<PinnedDockControl>().FirstOrDefault();
            var alignment = (dockable.OriginalOwner as IToolDock)?.Alignment
                            ?? (dockable.Owner as IToolDock)?.Alignment
                            ?? Alignment.Left;
            PrepareAnimation(alignment);

            factory.PreviewPinnedDockable(dockable);
            _previewedDockable = dockable;
            Dispatcher.UIThread.Post(StartAnimation, DispatcherPriority.Render);
        }

        private void ScheduleClose()
        {
            if (_previewedDockable is null || _closeTimer.IsEnabled || _isClosing)
                return;

            _closeTimer.Start();
        }

        private void CloseTimer_OnTick(object? sender, EventArgs e)
        {
            _closeTimer.Stop();
            StartCloseAnimation();
        }

        private void CancelClose()
        {
            _closeTimer.Stop();
            if (!_isClosing)
                return;

            _animationTimer.Stop();
            _isClosing = false;
            ResetAnimation();
        }

        private void ClosePreviewImmediately()
        {
            if (_previewedDockable is { } dockable && IsPreviewVisible(dockable))
                _dockControl.Layout?.Factory?.TogglePreviewPinnedDockable(dockable);

            _previewedDockable = null;
            _animationTimer.Stop();
            _isClosing = false;
            ResetAnimation();
        }

        private bool IsPointerInsideFlyout(PointerEventArgs e)
        {
            var content = _flyout?.GetVisualDescendants()
                .OfType<ContentControl>()
                .FirstOrDefault(control => control.Name == "PART_PinnedDock");
            if (content is null || !content.IsVisible || !content.IsEffectivelyVisible)
                return false;

            // Visual ancestry is reliable for every alignment and remains valid
            // while the slide RenderTransform is active. The old coordinate-only
            // check could classify children of the bottom flyout as outside.
            if (e.Source is Visual source &&
                source.GetSelfAndVisualAncestors().Contains(content))
            {
                return true;
            }

            var position = e.GetPosition(content);
            return position.X >= 0 && position.Y >= 0 &&
                   position.X <= content.Bounds.Width && position.Y <= content.Bounds.Height;
        }

        private bool IsPreviewVisible(IDockable dockable) =>
            _dockControl.Layout is IRootDock root &&
            root.PinnedDock?.VisibleDockables?.Contains(dockable) == true;

        private void PrepareAnimation(Alignment alignment)
        {
            if (_flyout is null)
                return;

            const double offset = 22;
            (_startX, _startY) = alignment switch
            {
                Alignment.Right => (offset, 0d),
                Alignment.Top => (0d, -offset),
                Alignment.Bottom => (0d, offset),
                _ => (-offset, 0d)
            };

            _translation = new TranslateTransform(_startX, _startY);
            _flyout.RenderTransform = _translation;
            _flyout.Opacity = 0;
        }

        private void StartAnimation()
        {
            if (_flyout is null || _translation is null || _previewedDockable is null)
                return;

            _animationStarted = DateTime.UtcNow;
            _isClosing = false;
            _animationTimer.Start();
        }

        private void StartCloseAnimation()
        {
            if (_previewedDockable is null)
                return;

            if (_flyout is null || _translation is null || !IsPreviewVisible(_previewedDockable))
            {
                ClosePreviewImmediately();
                return;
            }

            _animationStarted = DateTime.UtcNow;
            _isClosing = true;
            _animationTimer.Start();
        }

        private void AnimationTimer_OnTick(object? sender, EventArgs e)
        {
            if (_flyout is null || _translation is null)
            {
                _animationTimer.Stop();
                return;
            }

            var progress = Math.Clamp(
                (DateTime.UtcNow - _animationStarted).TotalMilliseconds / AnimationDuration.TotalMilliseconds,
                0,
                1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            _translation.X = _isClosing ? _startX * eased : _startX * (1 - eased);
            _translation.Y = _isClosing ? _startY * eased : _startY * (1 - eased);
            _flyout.Opacity = _isClosing ? 1 - eased : eased;

            if (progress >= 1)
            {
                _animationTimer.Stop();
                if (_isClosing)
                    ClosePreviewImmediately();
                else
                    ResetAnimation();
            }
        }

        private void ResetAnimation()
        {
            if (_translation is not null)
            {
                _translation.X = 0;
                _translation.Y = 0;
            }

            if (_flyout is not null)
                _flyout.Opacity = 1;
        }
    }
}
