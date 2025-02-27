﻿using System;
using System.Collections.Generic;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config.Categories;
using Raven.Server.Documents.Indexes.MapReduce;
using Raven.Server.Documents.Indexes.Persistence;
using Raven.Server.Documents.TimeSeries;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;

namespace Raven.Server.Documents.Indexes.Workers.Cleanup
{
    public class CleanupTimeSeries : CleanupItemsBase
    {
        private readonly TimeSeriesStorage _tsStorage;
        private readonly Dictionary<LazyStringValue, ((long Count, DateTime Start, DateTime End), bool Deleted)> _timeSeriesStats = new();

        public CleanupTimeSeries(Index index, DocumentsStorage documentsStorage, IndexStorage indexStorage,
            IndexingConfiguration configuration, MapReduceIndexingContext mapReduceContext) : base(index, indexStorage, configuration, mapReduceContext)
        {
            _tsStorage = documentsStorage.TimeSeriesStorage;
        }

        public override string Name => "TimeSeriesCleanup";

        protected override long ReadLastProcessedTombstoneEtag(RavenTransaction transaction, string collection) =>
            IndexStorage.ReadLastProcessedTimeSeriesDeletedRangeEtag(transaction, collection);

        protected override void WriteLastProcessedTombstoneEtag(RavenTransaction transaction, string collection, long lastEtag) =>
            IndexStorage.WriteLastTimeSeriesDeletedRangeEtag(transaction, collection, lastEtag);

        internal override void UpdateStats(IndexProgress.CollectionStats inMemoryStats, long lastEtag) =>
            inMemoryStats.UpdateTimeSeriesDeletedRangeLastEtag(lastEtag);

        protected override IEnumerable<TombstoneIndexItem> GetTombstonesFrom(DocumentsOperationContext context, long etag, long start, long take) =>
            _tsStorage.GetTimeSeriesDeletedRangeIndexItemsFrom(context, etag, take);


        protected override IEnumerable<TombstoneIndexItem> GetTombstonesFrom(DocumentsOperationContext context, string collection, long etag, long start, long take) =>
            _tsStorage.GetTimeSeriesDeletedRangeIndexItemsFrom(context, collection, etag, take);

        protected override bool IsValidTombstoneType(TombstoneIndexItem tombstone)
        {
            if (tombstone.Type != IndexItemType.TimeSeries)
                return false;

            return true;
        }

        protected override void HandleDelete(TombstoneIndexItem tombstone, string collection, Lazy<IndexWriteOperationBase> writer,
            QueryOperationContext queryContext, TransactionOperationContext indexContext, IndexingStatsScope stats)
        {
            if (_timeSeriesStats.TryGetValue(tombstone.PrefixKey, out var result) == false)
            {
                var tsStats = _tsStorage.Stats.GetStats(queryContext.Documents, tombstone.LowerId, tombstone.Name);
                result.Item1 = tsStats;
                result.Deleted = false;
                _timeSeriesStats.Add(tombstone.PrefixKey, result);
            }
            
            // if the time series is not completely deleted, we can skip further processing
            if (result.Item1 != default && result.Item1.Count != 0)
                return;

            if (result.Deleted)
                return;

            HandleTimeSeriesDelete(tombstone, collection, writer, queryContext, indexContext, stats);

            result.Deleted = true;
            _timeSeriesStats[tombstone.PrefixKey] = result;
        }

        protected virtual void HandleTimeSeriesDelete(TombstoneIndexItem tombstone, string collection, Lazy<IndexWriteOperationBase> writer,
            QueryOperationContext queryContext, TransactionOperationContext indexContext, IndexingStatsScope stats)
        {
            writer.Value.DeleteByPrefix(tombstone.PrefixKey, stats);
        }

        protected override void ClearStatsIfNeeded()
        {
            _timeSeriesStats.Clear();
        }
    }
}
