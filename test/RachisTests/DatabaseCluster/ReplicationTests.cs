﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Replication;
using Raven.Client.Documents;
using Raven.Client.Exceptions.Security;
using Raven.Client.Server;
using Raven.Client.Server.Operations;
using Raven.Client.Server.Operations.Certificates;
using Raven.Server.Web.System;
using Raven.Tests.Core.Utils.Entities;
using Xunit;

namespace RachisTests.DatabaseCluster
{
    public class ReplicationTests : ReplicationTestsBase
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EnsureDocumentsReplication(bool useSsl)
        {
            var clusterSize = 5;
            var databaseName = "ReplicationTestDB";
            var leader = await CreateRaftClusterAndGetLeader(clusterSize, false, useSsl: useSsl);
            CreateDatabaseResult databaseResult;
            using (var store = new DocumentStore()
            {
                Urls = leader.WebUrls,
                Database = databaseName,
                Conventions =
                {
                    DisableTopologyUpdates = true
                }
            }.Initialize())
            {
                var doc = MultiDatabase.CreateDatabaseDocument(databaseName);
                databaseResult = await store.Admin.Server.SendAsync(new CreateDatabaseOperation(doc, clusterSize));
            }
            Assert.Equal(clusterSize, databaseResult.Topology.AllNodes.Count());
            foreach (var server in Servers)
            {
                await server.ServerStore.Cluster.WaitForIndexNotification(databaseResult.RaftCommandIndex);
            }
            foreach (var server in Servers.Where(s => databaseResult.NodesAddedTo.Any(n => n == s.WebUrls[0])))
            {
                await server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName);
            }

