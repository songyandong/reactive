﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information. 

using System.Reactive.Disposables;
using System.Reactive.Subjects;
using System.Threading;

namespace System.Reactive.Linq.ObservableImpl
{
    internal sealed class RepeatWhen<T, U> : IObservable<T>
    {
        private readonly IObservable<T> source;
        private readonly Func<IObservable<object>, IObservable<U>> handler;

        internal RepeatWhen(IObservable<T> source, Func<IObservable<object>, IObservable<U>> handler)
        {
            this.source = source;
            this.handler = handler;
        }

        public IDisposable Subscribe(IObserver<T> observer)
        {
            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }

            var completeSignals = new Subject<object>();
            var redo = default(IObservable<U>);

            try
            {
                redo = handler(completeSignals);
                if (redo == null)
                {
                    throw new NullReferenceException("The handler returned a null IObservable");
                }
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
                return Disposable.Empty;
            }

            var parent = new MainObserver(observer, source, new RedoSerializedObserver<object>(completeSignals));

            var d = redo.SubscribeSafe(parent.handlerObserver);
            Disposable.SetSingle(ref parent.handlerUpstream, d);

            parent.HandlerNext();

            return parent;
        }

        private sealed class MainObserver : Sink<T>, IObserver<T>
        {
            private readonly IObserver<Exception> errorSignal;

            internal readonly HandlerObserver handlerObserver;
            private readonly IObservable<T> source;
            private IDisposable upstream;

            internal IDisposable handlerUpstream;
            private int trampoline;
            private int halfSerializer;
            private Exception error;

            internal MainObserver(IObserver<T> downstream, IObservable<T> source, IObserver<Exception> errorSignal) : base(downstream)
            {
                this.source = source;
                this.errorSignal = errorSignal;
                handlerObserver = new HandlerObserver(this);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Disposable.TryDispose(ref upstream);
                    Disposable.TryDispose(ref handlerUpstream);
                }
                base.Dispose(disposing);
            }

            public void OnCompleted()
            {
                if (Disposable.TrySetSerial(ref upstream, null))
                {
                    errorSignal.OnNext(null);
                }

            }

            public void OnError(Exception error)
            {
                HalfSerializer.ForwardOnError(this, error, ref halfSerializer, ref this.error);
            }

            public void OnNext(T value)
            {
                HalfSerializer.ForwardOnNext(this, value, ref halfSerializer, ref error);
            }

            internal void HandlerError(Exception error)
            {
                HalfSerializer.ForwardOnError(this, error, ref halfSerializer, ref this.error);
            }

            internal void HandlerComplete()
            {
                HalfSerializer.ForwardOnCompleted(this, ref halfSerializer, ref error);
            }

            internal void HandlerNext()
            {
                if (Interlocked.Increment(ref trampoline) == 1)
                {
                    do
                    {
                        var sad = new SingleAssignmentDisposable();
                        if (Interlocked.CompareExchange(ref upstream, sad, null) != null)
                        {
                            return;
                        }

                        sad.Disposable = source.SubscribeSafe(this);
                    }
                    while (Interlocked.Decrement(ref trampoline) != 0);
                }
            }

            internal sealed class HandlerObserver : IObserver<U>
            {
                private readonly MainObserver main;

                internal HandlerObserver(MainObserver main)
                {
                    this.main = main;
                }

                public void OnCompleted()
                {
                    main.HandlerComplete();
                }

                public void OnError(Exception error)
                {
                    main.HandlerError(error);
                }

                public void OnNext(U value)
                {
                    main.HandlerNext();
                }
            }
        }

    }
}
