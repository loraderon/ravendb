﻿using System;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Raven.Server.Documents.Handlers.Processors.Databases;
using Raven.Server.ServerWide.Context;
using Raven.Server.Web;
using Sparrow.Json;

namespace Raven.Server.Documents.Handlers.Processors.OngoingTasks
{
    internal abstract class AbstractOngoingTasksHandlerProcessorForResetEtl<TRequestHandler> : AbstractHandlerProcessorForUpdateDatabaseConfiguration<BlittableJsonReaderObject, TRequestHandler>
        where TRequestHandler : RequestHandler
    {
        protected AbstractOngoingTasksHandlerProcessorForResetEtl([NotNull] TRequestHandler requestHandler)
            : base(requestHandler)
        {
        }

        protected override Task<(long Index, object Result)> OnUpdateConfiguration(TransactionOperationContext context, string databaseName, BlittableJsonReaderObject configuration, string raftRequestId)
        {
            var configurationName = RequestHandler.GetStringQueryString("configurationName"); // etl task name
            var transformationName = RequestHandler.GetStringQueryString("transformationName");

            return RequestHandler.ServerStore.RemoveEtlProcessState(context, databaseName, configurationName, transformationName, raftRequestId);
        }
    }
}
