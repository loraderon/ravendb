﻿using JetBrains.Annotations;
using Raven.Client.Documents.Operations.Expiration;
using Raven.Server.Documents.Handlers.Processors.Expiration;
using Raven.Server.ServerWide.Context;

namespace Raven.Server.Documents.Sharding.Handlers.Processors.Expiration
{
    internal sealed class ShardedExpirationHandlerProcessorForGet : AbstractExpirationHandlerProcessorForGet<ShardedDatabaseRequestHandler, TransactionOperationContext>
    {
        public ShardedExpirationHandlerProcessorForGet([NotNull] ShardedDatabaseRequestHandler requestHandler) : base(requestHandler)
        {
        }

        protected override ExpirationConfiguration GetExpirationConfiguration()
        {
            return RequestHandler.DatabaseContext.DatabaseRecord.Expiration;
        }
    }
}
