// File: Host.Win/Converters/MarkdownText.cs
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using WpfControls = System.Windows.Controls;
using System.Windows.Documents;
using WpfMedia = System.Windows.Media;

namespace Host.Win.Converters
{
    public static class MarkdownText
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached(
                "Text",
                typeof(string),
                typeof(MarkdownText),
                new PropertyMetadata(string.Empty, OnTextChanged));

        private static readonly DependencyProperty PendingTextProperty =
            DependencyProperty.RegisterAttached(
                "PendingText",
                typeof(string),
                typeof(MarkdownText),
                new PropertyMetadata(string.Empty));

        private static readonly DependencyProperty RenderTimerProperty =
            DependencyProperty.RegisterAttached(
                "RenderTimer",
                typeof(DispatcherTimer),
                typeof(MarkdownText),
                new PropertyMetadata(null));

        private static readonly DependencyProperty LastRenderedTextProperty =
            DependencyProperty.RegisterAttached(
                "LastRenderedText",
                typeof(string),
                typeof(MarkdownText),
                new PropertyMetadata(string.Empty));

        private static readonly DependencyProperty LastRenderTicksProperty =
            DependencyProperty.RegisterAttached(
                "LastRenderTicks",
                typeof(long),
                typeof(MarkdownText),
                new PropertyMetadata(0L));

        private const int MaxMarkdownLength = 4000;

        public static void SetText(DependencyObject element, string value)
        {
            element.SetValue(TextProperty, value);
        }

        public static string GetText(DependencyObject element)
        {
            return (string)element.GetValue(TextProperty);
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not WpfControls.RichTextBox rtb) return;
            rtb.IsReadOnly = true;
            rtb.IsUndoEnabled = false;
            var text = e.NewValue as string ?? string.Empty;
            if (string.Equals(GetLastRenderedText(rtb), text, StringComparison.Ordinal))
            {
                return;
            }
            SetPendingText(rtb, text);
            var timer = GetRenderTimer(rtb);
            if (timer == null)
            {
                timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(120)
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (!rtb.IsVisible)
                    {
                        return;
                    }
                    var pending = GetPendingText(rtb);
                    if (string.Equals(GetLastRenderedText(rtb), pending, StringComparison.Ordinal))
                    {
                        return;
                    }
                    SetLastRenderedText(rtb, pending);
                    SetLastRenderTicks(rtb, DateTime.UtcNow.Ticks);
                    try
                    {
                        rtb.Document = BuildFlowDocument(pending);
                    }
                    catch
                    {
                        rtb.Document = BuildPlainDocument(pending);
                    }
                };
                SetRenderTimer(rtb, timer);
            }
            timer.Stop();
            timer.Start();
        }

        private static void SetPendingText(DependencyObject element, string value)
        {
            element.SetValue(PendingTextProperty, value);
        }

        private static string GetPendingText(DependencyObject element)
        {
            return (string)element.GetValue(PendingTextProperty);
        }

        private static void SetRenderTimer(DependencyObject element, DispatcherTimer value)
        {
            element.SetValue(RenderTimerProperty, value);
        }

        private static DispatcherTimer? GetRenderTimer(DependencyObject element)
        {
            return (DispatcherTimer?)element.GetValue(RenderTimerProperty);
        }

        private static void SetLastRenderedText(DependencyObject element, string value)
        {
            element.SetValue(LastRenderedTextProperty, value);
        }

        private static string GetLastRenderedText(DependencyObject element)
        {
            return (string)element.GetValue(LastRenderedTextProperty);
        }

        private static void SetLastRenderTicks(DependencyObject element, long value)
        {
            element.SetValue(LastRenderTicksProperty, value);
        }

        private static FlowDocument BuildFlowDocument(string text)
        {
            if (text.Length > MaxMarkdownLength)
            {
                return BuildPlainDocument(text);
            }

            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new WpfMedia.FontFamily("Segoe UI"),
                FontSize = 14
            };

            var lines = text.Replace("\r\n", "\n").Split('\n');
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }
                foreach (var inline in ParseInline(lines[index]))
                {
                    paragraph.Inlines.Add(inline);
                }
            }
            doc.Blocks.Add(paragraph);

            return doc;
        }

        private static FlowDocument BuildPlainDocument(string text)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new WpfMedia.FontFamily("Segoe UI"),
                FontSize = 14
            };
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }
                paragraph.Inlines.Add(new Run(lines[index]));
            }
            doc.Blocks.Add(paragraph);
            return doc;
        }

        private static IEnumerable<Inline> ParseInline(string text)
        {
            var inlines = new List<Inline>();
            var i = 0;
            while (i < text.Length)
            {
                if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > i + 2)
                    {
                        var content = text.Substring(i + 2, end - (i + 2));
                        var bold = new Bold(new Run(content)) { FontWeight = FontWeights.SemiBold };
                        inlines.Add(bold);
                        i = end + 2;
                        continue;
                    }
                }
                if (text[i] == '*')
                {
                    var end = text.IndexOf("*", i + 1, StringComparison.Ordinal);
                    if (end > i + 1)
                    {
                        var content = text.Substring(i + 1, end - (i + 1));
                        var italic = new Italic(new Run(content));
                        inlines.Add(italic);
                        i = end + 1;
                        continue;
                    }
                }
                if (text[i] == '`')
                {
                    var end = text.IndexOf("`", i + 1, StringComparison.Ordinal);
                    if (end > i + 1)
                    {
                        var content = text.Substring(i + 1, end - (i + 1));
                        var run = new Run(content)
                        {
                            FontFamily = new WpfMedia.FontFamily("Consolas")
                        };
                        inlines.Add(run);
                        i = end + 1;
                        continue;
                    }
                }

                var sb = new StringBuilder();
                while (i < text.Length)
                {
                    if (text[i] == '*' || text[i] == '`')
                    {
                        break;
                    }
                    sb.Append(text[i]);
                    i++;
                }
                if (sb.Length > 0)
                {
                    inlines.Add(new Run(sb.ToString()));
                }
            }

            return inlines;
        }
    }
}
