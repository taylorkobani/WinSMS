using System.Collections.Specialized;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinSMS.Models;
using WinSMS.ViewModels;

namespace WinSMS.Views;

public sealed partial class InboxPage : Page
{
    public InboxViewModel ViewModel { get; }

    private SmsConversation? _observedConversation;

    public InboxPage()
    {
        ViewModel = App.Services.GetRequiredService<InboxViewModel>();
        InitializeComponent();

        Loaded += OnLoaded;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
        ObserveSelectedConversation();
        ScrollConversationToEnd();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InboxViewModel.SelectedConversation))
        {
            ObserveSelectedConversation();
            ScrollConversationToEnd();
            FocusReplyTextBox();
        }
    }

    private void FocusReplyTextBox()
    {
        if (ViewModel.SelectedConversation == null)
            return;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ReplyTextBox.Focus(FocusState.Programmatic);
            ReplyTextBox.SelectionStart = ReplyTextBox.Text?.Length ?? 0;
        });
    }

    private void ReplyTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Enter)
            return;

        var shiftDown =
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Shift)
                .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (shiftDown)
            return;

        if (ViewModel.SendReplyCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.SendReplyCommand.Execute(null);
        }
    }

    private void ObserveSelectedConversation()
    {
        if (_observedConversation != null)
            _observedConversation.Messages.CollectionChanged -= OnConversationMessagesChanged;

        _observedConversation = ViewModel.SelectedConversation;

        if (_observedConversation != null)
            _observedConversation.Messages.CollectionChanged += OnConversationMessagesChanged;
    }

    private void OnConversationMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ScrollConversationToEnd();


    private void ConversationMessageList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not SmsMessage message)
            return;

        args.RegisterUpdateCallback((_, updateArgs) =>
        {
            if (updateArgs.ItemContainer.ContentTemplateRoot is not Grid row)
                return;

            var bubble = FindDescendant<Border>(row, "MessageBubble");
            var body = FindDescendant<TextBlock>(row, "MessageBody");
            var timestamp = FindDescendant<TextBlock>(row, "MessageTimestamp");
            var status = FindDescendant<TextBlock>(row, "MessageStatus");
            if (bubble == null) return;

            var outgoing = message.Direction == SmsDirection.Outgoing;

            bubble.HorizontalAlignment = outgoing
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;

            bubble.Background = outgoing
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];

            var outgoingForeground = new SolidColorBrush(Colors.White);
            var incomingForeground = new SolidColorBrush(Colors.Black);

            if (body != null)
                body.Foreground = outgoing ? outgoingForeground : incomingForeground;
            if (timestamp != null)
                timestamp.Foreground = outgoing ? outgoingForeground : incomingForeground;
            if (status != null)
            {
                status.Visibility = outgoing ? Visibility.Visible : Visibility.Collapsed;
                status.Foreground = outgoing ? outgoingForeground : incomingForeground;
            }

            AnimateMessageBubble(bubble);
        });
    }

    private static T? FindDescendant<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T element && element.Name == name)
                return element;

            var nested = FindDescendant<T>(child, name);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private static void AnimateMessageBubble(FrameworkElement bubble)
    {
        bubble.Opacity = 0;
        bubble.RenderTransform = new TranslateTransform { Y = 18 };

        var storyboard = new Storyboard();

        var slide = new DoubleAnimation
        {
            From = 18,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, bubble);
        Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.Y)");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(180)
        };
        Storyboard.SetTarget(fade, bubble);
        Storyboard.SetTargetProperty(fade, "Opacity");

        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    private void ScrollConversationToEnd()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var messages = ViewModel.SelectedConversation?.Messages;
            if (messages == null || messages.Count == 0) return;

            ConversationMessageList.ScrollIntoView(
                messages[^1],
                ScrollIntoViewAlignment.Leading);
        });
    }
}
