﻿namespace StreamStore
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.SqlClient;
    using System.Threading;
    using System.Threading.Tasks;
    using EnsureThat;
    using Microsoft.SqlServer.Server;
    using StreamStore.Infrastructure;
    using StreamStore.MsSqlScripts;
    using StreamStore.Subscriptions;

    public sealed partial class MsSqlEventStore : EventStoreBase
    {
        private readonly Func<SqlConnection> _createConnection;
        private readonly AsyncLazy<IEventStoreNotifier> _eventStoreNotifier;
        private readonly Scripts _scripts;
        private readonly SqlMetaData[] _appendToStreamSqlMetadata;

        public MsSqlEventStore(MsSqlEventStoreSettings settings)
            :base(settings.MetadataMaxAgeCacheExpire, settings.MetadataMaxAgeCacheMaxSize,
                 settings.GetUtcNow, settings.LogName)
        {
            Ensure.That(settings, nameof(settings)).IsNotNull();

            _createConnection = () => new SqlConnection(settings.ConnectionString);
            _eventStoreNotifier = new AsyncLazy<IEventStoreNotifier>(
                async () =>
                {
                    if(settings.CreateEventStoreNotifier == null)
                    {
                        throw new InvalidOperationException(
                            "Cannot create notifier because supplied createEventStoreNotifier was null");
                    }
                    return await settings.CreateEventStoreNotifier(this).NotOnCapturedContext();
                });
            _scripts = new Scripts(settings.Schema);

            var sqlMetaData = new List<SqlMetaData>
            {
                new SqlMetaData("StreamVersion", SqlDbType.Int, true, false, SortOrder.Unspecified, -1),
                new SqlMetaData("Id", SqlDbType.UniqueIdentifier),
                new SqlMetaData("Created", SqlDbType.DateTime, true, false, SortOrder.Unspecified, -1),
                new SqlMetaData("Type", SqlDbType.NVarChar, 128),
                new SqlMetaData("JsonData", SqlDbType.NVarChar, SqlMetaData.Max),
                new SqlMetaData("JsonMetadata", SqlDbType.NVarChar, SqlMetaData.Max)
            };

            if(settings.GetUtcNow != null)
            {
                // Created column value will be client supplied so should prevent using of the column default function
                sqlMetaData[2] = new SqlMetaData("Created", SqlDbType.DateTime);
            }

            _appendToStreamSqlMetadata = sqlMetaData.ToArray();
        }

        public async Task InitializeStore(
            bool ignoreErrors = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckIfDisposed();

            using(var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken).NotOnCapturedContext();

                if(_scripts.Schema != "dbo")
                {
                    using(var command = new SqlCommand($"CREATE SCHEMA {_scripts.Schema}", connection))
                    {
                        await command
                            .ExecuteNonQueryAsync(cancellationToken)
                            .NotOnCapturedContext();
                    }
                }

                using (var command = new SqlCommand(_scripts.InitializeStore, connection))
                {
                    if(ignoreErrors)
                    {
                        await ExecuteAndIgnoreErrors(() => command.ExecuteNonQueryAsync(cancellationToken))
                            .NotOnCapturedContext();
                    }
                    else
                    {
                        await command.ExecuteNonQueryAsync(cancellationToken)
                            .NotOnCapturedContext();
                    }
                }
            }
        }

        public async Task DropAll(
            bool ignoreErrors = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckIfDisposed();

            using(var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken).NotOnCapturedContext();

                using(var command = new SqlCommand(_scripts.DropAll, connection))
                {
                    if(ignoreErrors)
                    {
                        await ExecuteAndIgnoreErrors(() => command.ExecuteNonQueryAsync(cancellationToken))
                            .NotOnCapturedContext();
                    }
                    else
                    {
                        await command
                            .ExecuteNonQueryAsync(cancellationToken)
                            .NotOnCapturedContext();
                    }
                }
            }
        }

        public override async Task<int> GetStreamEventCount(
            string streamId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckIfDisposed();

            using(var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken).NotOnCapturedContext();

                using(var command = new SqlCommand(_scripts.GetStreamEventCount, connection))
                {
                    var streamIdInfo = new StreamIdInfo(streamId);
                    command.Parameters.AddWithValue("streamId", streamIdInfo.SqlStreamId.Id);

                    var result = await command
                        .ExecuteScalarAsync(cancellationToken)
                        .NotOnCapturedContext();

                    return (int) result;
                }
            }
        }

        public async Task<int> GetStreamEventCount(
            string streamId,
            DateTime createdBefore,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckIfDisposed();

            using (var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken).NotOnCapturedContext();

                using (var command = new SqlCommand(_scripts.GetStreamEventBeforeCreatedCount, connection))
                {
                    var streamIdInfo = new StreamIdInfo(streamId);
                    command.Parameters.AddWithValue("streamId", streamIdInfo.SqlStreamId.Id);
                    command.Parameters.AddWithValue("created", createdBefore);

                    var result = await command
                        .ExecuteScalarAsync(cancellationToken)
                        .NotOnCapturedContext();

                    return (int)result;
                }
            }
        }

        protected override async Task<long> ReadHeadCheckpointInternal(CancellationToken cancellationToken)
        {
            CheckIfDisposed();

            using(var connection = _createConnection())
            {
                await connection.OpenAsync(cancellationToken);

                using(var command = new SqlCommand(_scripts.ReadHeadCheckpoint, connection))
                {
                    var result = await command
                        .ExecuteScalarAsync(cancellationToken)
                        .NotOnCapturedContext();

                    if(result == DBNull.Value)
                    {
                        return -1;
                    }
                    return (long) result;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if(disposing)
            {
                if(_eventStoreNotifier.IsValueCreated)
                {
                    _eventStoreNotifier.Value.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private IObservable<Unit> GetStoreObservable => _eventStoreNotifier.Value.Result;

        private static async Task<T> ExecuteAndIgnoreErrors<T>(Func<Task<T>> operation)
        {
            try
            {
                return await operation().NotOnCapturedContext();
            }
            catch
            {
                return default(T);
            }
        }
    }
}