// File: Host.Win/Views/ToolApprovalWindow.xaml.cs
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Interop;
using Host.Win.Services;

namespace Host.Win.Views
{
    public partial class ToolApprovalWindow : Window
    {
        private readonly TaskCompletionSource<bool> _tcs = new();
        private bool _isClosing;

        public ToolApprovalWindow(string descriptionText, string paramsText)
        {
            InitializeComponent();
            DescriptionText = descriptionText;
            ParamsText = paramsText;
            DataContext = this;

            ApproveButton.Click += (_, _) => CloseWithResult(true);
            RejectButton.Click += (_, _) => CloseWithResult(false);
            Closed += (_, _) => _tcs.TrySetResult(false);
            Loaded += (_, _) =>
            {
                ApplyBackdrop();
                BeginFade(1, 0.18);
            };
            Closing += OnClosing;
        }

        public string DescriptionText { get; }
        public string ParamsText { get; }

        public Task<bool> WaitForDecisionAsync()
        {
            return _tcs.Task;
        }

        private void CloseWithResult(bool approved)
        {
            _tcs.TrySetResult(approved);
            BeginClose();
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isClosing)
            {
                return;
            }

            e.Cancel = true;
            BeginClose();
        }

        private void BeginClose()
        {
            if (_isClosing)
            {
                return;
            }

            _isClosing = true;
            BeginFade(0, 0.12, () => Close());
        }

        private void BeginFade(double targetOpacity, double seconds, Action? onComplete = null)
        {
            var animation = new DoubleAnimation
            {
                To = targetOpacity,
                Duration = TimeSpan.FromSeconds(seconds),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            if (onComplete != null)
            {
                animation.Completed += (_, _) => onComplete();
            }

            BeginAnimation(OpacityProperty, animation);
        }

        private void ApplyBackdrop()
        {
            var brush = System.Windows.Application.Current.Resources["OverlayBackground"] as SolidColorBrush;
            var useGlass = brush != null && brush.Color.A < 255;
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowBackdropService.ApplyGlass(hwnd, useGlass);
        }
    }
}
