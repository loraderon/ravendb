﻿using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations;
using Raven.Client.Http;
using Raven.Server.Documents.Operations;
using Raven.Server.Documents.Sharding.Operations;
using Raven.Server.ServerWide;
using Raven.Server.Utils;
using Sparrow.Json;
using Sparrow.Utils;
using Operation = Raven.Client.Documents.Operations.Operation;

namespace Raven.Server.Documents.Sharding;

public partial class ShardedDatabaseContext
{
    public ShardedOperations Operations;

    public class ShardedOperations : AbstractOperations<ShardedOperation>
    {
        private readonly ShardedDatabaseContext _context;

        private readonly ConcurrentDictionary<ShardedDatabaseIdentifier, DatabaseChanges> _changes = new();

        public ShardedOperations([NotNull] ShardedDatabaseContext context)
            : base(context.Changes)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public override long GetNextOperationId()
        {
            var nextId = _context._serverStore.Operations.GetNextOperationId();

            DevelopmentHelper.ShardingToDo(DevelopmentHelper.TeamMember.Pawel, DevelopmentHelper.Severity.Major, "Encode NodeTag");

            return nextId;
        }

        protected override void RaiseNotifications(OperationStatusChange change, ShardedOperation operation)
        {
            // TODO [ppekrol]
            base.RaiseNotifications(change, operation);
        }

        public Task AddOperation(
            long id,
            OperationType operationType,
            string description,
            IOperationDetailedDescription detailedDescription,
            Func<JsonOperationContext, RavenCommand<OperationIdResult>> commandFactory,
            OperationCancelToken token = null)
        {
            var operation = CreateOperationInstance(id, _context.DatabaseName, operationType, description, detailedDescription, token);

            return AddOperationInternalAsync(operation, onProgress => CreateTaskAsync(operation, commandFactory, onProgress, token));
        }

        private async Task<IOperationResult> CreateTaskAsync(
            ShardedOperation operation,
            Func<JsonOperationContext, RavenCommand<OperationIdResult>> commandFactory,
            Action<IOperationProgress> onProgress,
            OperationCancelToken token)
        {
            var t = token?.Token ?? default;

            operation.Operation = new MultiOperation(operation.Id, _context, onProgress);

            var tasks = new Task[_context.NumberOfShardNodes];
            using (_context._serverStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            {
                for (var shardNumber = 0; shardNumber < tasks.Length; shardNumber++)
                {
                    var command = commandFactory(context);

                    tasks[shardNumber] = ConnectAsync(_context, operation.Operation, command, shardNumber, t);
                }
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                await operation.KillAsync(waitForCompletion: true, t);
            }

            return await operation.Operation.WaitForCompletionAsync(t);

            async Task ConnectAsync(ShardedDatabaseContext context, MultiOperation operation, RavenCommand<OperationIdResult> command, int shardNumber, CancellationToken token)
            {
                await context.ShardExecutor.ExecuteSingleShardAsync(command, shardNumber, token);

                var key = new ShardedDatabaseIdentifier(command.Result.OperationNodeTag, shardNumber);

                var changes = GetChanges(key);

                var shardOperation = new Operation(context.ShardExecutor.GetRequestExecutorAt(shardNumber), () => changes, DocumentConventions.DefaultForServer, command.Result.OperationId, command.Result.OperationNodeTag);

                operation.Watch(key, shardOperation);
            }
        }

        private DatabaseChanges GetChanges(ShardedDatabaseIdentifier key) => _changes.GetOrAdd(key, k => new DatabaseChanges(_context.ShardExecutor.GetRequestExecutorAt(k.ShardNumber), ShardHelper.ToShardName(_context.DatabaseName, k.ShardNumber), onDispose: null, k.NodeTag));
    }
}
