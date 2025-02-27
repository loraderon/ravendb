﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Util;
using Raven.Server.Background;
using Raven.Server.Dashboard;
using Sparrow.Collections;
using Sparrow.Json.Parsing;
using Sparrow.Server.Collections;

namespace Raven.Server.NotificationCenter
{
    public abstract class NotificationsBase : IDisposable
    {
        private readonly object _watchersLock = new object();

        private sealed class State
        {
            public TaskCompletionSource<object> NewWebSocket = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<object> AllWebSocketsRemoved = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int NumberOfClients;
        }

        protected ConcurrentSet<ConnectedWatcher> Watchers { get; }
        protected List<BackgroundWorkBase> BackgroundWorkers { get; }

        private State _state = new State();

        public Task WaitForAnyWebSocketClient
        {
            get
            {
                var copy = _state;
                if (copy.NumberOfClients == 0)
                    return copy.NewWebSocket.Task;
                return Task.CompletedTask;
            }
        }

        public Task WaitForRemoveAllWebSocketClients
        {
            get
            {
                var copy = _state;
                if (copy.NumberOfClients == 0)
                    return Task.CompletedTask;
                return copy.AllWebSocketsRemoved.Task;
            }
        }

        protected NotificationsBase()
        {
            Watchers = new ConcurrentSet<ConnectedWatcher>();
            BackgroundWorkers = new List<BackgroundWorkBase>();
        }

        public IDisposable TrackActions(AsyncQueue<DynamicJsonValue> notificationsQueue, IWebsocketWriter webSocketWriter, CanAccessDatabase shouldWriteByDb = null)
        {
            var watcher = new ConnectedWatcher(notificationsQueue, 16 * 1024, webSocketWriter, shouldWriteByDb);

            lock (_watchersLock)
            {
                var first = Watchers.IsEmpty;
                Watchers.TryAdd(watcher);

                if (first)
                {
                    StartBackgroundWorkers();
                }

                if (watcher.Writer is INotificationCenterWebSocketWriter)
                {
                    if (_state.NumberOfClients == 0)
                    {
                        var copy = _state;
                        // we use interlocked here to make sure that other threads
                        // are immediately exposed to this
                        Interlocked.Exchange(ref _state, new State
                        {
                            NumberOfClients = 1
                        });
                        copy.NewWebSocket.TrySetResult(null);
                    }
                    else
                    {
                        Interlocked.Increment(ref _state.NumberOfClients);
                    }
                }
            }

            return new DisposableAction(() =>
            {
                lock (_watchersLock)
                {
                    Watchers.TryRemove(watcher);

                    if (Watchers.IsEmpty)
                    {
                        StopBackgroundWorkers();
                    }

                    if (watcher.Writer is INotificationCenterWebSocketWriter)
                    {
                        var copy = _state;
                        if (Interlocked.Decrement(ref copy.NumberOfClients) == 0)
                        {
                            Interlocked.Exchange(ref _state, new State());
                            copy.AllWebSocketsRemoved.TrySetResult(null);
                        }
                    }
                }
            });
        }

        private void StartBackgroundWorkers()
        {
            foreach (var worker in BackgroundWorkers)
            {
                worker.Start();
            }
        }

        private void StopBackgroundWorkers()
        {
            foreach (var worker in BackgroundWorkers)
            {
                worker.Stop();
            }
        }

        public virtual void Dispose()
        {
            foreach (var worker in BackgroundWorkers)
            {
                worker.Dispose();
            }
        }
    }
}
