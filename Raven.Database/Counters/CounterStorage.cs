using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Raven.Abstractions;
using Raven.Abstractions.Counters;
using Raven.Abstractions.Counters.Notifications;
using Raven.Abstractions.Logging;
using Raven.Abstractions.Util;
using Raven.Database.Config;
using Raven.Database.Counters.Controllers;
using Raven.Database.Counters.Notifications;
using Raven.Database.Extensions;
using Raven.Database.Impl;
using Raven.Database.Server.Abstractions;
using Raven.Database.Server.Connections;
using Raven.Database.Util;
using Raven.Imports.Newtonsoft.Json;
using Voron;
using Voron.Impl;
using Voron.Trees;
using Voron.Util;
using Voron.Util.Conversion;
using Constants = Raven.Abstractions.Data.Constants;

namespace Raven.Database.Counters
{
	public class CounterStorage : IResourceStore, IDisposable
	{
		private static readonly ILog Log = LogManager.GetCurrentClassLogger();

		private readonly StorageEnvironment storageEnvironment;
		private readonly TransportState transportState;
		private readonly CountersMetricsManager metricsCounters;
		private readonly NotificationPublisher notificationPublisher;
		private readonly ReplicationTask replicationTask;
		private readonly JsonSerializer jsonSerializer;
		private readonly Guid tombstoneId = Guid.Empty;
		private readonly int sizeOfGuid;
		private readonly Timer purgeTombstonesTimer;
		private readonly TimeSpan tombstoneRetentionTime;
		private readonly int deletedTombstonesInBatch;

		private long lastEtag;
		private long lastCounterId;
		public event Action CounterUpdated = () => { };

		public string CounterStorageUrl { get; private set; }

		public DateTime LastWrite { get; private set; }

		public Guid ServerId { get; private set; }

		public string Name { get; private set; }

		public string ResourceName { get; private set; }

		public int ReplicationTimeoutInMs { get; private set; }

		public unsafe CounterStorage(string serverUrl, string storageName, InMemoryRavenConfiguration configuration, TransportState receivedTransportState = null)
		{			
			CounterStorageUrl = string.Format("{0}cs/{1}", serverUrl, storageName);
			Name = storageName;
			ResourceName = string.Concat(Constants.Counter.UrlPrefix, "/", storageName);

			var options = configuration.RunInMemory ? StorageEnvironmentOptions.CreateMemoryOnly()
				: CreateStorageOptionsFromConfiguration(configuration.Counter.DataDirectory, configuration.Settings);

			storageEnvironment = new StorageEnvironment(options);
			transportState = receivedTransportState ?? new TransportState();
			notificationPublisher = new NotificationPublisher(transportState);
			replicationTask = new ReplicationTask(this);
			ReplicationTimeoutInMs = configuration.Replication.ReplicationRequestTimeoutInMilliseconds;
			tombstoneRetentionTime = configuration.Counter.TombstoneRetentionTime;
			deletedTombstonesInBatch = configuration.Counter.DeletedTombstonesInBatch;
			metricsCounters = new CountersMetricsManager();
			Configuration = configuration;
			ExtensionsState = new AtomicDictionary<object>();
			jsonSerializer = new JsonSerializer();
			sizeOfGuid = sizeof(Guid);

			Initialize();
			//purgeTombstonesTimer = new Timer(BackgroundActionsCallback, null, TimeSpan.Zero, TimeSpan.FromHours(1));
		}

		private void Initialize()
		{
			using (var tx = CounterStorageEnvironment.NewTransaction(TransactionFlags.ReadWrite))
			{
				storageEnvironment.CreateTree(tx, TreeNames.ServersLastEtag);
				storageEnvironment.CreateTree(tx, TreeNames.Counters);
				storageEnvironment.CreateTree(tx, TreeNames.DateToTombstones);
				storageEnvironment.CreateTree(tx, TreeNames.GroupToCounters);
				storageEnvironment.CreateTree(tx, TreeNames.TombstonesGroupToCounters);
				storageEnvironment.CreateTree(tx, TreeNames.CounterIdWithNameToGroup);
				storageEnvironment.CreateTree(tx, TreeNames.CountersToEtag);
				
				var etags = CounterStorageEnvironment.CreateTree(tx, TreeNames.EtagsToCounters);
				var metadata = CounterStorageEnvironment.CreateTree(tx, TreeNames.Metadata);
				var id = metadata.Read("id");
				var lastCounterIdRead = metadata.Read("lastCounterId");

				if (id == null) // new counter db
				{
					ServerId = Guid.NewGuid();
					var serverIdBytes = ServerId.ToByteArray();
					metadata.Add("id", serverIdBytes);
				}
				else // existing counter db
				{
					int used;
					ServerId = new Guid(id.Reader.ReadBytes(sizeOfGuid, out used));
					

					using (var it = etags.Iterate())
					{
						if (it.Seek(Slice.AfterAllKeys))
						{
							lastEtag = it.CurrentKey.CreateReader().ReadBigEndianInt64();
						}
					}
				}

				if (lastCounterIdRead == null)
				{
					var buffer = new byte[sizeof (long)];
					var slice = new Slice(buffer);
					metadata.Add("lastCounterId", slice);
					lastCounterId = 0;
				}
				else
				{
					lastCounterId = lastCounterIdRead.Reader.ReadBigEndianInt64();
				}

				tx.Commit();

				replicationTask.StartReplication();
			}
		}