            using (var store = new DocumentStore()
            {
                Urls = new[] { databaseResult.NodesAddedTo[0] },
                Database = databaseName,
                Conventions =
                {
                    DisableTopologyUpdates = true
                }
            }.Initialize())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Karmel" }, "users/1");
                    await session.SaveChangesAsync();
                }
                Assert.True(await WaitForDocumentInClusterAsync<User>(
                    databaseResult.Topology,
                    databaseName,
                    "users/1",
                    u => u.Name.Equals("Karmel"),
                    TimeSpan.FromSeconds(clusterSize + 5)));

            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EnsureReplicationToWatchers(bool useSsl)
        {
            var clusterSize = 3;
            var databaseName = "ReplicationTestDB";
            var leader = await CreateRaftClusterAndGetLeader(clusterSize, useSsl: useSsl);
            var watchers = new List<ExternalReplication>();

            using (var store = new DocumentStore()
            {
                Urls = leader.WebUrls,
                Database = databaseName,
                Conventions =
                {
                    DisableTopologyUpdates = true
                }
            }.Initialize())
            {
                var doc = MultiDatabase.CreateDatabaseDocument(databaseName);
                var databaseResult = await store.Admin.Server.SendAsync(new CreateDatabaseOperation(doc, clusterSize));
                Assert.Equal(clusterSize, databaseResult.Topology.AllNodes.Count());
                foreach (var server in Servers)
                {
                    await server.ServerStore.Cluster.WaitForIndexNotification(databaseResult.RaftCommandIndex);
                }
                foreach (var server in Servers)
                {
                    await server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName);
                }
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Karmel" }, "users/1");
                    await session.SaveChangesAsync();
                }
                Assert.True(await WaitForDocumentInClusterAsync<User>(
                    databaseResult.Topology,
                    databaseName,
                    "users/1",
                    u => u.Name.Equals("Karmel"),
                    TimeSpan.FromSeconds(clusterSize + 5)));

                for (var i = 0; i < 5; i++)
                {
                    doc = MultiDatabase.CreateDatabaseDocument($"Watcher{i}");
                    var res = await store.Admin.Server.SendAsync(new CreateDatabaseOperation(doc));
                    var server = Servers.Single(x => x.WebUrls[0] == res.NodesAddedTo[0]);
                    await server.ServerStore.Cluster.WaitForIndexNotification(res.RaftCommandIndex);
                    await server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore($"Watcher{i}");

                    var watcher = new ExternalReplication
                    {
                        Database = $"Watcher{i}",
                        Url = res.NodesAddedTo[0]
                    };
                    watchers.Add(watcher);

                    await AddWatcherToReplicationTopology((DocumentStore)store, watcher);

                }
            }

            foreach (var watcher in watchers)
            {
                using (var store = new DocumentStore
                {
                    Urls = new[] { watcher.Url },
                    Database = watcher.Database,
                    Conventions =
                    {
                        DisableTopologyUpdates = true
                    }
                }.Initialize())
                {
                    Assert.True(WaitForDocument<User>(store, "users/1", u => u.Name == "Karmel"));
                }
            }
        }

        [Fact]
        public async Task CanAddAndModifySingleWatcher()
        {
            var clusterSize = 3;
            var databaseName = "ReplicationTestDB";
            var leader = await CreateRaftClusterAndGetLeader(clusterSize);
            ExternalReplication watcher;

            using (var store = new DocumentStore()
            {
                Urls = leader.WebUrls,
                Database = databaseName,
                Conventions =
                {
                    DisableTopologyUpdates = true
                }
            }.Initialize())
            {
                var doc = MultiDatabase.CreateDatabaseDocument(databaseName);
                var databaseResult = await store.Admin.Server.SendAsync(new CreateDatabaseOperation(doc, clusterSize));
                Assert.Equal(clusterSize, databaseResult.Topology.AllNodes.Count());
                foreach (var server in Servers)
                {
                    await server.ServerStore.Cluster.WaitForIndexNotification(databaseResult.RaftCommandIndex);
                }
                foreach (var server in Servers)
                {
                    await server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName);
                }
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Karmel" }, "users/1");
                    await session.SaveChangesAsync();
                }
                Assert.True(await WaitForDocumentInClusterAsync<User>(
                    databaseResult.Topology,
                    databaseName,
                    "users/1",
                    u => u.Name.Equals("Karmel"),
                    TimeSpan.FromSeconds(clusterSize + 5)));


                doc = MultiDatabase.CreateDatabaseDocument("Watcher");
                var res = await store.Admin.Server.SendAsync(new CreateDatabaseOperation(doc));
                var node = Servers.Single(x => x.WebUrls[0] == res.NodesAddedTo[0]);
                await node.ServerStore.Cluster.WaitForIndexNotification(res.RaftCommandIndex);
                await node.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore("Watcher");

                watcher = new ExternalReplication
                {
                    Database = "Watcher",
                    Url = res.NodesAddedTo[0],
                    Name = "MyExternalReplication1"
                };

                await AddWatcherToReplicationTopology((DocumentStore)store, watcher);
            }

            var tasks = OngoingTasksHandler.GetOngoingTasksFor(databaseName, leader.ServerStore);
            Assert.Equal(1, tasks.OngoingTasksList.Count);
            var repTask = tasks.OngoingTasksList[0] as OngoingTaskReplication;
            Assert.Equal(repTask?.DestinationDatabase, watcher.Database);
            Assert.Equal(repTask?.DestinationUrl, watcher.Url);
            Assert.Equal(repTask?.TaskName, watcher.Name);

            watcher.TaskId = Convert.ToInt64(repTask?.TaskId);

            using (var store = new DocumentStore
            {
                Urls = new[] { watcher.Url },
                Database = watcher.Database,
                Conventions =
                {
                    DisableTopologyUpdates = true
                }
            }.Initialize())
            {
                Assert.True(WaitForDocument<User>(store, "users/1", u => u.Name == "Karmel"));
            }

            using (var store = new DocumentStore
            {
                Urls = leader.WebUrls,
                Database = databaseName,
                Conventions =
                {
                    DisableTopologyUpdates = true
                }
            }.Initialize())
            {
                var doc = MultiDatabase.CreateDatabaseDocument("Watcher2");
                var res = await store.Admin.Server.SendAsync(new CreateDatabaseOperation(doc));
                var node = Servers.Single(x => x.WebUrls[0] == res.NodesAddedTo[0]);
                await node.ServerStore.Cluster.WaitForIndexNotification(res.RaftCommandIndex);
                await node.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore("Watcher2");

                //modify watcher
                watcher.Database = "Watcher2";
                watcher.Url = res.NodesAddedTo[0];
                watcher.Name = "MyExternalReplication2";

                await AddWatcherToReplicationTopology((DocumentStore)store, watcher);
            }

            tasks = OngoingTasksHandler.GetOngoingTasksFor(databaseName, leader.ServerStore);
            Assert.Equal(1, tasks.OngoingTasksList.Count);
            repTask = tasks.OngoingTasksList[0] as OngoingTaskReplication;
            Assert.Equal(repTask?.DestinationDatabase, watcher.Database);
            Assert.Equal(repTask?.DestinationUrl, watcher.Url);
            Assert.Equal(repTask?.TaskName, watcher.Name);

            using (var store = new DocumentStore
            {
                Urls = new[] { watcher.Url },
                Database = watcher.Database,
                Conventions =
                {
                    DisableTopologyUpdates = true
                }
            }.Initialize())
            {
                Assert.True(WaitForDocument<User>(store, "users/1", u => u.Name == "Karmel"));
            }

            //delete watcher
            using (var store = new DocumentStore
            {
                Urls = leader.WebUrls,
                Database = databaseName,
                Conventions =
                {
                    DisableTopologyUpdates = true
                }
            }.Initialize())
            {
                await DeleteOngoingTask((DocumentStore)store, watcher.TaskId, OngoingTaskType.Replication);
                tasks = OngoingTasksHandler.GetOngoingTasksFor(databaseName, leader.ServerStore);
                Assert.Equal(0, tasks.OngoingTasksList.Count);
            }
        }


        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DoNotReplicateBack(bool useSsl)
        {
            var clusterSize = 5;
            var databaseName = "ReplicationTestDB";
            var leader = await CreateRaftClusterAndGetLeader(clusterSize, useSsl: useSsl);
            using (var store = new DocumentStore()
            {
                Urls = leader.WebUrls,
                Database = databaseName,
                Conventions =
                {
                    DisableTopologyUpdates = true
                }
            }.Initialize())
            {
                var doc = MultiDatabase.CreateDatabaseDocument(databaseName);
                var databaseResult = await store.Admin.Server.SendAsync(new CreateDatabaseOperation(doc, clusterSize));
                var topology = databaseResult.Topology;
                Assert.Equal(clusterSize, topology.AllNodes.Count());

                await WaitForValueOnGroupAsync(topology, s =>
               {
                   var db = s.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName).Result;
                   return db.ReplicationLoader?.OutgoingConnections.Count();
               }, clusterSize - 1);

                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Karmel" }, "users/1");
                    await session.SaveChangesAsync();
                }
                Assert.True(await WaitForDocumentInClusterAsync<User>(
                    databaseResult.Topology,
                    databaseName,
                    "users/1",
                    u => u.Name.Equals("Karmel"),
                    TimeSpan.FromSeconds(clusterSize + 5)));

                topology.RemoveFromTopology(leader.ServerStore.NodeTag);
                await Task.Delay(200); // twice the heartbeat
                await Assert.ThrowsAsync<Exception>(async () =>
                {
                    await WaitForValueOnGroupAsync(topology, (s) =>
                    {
                        var db = s.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName).Result;
                        return db.ReplicationLoader?.OutgoingHandlers.Any(o => o.GetReplicationPerformance().Any(p => p.Network.DocumentOutputCount > 0)) ?? false;
                    }, true);
                });
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AddGlobalChangeVectorToNewDocument(bool useSsl)
        {
            var clusterSize = 3;
            var databaseName = "ReplicationTestDB";
            var leader = await CreateRaftClusterAndGetLeader(clusterSize, true, 0, useSsl: useSsl);
            var doc = MultiDatabase.CreateDatabaseDocument(databaseName);
            using (var store = new DocumentStore()
            {
                Urls = leader.WebUrls,
                Database = databaseName,
                Conventions =
                {
                    DisableTopologyUpdates = true
                }
            }.Initialize())
            {
                var databaseResult = await store.Admin.Server.SendAsync(new CreateDatabaseOperation(doc, clusterSize));
                var topology = databaseResult.Topology;
                Assert.Equal(clusterSize, topology.AllNodes.Count());
                foreach (var server in Servers)
                {
                    await server.ServerStore.Cluster.WaitForIndexNotification(databaseResult.RaftCommandIndex);
                }
                foreach (var server in Servers)
                {
                    await server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName);
                }
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Karmel" }, "users/1");
                    await session.SaveChangesAsync();
                }
                Assert.True(await WaitForDocumentInClusterAsync<User>(
                    topology,
                    databaseName,
                    "users/1",
                    u => u.Name.Equals("Karmel"),
                    TimeSpan.FromSeconds(clusterSize + 5)));
            }

            using (var store = new DocumentStore()
            {
                Urls = Servers[1].WebUrls,
                Database = databaseName,
                Conventions =
                {
                    DisableTopologyUpdates = true
                }
            }.Initialize())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Indych" }, "users/2");
                    await session.SaveChangesAsync();
                }

                using (var session = store.OpenAsyncSession())
                {
                    var user = await session.LoadAsync<User>("users/2");
                    Assert.Equal(2, session.Advanced.GetChangeVectorFor(user).Length);
                }
            }
        }
        
        [Fact]
        public async Task ReplicateToWatcherWithAuth()
        {
            var serverCertPath = SetupServerAuthentication();
            var clientCert1 = AskServerForClientCertificate(serverCertPath, new string[0], serverAdmin:true);
            var clientCert2 = AskServerForClientCertificate(serverCertPath, new[] { "ReplicateToWatcherWithAuthDB" });

            using (var store1 = GetDocumentStore(certificate: clientCert1, modifyName: s => "ReplicateToWatcherWithAuthDB"))
            using (var store2 = GetDocumentStore(certificate: clientCert2, modifyName: s => "ReplicateToWatcherWithAuthDB", createDatabase: false))
            {
                var watcher2 = new ExternalReplication
                {
                    Database = store2.Database,
                    Url = store2.Urls.First()
                };

                await AddWatcherToReplicationTopology(store1, watcher2);

                using (var session = store1.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Karmel" }, "users/1");
                    await session.SaveChangesAsync();
                }
                
                Assert.True(WaitForDocument<User>(store2, "users/1", (u) => u.Name == "Karmel"));
            }
        }

        [Fact]
        public async Task ReplicateToWatcherWithInvalidAuth()
        {
            var serverCertPath = SetupServerAuthentication();
            var clientCert1 = AskServerForClientCertificate(serverCertPath, new string[0], serverAdmin: true);
            var clientCert2 = AskServerForClientCertificate(serverCertPath, new[] { "OtherDB" });

            using (var store1 = GetDocumentStore(certificate: clientCert1, modifyName: s => "ReplicateToWatcherWithAuthDB"))
            using (var store2 = GetDocumentStore(certificate: clientCert2, modifyName: s => "ReplicateToWatcherWithAuthDB", createDatabase: false))
            {
                var watcher2 = new ExternalReplication
                {
                    Database = store2.Database,
                    Url = store2.Urls.First()
                };

                await AddWatcherToReplicationTopology(store1, watcher2);

                using (var session = store1.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Karmel" }, "users/1");
                    await Assert.ThrowsAsync<AuthorizationException>(async () => await session.SaveChangesAsync());
                }
                
            }
        }
    }
}
