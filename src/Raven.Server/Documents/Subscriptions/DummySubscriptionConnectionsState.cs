﻿using System.Threading.Tasks;
using Raven.Client.Documents.Subscriptions;
using Raven.Server.Documents.TcpHandlers;

namespace Raven.Server.Documents.Subscriptions;

public sealed class DummySubscriptionConnectionsState : SubscriptionConnectionsState
{
    // for subscription test only
    public DummySubscriptionConnectionsState(string name, DocumentsStorage storage, SubscriptionState state) : base(name, -0x42, storage.DocumentDatabase.SubscriptionStorage)
    {
        _subscriptionName = "dummy";

        LastChangeVectorSent = state.ChangeVectorForNextBatchStartingPoint;
        Query = state.Query;
    }

    public override Task InitializeAsync(SubscriptionConnection connection, bool afterSubscribe = false)
    {
        return Task.CompletedTask;
    }
}