		private void BackgroundActionsCallback(object state)
		{
			while (true)
			{
				using (var writer = CreateWriter())
				{
					if (writer.PurgeOutdatedTombstones() == false)
						break;

					writer.Commit();
				}
			}
		}

		string IResourceStore.Name
		{
			get { return Name; }
		}

		[CLSCompliant(false)]
		public CountersMetricsManager MetricsCounters
		{
			get { return metricsCounters; }
		}

		public TransportState TransportState
		{
			get { return transportState; }
		}

		public NotificationPublisher Publisher
		{
			get { return notificationPublisher; }
		}

		public ReplicationTask ReplicationTask
		{
			get { return replicationTask; }
		}

		public StorageEnvironment CounterStorageEnvironment
		{
			get { return storageEnvironment; }
		}

		private JsonSerializer JsonSerializer
		{
			get { return jsonSerializer; }
		}

		public AtomicDictionary<object> ExtensionsState { get; private set; }

		public InMemoryRavenConfiguration Configuration { get; private set; }

		public CounterStorageStats CreateStats()
		{
			using (var reader = CreateReader())
			{
				var stats = new CounterStorageStats
				{
					Name = Name,
					Url = CounterStorageUrl,
					CountersCount = reader.GetCountersCount(),
					GroupsCount = reader.GetGroupsCount(),
					LastCounterEtag = lastEtag,
					ReplicationTasksCount = replicationTask.GetActiveTasksCount(),
					CounterStorageSize = SizeHelper.Humane(CounterStorageEnvironment.Stats().UsedDataFileSizeInBytes),
					ReplicatedServersCount = 0, //TODO: get the correct number
					RequestsPerSecond = Math.Round(metricsCounters.RequestsPerSecondCounter.CurrentValue, 3),
				};
				return stats;
			}
		}

		public CountersStorageMetrics CreateMetrics()
		{
			var metrics = metricsCounters;

			return new CountersStorageMetrics
			{
				RequestsPerSecond = Math.Round(metrics.RequestsPerSecondCounter.CurrentValue, 3),
				Resets = metrics.Resets.CreateMeterData(),
				Increments = metrics.Increments.CreateMeterData(),
				Decrements = metrics.Decrements.CreateMeterData(),
				ClientRequests = metrics.ClientRequests.CreateMeterData(),
				IncomingReplications = metrics.IncomingReplications.CreateMeterData(),
				OutgoingReplications = metrics.OutgoingReplications.CreateMeterData(),

				RequestsDuration = metrics.RequestDurationMetric.CreateHistogramData(),
				IncSizes = metrics.IncSizeMetrics.CreateHistogramData(),
				DecSizes = metrics.DecSizeMetrics.CreateHistogramData(),

				ReplicationBatchSizeMeter = metrics.ReplicationBatchSizeMeter.ToMeterDataDictionary(),
				ReplicationBatchSizeHistogram = metrics.ReplicationBatchSizeHistogram.ToHistogramDataDictionary(),
				ReplicationDurationHistogram = metrics.ReplicationDurationHistogram.ToHistogramDataDictionary()
			};
		}

		private static StorageEnvironmentOptions CreateStorageOptionsFromConfiguration(string path, NameValueCollection settings)
		{
			bool result;
			if (bool.TryParse(settings[Constants.RunInMemory] ?? "false", out result) && result)
				return StorageEnvironmentOptions.CreateMemoryOnly();

			bool allowIncrementalBackupsSetting;
			if (bool.TryParse(settings[Constants.Voron.AllowIncrementalBackups] ?? "false", out allowIncrementalBackupsSetting) == false)
				throw new ArgumentException(Constants.Voron.AllowIncrementalBackups + " settings key contains invalid value");

			var directoryPath = path ?? AppDomain.CurrentDomain.BaseDirectory;
			var filePathFolder = new DirectoryInfo(directoryPath);
			if (filePathFolder.Exists == false)
				filePathFolder.Create();

			var tempPath = settings[Constants.Voron.TempPath];
			var journalPath = settings[Constants.RavenTxJournalPath];
			var options = StorageEnvironmentOptions.ForPath(directoryPath, tempPath, journalPath);
			options.IncrementalBackupEnabled = allowIncrementalBackupsSetting;
			return options;
		}

		[CLSCompliant(false)]
		public Reader CreateReader()
		{
			return new Reader(this, CounterStorageEnvironment.NewTransaction(TransactionFlags.Read));
		}

		[CLSCompliant(false)]
		public Writer CreateWriter()
		{
			return new Writer(this, CounterStorageEnvironment.NewTransaction(TransactionFlags.ReadWrite));
		}

		private void Notify()
		{
			CounterUpdated();
		}

		public void Dispose()
		{
			var exceptionAggregator = new ExceptionAggregator(Log, "Could not properly dispose of CounterStorage: " + Name);

			if (replicationTask != null)
				exceptionAggregator.Execute(replicationTask.Dispose);

			if (storageEnvironment != null)
				exceptionAggregator.Execute(storageEnvironment.Dispose);

			if (metricsCounters != null)
				exceptionAggregator.Execute(metricsCounters.Dispose);

			if (purgeTombstonesTimer != null)
				exceptionAggregator.Execute(purgeTombstonesTimer.Dispose);
			purgeTombstonesTimer = null;

			exceptionAggregator.ThrowIfNeeded();
		}

