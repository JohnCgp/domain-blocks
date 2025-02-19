﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DomainBlocks.Common;
using DomainBlocks.Serialization;
using EventStore.Client;
using Microsoft.Extensions.Logging;

namespace DomainBlocks.Persistence.EventStore
{
    public class EventStoreEventsRepository : IEventsRepository<ReadOnlyMemory<byte>>
    {
        private static readonly ILogger<EventStoreEventsRepository> Log = Logger.CreateFor<EventStoreEventsRepository>();
        private readonly EventStoreClient _client;
        private readonly IEventSerializer<ReadOnlyMemory<byte>> _serializer;

        public EventStoreEventsRepository(EventStoreClient client, IEventSerializer<ReadOnlyMemory<byte>> serializer)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            Log.LogDebug("EventStoreEventsRepository created using {SerializerType} serializer", serializer.GetType().Name);
        }

        public async Task<long> SaveEventsAsync<TEvent>(string streamName, long expectedStreamVersion, IEnumerable<TEvent> events)
        {
            if (streamName == null) throw new ArgumentNullException(nameof(streamName));
            if (events == null) throw new ArgumentNullException(nameof(events));

            EventData[] eventDatas;
            try
            {
                eventDatas = events.Select(e => _serializer.ToEventData(e)).ToArray();
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Unable to serialize events. Aborting write to stream {StreamName}", streamName);
                throw;
            }

            var streamVersion = MapStreamVersionToEventStoreStreamRevision(expectedStreamVersion);
            if (eventDatas.Length == 0)
            {
                Log.LogWarning("No events in batch. Exiting");
                return streamVersion.ToInt64();
            }

            // Use the ID of the first event in the batch as an identifier for the whole write to ES
            var writeId = eventDatas[0].EventId;

            Log.LogDebug("Appending {EventCount} events to stream {StreamName}. Expected stream version {StreamVersion}. Write ID {WriteId}", 
                         eventDatas.Length, streamName, streamVersion, writeId);

            if (Log.IsEnabled(LogLevel.Trace))
            {
                foreach (var eventData in eventDatas)
                {
                    Log.LogTrace("Event to append {EventId}. EventType {EventType}. WriteId {WriteId}. " +
                                     "EventBytes {EventBytes}. MetadataBytes {MetadataBytes}. ContentType {ContentType} ",
                                     eventData.EventId, eventData.Type, writeId, eventData.Data, eventData.Metadata, eventData.ContentType);
                }
            }

            IWriteResult writeResult;
            try
            {
                writeResult = await _client.AppendToStreamAsync(streamName, streamVersion, eventDatas);
                Log.LogDebug("Written events to stream. WriteId {WriteId}", writeId);
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Unable to save events to stream {StreamName}. Write Id {WriteId}", streamName, writeId);
                throw;
            }

            return writeResult.NextExpectedStreamRevision.ToInt64();
        }

        public async Task<IList<TEvent>> LoadEventsAsync<TEvent>(string streamName, long startPosition = 0, Action<IEventPersistenceData<ReadOnlyMemory<byte>>> onEventError = null)
        {
            if (streamName == null) throw new ArgumentNullException(nameof(streamName));
            var events = new List<TEvent>();

            try
            {
                var readStreamResult =
                    _client.ReadStreamAsync(Direction.Forwards,
                                            streamName,
                                            StreamPosition.FromInt64(startPosition));

                var readState = await readStreamResult.ReadState;

                if (readState == ReadState.StreamNotFound)
                {
                    return events;
                }

                await foreach (var resolvedEvent in readStreamResult)
                {
                    try
                    {
                        events.Add(_serializer.DeserializeEvent<TEvent>(resolvedEvent.OriginalEvent.Data,
                                                                        resolvedEvent.OriginalEvent.EventType));
                    }
                    catch (EventDeserializeException e)
                    {
                        if (onEventError == null)
                        {
                            Log.LogWarning(e,
                                           "Error deserializing event and no error handler set up. This may cause data inconsistencies");
                        }
                        else
                        {
                            onEventError(EventStoreEventPersistenceData.FromRecordedEvent(resolvedEvent.Event));
                            Log.LogInformation(e, "Error deserializing event. Calling onEventError handler");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Unable to load events from {StreamName}", streamName);
                throw;
            }

            return events;
        }

        private static StreamRevision MapStreamVersionToEventStoreStreamRevision(long streamVersion)
        {
            return streamVersion switch
            {
                StreamVersion.NewStream => StreamRevision.None,
                _ => StreamRevision.FromInt64(streamVersion)
            };
        }
    }
}