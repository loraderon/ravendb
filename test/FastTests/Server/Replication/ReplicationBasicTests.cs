﻿using System;
using Raven.Client.Documents.Operations;
using Xunit;

namespace FastTests.Server.Replication
{
    public class ReplicationBasicTests : ReplicationTestsBase
    {
        public readonly string DbName = "TestDB" + Guid.NewGuid();

        public class User
        {
            public string Name { get; set; }
            public int Age { get; set; }
        }

     
        [Fact]
        public void Master_slave_replication_from_etag_zero_should_work()
        {
            var dbName1 = DbName + "-1";
            var dbName2 = DbName + "-2";
            using (var store1 = GetDocumentStore(dbSuffixIdentifier: dbName1))
            using (var store2 = GetDocumentStore(dbSuffixIdentifier: dbName2))
            {
                SetupReplication(store1, store2);

                using (var session = store1.OpenSession())
                {
                    session.Store(new User
                    {
                        Name = "John Dow",
                        Age = 30
                    }, "users/1");

                    session.Store(new User
                    {
                        Name = "Jane Dow",
                        Age = 31
                    }, "users/2");

                    session.SaveChanges();
                }

                var replicated1 = WaitForDocumentToReplicate<User>(store2, "users/1", 15000);

                Assert.NotNull(replicated1);
                Assert.Equal("John Dow", replicated1.Name);
                Assert.Equal(30, replicated1.Age);

                var replicated2 = WaitForDocumentToReplicate<User>(store2, "users/2", 5000);
                Assert.NotNull(replicated2);
                Assert.Equal("Jane Dow", replicated2.Name);
                Assert.Equal(31, replicated2.Age);
            }
        }

        [Fact]
        public void Master_slave_replication_with_multiple_PUTS_should_work()
        {
            var dbName1 = DbName + "-1";
            var dbName2 = DbName + "-2";
            using (var store1 = GetDocumentStore(dbSuffixIdentifier: dbName1))
            using (var store2 = GetDocumentStore(dbSuffixIdentifier: dbName2))
            {
                SetupReplication(store1, store2);

                using (var session = store1.OpenSession())
                {
                    session.Store(new User
                    {
                        Name = "John Dow",
                        Age = 30
                    }, "users/1");

                    session.Store(new User
                    {
                        Name = "Jane Dow",
                        Age = 31
                    }, "users/2");

                    session.SaveChanges();
                }

                using (var session = store1.OpenSession())
                {
                    session.Store(new User
                    {
                        Name = "Jack Dow",
                        Age = 33
                    }, "users/3");

                    session.Store(new User
                    {
                        Name = "Jessy Dow",
                        Age = 34
                    }, "users/4");

                    session.SaveChanges();
                }

                WaitForDocumentToReplicate<User>(store2, "users/1", 15000);
                WaitForDocumentToReplicate<User>(store2, "users/2", 15000);
                WaitForDocumentToReplicate<User>(store2, "users/3", 15000);
                WaitForDocumentToReplicate<User>(store2, "users/4", 15000);

                using (var session = store2.OpenSession())
                {
                    var docs = session.Load<User>(new[]
                    {
                        "users/1",
                        "users/2",
                        "users/3",
                        "users/4"
                    });

                    Assert.Contains(docs, d => d.Value.Name.Equals("John Dow"));
                    Assert.Contains(docs, d => d.Value.Name.Equals("Jane Dow"));
                    Assert.Contains(docs, d => d.Value.Name.Equals("Jack Dow"));
                    Assert.Contains(docs, d => d.Value.Name.Equals("Jessy Dow"));
                }

            }
        }