		[CLSCompliant(false)]
		public class Reader : IDisposable
		{
			private readonly Transaction transaction;
			private readonly Tree counters, tombstonesByDate, groupToCounters, tombstonesGroupToCounters, counterIdWithNameToGroup, etagsToCounters, countersToEtag, serversLastEtag, metadata;
			private readonly CounterStorage parent;

			[CLSCompliant(false)]
			public Reader(CounterStorage parent, Transaction transaction)
			{
				this.transaction = transaction;
				this.parent = parent;
				counters = transaction.State.GetTree(transaction, TreeNames.Counters);
				tombstonesByDate = transaction.State.GetTree(transaction, TreeNames.DateToTombstones);
				groupToCounters = transaction.State.GetTree(transaction, TreeNames.GroupToCounters);
				tombstonesGroupToCounters = transaction.State.GetTree(transaction, TreeNames.TombstonesGroupToCounters);
				counterIdWithNameToGroup = transaction.State.GetTree(transaction, TreeNames.CounterIdWithNameToGroup);
				countersToEtag = transaction.State.GetTree(transaction, TreeNames.CountersToEtag);
				etagsToCounters = transaction.State.GetTree(transaction, TreeNames.EtagsToCounters);
				serversLastEtag = transaction.State.GetTree(transaction, TreeNames.ServersLastEtag);
				metadata = transaction.State.GetTree(transaction, TreeNames.Metadata);
			}

			public long GetCountersCount()
			{
				long countersCount = 0;
				using (var it = groupToCounters.Iterate())
				{
					if (it.Seek(Slice.BeforeAllKeys) == false)
						return countersCount;

					do
					{
						countersCount += groupToCounters.MultiCount(it.CurrentKey);
					} while (it.MoveNext());
				}
				return countersCount;
			}

			public long GetGroupsCount()
			{
				return groupToCounters.State.EntriesCount;
			}

			private class CounterDetails
			{
				public byte[] IdBuffer { get; set; }
				public string Name { get; set; }
				public string Group { get; set; }
			}

			public bool DoesCounterExist(string groupName, string counterName)
			{
				using (var it = groupToCounters.MultiRead(groupName))
				{
					it.RequiredPrefix = counterName;
					if (it.Seek(it.RequiredPrefix) == false || it.CurrentKey.Size != it.RequiredPrefix.Size + sizeof(long))
						return false;
				}

				return true;
			}

			private IEnumerable<CounterDetails> GetCountersDetails(string groupName, int skip, int take)
			{
				var nameBuffer = new byte[0];
				var counterIdBytes = new byte[sizeof(long)];
				using (var it = groupToCounters.Iterate())
				{
					it.RequiredPrefix = groupName;
					if (it.Seek(it.RequiredPrefix) == false)
						yield break;

					do
					{
						var countersInGroup = groupToCounters.State.EntriesCount;
						if (skip - countersInGroup <= 0)
							break;
						skip -= (int)countersInGroup; //TODO: is there a better way?
					} while (it.MoveNext());

					do
					{
						using (var iterator = groupToCounters.MultiRead(it.CurrentKey))
						{
							iterator.Skip(skip);
							if (iterator.Seek(Slice.BeforeAllKeys) == false)
								yield break;

							do
							{
								var valueReader = iterator.CurrentKey.CreateReader();
								var requiredBufferSize = iterator.CurrentKey.Size - sizeof(long);
								valueReader.Skip(requiredBufferSize);
								valueReader.Read(counterIdBytes, 0, sizeof(long));

								var counterDetails = new CounterDetails
								{
									Group = groupName.Equals(string.Empty) ? it.CurrentKey.ToString() : groupName
								};

								EnsureBufferSize(ref nameBuffer, requiredBufferSize);
								valueReader.Reset();
								valueReader.Read(nameBuffer, 0, requiredBufferSize);
								counterDetails.Name = Encoding.UTF8.GetString(nameBuffer, 0, requiredBufferSize);
								counterDetails.IdBuffer = counterIdBytes;
								yield return counterDetails;
							} while (iterator.MoveNext() && --take > 0);
						}
					} while (it.MoveNext() && --take > 0);
				}
			}


			public List<CounterSummary> GetCountersSummary(string groupName, int skip = 0, int take = int.MaxValue)
			{
				var countersDetails = GetCountersDetails(groupName, skip, take);
				var serverIdBuffer = new byte[parent.sizeOfGuid];
				return countersDetails.Select(counterDetails => new CounterSummary
				{
					Group = counterDetails.Group,
					CounterName = counterDetails.Name,
					Total = CalculateCounterTotal(counterDetails.IdBuffer, serverIdBuffer)
				}).ToList();
			}

			private long CalculateCounterTotal(byte[] counterIdBuffer, byte[] serverIdBuffer)
			{
				using (var it = counters.Iterate())
				{
					var slice = new Slice(counterIdBuffer);
					it.RequiredPrefix = slice;
					if (it.Seek(it.RequiredPrefix) == false)
						return 0; //TODO: throw exception

					long total = 0;
					do
					{
						var reader = it.CurrentKey.CreateReader();
						reader.Skip(sizeof(long));
						reader.Read(serverIdBuffer, 0, parent.sizeOfGuid);
						var serverId = new Guid(serverIdBuffer);
						//this means that this used to be a deleted counter
						if (serverId.Equals(parent.tombstoneId))
							continue;

						var lastByte = it.CurrentKey[it.CurrentKey.Size - 1];
						var sign = Convert.ToChar(lastByte);
						Debug.Assert(sign == ValueSign.Positive || sign == ValueSign.Negative);
						var value = it.CreateReaderForCurrent().ReadLittleEndianInt64();
						if (sign == ValueSign.Positive)
							total += value;
						else
							total -= value;
					} while (it.MoveNext());

					return total;
				}
			}

