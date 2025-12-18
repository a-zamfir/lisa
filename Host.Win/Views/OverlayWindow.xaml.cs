// File: Host.Win/Views/OverlayWindow.xaml.cs
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Collections.Specialized;
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
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.ChatMessages.CollectionChanged -= ChatMessages_CollectionChanged;
            }

            _viewModel = DataContext as OverlayViewModel;
            if (_viewModel != null)
            {
                _viewModel.ChatMessages.CollectionChanged += ChatMessages_CollectionChanged;
            }
        }

        private void ChatMessages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                ChatScrollViewer?.ScrollToEnd();
            });
        }
    }
}
