﻿using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.Documents.Operations.TimeSeries;
using Raven.Server.ServerWide.Context;
using Sparrow.Binary;
using Sparrow.Server;
using Sparrow.Server.Utils;
using Voron;
using Voron.Data.Tables;
using Voron.Impl;

namespace Raven.Server.Documents.TimeSeries
{
    public class TimeSeriesStats
    {
        private static readonly Slice RawPolicySlice;
        private static readonly Slice TimeSeriesStatsKey;
        private static readonly Slice PolicyIndex;
        private static readonly Slice StartTimeIndex;
        private static readonly TableSchema TimeSeriesStatsSchema = new TableSchema();

        private enum StatsColumns
        {
            Key = 0, // documentId, separator, name
            PolicyName = 1,
            Start = 2,
            End = 3,
            Count = 4,
            
        }

        static TimeSeriesStats()
        {
            using (StorageEnvironment.GetStaticContext(out ByteStringContext ctx))
            {
                Slice.From(ctx, RawTimeSeriesPolicy.PolicyString, SpecialChars.RecordSeparator, ByteStringType.Immutable, out RawPolicySlice);
                Slice.From(ctx, nameof(TimeSeriesStatsKey), ByteStringType.Immutable, out TimeSeriesStatsKey);
                Slice.From(ctx, nameof(PolicyIndex), ByteStringType.Immutable, out PolicyIndex);
                Slice.From(ctx, nameof(StartTimeIndex), ByteStringType.Immutable, out StartTimeIndex);
            }

            TimeSeriesStatsSchema.DefineKey(new TableSchema.SchemaIndexDef
            {
                StartIndex = (int)StatsColumns.Key,
                Count = 1, 
                Name = TimeSeriesStatsKey,
                IsGlobal = true
            });

            TimeSeriesStatsSchema.DefineIndex(new TableSchema.SchemaIndexDef
            {
                StartIndex = (int)StatsColumns.PolicyName,
                Name = PolicyIndex
            });

            TimeSeriesStatsSchema.DefineIndex(new TableSchema.SchemaIndexDef
            {
                // policy/start
                StartIndex = (int)StatsColumns.PolicyName,
                Count = 2, 
                Name = StartTimeIndex
            });
        }

        public TimeSeriesStats(Transaction tx)
        {
            tx.CreateTree(TimeSeriesStatsKey);
        }

        private Table GetOrCreateTable(Transaction tx, CollectionName collection)
        {
            var tableName = collection.GetTableName(CollectionTableType.TimeSeriesStats); // TODO: cache the collection and pass Slice
            TimeSeriesStatsSchema.Create(tx, tableName, 16);
            return tx.OpenTable(TimeSeriesStatsSchema, tableName);
        }

        public void UpdateCount(DocumentsOperationContext context, string docId, string name, CollectionName collection, long count)
        {
            if (count == 0)
                return;

            using (var slicer = new TimeSeriesSliceHolder(context, docId, name))
            {
                UpdateCount(context, slicer, collection, count);
            }
        }

        public void UpdateCount(DocumentsOperationContext context, TimeSeriesSliceHolder slicer, CollectionName collection, long count)
        {
            if (count == 0)
                return;

            var table = GetOrCreateTable(context.Transaction.InnerTransaction, collection);
            if (table.ReadByKey(slicer.StatsKey, out var tvr) == false)
                return;

            var previousCount = DocumentsStorage.TableValueToLong((int)StatsColumns.Count, ref tvr);
            var start = new DateTime(Bits.SwapBytes(DocumentsStorage.TableValueToLong((int)StatsColumns.Start, ref tvr)));
            var end = DocumentsStorage.TableValueToDateTime((int)StatsColumns.End, ref tvr);

            using (table.Allocate(out var tvb))
            {
                tvb.Add(slicer.StatsKey);
                tvb.Add(GetPolicy(slicer));
                tvb.Add(Bits.SwapBytes(start.Ticks));
                tvb.Add(end);
                tvb.Add(previousCount + count);
                
                table.Set(tvb);
            }
        }

        public void UpdateDates(DocumentsOperationContext context, TimeSeriesSliceHolder slicer, CollectionName collection, DateTime start, DateTime end)
        {
            var table = GetOrCreateTable(context.Transaction.InnerTransaction, collection);
            if (table.ReadByKey(slicer.StatsKey, out var tvr) == false)
                return;

            var previousCount = DocumentsStorage.TableValueToLong((int)StatsColumns.Count, ref tvr);
            using (table.Allocate(out var tvb))
            {
                tvb.Add(slicer.StatsKey);
                tvb.Add(GetPolicy(slicer));
                tvb.Add(Bits.SwapBytes(start.Ticks));
                tvb.Add(end);
                tvb.Add(previousCount);

                table.Set(tvb);
            }
        }

        public void UpdateStats(DocumentsOperationContext context, string docId, string name, CollectionName collection, TimeSeriesValuesSegment segment, DateTime baseline)
        {
            using (var slicer = new TimeSeriesSliceHolder(context, docId, name))
            {
                UpdateStats(context, slicer, collection, segment, baseline);
            }
        }

