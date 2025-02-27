﻿using System;
using System.Net.Http;
using System.Text;
using Raven.Client.Documents.Conventions;
using Raven.Client.Http;
using Raven.Client.Util;
using Sparrow.Json;

namespace Raven.Client.Documents.Operations.ETL
{
    /// <summary>
    /// <para>Resets an ETL process using the ResetEtlOperation. </para>
    /// <para>ETL (Extract, Transform, Load) typically processes documents from the point where the last batch finished.
    /// By resetting the ETL, processing will start again from the very beginning.</para>
    /// </summary>
    public sealed class ResetEtlOperation : IMaintenanceOperation
    {
        private readonly string _configurationName;
        private readonly string _transformationName;

        /// <inheritdoc cref="ResetEtlOperation" />
        /// <param name="configurationName">The name of the ETL configuration to reset.</param>
        /// <param name="transformationName">The name of the specific transformation within the ETL configuration to reset.</param>
        public ResetEtlOperation(string configurationName, string transformationName)
        {
            _configurationName = configurationName ?? throw new ArgumentNullException(nameof(configurationName));
            _transformationName = transformationName ?? throw new ArgumentNullException(nameof(transformationName));
        }

        public RavenCommand GetCommand(DocumentConventions conventions, JsonOperationContext context)
        {
            return new ResetEtlCommand(_configurationName, _transformationName);
        }

        private sealed class ResetEtlCommand : RavenCommand, IRaftCommand
        {
            private readonly string _configurationName;
            private readonly string _transformationName;

            public ResetEtlCommand(string configurationName, string transformationName)
            {
                _configurationName = configurationName;
                _transformationName = transformationName;
            }

            public override bool IsReadRequest => false;

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                var path = new StringBuilder(node.Url)
                    .Append("/databases/")
                    .Append(node.Database)
                    .Append("/admin/etl?configurationName=")
                    .Append(Uri.EscapeDataString(_configurationName))
                    .Append("&transformationName=")
                    .Append(Uri.EscapeDataString(_transformationName));
                
                url = path.ToString();
                var request = new HttpRequestMessage
                {
                    Method = HttpMethods.Reset,
                    Content = new StringContent("{}")
                };

                return request;
            }

            public string RaftUniqueRequestId { get; } = RaftIdGenerator.NewId();
        }
    }
}
