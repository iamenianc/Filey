using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Filey
{
    /// <summary>
    /// Animates a ListBox's internal ScrollViewer to a target item's offset instead of the
    /// instant jump <see cref="ItemsControl.ScrollIntoView"/> gives, via an attached
    /// VerticalOffset property that proxies to ScrollToVerticalOffset.
    /// </summary>
    public static class SmoothScrollHelper
    {
        public static readonly DependencyProperty VerticalOffsetProperty =
            DependencyProperty.RegisterAttached("VerticalOffset", typeof(double), typeof(SmoothScrollHelper),
                new PropertyMetadata(0.0, OnVerticalOffsetChanged));

        public static double GetVerticalOffset(DependencyObject obj) => (double)obj.GetValue(VerticalOffsetProperty);
        public static void SetVerticalOffset(DependencyObject obj, double value) => obj.SetValue(VerticalOffsetProperty, value);

        private static void OnVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer scrollViewer)
            {
                scrollViewer.ScrollToVerticalOffset((double)e.NewValue);
            }
        }

        /// <summary>Smoothly scrolls <paramref name="item"/> to the top of the ListBox's viewport.</summary>
        public static void ScrollItemToTop(ListBox listBox, object item)
        {
            var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
            if (scrollViewer == null) return;

            var container = listBox.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
            if (container == null)
            {
                // Virtualized and not yet realized: force one instant realization step,
                // then resolve the container and continue with the smooth animation.
                listBox.ScrollIntoView(item);
                listBox.UpdateLayout();
                container = listBox.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
                if (container == null) return;
            }

            var transform = container.TransformToAncestor(scrollViewer);
            double containerTop = transform.Transform(new Point(0, 0)).Y;
            double targetOffset = scrollViewer.VerticalOffset + containerTop;
            targetOffset = System.Math.Max(0, System.Math.Min(scrollViewer.ScrollableHeight, targetOffset));

            var animation = new DoubleAnimation
            {
                From = scrollViewer.VerticalOffset,
                To = targetOffset,
                Duration = new Duration(System.TimeSpan.FromMilliseconds(220)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            SetVerticalOffset(scrollViewer, scrollViewer.VerticalOffset);
            scrollViewer.BeginAnimation(VerticalOffsetProperty, animation);
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild) return typedChild;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }
    }
}