			public long GetCounterTotal(string groupName, string counterName)
			{
				using (var it = groupToCounters.MultiRead(groupName))
				{
					it.RequiredPrefix = counterName;
					if (it.Seek(it.RequiredPrefix) == false || it.CurrentKey.Size != it.RequiredPrefix.Size + sizeof (long))
						throw new Exception("Counter doesn't exist!");

					var valueReader = it.CurrentKey.CreateReader();
					valueReader.Skip(it.RequiredPrefix.Size);
					var counterIdBuffer = new byte[sizeof(long)];
					valueReader.Read(counterIdBuffer, 0, sizeof(long));

					return CalculateCounterTotal(counterIdBuffer, new byte[parent.sizeOfGuid]);
				}
			}

			public IEnumerable<CounterGroup> GetCounterGroups()
			{
				using (var it = groupToCounters.Iterate())
				{
					if (it.Seek(Slice.BeforeAllKeys) == false)
						yield break;

					do
					{
						yield return new CounterGroup
						{
							Name = it.CurrentKey.ToString(),
							Count = groupToCounters.State.EntriesCount
						};
					} while (it.MoveNext());
				}
			}

			//{counterId}{serverId}{sign}
			internal long GetSingleCounterValue(Slice singleCounterName)
			{
				var readResult = counters.Read(singleCounterName);
				if (readResult == null)
					return -1;

				return readResult.Reader.ReadLittleEndianInt64();
			}

			//namePrefix: foo/bar/
			public Counter GetCounterValuesByPrefix(string namePrefix)
			{
				using (var it = counters.Iterate())
				{
					it.RequiredPrefix = namePrefix;
					if (it.Seek(namePrefix) == false)
						return null;

					var result = new Counter();
					do
					{
						var counterValue = new CounterValue(it.CurrentKey.ToString(), it.CreateReaderForCurrent().ReadLittleEndianInt64());
						if (counterValue.ServerId().Equals(parent.tombstoneId) && counterValue.Value == DateTime.MaxValue.Ticks)
							continue;

						result.CounterValues.Add(counterValue);
					} while (it.MoveNext());
					return result;
				}
			}

			public IEnumerable<ReplicationCounter> GetCountersSinceEtag(long etag)
			{
				using (var it = etagsToCounters.Iterate())
				{
					var buffer = new byte[sizeof(long)];
					EndianBitConverter.Big.CopyBytes(etag, buffer, 0);
					var slice = new Slice(buffer);
					if (it.Seek(slice) == false)
						yield break;

					var counterIdBuffer = new byte[sizeof(long)];
					var serverIdBuffer = new byte[parent.sizeOfGuid];
					var counterNameBuffer = new byte[0];
					var groupNameBuffer = new byte[0];
					var signBuffer = new byte[sizeof (char)];
					do
					{
						//{counterId}{serverId}{sign}
						var valueReader = it.CreateReaderForCurrent();
						valueReader.Read(counterIdBuffer, 0, sizeof(long));
						valueReader.Read(serverIdBuffer, 0, parent.sizeOfGuid);
						var serverId = new Guid(serverIdBuffer);

						valueReader.Read(signBuffer, 0, sizeof(char));
						var sign = EndianBitConverter.Big.ToChar(signBuffer, 0);
						Debug.Assert(sign == ValueSign.Positive || sign == ValueSign.Negative);
						var singleCounterName = valueReader.AsSlice();
						var value = GetSingleCounterValue(singleCounterName);
						
						//read counter name and group
						var counterNameAndGroup = GetCounterNameAndGroupByServerId(counterIdBuffer, counterNameBuffer, groupNameBuffer);

						valueReader.Reset();
						var etagResult = countersToEtag.Read(singleCounterName);
						var counterEtag = etagResult == null ? 0 : etagResult.Reader.ReadBigEndianInt64();

						yield return new ReplicationCounter
						{
							GroupName = counterNameAndGroup.GroupName,
							CounterName = counterNameAndGroup.CounterName,
							ServerId = serverId,
							Sign = sign,
							Value = value,	
							Etag = counterEtag
						};
					} while (it.MoveNext());
				}
			}

			private class CounterNameAndGroup
			{
				public string CounterName { get; set; }
				public string GroupName { get; set; }
			}

			private CounterNameAndGroup GetCounterNameAndGroupByServerId(byte[] counterIdBuffer, byte[] counterNameBuffer, byte[] groupNameBuffer)
			{
				var counterNameAndGroup = new CounterNameAndGroup();
				using (var it = counterIdWithNameToGroup.Iterate())
				{
					var slice = new Slice(counterIdBuffer);
					it.RequiredPrefix = slice;
					if (it.Seek(it.RequiredPrefix) == false)
						throw new InvalidOperationException("Couldn't find counter id!");

					var counterNameSize = it.CurrentKey.Size - sizeof(long);
					EnsureBufferSize(ref counterNameBuffer, counterNameSize);
					var reader = it.CurrentKey.CreateReader();
					reader.Skip(sizeof(long));
					reader.Read(counterNameBuffer, 0, counterNameSize);
					counterNameAndGroup.CounterName = Encoding.UTF8.GetString(counterNameBuffer, 0, counterNameSize);
					
					var valueReader = it.CreateReaderForCurrent();
					EnsureBufferSize(ref groupNameBuffer, valueReader.Length);
					valueReader.Read(groupNameBuffer, 0, valueReader.Length);
					counterNameAndGroup.GroupName = Encoding.UTF8.GetString(groupNameBuffer, 0, groupNameBuffer.Length);
				}
				return counterNameAndGroup;
			}

