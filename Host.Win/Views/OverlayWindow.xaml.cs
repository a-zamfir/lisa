// File: Host.Win/Views/OverlayWindow.xaml.cs
using System.ComponentModel;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Collections.Specialized;
using System.Windows.Interop;
using Host.Win.Models;
using Host.Win.Services;
using Host.Win.ViewModels;

namespace Host.Win.Views
{
    /// <summary>
    /// Overlay stays topmost and positions at bottom-right. Avoids force-activation to reduce focus theft.
    /// </summary>
    public partial class OverlayWindow : Window
    {
        public bool AllowClose { get; set; }
        private OverlayViewModel? _viewModel;
        private Storyboard? _listeningStoryboard;
        private Storyboard? _processingStoryboard;
        private Storyboard? _speakingStoryboard;
        private IntPtr _hwnd;

        public OverlayWindow()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        public void ShowAtBottomRight(Screen screen, double margin)
        {
            var workingArea = screen.WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);

            var dipRight = workingArea.Right / dpi.DpiScaleX;
            var dipBottom = workingArea.Bottom / dpi.DpiScaleY;

            Left = dipRight - Width - margin;
            Top = dipBottom - Height - margin;
        }

        public void PlayShowAnimation()
        {
            // Subtle slide-up + fade for a minimal, sleek entrance.
            Opacity = 0;
            if (RootTranslate != null)
            {
                RootTranslate.Y = 12;
            }

            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var slide = new DoubleAnimation
            {
                From = 12,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            BeginAnimation(OpacityProperty, fade);
            RootTranslate?.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        public void PlayHideAnimation(System.Action? onCompleted = null)
        {
            // Reverse of show: fade out and slide down slightly, then hide.
            var fade = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(140),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            var slide = new DoubleAnimation
            {
                From = 0,
                To = 8,
                Duration = TimeSpan.FromMilliseconds(140),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            fade.Completed += (_, _) => onCompleted?.Invoke();

            BeginAnimation(OpacityProperty, fade);
            RootTranslate?.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!AllowClose)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            base.OnClosing(e);
        }

        protected override void OnSourceInitialized(System.EventArgs e)
        {
            base.OnSourceInitialized(e);
            Topmost = true;
            _hwnd = new WindowInteropHelper(this).Handle;
            UpdateBackdrop();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.ChatMessages.CollectionChanged -= ChatMessages_CollectionChanged;
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }

            _viewModel = DataContext as OverlayViewModel;
            if (_viewModel != null)
            {
                _viewModel.ChatMessages.CollectionChanged += ChatMessages_CollectionChanged;
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
                Width = _viewModel.OverlayWidth;
                Height = _viewModel.OverlayHeight;
                UpdateVoiceState();
                UpdateBackdrop();
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_viewModel == null) return;
            if (e.PropertyName == nameof(OverlayViewModel.OverlayWidth)
                || e.PropertyName == nameof(OverlayViewModel.OverlayHeight))
            {
                ApplyResize(_viewModel.OverlayWidth, _viewModel.OverlayHeight);
                return;
            }

            if (e.PropertyName == nameof(OverlayViewModel.CurrentTheme))
            {
                UpdateBackdrop();
                return;
            }

            if (e.PropertyName == nameof(OverlayViewModel.IsListening)
                || e.PropertyName == nameof(OverlayViewModel.IsProcessing)
                || e.PropertyName == nameof(OverlayViewModel.IsSpeaking))
            {
                UpdateVoiceState();
            }
        }

        private void ApplyResize(double targetWidth, double targetHeight)
        {
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Width = targetWidth;
            Height = targetHeight;
            if (IsVisible)
            {
                ShowAtBottomRight(Screen.PrimaryScreen, 16);
            }
        }


        private void ChatMessages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                ChatScrollViewer?.ScrollToEnd();
            });
        }

        private void UpdateVoiceState()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(UpdateVoiceState);
                return;
            }

            if (_viewModel == null) return;
            EnsureVoiceStoryboards();
            StopVoiceStoryboards();
            StopProcessingAnimation();

            if (_viewModel.IsSpeaking)
            {
                ApplyVoiceVisuals(showListening: false, showProcessing: false, showSpeaking: true);
                _speakingStoryboard?.Begin();
            }
            else if (_viewModel.IsProcessing)
            {
                ApplyVoiceVisuals(showListening: false, showProcessing: true, showSpeaking: false);
                StartProcessingAnimation();
            }
            else if (_viewModel.IsListening)
            {
                ApplyVoiceVisuals(showListening: true, showProcessing: false, showSpeaking: false);
                _listeningStoryboard?.Begin();
            }
            else
            {
                ApplyVoiceVisuals(showListening: false, showProcessing: false, showSpeaking: false);
            }
        }

        private void UpdateBackdrop()
        {
            var useGlass = _viewModel?.CurrentTheme == AppTheme.Glass;
            WindowBackdropService.ApplyGlass(_hwnd, useGlass);
        }

        private void EnsureVoiceStoryboards()
        {
            if (_listeningStoryboard != null && _processingStoryboard != null && _speakingStoryboard != null)
            {
                return;
            }

            if (MicCluster == null || ListeningGlow == null || ThinkingRing == null || ThinkingTrail == null || ThinkingTrailRotate == null
                || SpeakingBars == null || SpeakingGlow == null || MicIcon == null)
            {
                return;
            }

            var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

            _listeningStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var scaleX = new DoubleAnimation(1, 1.08, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, EasingFunction = ease };
            var scaleY = new DoubleAnimation(1, 1.08, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, EasingFunction = ease };
            var glowPulse = new DoubleAnimation(0.1, 0.35, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, EasingFunction = ease };
            Storyboard.SetTarget(scaleX, MicCluster);
            Storyboard.SetTarget(scaleY, MicCluster);
            Storyboard.SetTarget(glowPulse, ListeningGlow);
            Storyboard.SetTargetProperty(scaleX, new PropertyPath("RenderTransform.ScaleX"));
            Storyboard.SetTargetProperty(scaleY, new PropertyPath("RenderTransform.ScaleY"));
            Storyboard.SetTargetProperty(glowPulse, new PropertyPath(UIElement.OpacityProperty));
            _listeningStoryboard.Children.Add(scaleX);
            _listeningStoryboard.Children.Add(scaleY);
            _listeningStoryboard.Children.Add(glowPulse);

            _processingStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var ringFade = new DoubleAnimation(0.2, 0.5, TimeSpan.FromSeconds(1.2)) { AutoReverse = true };
            var trailFade = new DoubleAnimation(0.6, 1.0, TimeSpan.FromSeconds(1.2)) { AutoReverse = true };
            var rotate = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever };
            Storyboard.SetTarget(ringFade, ThinkingRing);
            Storyboard.SetTarget(trailFade, ThinkingTrail);
            Storyboard.SetTarget(rotate, ThinkingTrailRotate);
            Storyboard.SetTargetProperty(ringFade, new PropertyPath(UIElement.OpacityProperty));
            Storyboard.SetTargetProperty(trailFade, new PropertyPath(UIElement.OpacityProperty));
            Storyboard.SetTargetProperty(rotate, new PropertyPath(RotateTransform.AngleProperty));
            _processingStoryboard.Children.Add(ringFade);
            _processingStoryboard.Children.Add(trailFade);
            _processingStoryboard.Children.Add(rotate);

            _speakingStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var bar1 = new DoubleAnimation(0.4, 1.2, TimeSpan.FromSeconds(0.4)) { AutoReverse = true };
            var bar2 = new DoubleAnimation(0.3, 1.4, TimeSpan.FromSeconds(0.5)) { AutoReverse = true };
            var bar3 = new DoubleAnimation(0.5, 1.1, TimeSpan.FromSeconds(0.45)) { AutoReverse = true };
            Storyboard.SetTarget(bar1, SpeakBar1);
            Storyboard.SetTarget(bar2, SpeakBar2);
            Storyboard.SetTarget(bar3, SpeakBar3);
            Storyboard.SetTargetProperty(bar1, new PropertyPath("RenderTransform.ScaleY"));
            Storyboard.SetTargetProperty(bar2, new PropertyPath("RenderTransform.ScaleY"));
            Storyboard.SetTargetProperty(bar3, new PropertyPath("RenderTransform.ScaleY"));
            _speakingStoryboard.Children.Add(bar1);
            _speakingStoryboard.Children.Add(bar2);
            _speakingStoryboard.Children.Add(bar3);
        }

        private void StopVoiceStoryboards()
        {
            _listeningStoryboard?.Stop();
            _processingStoryboard?.Stop();
            _speakingStoryboard?.Stop();
        }

        private void StartProcessingAnimation()
        {
            if (ThinkingRing == null || ThinkingTrail == null || ThinkingTrailRotate == null)
            {
                return;
            }

            var ring = new DoubleAnimation(0.2, 0.55, TimeSpan.FromSeconds(1.2))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            ThinkingRing.BeginAnimation(UIElement.OpacityProperty, ring);

            var trail = new DoubleAnimation(0.6, 1.0, TimeSpan.FromSeconds(1.2))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            ThinkingTrail.BeginAnimation(UIElement.OpacityProperty, trail);

            var rotate = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            ThinkingTrailRotate.BeginAnimation(RotateTransform.AngleProperty, rotate);
        }

        private void StopProcessingAnimation()
        {
            ThinkingRing?.BeginAnimation(UIElement.OpacityProperty, null);
            ThinkingTrail?.BeginAnimation(UIElement.OpacityProperty, null);
            ThinkingTrailRotate?.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        private void ApplyVoiceVisuals(bool showListening, bool showProcessing, bool showSpeaking)
        {
            if (ListeningGlow != null)
            {
                ListeningGlow.Opacity = showListening ? 0.2 : 0;
            }

            if (ThinkingRing != null)
            {
                ThinkingRing.Opacity = showProcessing ? 0.4 : 0;
            }

            if (ThinkingTrail != null)
            {
                ThinkingTrail.Opacity = showProcessing ? 1 : 0;
            }

            if (SpeakingBars != null)
            {
                SpeakingBars.Opacity = showSpeaking ? 1 : 0;
            }

            if (SpeakingGlow != null)
            {
                SpeakingGlow.Opacity = showSpeaking ? 0.35 : 0;
            }

            if (MicIcon != null)
            {
                MicIcon.Opacity = showSpeaking ? 0 : 1;
            }
        }
    }
}
