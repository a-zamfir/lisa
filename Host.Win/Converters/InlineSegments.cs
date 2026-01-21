// File: Host.Win/Converters/InlineSegments.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Host.Win.Models;

namespace Host.Win.Converters
{
    public static class InlineSegments
    {
        public static readonly DependencyProperty SegmentsProperty =
            DependencyProperty.RegisterAttached(
                "Segments",
                typeof(IEnumerable),
                typeof(InlineSegments),
                new PropertyMetadata(null, OnSegmentsChanged));

        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached(
                "State",
                typeof(SegmentsState),
                typeof(InlineSegments),
                new PropertyMetadata(null));

        public static void SetSegments(DependencyObject element, IEnumerable value)
        {
            element.SetValue(SegmentsProperty, value);
        }

        public static IEnumerable GetSegments(DependencyObject element)
        {
            return (IEnumerable)element.GetValue(SegmentsProperty);
        }

        private static SegmentsState? GetState(DependencyObject element)
        {
            return (SegmentsState?)element.GetValue(StateProperty);
        }

        private static void SetState(DependencyObject element, SegmentsState? value)
        {
            element.SetValue(StateProperty, value);
        }

        private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock tb)
            {
                return;
            }

            var state = GetState(tb);
            state?.Detach(tb);
            tb.Inlines.Clear();

            if (e.NewValue is not IEnumerable segments)
            {
                SetState(tb, null);
                return;
            }

            state = new SegmentsState(segments);
            SetState(tb, state);
            state.Attach(tb);
        }

        private sealed class SegmentsState
        {
            private readonly IEnumerable _segments;
            private readonly Dictionary<ChatContentSegment, Run> _runsBySegment = new();
            private readonly Dictionary<ChatContentSegment, PropertyChangedEventHandler> _segmentHandlers = new();
            private INotifyCollectionChanged? _collection;
            private NotifyCollectionChangedEventHandler? _collectionHandler;
            private RoutedEventHandler? _unloadedHandler;

            public SegmentsState(IEnumerable segments)
            {
                _segments = segments;
            }

            public void Attach(TextBlock tb)
            {
                Rebuild(tb);

                if (_segments is INotifyCollectionChanged notify)
                {
                    _collection = notify;
                    _collectionHandler = (_, args) => OnCollectionChanged(tb, args);
                    _collection.CollectionChanged += _collectionHandler;
                }

                _unloadedHandler = (_, _) => Detach(tb);
                tb.Unloaded += _unloadedHandler;
            }

            public void Detach(TextBlock tb)
            {
                if (_collection != null && _collectionHandler != null)
                {
                    _collection.CollectionChanged -= _collectionHandler;
                }

                foreach (var (segment, handler) in _segmentHandlers)
                {
                    segment.PropertyChanged -= handler;
                }

                if (_unloadedHandler != null)
                {
                    tb.Unloaded -= _unloadedHandler;
                }

                _collection = null;
                _collectionHandler = null;
                _unloadedHandler = null;
                _segmentHandlers.Clear();
                _runsBySegment.Clear();
                tb.Inlines.Clear();
            }

            private void OnCollectionChanged(TextBlock tb, NotifyCollectionChangedEventArgs e)
            {
                if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
                {
                    foreach (var item in e.NewItems)
                    {
                        if (item is ChatContentSegment segment)
                        {
                            Append(tb, segment);
                        }
                    }

                    return;
                }

                Rebuild(tb);
            }

            private void Rebuild(TextBlock tb)
            {
                foreach (var (segment, handler) in _segmentHandlers)
                {
                    segment.PropertyChanged -= handler;
                }

                _segmentHandlers.Clear();
                _runsBySegment.Clear();
                tb.Inlines.Clear();

                foreach (var item in _segments)
                {
                    if (item is ChatContentSegment segment)
                    {
                        Append(tb, segment);
                    }
                }
            }

            private void Append(TextBlock tb, ChatContentSegment segment)
            {
                if (segment.IsApproval || segment.IsBadge)
                {
                    return;
                }

                var run = new Run { Text = segment.Text ?? string.Empty };
                tb.Inlines.Add(run);
                _runsBySegment[segment] = run;

                PropertyChangedEventHandler handler = (_, args) =>
                {
                    if (args.PropertyName == nameof(ChatContentSegment.Text))
                    {
                        run.Text = segment.Text ?? string.Empty;
                    }
                };

                segment.PropertyChanged += handler;
                _segmentHandlers[segment] = handler;
            }
        }
    }
}