			public IEnumerable<ServerEtag> GetServerEtags()
			{
				using (var it = serversLastEtag.Iterate())
				{
					if (it.Seek(Slice.BeforeAllKeys) == false)
						yield break;

					do
					{
						//should never ever happen :)
						/*Debug.Assert(buffer.Length >= it.GetCurrentDataSize());

						it.CreateReaderForCurrent().Read(buffer, 0, buffer.Length);*/
						yield return new ServerEtag
						{
							ServerId = Guid.Parse(it.CurrentKey.ToString()),
							Etag = it.CreateReaderForCurrent().ReadBigEndianInt64()
						};

					} while (it.MoveNext());
				}
			}

			public long GetLastEtagFor(Guid serverId)
			{
				var slice = new Slice(serverId.ToByteArray());
				var readResult = serversLastEtag.Read(slice);
				return readResult != null ? readResult.Reader.ReadBigEndianInt64() : 0;
			}

			public CountersReplicationDocument GetReplicationData()
			{
				var readResult = metadata.Read("replication");
				if (readResult == null)
					return null;

				var stream = readResult.Reader.AsStream();
				stream.Position = 0;
				using (var streamReader = new StreamReader(stream))
				using (var jsonTextReader = new JsonTextReader(streamReader))
				{
					return new JsonSerializer().Deserialize<CountersReplicationDocument>(jsonTextReader);
				}
			}

			public void Dispose()
			{
				if (transaction != null)
					transaction.Dispose();
			}
		}

		[CLSCompliant(false)]
		public class Writer : IDisposable
		{
			private readonly CounterStorage parent;
			private readonly Transaction transaction;
			private readonly Reader reader;
			private readonly Tree counters, dateToTombstones, groupToCounters, tombstonesGroupToCounters, counterIdWithNameToGroup, etagsToCounters, countersToEtag, serversLastEtag, metadata;
			private readonly Buffer buffer;

			private class Buffer
			{
				public Buffer(int sizeOfGuid)
				{
					FullCounterName = new byte[sizeof(long) + sizeOfGuid + sizeof(char)];
					FullTombstoneName = new byte[sizeof(long) + sizeOfGuid + sizeof(char)];
				}

				public readonly byte[] FullCounterName;
				public readonly byte[] FullTombstoneName;
				public readonly byte[] Etag = new byte[sizeof(long)];
				public readonly byte[] CounterValue = new byte[sizeof(long)];
				public readonly byte[] CounterId = new byte[sizeof(long)];
				public readonly byte[] TombstoneTicks = new byte[sizeof(long)];
				public byte[] CounterNameBuffer = new byte[0];
			}

			public Writer(CounterStorage parent, Transaction transaction)
			{
				if (transaction.Flags != TransactionFlags.ReadWrite) //precaution
					throw new InvalidOperationException(string.Format("Counters writer cannot be created with read-only transaction. (tx id = {0})", transaction.Id));

				this.parent = parent;
				this.transaction = transaction;
				reader = new Reader(parent, transaction);
				counters = transaction.State.GetTree(transaction, TreeNames.Counters);
				dateToTombstones = transaction.State.GetTree(transaction, TreeNames.DateToTombstones);
				groupToCounters = transaction.State.GetTree(transaction, TreeNames.GroupToCounters);
				tombstonesGroupToCounters = transaction.State.GetTree(transaction, TreeNames.TombstonesGroupToCounters);
				counterIdWithNameToGroup = transaction.State.GetTree(transaction, TreeNames.CounterIdWithNameToGroup);
				countersToEtag = transaction.State.GetTree(transaction, TreeNames.CountersToEtag);
				etagsToCounters = transaction.State.GetTree(transaction, TreeNames.EtagsToCounters);
				serversLastEtag = transaction.State.GetTree(transaction, TreeNames.ServersLastEtag);
				metadata = transaction.State.GetTree(transaction, TreeNames.Metadata);
				buffer = new Buffer(parent.sizeOfGuid);
			}

			private bool DoesCounterExist(string groupName, string counterName)
			{
				return reader.DoesCounterExist(groupName, counterName);
			}

			public long GetLastEtagFor(Guid serverId)
			{
				return reader.GetLastEtagFor(serverId);
			}

			public long GetCounterTotal(string groupName, string counterName)
			{
				return reader.GetCounterTotal(groupName, counterName);
			}

			//Local Counters
			public CounterChangeAction Store(string groupName, string counterName, long delta)
			{
				var sign = delta >= 0 ? ValueSign.Positive : ValueSign.Negative;
				var doesCounterExist = Store(groupName, counterName, parent.ServerId, sign, counterKeySlice =>
				{
					if (sign == ValueSign.Negative)
						delta = -delta;
					counters.Increment(counterKeySlice, delta);
				});

				if (doesCounterExist)
					return sign == ValueSign.Positive ? CounterChangeAction.Increment : CounterChangeAction.Decrement;

				return CounterChangeAction.Add;
			}

