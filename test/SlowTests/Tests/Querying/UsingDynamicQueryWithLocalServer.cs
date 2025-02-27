﻿//-----------------------------------------------------------------------
// <copyright file="UsingDynamicQueryWithLocalServer.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using FastTests;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries.Highlighting;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Tests.Querying
{
    public class UsingDynamicQueryWithLocalServer : RavenTestBase
    {
        public UsingDynamicQueryWithLocalServer(ITestOutputHelper output) : base(output)
        {
        }

        [RavenTheory(RavenTestCategory.Querying)]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.All, DatabaseMode = RavenDatabaseMode.All)]
        public void CanPerformDynamicQueryUsingClientLinqQueryWithNestedCollection(Options options)
        {
            var blogOne = new Blog
            {
                Title = "one",
                Category = "Ravens",
                Tags = new[]{
                     new BlogTag(){ Name = "Birds" }
                 }
            };
            var blogTwo = new Blog
            {
                Title = "two",
                Category = "Rhinos",
                Tags = new[]{
                     new BlogTag(){ Name = "Mammals" }
                 }
            };
            var blogThree = new Blog
            {
                Title = "three",
                Category = "Rhinos",
                Tags = new[]{
                     new BlogTag(){ Name = "Mammals" }
                 }
            };

            using (var store = GetDocumentStore(options))
            {
                using (var s = store.OpenSession())
                {
                    s.Store(blogOne);
                    s.Store(blogTwo);
                    s.Store(blogThree);
                    s.SaveChanges();
                }

                using (var s = store.OpenSession())
                {
                    var results = s.Query<Blog>()
                        .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                        .Where(x => x.Tags.Any(y => y.Name == "Birds"))
                        .ToArray();

                    Assert.Equal(1, results.Length);
                    Assert.Equal("one", results[0].Title);
                    Assert.Equal("Ravens", results[0].Category);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Querying)]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.All)]
        public void CanPerformDynamicQueryUsingClientLinqQuery(Options options)
        {
            var blogOne = new Blog
            {
                Title = "one",
                Category = "Ravens"
            };
            var blogTwo = new Blog
            {
                Title = "two",
                Category = "Rhinos"
            };
            var blogThree = new Blog
            {
                Title = "three",
                Category = "Rhinos"
            };

            using (var store = GetDocumentStore(options))
            {
                using (var s = store.OpenSession())
                {
                    s.Store(blogOne);
                    s.Store(blogTwo);
                    s.Store(blogThree);
                    s.SaveChanges();
                }

                using (var s = store.OpenSession())
                {
                    var results = s.Query<Blog>()
                        .Customize(x => x.WaitForNonStaleResults())
                        .Where(x => x.Category == "Rhinos" && x.Title.Length == 3)
                        .ToArray();

                    Assert.Equal(1, results.Length);
                    Assert.Equal("two", results[0].Title);
                    Assert.Equal("Rhinos", results[0].Category);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Querying)]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.All)]
        public void QueryForASpecificTypeDoesNotBringBackOtherTypes(Options options)
        {
            using (var store = GetDocumentStore(options))
            {
                using (var s = store.OpenSession())
                {
                    s.Store(new BlogTag());
                    s.SaveChanges();
                }

                using (var s = store.OpenSession())
                {
                    var results = s.Query<Blog>()
                        .Select(b => new { b.Category })
                        .ToArray();
                    Assert.Equal(0, results.Length);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Querying)]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.Lucene)]
        public void CanPerformDynamicQueryUsingClientLuceneQuery(Options options)
        {
            var blogOne = new Blog
            {
                Title = "one",
                Category = "Ravens"
            };
            var blogTwo = new Blog
            {
                Title = "two",
                Category = "Rhinos"
            };
            var blogThree = new Blog
            {
                Title = "three",
                Category = "Rhinos"
            };

            using (var store = GetDocumentStore(options))
            {
                using (var s = store.OpenSession())
                {
                    s.Store(blogOne);
                    s.Store(blogTwo);
                    s.Store(blogThree);
                    s.SaveChanges();
                }

                using (var s = store.OpenSession())
                {
                    var results = s.Advanced.DocumentQuery<Blog>()
                        .WhereLucene("Title.Length", "3")
                        .AndAlso()
                        .WhereLucene("Category", "Rhinos")
                        .WaitForNonStaleResults().ToArray();

                    Assert.Equal(1, results.Length);
                    Assert.Equal("two", results[0].Title);
                    Assert.Equal("Rhinos", results[0].Category);
                }
            }
        }

        [RavenTheory(RavenTestCategory.Highlighting)]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.All)]
        public void CanPerformDynamicQueryWithHighlightingUsingClientLuceneQuery(Options options)
        {
            var blogOne = new Blog
            {
                Title = "Lorem ipsum dolor sit amet, target word, consectetur adipiscing elit.",
                Category = "Ravens"
            };
            var blogTwo = new Blog
            {
                Title = "Maecenas mauris leo, feugiat sodales facilisis target word, pellentesque, suscipit aliquet turpis.",
                Category = "The Rhinos"
            };
            var blogThree = new Blog
            {
                Title = "Target cras vitae felis arcu word.",
                Category = "Los Rhinos"
            };

            using (var store = GetDocumentStore(options))
            {
                string blogOneId;
                string blogTwoId;
                using (var s = store.OpenSession())
                {
                    s.Store(blogOne);
                    s.Store(blogTwo);
                    s.Store(blogThree);
                    s.SaveChanges();

                    blogOneId = s.Advanced.GetDocumentId(blogOne);
                    blogTwoId = s.Advanced.GetDocumentId(blogTwo);
                }

                using (var s = store.OpenSession())
                {
                    var highlightingOptions = new HighlightingOptions
                    {
                        PreTags = new[] { "*" },
                        PostTags = new[] { "*" }
                    };

                    var results = s.Advanced.DocumentQuery<Blog>()
                        .Highlight("Title", 18, 2, highlightingOptions, out Highlightings titleHighlightings)
                        .Highlight("Category", 18, 2, highlightingOptions, out Highlightings categoryHighlightings)
                        .Search("Title", "target word")
                        .Search("Category", "rhinos")
                        .WaitForNonStaleResults()
                        .ToArray();

                    Assert.Equal(3, results.Length);
                    Assert.NotEmpty(titleHighlightings.GetFragments(blogOneId));
                    Assert.Empty(categoryHighlightings.GetFragments(blogOneId));

                    Assert.NotEmpty(titleHighlightings.GetFragments(blogTwoId));
                    Assert.NotEmpty(categoryHighlightings.GetFragments(blogTwoId));
                }
            }
        }

        [RavenTheory(RavenTestCategory.Highlighting)]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.All)]
        public void CanPerformDynamicQueryWithHighlighting(Options options)
        {
            var blogOne = new Blog
            {
                Title = "Lorem ipsum dolor sit amet, target word, consectetur adipiscing elit.",
                Category = "Ravens"
            };
            var blogTwo = new Blog
            {
                Title = "Maecenas mauris leo, feugiat sodales facilisis target word, pellentesque, suscipit aliquet turpis.",
                Category = "The Rhinos"
            };
            var blogThree = new Blog
            {
                Title = "Target cras vitae felis arcu word.",
                Category = "Los Rhinos"
            };

            using (var store = GetDocumentStore(options))
            {
                string blogOneId;
                string blogTwoId;
                using (var s = store.OpenSession())
                {
                    s.Store(blogOne);
                    s.Store(blogTwo);
                    s.Store(blogThree);
                    s.SaveChanges();

                    blogOneId = s.Advanced.GetDocumentId(blogOne);
                    blogTwoId = s.Advanced.GetDocumentId(blogTwo);
                }

                using (var s = store.OpenSession())
                {
                    var results = s.Query<Blog>()
                        .Customize(c => c.WaitForNonStaleResults())
                        .Highlight("Title", 18, 2, out Highlightings titleHighlightings)
                        .Highlight("Category", 18, 2, out Highlightings categoryHighlightings)
                        .Search(x => x.Category, "rhinos")
                        .Search(x => x.Title, "target word")
                        .ToArray();

                    Assert.Equal(3, results.Length);
                    Assert.NotEmpty(titleHighlightings.GetFragments(blogOneId));
                    Assert.Empty(categoryHighlightings.GetFragments(blogOneId));

                    Assert.NotEmpty(titleHighlightings.GetFragments(blogTwoId));
                    Assert.NotEmpty(categoryHighlightings.GetFragments(blogTwoId));
                }
            }
        }

        [RavenTheory(RavenTestCategory.Highlighting)]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.All)]
        public void ExecutesQueryWithHighlightingsAgainstSimpleIndex(Options options)
        {
            using (var store = GetDocumentStore(options))
            {
                const string indexName = "BlogsForHighlightingTests";
                store.Maintenance.Send(new PutIndexesOperation(new[] {
                    new IndexDefinition
                    {
                        Name = indexName,
                        Maps = { "from blog in docs.Blogs select new { blog.Title, blog.Category }" },
                        Fields = new Dictionary<string, IndexFieldOptions>
                        {
                            {"Title", new IndexFieldOptions { Storage = FieldStorage.Yes, Indexing = FieldIndexing.Search, TermVector = FieldTermVector.WithPositionsAndOffsets} },
                            {"Category", new IndexFieldOptions { Storage = FieldStorage.Yes, Indexing = FieldIndexing.Search, TermVector = FieldTermVector.WithPositionsAndOffsets} }
                        }
                    }}));

                var blogOne = new Blog
                {
                    Title = "Lorem ipsum dolor sit amet, target word, consectetur adipiscing elit.",
                    Category = "Ravens"
                };
                var blogTwo = new Blog
                {
                    Title =
                        "Maecenas mauris leo, feugiat sodales facilisis target word, pellentesque, suscipit aliquet turpis.",
                    Category = "The Rhinos"
                };
                var blogThree = new Blog
                {
                    Title = "Target cras vitae felis arcu word.",
                    Category = "Los Rhinos"
                };

                string blogOneId;
                string blogTwoId;
                using (var s = store.OpenSession())
                {
                    s.Store(blogOne);
                    s.Store(blogTwo);
                    s.Store(blogThree);
                    s.SaveChanges();

                    blogOneId = s.Advanced.GetDocumentId(blogOne);
                    blogTwoId = s.Advanced.GetDocumentId(blogTwo);
                }

                using (var s = store.OpenSession())
                {
                    var results = s.Query<Blog>(indexName)
                        .Customize(c => c.WaitForNonStaleResults())
                        .Highlight("Title", 18, 2, out Highlightings titleHighlightings)
                        .Highlight("Category", 18, 2, out Highlightings categoryHighlightings)
                        .Search(x => x.Category, "rhinos")
                        .Search(x => x.Title, "target word")
                        .ToArray();

                    Assert.Equal(3, results.Length);
                    Assert.NotEmpty(titleHighlightings.GetFragments(blogOneId));
                    Assert.Empty(categoryHighlightings.GetFragments(blogOneId));

                    Assert.NotEmpty(titleHighlightings.GetFragments(blogTwoId));
                    Assert.NotEmpty(categoryHighlightings.GetFragments(blogTwoId));
                }
            }
        }

        private class Blog
        {
            public string Title
            {
                get;
                set;
            }

            public string Category
            {
                get;
                set;
            }

            public BlogTag[] Tags
            {
                get;
                set;
            }
        }

        private class BlogTag
        {
            public string Name { get; set; }
        }
    }
}
