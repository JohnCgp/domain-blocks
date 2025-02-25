﻿using System;
using System.Threading;
using System.Threading.Tasks;
using DomainBlocks.Common;
using EventStore.Client;
using Microsoft.Extensions.Logging;

namespace DomainBlocks.Projections.EventStore
{
    public class AcknowledgingEventStoreEventPublisher : IEventPublisher<ReadOnlyMemory<byte>>, IDisposable
    {
        private static readonly ILogger<AcknowledgingEventStoreEventPublisher> Log = 
            Logger.CreateFor<AcknowledgingEventStoreEventPublisher>();
        private readonly EventStorePersistentSubscriptionsClient _client;
        private Func<EventNotification<ReadOnlyMemory<byte>>, Task> _onEvent;
        private readonly EventStorePersistentConnectionDescriptor _persistentConnectionDescriptor;
        private PersistentSubscription _subscription;
        private readonly EventStoreDroppedSubscriptionHandler _subscriptionDroppedHandler;

        public AcknowledgingEventStoreEventPublisher(EventStorePersistentSubscriptionsClient client, 
                                                     EventStorePersistentConnectionDescriptor persistentConnectionDescriptor)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _persistentConnectionDescriptor = persistentConnectionDescriptor;
            _subscriptionDroppedHandler = new EventStoreDroppedSubscriptionHandler(Stop, ReSubscribeAfterDrop);

        }

        public async Task StartAsync(Func<EventNotification<ReadOnlyMemory<byte>>, Task> onEvent)
        {
            _onEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent));
            await SubscribeToPersistentSubscription();
        }

        public void Stop()
        {
            Dispose();
        }

        public void Dispose()
        {
            _subscription.Dispose();
        }

        private async Task HandleEvent(PersistentSubscription subscription, ResolvedEvent resolvedEvent, int? arg3, CancellationToken token)
        {
            // TODO: I'm not sure awaiting here is the best move if we want to utilize the ES buffer properly
            // It might be better to return straight away and then ack/nack as and when we finish processing
            // This needs some more investigation
            await TryHandlingEvent(subscription, resolvedEvent, 0);
        }

        private async Task SubscribeToPersistentSubscription()
        {
            _subscription = await _client.SubscribeAsync(_persistentConnectionDescriptor.Stream,
                                _persistentConnectionDescriptor.GroupName,
                                HandleEvent,
                                OnSubscriptionDropped,
                                _persistentConnectionDescriptor
                                    .UserCredentials,
                                _persistentConnectionDescriptor.BufferSize,
                                false);
        }

        private async Task TryHandlingEvent(PersistentSubscription subscription, ResolvedEvent resolvedEvent, int retryNumber)
        {
            try
            {
                var notification = EventNotification.FromEvent(resolvedEvent.Event.Data,
                                                               resolvedEvent.Event.EventType,
                                                               resolvedEvent.Event.EventId.ToGuid());
                await _onEvent(notification);
                await subscription.Ack(resolvedEvent);
                Log.LogTrace("Handled and acknowledged event {EventId}", resolvedEvent.Event.EventId);
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Failed to handle event {EventId}", resolvedEvent.Event.EventId);
                try
                {
                    await RetryHandlingEventOrFail(subscription, resolvedEvent, retryNumber);
                }
                catch (Exception ex2)
                {
                    Log.LogCritical(ex2, "Failed while trying to handle the failure case for event {EventId}. " +
                                         "Stopping persistent subscription for stream {StreamName} and group {GroupName}",
                                    resolvedEvent.Event.EventId,
                                    _persistentConnectionDescriptor.Stream,
                                    _persistentConnectionDescriptor.GroupName);

                    await subscription.Nack(PersistentSubscriptionNakEventAction.Stop,
                                            "Stopping subscription after unhandled exception trying to process event",
                                            resolvedEvent);

                    throw;
                }
            }
        }

        private async Task RetryHandlingEventOrFail(PersistentSubscription subscription,
                                                    ResolvedEvent resolvedEvent, 
                                                    int retryNumber)
        {
            var retrySettings = _persistentConnectionDescriptor.RetrySettings;
            if (retryNumber <= retrySettings.MaxRetryCount)
            {
                var nextRetryNumber = retryNumber + 1;
                var delay = retrySettings.GetRetryDelay(nextRetryNumber);

                Log.LogInformation("Retrying event {EventId}. Retry number {RetryNumber}. Delaying for {RetryDelay} before trying again",
                                   resolvedEvent.Event.EventId,
                                   nextRetryNumber,
                                   delay);
                await Task.Delay(delay);

                // Recurse back into TryHandlingEvent with the retry number incremented
                await TryHandlingEvent(subscription, resolvedEvent, nextRetryNumber);
            }
            else
            {
                var (actionDescription, failureAction) = retrySettings.MaxRetriesFailureAction switch
                {
                    MaxRetriesFailureAction.Park => ("Parking", PersistentSubscriptionNakEventAction.Park),
                    MaxRetriesFailureAction.Skip => ("Skipping", PersistentSubscriptionNakEventAction.Skip),
                    _ => throw new ArgumentOutOfRangeException()
                };

                var reason = $"{actionDescription} event {resolvedEvent.Event.EventId} after maximum retries reached " +
                              "and event could not be processed successfully";

                Log.LogError($"Could not handle event {{EventId}} after maximum retries. {actionDescription} event",
                             resolvedEvent.Event.EventId);

                await subscription.Nack(failureAction, reason, resolvedEvent);
            }
        }

        private void OnSubscriptionDropped(PersistentSubscription subscription,
                                           SubscriptionDroppedReason reason,
                                           Exception exception)
        {
            _subscriptionDroppedHandler.HandleDroppedSubscription(reason, exception);
        }

        private async Task ReSubscribeAfterDrop()
        {
            await SubscribeToPersistentSubscription();
        }
    }
}
