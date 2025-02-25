﻿using System;
using System.Linq;
using System.Threading.Tasks;
using DomainBlocks.Aggregates.Registration;
using DomainBlocks.EventStore.Testing;
using DomainBlocks.Persistence;
using DomainBlocks.Persistence.EventStore;
using DomainBlocks.Serialization.Json;
using NUnit.Framework;
using Shopping.Domain.Aggregates;
using Shopping.Domain.Commands;
using Shopping.Domain.Events;

namespace Shopping.Infrastructure.Tests
{
    [TestFixture]
    public class ShoppingCartInfrastructureTests : EventStoreIntegrationTest
    {
       [Test]
        public async Task PersistedRoundTripTest()
        {
            var registryBuilder = AggregateRegistryBuilder.Create<object, IDomainEvent>();
            ShoppingCartFunctions.Register(registryBuilder);
            var shoppingCartId = Guid.NewGuid(); // This could come from a sequence, or could be the customer's ID.

            var aggregateRegistry = registryBuilder.Build();

            var serializer = new JsonBytesEventSerializer(aggregateRegistry.EventNameMap);
            var eventsRepository = new EventStoreEventsRepository(EventStoreClient, serializer);
            var snapshotRepository = new EventStoreSnapshotRepository(EventStoreClient, serializer);

            var aggregateRepository = AggregateRepository.Create(eventsRepository,
                                                                 snapshotRepository,
                                                                 aggregateRegistry);

            var loadedAggregate = await aggregateRepository.LoadAggregate<ShoppingCartState>(shoppingCartId.ToString());

            // Execute the first command.
            var command1 = new AddItemToShoppingCart(shoppingCartId, Guid.NewGuid(), "First Item");
            loadedAggregate.ImmutableDispatchCommand(command1);

            // Execute the second command to the result of the first command.
            var command2 = new AddItemToShoppingCart(shoppingCartId, Guid.NewGuid(), "Second Item");
            loadedAggregate.ImmutableDispatchCommand(command2);

            Assert.That(loadedAggregate.AggregateState.Id.HasValue, "Expected ShoppingCart ID to be set");

            var eventsToPersist = loadedAggregate.EventsToPersist.ToList();

            var nextEventVersion = await aggregateRepository.SaveAggregate(loadedAggregate);
            var expectedNextEventVersion = eventsToPersist.Count - 1;

            Assert.That(nextEventVersion, Is.EqualTo(expectedNextEventVersion));

            var loadedData = await aggregateRepository.LoadAggregate<ShoppingCartState>(shoppingCartId.ToString());

            var loadedState = loadedData.AggregateState;
            var loadedVersion = loadedData.Version;

            // Check the loaded aggregate root state.
            Assert.That(loadedVersion, Is.EqualTo(2));
            Assert.That(loadedState.Id, Is.EqualTo(shoppingCartId));
            Assert.That(loadedState.Items, Has.Count.EqualTo(2));
            Assert.That(loadedState.Items[0].Name, Is.EqualTo("First Item"));
            Assert.That(loadedState.Items[1].Name, Is.EqualTo("Second Item"));
        }
    }
}