			//Counters from replication
			public CounterChangeAction Store(string groupName, string counterName, Guid serverId, char sign, long value)
			{
				var doesCounterExist = Store(groupName, counterName, serverId, sign, counterKeySlice =>
				{
					//counter value is little endian
					EndianBitConverter.Little.CopyBytes(value, buffer.CounterValue, 0);
					var counterValueSlice = new Slice(buffer.CounterValue);
					counters.Add(counterKeySlice, counterValueSlice);

					if (serverId.Equals(parent.tombstoneId))
					{
						//tombstone key is big endian
						Array.Reverse(buffer.CounterValue);
						var tombstoneKeySlice = new Slice(buffer.CounterValue);
						dateToTombstones.MultiAdd(tombstoneKeySlice, counterKeySlice);
					}	
				});

				if (serverId.Equals(parent.tombstoneId))
					return CounterChangeAction.Delete;

				if (doesCounterExist)
					return value >= 0 ? CounterChangeAction.Increment : CounterChangeAction.Decrement;

				return CounterChangeAction.Add;
			}

			// full counter name: foo/bar/server-id/+
			private bool Store(string groupName, string counterName, Guid serverId, char sign, Action<Slice> storeAction)
			{
				var groupSize = Encoding.UTF8.GetByteCount(groupName);
				var sliceWriter = new SliceWriter(groupSize);
				sliceWriter.Write(groupName);
				var groupNameSlice = sliceWriter.CreateSlice();
				var counterIdBuffer = GetCounterIdBufferFromTree(groupToCounters, groupNameSlice, counterName);
				var doesCounterExist = counterIdBuffer != null;
				if (doesCounterExist == false)
				{
					counterIdBuffer = GetCounterIdBufferFromTree(tombstonesGroupToCounters, groupNameSlice, counterName);
					if (counterIdBuffer == null)
					{
						parent.lastCounterId++;
						EndianBitConverter.Big.CopyBytes(parent.lastCounterId, buffer.CounterId, 0);
						var slice = new Slice(buffer.CounterId);
						metadata.Add("lastCounterId", slice);
						counterIdBuffer = buffer.CounterId;
					}
				}

				UpdateGroups(counterName, counterIdBuffer, serverId, groupNameSlice);

				var counterKeySlice = GetFullCounterNameSlice(counterIdBuffer, serverId, sign);

				storeAction(counterKeySlice);

				RemoveOldEtagIfNeeded(counterKeySlice);
				UpdateCounterMetadata(counterKeySlice);

				parent.replicationTask.SignalCounterUpdate();
				return doesCounterExist;


				


				/*var counterNameSize = Encoding.UTF8.GetByteCount(counterName);
				var fullCounterNameSize = groupSize + 
										  (sizeof(byte) * 3) + 
										  counterNameSize + 
									      32 + 
										  sizeof(byte);

				sliceWriter = GetFullCounterNameAsSliceWriter(buffer.FullCounterName,
					groupName,
					counterName,
					serverId,
					sign,
					fullCounterNameSize);

				var groupAndCounterNameSlice = sliceWriter.CreateSlice(groupSize + counterNameSize + (2*sizeof (byte)));
				//var doesCounterExist = DoesCounterExist(groupAndCounterNameSlice);
				var groupKey = sliceWriter.CreateSlice(groupSize);
				if (serverId.Equals(parent.tombstoneId))
				{
					//if it's a tombstone, we can remove the counter from the GroupsToCounters Tree
					GetOrCreateCounterId(groupKey, counterName);

					/*var readResult = countersGroups.Read(groupKey);
					if (readResult != null)
					{
						if (readResult.Reader.ReadLittleEndianInt64() == 1)
							countersGroups.Delete(groupKey);
						else
							countersGroups.Increment(groupKey, -1);	
					}
					groupAndCounterName.Delete(groupAndCounterNameSlice);#1#
				}
				else if (doesCounterExist == false)
				{
					//if the counter doesn't exist we need to update the appropriate trees
					groupToCounters.MultiAdd(groupKey, counterName);
					/*countersGroups.Increment(groupKey, 1);
					groupAndCounterName.Add(groupAndCounterNameSlice, new byte[0]);

					DeleteExistingTombstone(groupName, counterName, fullCounterNameSize);#1#
				}*/

				//save counter full name and its value into the counters tree
				
			}

			private void UpdateGroups(string counterName, byte[] counterId, Guid serverId, Slice groupNameSlice)
			{
				var sliceWriter = new SliceWriter(Encoding.UTF8.GetByteCount(counterName) + sizeof(long));
				sliceWriter.Write(counterName);
				sliceWriter.WriteBytes(counterId);
				var counterWithIdSlice = sliceWriter.CreateSlice();

				if (serverId.Equals(parent.tombstoneId))
				{
					//if it's a tombstone, we can remove the counter from the groupToCounters Tree
					//and add it to the tombstonesGroupToCounters tree
					groupToCounters.MultiDelete(groupNameSlice, counterWithIdSlice);
					tombstonesGroupToCounters.MultiAdd(groupNameSlice, counterWithIdSlice);
				}
				else
				{
					//if it's not a tombstone, we need to add it to the groupToCounters Tree
					//and remove it from the tombstonesGroupToCounters tree
					groupToCounters.MultiAdd(groupNameSlice, counterWithIdSlice);
					tombstonesGroupToCounters.MultiDelete(groupNameSlice, counterWithIdSlice);

					DeleteExistingTombstone(counterId);
				}

				sliceWriter.ResetSliceWriter();
				sliceWriter.WriteBytes(counterId);
				sliceWriter.Write(counterName);
				var idWithCounterNameSlice = sliceWriter.CreateSlice();
				counterIdWithNameToGroup.Add(idWithCounterNameSlice, groupNameSlice);
			}


