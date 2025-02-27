﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using Raven.Client.Documents.Conventions;
using Raven.Client.Http;
using Raven.Client.Json;
using Raven.Client.Json.Serialization;
using Sparrow.Json;

namespace Raven.Client.Documents.Operations.Counters
{
    public sealed class GetCountersOperation : IOperation<CountersDetail>
    {
        private readonly string _docId;
        private readonly string[] _counters;
        private readonly bool _returnFullResults;

        /// <summary>
        /// Initializes a new instance of the <see cref="GetCountersOperation"/> class with a specific document ID, 
        /// an array of counter names, and a flag indicating whether to include counter values from each node in the result.
        /// </summary>
        /// <param name="docId">The ID of the document for which to retrieve counters.</param>
        /// <param name="counters">An array of counter names to retrieve.</param>
        /// <param name="returnFullResults">A value indicating whether the result should include counter values from each node.</param>
        public GetCountersOperation(string docId, string[] counters, bool returnFullResults = false)
        {
            _docId = docId;
            _counters = counters;
            _returnFullResults = returnFullResults;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GetCountersOperation"/> class with a specific document ID 
        /// and a single counter name, along with a flag indicating whether to include counter values from each node in the result.
        /// </summary>
        /// <inheritdoc cref="GetCountersOperation(string, string[], bool)"/>
        /// <param name="counter">The name of the counter to retrieve.</param>
        public GetCountersOperation(string docId, string counter, bool returnFullResults = false)
        {
            _docId = docId;
            _counters = new[] { counter };
            _returnFullResults = returnFullResults;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GetCountersOperation"/> class with a specific document ID 
        /// and a flag indicating whether to include counter values from each node in the result.
        /// This operation retrieves all counters associated with the given document.
        /// </summary>
        /// <inheritdoc cref="GetCountersOperation(string, string[], bool)"/>
        public GetCountersOperation(string docId, bool returnFullResults = false)
        {
            _docId = docId;
            _counters = Array.Empty<string>();
            _returnFullResults = returnFullResults;
        }

        public RavenCommand<CountersDetail> GetCommand(IDocumentStore store, DocumentConventions conventions, JsonOperationContext context, HttpCache cache)
        {
            return new GetCounterValuesCommand(conventions, _docId, _counters, _returnFullResults);
        }

        internal sealed class GetCounterValuesCommand : RavenCommand<CountersDetail>
        {
            private readonly DocumentConventions _conventions;
            private readonly string _docId;
            private readonly string[] _counters;
            private readonly bool _returnFullResults;

            public GetCounterValuesCommand(DocumentConventions conventions, string docId, string[] counters, bool returnFullResults)
            {
                _conventions = conventions ?? throw new ArgumentNullException(nameof(conventions));
                _docId = docId ?? throw new ArgumentNullException(nameof(docId));
                _counters = counters;
                _returnFullResults = returnFullResults;
            }

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                var pathBuilder = new StringBuilder(node.Url);
                pathBuilder.Append("/databases/")
                    .Append(node.Database)
                    .Append("/counters?")
                    .Append("docId=")
                    .Append(Uri.EscapeDataString(_docId));

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Get
                };

                if (_counters != null && _counters.Length > 0)
                {
                    if (_counters.Length > 1)
                    {
                        PrepareRequestWithMultipleCounters(pathBuilder, request, ctx);
                    }
                    else
                    {
                        pathBuilder.Append("&counter=").Append(Uri.EscapeDataString(_counters[0]));
                    }
                }

                if (_returnFullResults && request.Method == HttpMethod.Get) // if we dropped to Post, _returnFullResults is part of the request content 
                {
                    pathBuilder.Append("&full=").Append(true);
                }

                url = pathBuilder.ToString();

                return request;
            }

            public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject response, bool fromCache)
            {
                if (response == null)
                    return;

                Result = JsonDeserializationClient.CountersDetail(response);
            }

            private void PrepareRequestWithMultipleCounters(StringBuilder pathBuilder, HttpRequestMessage request, JsonOperationContext ctx)
            {
                var uniqueNames = GetOrderedUniqueNames(out int sumLength);

                // if it is too big, we drop to POST (note that means that we can't use the HTTP cache any longer)
                // we are fine with that, such requests are going to be rare
                if (sumLength < 1024)
                {
                    foreach (var counter in uniqueNames)
                    {
                        pathBuilder.Append("&counter=").Append(Uri.EscapeDataString(counter ?? string.Empty));
                    }
                }
                else
                {
                    request.Method = HttpMethod.Post;

                    var docOps = new DocumentCountersOperation
                    {
                        DocumentId = _docId,
                        Operations = new List<CounterOperation>()
                    };

                    foreach (var counter in uniqueNames)
                    {
                        docOps.Operations.Add(new CounterOperation
                        {
                            Type = CounterOperationType.Get,
                            CounterName = counter
                        });
                    }

                    var batch = new CounterBatch
                    {
                        Documents = new List<DocumentCountersOperation>
                        {
                            docOps
                        },
                        ReplyWithAllNodesValues = _returnFullResults
                    };

                    request.Content = new BlittableJsonContent(async stream => await ctx.WriteAsync(stream, DocumentConventions.Default.Serialization.DefaultConverter.ToBlittable(batch, ctx)).ConfigureAwait(false), _conventions);
                }
            }

            private List<string> GetOrderedUniqueNames(out int sum)
            {
                var uniqueNames = new HashSet<string>();
                var orderedUniqueNames = new List<string>();
                sum = 0;

                foreach (var counter in _counters)
                {
                    if (uniqueNames.Add(counter))
                    {
                        orderedUniqueNames.Add(counter);
                        sum += counter?.Length ?? 0;
                    }
                }

                return orderedUniqueNames;
            }

            public override bool IsReadRequest => true;
        }
    }
}