        [Fact]
        public void Master_master_replication_with_multiple_PUTS_should_work()
        {
            var dbName1 = DbName + "-1";
            var dbName2 = DbName + "-2";
            using (var store1 = GetDocumentStore(dbSuffixIdentifier: dbName1))
            using (var store2 = GetDocumentStore(dbSuffixIdentifier: dbName2))
            {
                SetupReplication(store1, store2);
                SetupReplication(store2, store1);

                using (var session = store1.OpenSession())
                {
                    session.Store(new User
                    {
                        Name = "John Dow",
                        Age = 30
                    }, "users/1");

                    session.Store(new User
                    {
                        Name = "Jane Dow",
                        Age = 31
                    }, "users/2");

                    session.SaveChanges();
                }

                using (var session = store2.OpenSession())
                {
                    session.Store(new User
                    {
                        Name = "Jack Dow",
                        Age = 33
                    }, "users/3");

                    session.Store(new User
                    {
                        Name = "Jessy Dow",
                        Age = 34
                    }, "users/4");

                    session.SaveChanges();
                }
                
                Assert.NotNull(WaitForDocumentToReplicate<User>(store1, "users/1", 15000));
                Assert.NotNull(WaitForDocumentToReplicate<User>(store1, "users/2", 15000));
                Assert.NotNull(WaitForDocumentToReplicate<User>(store1, "users/3", 15000));
                Assert.NotNull(WaitForDocumentToReplicate<User>(store1, "users/4", 15000));

                Assert.NotNull(WaitForDocumentToReplicate<User>(store2, "users/1", 15000));
                Assert.NotNull(WaitForDocumentToReplicate<User>(store2, "users/2", 15000));
                Assert.NotNull(WaitForDocumentToReplicate<User>(store2, "users/3", 15000));
                Assert.NotNull(WaitForDocumentToReplicate<User>(store2, "users/4", 15000));

                using (var session = store1.OpenSession())
                {
                    var docs = session.Load<User>(new[]
                    {
                        "users/1",
                        "users/2",
                        "users/3",
                        "users/4"
                    });

                    Assert.Contains(docs, d => d.Value.Name.Equals("John Dow"));
                    Assert.Contains(docs, d => d.Value.Name.Equals("Jane Dow"));
                    Assert.Contains(docs, d => d.Value.Name.Equals("Jack Dow"));
                    Assert.Contains(docs, d => d.Value.Name.Equals("Jessy Dow"));
                }

                using (var session = store2.OpenSession())
                {
                    var docs = session.Load<User>(new[]
                    {
                        "users/1",
                        "users/2",
                        "users/3",
                        "users/4"
                    });

                    Assert.Contains(docs, d => d.Value.Name.Equals("John Dow"));
                    Assert.Contains(docs, d => d.Value.Name.Equals("Jane Dow"));
                    Assert.Contains(docs, d => d.Value.Name.Equals("Jack Dow"));
                    Assert.Contains(docs, d => d.Value.Name.Equals("Jessy Dow"));
                }

            }
        }

        [Fact]
        public void Master_slave_replication_with_exceptions_should_work()
        {
            var dbName1 = DbName + "-1";
            var dbName2 = DbName + "-2";
            using (var store1 = GetDocumentStore(dbSuffixIdentifier: dbName1))
            using (var store2 = GetDocumentStore(dbSuffixIdentifier: dbName2))
            {
                //TODO : configure test code to throw exceptions at server-side during replication
                //TODO : (find a way to do so)

                SetupReplication(store1, store2);

                using (var session = store1.OpenSession())
                {
                    session.Store(new User
                    {
                        Name = "John Dow",
                        Age = 30
                    }, "users/1");

                    session.Store(new User
                    {
                        Name = "Jane Dow",
                        Age = 31
                    }, "users/2");

                    session.SaveChanges();
                }

                var replicated1 = WaitForDocumentToReplicate<User>(store2, "users/1", 15000);

                Assert.NotNull(replicated1);
                Assert.Equal("John Dow", replicated1.Name);
                Assert.Equal(30, replicated1.Age);

                var replicated2 = WaitForDocumentToReplicate<User>(store2, "users/2", 5000);
                Assert.NotNull(replicated2);
                Assert.Equal("Jane Dow", replicated2.Name);
                Assert.Equal(31, replicated2.Age);
            }
        }

        [Fact]
        public void Can_get_performance_stats()
        {
            var dbName1 = DbName + "-1";
            var dbName2 = DbName + "-2";
            using (var store1 = GetDocumentStore(dbSuffixIdentifier: dbName1))
            using (var store2 = GetDocumentStore(dbSuffixIdentifier: dbName2))
            {
                SetupReplication(store1, store2); // master-slave

                using (var session = store1.OpenSession())
                {
                    session.Store(new User
                    {
                        Name = "John Dow",
                        Age = 30
                    }, "users/1");

                    session.Store(new User
                    {
                        Name = "Jane Dow",
                        Age = 31
                    }, "users/2");

                    session.SaveChanges();
                }

                Assert.NotNull(WaitForDocumentToReplicate<User>(store2, "users/1", 15000));
                Assert.NotNull(WaitForDocumentToReplicate<User>(store2, "users/2", 5000));

                var stats1 = store1.Admin.Send(new GetReplicationPerformanceStatisticsOperation());
                var stats2 = store2.Admin.Send(new GetReplicationPerformanceStatisticsOperation());

                Assert.NotEmpty(stats1.Outgoing);
                Assert.Empty(stats1.Incoming);

                Assert.Empty(stats2.Outgoing);
                Assert.NotEmpty(stats2.Incoming);
            }
        }
    }
}