			private Slice GetFullCounterNameSlice(byte[] counterIdBytes, Guid serverId, char sign)
			{
				var sliceWriter = new SliceWriter(buffer.FullCounterName);
				sliceWriter.WriteBytes(counterIdBytes);
				sliceWriter.WriteBytes(serverId.ToByteArray());
				sliceWriter.Write(sign);
				return sliceWriter.CreateSlice();
			}

			private byte[] GetCounterIdBufferFromTree(Tree tree, Slice groupNameSlice, string counterName)
			{
				using (var it = tree.MultiRead(groupNameSlice))
				{
					it.RequiredPrefix = counterName;
					if (it.Seek(it.RequiredPrefix) == false || it.CurrentKey.Size != it.RequiredPrefix.Size + sizeof (long))
						return null;

					var valueReader = it.CurrentKey.CreateReader();
					valueReader.Skip(it.RequiredPrefix.Size);
					valueReader.Read(buffer.CounterId, 0, sizeof(long));
					return buffer.CounterId;
				}
			}

			private void DeleteExistingTombstone(byte[] counterIdBuffer)
			{
				var tombstoneNameSlice = GetTombstoneSlice(counterIdBuffer);
				var tombstone = counters.Read(tombstoneNameSlice);
				if (tombstone == null)
					return;

				//delete the tombstone from the tombstones tree
				tombstone.Reader.Read(buffer.TombstoneTicks, 0, sizeof (long));
				Array.Reverse(buffer.TombstoneTicks);
				var slice = new Slice(buffer.TombstoneTicks);
				dateToTombstones.MultiDelete(slice, tombstoneNameSlice);

				//Update the tombstone in the counters tree
				counters.Delete(tombstoneNameSlice);
				RemoveOldEtagIfNeeded(tombstoneNameSlice);
				countersToEtag.Delete(tombstoneNameSlice);
			}

			private Slice GetTombstoneSlice(byte[] counterIdBuffer)
			{
				var sliceWriter = new SliceWriter(buffer.FullTombstoneName);
				sliceWriter.WriteBytes(counterIdBuffer);
				sliceWriter.WriteBytes(parent.tombstoneId.ToByteArray());
				sliceWriter.Write(ValueSign.Positive);
				return sliceWriter.CreateSlice();
			}

			private void RemoveOldEtagIfNeeded(Slice counterKey)
			{
				var readResult = countersToEtag.Read(counterKey);
				if (readResult != null) // remove old etag entry
				{
					readResult.Reader.Read(buffer.Etag, 0, sizeof(long));
					var oldEtagSlice = new Slice(buffer.Etag);
					etagsToCounters.Delete(oldEtagSlice);
				}
			}

			private void UpdateCounterMetadata(Slice counterKey)
			{
				parent.lastEtag++;
				EndianBitConverter.Big.CopyBytes(parent.lastEtag, buffer.Etag, 0);
				var newEtagSlice = new Slice(buffer.Etag);
				etagsToCounters.Add(newEtagSlice, counterKey);
				countersToEtag.Add(counterKey, newEtagSlice);
			}

			public long Reset(string groupName, string counterName)
			{
				var doesCounterExist = DoesCounterExist(groupName, counterName);
				if (doesCounterExist == false)
					throw new InvalidOperationException(string.Format("Counter doesn't exist. Group: {0}, Counter Name: {1}", groupName, counterName));

				return ResetCounterInternal(groupName, counterName);
			}

			private long ResetCounterInternal(string groupName, string counterName)
			{
				var difference = GetCounterTotal(groupName, counterName);
				if (difference == 0)
					return 0;

				difference = -difference;
				Store(groupName, counterName, difference);
				return difference;
			}

			public void Delete(string groupName, string counterName)
			{
				var counterExists = DoesCounterExist(groupName, counterName);
				if (counterExists == false)
					throw new InvalidOperationException(string.Format("Counter doesn't exist. Group: {0}, Counter Name: {1}", groupName, counterName));

				ResetCounterInternal(groupName, counterName);
				Store(groupName, counterName, parent.tombstoneId, ValueSign.Positive, counterKeySlice =>
				{
					//counter value is little endian
					EndianBitConverter.Little.CopyBytes(DateTime.Now.Ticks, buffer.CounterValue, 0);
					var counterValueSlice = new Slice(buffer.CounterValue);
					counters.Add(counterKeySlice, counterValueSlice);

					//all keys are big endian
					Array.Reverse(buffer.CounterValue);
					var tombstoneKeySlice = new Slice(buffer.CounterValue);
					dateToTombstones.MultiAdd(tombstoneKeySlice, counterKeySlice);
				});
			}

			public void RecordLastEtagFor(Guid serverId, long lastEtag)
			{
				var serverIdSlice = new Slice(serverId.ToByteArray());
				EndianBitConverter.Big.CopyBytes(lastEtag, buffer.Etag, 0);
				var etagSlice = new Slice(buffer.Etag);
				serversLastEtag.Add(serverIdSlice, etagSlice);
			}

