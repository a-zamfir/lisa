// File: Host.Win/Converters/MarkdownText.cs
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.ComponentModel;
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
        
        private static readonly DependencyProperty ForegroundHookedProperty =
            DependencyProperty.RegisterAttached(
                "ForegroundHooked",
                typeof(bool),
                typeof(MarkdownText),
                new PropertyMetadata(false));

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
            HookForegroundChanges(rtb);
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
                    var pending = GetPendingText(rtb);
                    if (string.Equals(GetLastRenderedText(rtb), pending, StringComparison.Ordinal))
                    {
                        return;
                    }
                    SetLastRenderedText(rtb, pending);
                    SetLastRenderTicks(rtb, DateTime.UtcNow.Ticks);
                    try
                    {
                        rtb.Document = BuildFlowDocument(pending, rtb.Foreground as WpfMedia.Brush);
                    }
                    catch
                    {
                        rtb.Document = BuildPlainDocument(pending, rtb.Foreground as WpfMedia.Brush);
                    }
                };
                SetRenderTimer(rtb, timer);
            }
            timer.Stop();
            timer.Start();
        }

        private static void HookForegroundChanges(WpfControls.RichTextBox rtb)
        {
            if (GetForegroundHooked(rtb)) return;
            SetForegroundHooked(rtb, true);
            var descriptor = DependencyPropertyDescriptor.FromProperty(
                WpfControls.Control.ForegroundProperty, typeof(WpfControls.RichTextBox));
            descriptor?.AddValueChanged(rtb, (_, _) =>
            {
                var current = GetText(rtb);
                if (string.IsNullOrEmpty(current)) return;
                try
                {
                    rtb.Document = BuildFlowDocument(current, rtb.Foreground as WpfMedia.Brush);
                }
                catch
                {
                    rtb.Document = BuildPlainDocument(current, rtb.Foreground as WpfMedia.Brush);
                }
            });
        }

        private static void SetForegroundHooked(DependencyObject element, bool value)
        {
            element.SetValue(ForegroundHookedProperty, value);
        }

        private static bool GetForegroundHooked(DependencyObject element)
        {
            return (bool)element.GetValue(ForegroundHookedProperty);
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

        private static FlowDocument BuildFlowDocument(string text, WpfMedia.Brush? foreground)
        {
            if (text.Length > MaxMarkdownLength)
            {
                return BuildPlainDocument(text, foreground);
            }

            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new WpfMedia.FontFamily("Segoe UI"),
                FontSize = 14
            };
            if (foreground != null)
            {
                doc.Foreground = foreground;
            }

            var lines = text.Replace("\r\n", "\n").Split('\n');
            var inCodeBlock = false;

            foreach (var raw in lines)
            {
                var line = raw ?? string.Empty;

                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    inCodeBlock = !inCodeBlock;
                    continue;
                }

                if (inCodeBlock)
                {
                    doc.Blocks.Add(BuildCodeLine(line, foreground));
                    continue;
                }

                if (TryParseHeading(line, out var headingLevel, out var headingText))
                {
                    doc.Blocks.Add(BuildHeading(headingLevel, headingText, foreground));
                    continue;
                }

                if (IsHorizontalRule(line))
                {
                    doc.Blocks.Add(BuildHorizontalRule(foreground));
                    continue;
                }

                if (TryParseListItem(line, out var itemText))
                {
                    doc.Blocks.Add(BuildListItem(itemText, foreground));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 6, 0, 6) });
                    continue;
                }

                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                foreach (var inline in ParseInline(line, foreground))
                {
                    paragraph.Inlines.Add(inline);
                }
                doc.Blocks.Add(paragraph);
            }

            return doc;
        }

        private static Block BuildCodeLine(string line, WpfMedia.Brush? foreground)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
            var run = new Run(line)
            {
                FontFamily = new WpfMedia.FontFamily("Consolas"),
                FontSize = 13
            };
            if (foreground != null)
            {
                run.Foreground = foreground;
            }
            paragraph.Inlines.Add(run);
            return paragraph;
        }

        private static bool TryParseHeading(string line, out int level, out string text)
        {
            level = 0;
            text = string.Empty;
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var i = 0;
            while (i < line.Length && line[i] == '#')
            {
                i++;
            }

            if (i is < 1 or > 6)
            {
                return false;
            }

            if (i < line.Length && line[i] == ' ')
            {
                level = i;
                text = line[(i + 1)..].Trim();
                return !string.IsNullOrWhiteSpace(text);
            }

            return false;
        }

        private static Block BuildHeading(int level, string text, WpfMedia.Brush? foreground)
        {
            var size = level switch
            {
                1 => 20.0,
                2 => 18.0,
                3 => 16.0,
                _ => 15.0
            };

            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 8, 0, 4),
                FontSize = size,
                FontWeight = FontWeights.SemiBold
            };

            foreach (var inline in ParseInline(text, foreground))
            {
                paragraph.Inlines.Add(inline);
            }

            return paragraph;
        }

        private static bool IsHorizontalRule(string line)
        {
            var trimmed = (line ?? string.Empty).Trim();
            return trimmed is "---" or "***" or "___";
        }

        private static Block BuildHorizontalRule(WpfMedia.Brush? foreground)
        {
            var brush = CreateRuleBrush(foreground);
            var border = new WpfControls.Border
            {
                Height = 1,
                Background = brush,
                Margin = new Thickness(0, 10, 0, 10)
            };
            return new BlockUIContainer(border);
        }

        private static WpfMedia.Brush CreateRuleBrush(WpfMedia.Brush? foreground)
        {
            if (foreground is WpfMedia.SolidColorBrush solid)
            {
                var color = solid.Color;
                var rule = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(70, color.R, color.G, color.B));
                rule.Freeze();
                return rule;
            }

            return WpfMedia.Brushes.Gray;
        }

        private static bool TryParseListItem(string line, out string itemText)
        {
            itemText = string.Empty;
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith("- ", StringComparison.Ordinal) || trimmedStart.StartsWith("* ", StringComparison.Ordinal))
            {
                itemText = trimmedStart[2..].TrimEnd();
                return true;
            }

            return false;
        }

        private static Block BuildListItem(string itemText, WpfMedia.Brush? foreground)
        {
            var paragraph = new Paragraph { Margin = new Thickness(14, 0, 0, 2) };
            var bullet = new Run("• ")
            {
                FontWeight = FontWeights.SemiBold
            };
            if (foreground != null)
            {
                bullet.Foreground = foreground;
            }
            paragraph.Inlines.Add(bullet);
            foreach (var inline in ParseInline(itemText, foreground))
            {
                paragraph.Inlines.Add(inline);
            }
            return paragraph;
        }

        private static FlowDocument BuildPlainDocument(string text, WpfMedia.Brush? foreground)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new WpfMedia.FontFamily("Segoe UI"),
                FontSize = 14
            };
            if (foreground != null)
            {
                doc.Foreground = foreground;
            }
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }
                var run = new Run(lines[index]);
                if (foreground != null)
                {
                    run.Foreground = foreground;
                }
                paragraph.Inlines.Add(run);
            }
            doc.Blocks.Add(paragraph);
            return doc;
        }

        private static IEnumerable<Inline> ParseInline(string text, WpfMedia.Brush? foreground)
        {
            var inlines = new List<Inline>();
            var i = 0;
            while (i < text.Length)
            {
                // Bold: **text**
                if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > i + 2)
                    {
                        var content = text.Substring(i + 2, end - (i + 2));
                        var run = new Run(content);
                        if (foreground != null)
                        {
                            run.Foreground = foreground;
                        }
                        inlines.Add(new Bold(run) { FontWeight = FontWeights.SemiBold });
                        i = end + 2;
                        continue;
                    }

                    // Unmatched opener: treat literally and advance.
                    inlines.Add(new Run("*"));
                    i += 1;
                    continue;
                }

                // Italic: *text*
                if (text[i] == '*')
                {
                    var end = text.IndexOf("*", i + 1, StringComparison.Ordinal);
                    if (end > i + 1)
                    {
                        var content = text.Substring(i + 1, end - (i + 1));
                        var run = new Run(content);
                        if (foreground != null)
                        {
                            run.Foreground = foreground;
                        }
                        inlines.Add(new Italic(run));
                        i = end + 1;
                        continue;
                    }

                    inlines.Add(new Run("*"));
                    i += 1;
                    continue;
                }

                // Inline code: `text`
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
                        if (foreground != null)
                        {
                            run.Foreground = foreground;
                        }
                        inlines.Add(run);
                        i = end + 1;
                        continue;
                    }

                    inlines.Add(new Run("`"));
                    i += 1;
                    continue;
                }

                // Plain chunk until the next markdown marker.
                var next = i;
                while (next < text.Length && text[next] != '*' && text[next] != '`')
                {
                    next++;
                }

                if (next > i)
                {
                    var run = new Run(text.Substring(i, next - i));
                    if (foreground != null)
                    {
                        run.Foreground = foreground;
                    }
                    inlines.Add(run);
                    i = next;
                    continue;
                }

                // Safety: always advance to avoid infinite loops.
                inlines.Add(new Run(text[i].ToString()));
                i += 1;
            }

            return inlines;
        }
    }
}
