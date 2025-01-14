﻿namespace Raven.Server.Documents.Indexes.Static.Roslyn.Rewriters.Counters
{
    public sealed class CountersCollectionNameRetriever : CollectionNameRetrieverBase
    {
        public static CollectionNameRetrieverBase QuerySyntax => new QuerySyntaxRewriter("counters", "Counters");

        public static CollectionNameRetrieverBase MethodSyntax => new MethodSyntaxRewriter("counters", "Counters");
    }
}