			public long GetSingleCounterValue(string groupName, string counterName, Guid serverId, char sign)
			{
				var counterIdBuffer = GetCounterIdBuffer(groupName, counterName);
				if (counterIdBuffer == null)
					return -1;

				var slice = GetFullCounterNameSlice(counterIdBuffer, serverId, sign);
				return reader.GetSingleCounterValue(slice);
			}

			private byte[] GetCounterIdBuffer(string groupName, string counterName)
			{
				var groupSize = Encoding.UTF8.GetByteCount(groupName);
				var sliceWriter = new SliceWriter(groupSize);
				sliceWriter.Write(groupName);
				var groupNameSlice = sliceWriter.CreateSlice();

				var counterIdBuffer = GetCounterIdBufferFromTree(groupToCounters, groupNameSlice, counterName);
				if (counterIdBuffer != null)
					return counterIdBuffer;

				return GetCounterIdBufferFromTree(tombstonesGroupToCounters, groupNameSlice, counterName);
			}

			public void UpdateReplications(CountersReplicationDocument newReplicationDocument)
			{
				using (var memoryStream = new MemoryStream())
				using (var streamWriter = new StreamWriter(memoryStream))
				using (var jsonTextWriter = new JsonTextWriter(streamWriter))
				{
					parent.JsonSerializer.Serialize(jsonTextWriter, newReplicationDocument);
					streamWriter.Flush();
					memoryStream.Position = 0;
					metadata.Add("replication", memoryStream);
				}

				parent.replicationTask.SignalCounterUpdate();
			}

			public bool PurgeOutdatedTombstones()
			{
				var timeAgo = DateTime.Now.AddTicks(-parent.tombstoneRetentionTime.Ticks);
				EndianBitConverter.Big.CopyBytes(timeAgo.Ticks, buffer.TombstoneTicks, 0);
				var tombstone = new Slice(buffer.TombstoneTicks);
				var deletedTombstonesInBatch = parent.deletedTombstonesInBatch;
				using (var it = dateToTombstones.Iterate())
				{
					it.RequiredPrefix = tombstone;
					if (it.Seek(it.RequiredPrefix) == false)
						return false;

					do
					{
						using (var iterator = dateToTombstones.MultiRead(it.CurrentKey))
						{
							var valueReader = iterator.CurrentKey.CreateReader();
							valueReader.Read(buffer.CounterId, 0, iterator.CurrentKey.Size - sizeof(long));
							DeleteCounterById(buffer.CounterId);
							dateToTombstones.MultiDelete(it.CurrentKey, iterator.CurrentKey);
						}
					} while (it.MoveNext() && --deletedTombstonesInBatch > 0);
				}

				return true;
			}

			private void DeleteCounterById(byte[] counterIdBuffer)
			{
				var counterIdSlice = new Slice(counterIdBuffer);
				using (var it = counterIdWithNameToGroup.Iterate())
				{
					it.RequiredPrefix = counterIdSlice;
					var seek = it.Seek(it.RequiredPrefix);
					Debug.Assert(seek == true);

					var counterNameSlice = GetCounterNameSlice(it);
					var valueReader = it.CreateReaderForCurrent();
					var groupNameSlice = valueReader.AsSlice();

					tombstonesGroupToCounters.MultiDelete(groupNameSlice, counterNameSlice);
					counterIdWithNameToGroup.Delete(it.CurrentKey);
				}

				//remove all counters values for all servers
				using (var it = counters.Iterate())
				{
					it.RequiredPrefix = counterIdSlice;
					if (it.Seek(it.RequiredPrefix) == false)
						return;

					do
					{
						var counterKey = it.CurrentKey;
						RemoveOldEtagIfNeeded(counterKey);
						countersToEtag.Delete(counterKey);
						counters.Delete(counterKey);
					} while (it.MoveNext());
				}
			}

			private Slice GetCounterNameSlice(TreeIterator it)
			{
				var counterNameSize = it.CurrentKey.Size - sizeof(long);
				EnsureBufferSize(ref buffer.CounterNameBuffer, counterNameSize);
				var currentReader = it.CurrentKey.CreateReader();
				currentReader.Skip(sizeof(long));
				currentReader.Read(buffer.CounterNameBuffer, 0, counterNameSize);
				var counterNameSlice = new Slice(buffer.CounterNameBuffer);
				return counterNameSlice;
			}

			public void Commit(bool notifyParent = true)
			{
				transaction.Commit();
				parent.LastWrite = SystemTime.UtcNow;
				if (notifyParent)
				{
					parent.Notify();
				}
			}

			public void Dispose()
			{
				//parent.LastWrite = SystemTime.UtcNow;
				if (transaction != null)
					transaction.Dispose();
			}
		}

		private static void EnsureBufferSize(ref byte[] buffer, int requiredBufferSize)
		{
			if (buffer.Length < requiredBufferSize)
				buffer = new byte[Utils.NearestPowerOfTwo(requiredBufferSize)];
		}

		public class ServerEtag
		{
			public Guid ServerId { get; set; }
			public long Etag { get; set; }
		}

		private static class TreeNames
		{
			public const string ServersLastEtag = "servers->lastEtag";
			public const string Counters = "counters";
			public const string DateToTombstones = "date->tombstones";
			public const string GroupToCounters = "group->counters";
			public const string TombstonesGroupToCounters = "tombstones-group->counters";
			public const string CounterIdWithNameToGroup = "counterIdWithName->group";
			public const string CountersToEtag = "counters->etags";
			public const string EtagsToCounters = "etags->counters";
			public const string Metadata = "$metadata";
		}
	}
}