        public void UpdateStats(DocumentsOperationContext context, TimeSeriesSliceHolder slicer, CollectionName collection, TimeSeriesValuesSegment segment, DateTime baseline)
        {
            long previousCount = 0;
            var start = DateTime.MaxValue;
            var end = DateTime.MinValue;

            var table = GetOrCreateTable(context.Transaction.InnerTransaction, collection);
            if (table.ReadByKey(slicer.StatsKey, out var tvr))
            {
                previousCount = DocumentsStorage.TableValueToLong((int)StatsColumns.Count, ref tvr);
                start = new DateTime(Bits.SwapBytes(DocumentsStorage.TableValueToLong((int)StatsColumns.Start, ref tvr)));
                end = DocumentsStorage.TableValueToDateTime((int)StatsColumns.End, ref tvr);
            }

            var count = segment.NumberOfLiveEntries;
            if (count > 0)
            {
                var last = segment.GetLastTimestamp(baseline);
                if (last > end)
                    end = last;
                
                var first = segment.YieldAllValues(context, baseline, includeDead: false).First().Timestamp;
                if (first < start)
                    start = first;
            }

            using (table.Allocate(out var tvb))
            {
                tvb.Add(slicer.StatsKey);
                tvb.Add(GetPolicy(slicer));
                tvb.Add(Bits.SwapBytes(start.Ticks));
                tvb.Add(end);
                tvb.Add(previousCount + count);

                table.Set(tvb);
            }
        }

        public (long Count, DateTime Start, DateTime End) GetStats(DocumentsOperationContext context, string docId, string name)
        {
            using (var slicer = new TimeSeriesSliceHolder(context, docId, name))
            {
                return GetStats(context, slicer);
            }
        }

        public (long Count, DateTime Start, DateTime End) GetStats(DocumentsOperationContext context, TimeSeriesSliceHolder slicer)
        {
            var table = new Table(TimeSeriesStatsSchema, context.Transaction.InnerTransaction);
            if (table.ReadByKey(slicer.StatsKey, out var tvr) == false)
                return default;

            var count = DocumentsStorage.TableValueToLong((int)StatsColumns.Count, ref tvr);
            var start = new DateTime(Bits.SwapBytes(DocumentsStorage.TableValueToLong((int)StatsColumns.Start, ref tvr)));
            var end = DocumentsStorage.TableValueToDateTime((int)StatsColumns.End, ref tvr);

            return (count, start, end);
        }

        public IEnumerable<Slice> GetTimeSeriesNameByPolicy(DocumentsOperationContext context, CollectionName collection, string policy, long skip, int take)
        {
            var table = GetOrCreateTable(context.Transaction.InnerTransaction, collection);
            using (Slice.From(context.Allocator, policy.ToLowerInvariant(), SpecialChars.RecordSeparator, ByteStringType.Immutable, out var name))
            {
                foreach (var result in table.SeekForwardFrom(TimeSeriesStatsSchema.Indexes[PolicyIndex], name, skip, startsWith: true))
                {
                    var reader = result.Result.Reader;
                    DocumentsStorage.TableValueToSlice(context, (int)StatsColumns.Key, ref reader, out var slice);
                    yield return slice;

                    take--;
                    if (take <= 0)
                        yield break;
                }
            }
        }

        public IEnumerable<Slice> GetTimeSeriesByPolicyFromStartDate(DocumentsOperationContext context, CollectionName collection, string policy, DateTime start, int take)
        {
            var table = GetOrCreateTable(context.Transaction.InnerTransaction, collection);
            using (CombinePolicyNameAndTicks(context, policy.ToLowerInvariant(), start.Ticks, out var key,out var policySlice))
            {
                foreach (var result in table.SeekBackwardFrom(TimeSeriesStatsSchema.Indexes[StartTimeIndex], policySlice, key))
                {
                    DocumentsStorage.TableValueToSlice(context, (int)StatsColumns.Key, ref result.Result.Reader, out var slice);
                    var currentStart = new DateTime(Bits.SwapBytes(DocumentsStorage.TableValueToLong((int)StatsColumns.Start, ref result.Result.Reader)));
                    if (currentStart > start)
                        yield break;

                    yield return slice;

                    take--;
                    if (take <= 0)
                        yield break;
                }
            }
        }

        private static unsafe ByteStringContext<ByteStringMemoryCache>.InternalScope CombinePolicyNameAndTicks(DocumentsOperationContext context, string policy, long ticks, out Slice slice, out Slice policySlice)
        {
            using (DocumentIdWorker.GetSliceFromId(context, policy, out policySlice, SpecialChars.RecordSeparator))
            {
                var size = policySlice.Size + sizeof(long);
                var scope = context.Allocator.Allocate(size, out var str);
                policySlice.CopyTo(str.Ptr);
                *(long*)(str.Ptr + policySlice.Size) = Bits.SwapBytes(ticks);

                slice = new Slice(str);
                return scope;
            }
        }

        public IEnumerable<Slice> GetAllPolicies(DocumentsOperationContext context, CollectionName collection)
        {
            var table = GetOrCreateTable(context.Transaction.InnerTransaction, collection);

            var policies = table.GetTree(TimeSeriesStatsSchema.Indexes[PolicyIndex]);
            using (var it = policies.Iterate(true))
            {
                if (it.Seek(Slices.BeforeAllKeys) == false)
                    yield break;

                do
                {
                    yield return it.CurrentKey.Clone(context.Allocator);
                } while (it.MoveNext());
            }
        }

        public void DeleteStats(DocumentsOperationContext context, CollectionName collection, Slice key)
        {
            var table = GetOrCreateTable(context.Transaction.InnerTransaction, collection);
            table.DeleteByKey(key);
        }

        private Slice GetPolicy(TimeSeriesSliceHolder slicer)
        {
            var name = slicer.LowerTimeSeriesName;
            var index = name.Content.IndexOf((byte)TimeSeriesConfiguration.TimeSeriesRollupSeparator);
            if (index < 0)
                return RawPolicySlice;
            var offset = index + 1;
            return slicer.PolicyNameWithSeparator(name, offset, name.Content.Length - offset);
        }
    }
}
