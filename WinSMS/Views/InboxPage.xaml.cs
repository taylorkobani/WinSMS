using System.Collections.Specialized;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private void ScrollConversationToEnd()
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var messages = ViewModel.SelectedConversation?.Messages;
            if (messages == null || messages.Count == 0) return;

            ConversationMessageList.ScrollIntoView(
                messages[^1],
                ScrollIntoViewAlignment.Leading);
        });
    }
}
