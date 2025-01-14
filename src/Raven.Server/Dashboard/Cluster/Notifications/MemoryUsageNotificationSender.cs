// -----------------------------------------------------------------------
//  <copyright file="MemoryUsageNotificationSender.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Threading;
using Raven.Client.Util;
using Raven.Server.NotificationCenter;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.LowMemory;
using Sparrow.Server.LowMemory;
using Sparrow.Utils;
using Voron.Impl;

namespace Raven.Server.Dashboard.Cluster.Notifications
{
    public sealed class MemoryUsageNotificationSender : AbstractClusterDashboardNotificationSender
    {
        private readonly RavenServer _server;
        private readonly LowMemoryMonitor _lowMemoryMonitor = new();
        private readonly TimeSpan _defaultInterval = TimeSpan.FromSeconds(5);

        public MemoryUsageNotificationSender(int widgetId, RavenServer server, ConnectedWatcher watcher, CancellationToken shutdown) : base(widgetId, watcher, shutdown)
        {
            _server = server;
        }

        protected override TimeSpan NotificationInterval => _defaultInterval;

        protected override AbstractClusterDashboardNotification CreateNotification()
        {
            var memoryInfo = _server.MetricCacher.GetValue<MemoryInfoResult>(MetricCacher.Keys.Server.MemoryInfoExtended.RefreshRate15Seconds);
            long managedMemoryInBytes = AbstractLowMemoryMonitor.GetManagedMemoryInBytes();
            long totalUnmanagedAllocations = NativeMemory.TotalAllocatedMemory;
            var encryptionBuffers = EncryptionBuffersPool.Instance.GetStats();
            var dirtyMemoryState = MemoryInformation.GetDirtyMemoryState();

            long totalMapping = 0;
            foreach (var mapping in NativeMemory.FileMapping)
            foreach (var singleMapping in mapping.Value.Value.Info)
            {
                totalMapping += singleMapping.Value;
            }

            return new MemoryUsagePayload
            {
                Time = SystemTime.UtcNow,
                LowMemorySeverity = LowMemoryNotification.Instance.IsLowMemory(memoryInfo, _lowMemoryMonitor, out _),
                PhysicalMemory = memoryInfo.TotalPhysicalMemory.GetValue(SizeUnit.Bytes),
                WorkingSet = memoryInfo.WorkingSet.GetValue(SizeUnit.Bytes),
                ManagedAllocations = managedMemoryInBytes,
                UnmanagedAllocations = totalUnmanagedAllocations,
                SystemCommitLimit = memoryInfo.TotalCommittableMemory.GetValue(SizeUnit.Bytes),
                EncryptionBuffersInUse = encryptionBuffers.CurrentlyInUseSize,
                EncryptionBuffersPool = encryptionBuffers.TotalPoolSize,
                MemoryMapped = totalMapping,
                DirtyMemory = dirtyMemoryState.TotalDirty.GetValue(SizeUnit.Bytes),
                AvailableMemory = memoryInfo.AvailableMemory.GetValue(SizeUnit.Bytes),
                AvailableMemoryForProcessing = memoryInfo.AvailableMemoryForProcessing.GetValue(SizeUnit.Bytes),
                TotalSwapUsage = memoryInfo.TotalSwapUsage.GetValue(SizeUnit.Bytes),
                LuceneManagedTermCacheAllocations = NativeMemory.TotalLuceneManagedAllocationsForTermCache,
                LuceneUnmanagedAllocations = NativeMemory.TotalLuceneUnmanagedAllocationsForSorting
            };
        }
    }
}
