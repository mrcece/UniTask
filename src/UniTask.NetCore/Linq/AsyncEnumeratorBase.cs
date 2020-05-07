﻿using System;
using System.Threading;

namespace Cysharp.Threading.Tasks.Linq
{
    public abstract class MoveNextSource : IUniTaskSource<bool>
    {
        protected UniTaskCompletionSourceCore<bool> completionSource;

        public bool GetResult(short token)
        {
            return completionSource.GetResult(token);
        }

        public UniTaskStatus GetStatus(short token)
        {
            return completionSource.GetStatus(token);
        }

        public void OnCompleted(Action<object> continuation, object state, short token)
        {
            completionSource.OnCompleted(continuation, state, token);
        }

        public UniTaskStatus UnsafeGetStatus()
        {
            return completionSource.UnsafeGetStatus();
        }

        void IUniTaskSource.GetResult(short token)
        {
            completionSource.GetResult(token);
        }
    }


    public abstract class AsyncEnumeratorBase<TSource, TResult> : MoveNextSource, IUniTaskAsyncEnumerator<TResult>
    {
        static readonly Action<object> moveNextCallbackDelegate = MoveNextCallBack;

        readonly IUniTaskAsyncEnumerable<TSource> source;
        protected CancellationToken cancellationToken;

        IUniTaskAsyncEnumerator<TSource> enumerator;
        UniTask<bool>.Awaiter sourceMoveNext;

        public AsyncEnumeratorBase(IUniTaskAsyncEnumerable<TSource> source, CancellationToken cancellationToken)
        {
            this.source = source;
            this.cancellationToken = cancellationToken;
        }

        // abstract

        /// <summary>
        /// If return value is false, continue source.MoveNext.
        /// </summary>
        protected abstract bool TryMoveNextCore(bool sourceHasCurrent, out bool result);

        // Util
        protected TSource SourceCurrent => enumerator.Current;

        // IUniTaskAsyncEnumerator<T>

        public TResult Current { get; protected set; }

        public UniTask<bool> MoveNextAsync()
        {
            if (enumerator == null)
            {
                enumerator = source.GetAsyncEnumerator(cancellationToken);
            }

            completionSource.Reset();
            SourceMoveNext();
            return new UniTask<bool>(this, completionSource.Version);
        }

        protected void SourceMoveNext()
        {
            CONTINUE:
            sourceMoveNext = enumerator.MoveNextAsync().GetAwaiter();
            if (sourceMoveNext.IsCompleted)
            {
                bool result = false;
                try
                {
                    if (!TryMoveNextCore(sourceMoveNext.GetResult(), out result))
                    {
                        goto CONTINUE;
                    }
                }
                catch (Exception ex)
                {
                    completionSource.TrySetException(ex);
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    completionSource.TrySetCanceled(cancellationToken);
                }
                else
                {
                    completionSource.TrySetResult(result);
                }
            }
            else
            {
                sourceMoveNext.SourceOnCompleted(moveNextCallbackDelegate, this);
            }
        }

        static void MoveNextCallBack(object state)
        {
            var self = (AsyncEnumeratorBase<TSource, TResult>)state;
            bool result;
            try
            {
                if (!self.TryMoveNextCore(self.sourceMoveNext.GetResult(), out result))
                {
                    self.SourceMoveNext();
                    return;
                }
            }
            catch (Exception ex)
            {
                self.completionSource.TrySetException(ex);
                return;
            }

            if (self.cancellationToken.IsCancellationRequested)
            {
                self.completionSource.TrySetCanceled(self.cancellationToken);
            }
            else
            {
                self.completionSource.TrySetResult(result);
            }
        }

        // if require additional resource to dispose, override and call base.DisposeAsync.
        public virtual UniTask DisposeAsync()
        {
            if (enumerator != null)
            {
                return enumerator.DisposeAsync();
            }
            return default;
        }
    }

    public abstract class AsyncEnumeratorAwaitSelectorBase<TSource, TResult, TAwait> : MoveNextSource, IUniTaskAsyncEnumerator<TResult>
    {
        static readonly Action<object> moveNextCallbackDelegate = MoveNextCallBack;

        readonly IUniTaskAsyncEnumerable<TSource> source;
        protected CancellationToken cancellationToken;

        IUniTaskAsyncEnumerator<TSource> enumerator;
        UniTask<bool>.Awaiter sourceMoveNext;

        UniTask<TAwait>.Awaiter resultAwaiter;

        public AsyncEnumeratorAwaitSelectorBase(IUniTaskAsyncEnumerable<TSource> source, CancellationToken cancellationToken)
        {
            this.source = source;
            this.cancellationToken = cancellationToken;
        }

        // abstract

        protected abstract UniTask<TAwait> TransformAsync(TSource sourceCurrent);
        protected abstract bool TrySetCurrentCore(TAwait awaitResult);

        // Util
        protected TSource SourceCurrent => enumerator.Current;

        protected (bool waitCallback, bool requireNextIteration) ActionCompleted(bool trySetCurrentResult, out bool moveNextResult)
        {
            if (trySetCurrentResult)
            {
                moveNextResult = true;
                return (false, false);
            }
            else
            {
                moveNextResult = default;
                return (false, true);
            }
        }
        protected (bool waitCallback, bool requireNextIteration) WaitAwaitCallback(out bool moveNextResult) { moveNextResult = default; return (true, false); }
        protected (bool waitCallback, bool requireNextIteration) IterateFinished(out bool moveNextResult) { moveNextResult = false; return (false, false); }

        // IUniTaskAsyncEnumerator<T>

        public TResult Current { get; protected set; }

        public UniTask<bool> MoveNextAsync()
        {
            if (enumerator == null)
            {
                enumerator = source.GetAsyncEnumerator(cancellationToken);
            }

            completionSource.Reset();
            SourceMoveNext();
            return new UniTask<bool>(this, completionSource.Version);
        }

        protected void SourceMoveNext()
        {
            CONTINUE:
            sourceMoveNext = enumerator.MoveNextAsync().GetAwaiter();
            if (sourceMoveNext.IsCompleted)
            {
                bool result = false;
                try
                {
                    (bool waitCallback, bool requireNextIteration) = TryMoveNextCore(sourceMoveNext.GetResult(), out result);

                    if (waitCallback)
                    {
                        return;
                    }

                    if (requireNextIteration)
                    {
                        goto CONTINUE;
                    }
                    else
                    {
                        completionSource.TrySetResult(result);
                    }
                }
                catch (Exception ex)
                {
                    completionSource.TrySetException(ex);
                    return;
                }
            }
            else
            {
                sourceMoveNext.SourceOnCompleted(moveNextCallbackDelegate, this);
            }
        }

        (bool waitCallback, bool requireNextIteration) TryMoveNextCore(bool sourceHasCurrent, out bool result)
        {
            if (sourceHasCurrent)
            {
                var task = TransformAsync(enumerator.Current);
                if (UnwarapTask(task, out var taskResult))
                {
                    return ActionCompleted(TrySetCurrentCore(taskResult), out result);
                }
                else
                {
                    return WaitAwaitCallback(out result);
                }
            }

            return IterateFinished(out result);
        }

        protected bool UnwarapTask(UniTask<TAwait> taskResult, out TAwait result)
        {
            resultAwaiter = taskResult.GetAwaiter();

            if (resultAwaiter.IsCompleted)
            {
                result = resultAwaiter.GetResult();
                return true;
            }
            else
            {
                resultAwaiter.SourceOnCompleted(SetCurrentCallBack, this); // TODO:cache
                result = default;
                return false;
            }
        }

        static void MoveNextCallBack(object state)
        {
            var self = (AsyncEnumeratorAwaitSelectorBase<TSource, TResult, TAwait>)state;
            bool result = false;
            try
            {
                (bool waitCallback, bool requireNextIteration) = self.TryMoveNextCore(self.sourceMoveNext.GetResult(), out result);

                if (waitCallback)
                {
                    return;
                }

                if (requireNextIteration)
                {
                    self.SourceMoveNext();
                    return;
                }
                else
                {
                    self.completionSource.TrySetResult(result);
                }
            }
            catch (Exception ex)
            {
                self.completionSource.TrySetException(ex);
                return;
            }
        }

        static void SetCurrentCallBack(object state)
        {
            var self = (AsyncEnumeratorAwaitSelectorBase<TSource, TResult, TAwait>)state;

            bool doneSetCurrent;
            try
            {
                var result = self.resultAwaiter.GetResult();
                doneSetCurrent = self.TrySetCurrentCore(result);
            }
            catch (Exception ex)
            {
                self.completionSource.TrySetException(ex);
                return;
            }

            if (self.cancellationToken.IsCancellationRequested)
            {
                self.completionSource.TrySetCanceled(self.cancellationToken);
            }
            else
            {
                if (doneSetCurrent)
                {
                    self.completionSource.TrySetResult(true);
                }
                else
                {
                    self.SourceMoveNext();
                }
            }
        }

        // if require additional resource to dispose, override and call base.DisposeAsync.
        public virtual UniTask DisposeAsync()
        {
            if (enumerator != null)
            {
                return enumerator.DisposeAsync();
            }
            return default;
        }
    }

}