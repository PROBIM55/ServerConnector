#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace Platform.Contracts.TeklaPlan
{
    /// <summary>
    /// A prepared native mutation. Implementations capture rollback state before
    /// entering the transaction and must not commit the CAD model themselves.
    /// </summary>
    public interface ITeklaPreparedMutation<out TReadback>
    {
        string CommandId { get; }
        void Apply();
        TReadback Readback();
        void Restore();
    }

    public sealed class TeklaPlanRollbackException : Exception
    {
        public TeklaPlanRollbackException(
            Exception transactionError,
            IReadOnlyList<Exception> rollbackErrors)
            : base(
                $"TeklaPlan transaction failed and rollback reported {rollbackErrors.Count} error(s).",
                transactionError)
        {
            TransactionError = transactionError;
            RollbackErrors = rollbackErrors;
        }

        public Exception TransactionError { get; }
        public IReadOnlyList<Exception> RollbackErrors { get; }
    }

    /// <summary>
    /// CAD-independent transaction boundary shared by native adapters. Readback
    /// happens after every mutation is applied and before the single final
    /// commit, so a failed verification can still be rolled back atomically.
    /// </summary>
    public static class TeklaPlanTransaction
    {
        public static IReadOnlyList<TReadback> Execute<TReadback>(
            IReadOnlyList<ITeklaPreparedMutation<TReadback>> mutations,
            Action commit)
        {
            if (mutations is null) throw new ArgumentNullException(nameof(mutations));
            if (commit is null) throw new ArgumentNullException(nameof(commit));

            var applied = new List<ITeklaPreparedMutation<TReadback>>(mutations.Count);
            var applyAttempted = false;
            try
            {
                foreach (var mutation in mutations)
                {
                    if (mutation is null)
                        throw new ArgumentException("Prepared mutations cannot contain null values.", nameof(mutations));
                    applyAttempted = true;
                    mutation.Apply();
                    applied.Add(mutation);
                }

                var readbacks = applied.Select(static mutation => mutation.Readback()).ToArray();
                commit();
                return readbacks;
            }
            catch (Exception transactionError)
            {
                var rollbackErrors = new List<Exception>();
                foreach (var mutation in applied.AsEnumerable().Reverse())
                {
                    try
                    {
                        mutation.Restore();
                    }
                    catch (Exception rollbackError)
                    {
                        rollbackErrors.Add(new InvalidOperationException(
                            $"Rollback failed for command '{mutation.CommandId}'.",
                            rollbackError));
                    }
                }

                // The failing mutation may have performed and self-restored a
                // partial native change before throwing. Commit the restored
                // model even when no mutation reached the applied list.
                if (applyAttempted)
                {
                    try
                    {
                        commit();
                    }
                    catch (Exception rollbackCommitError)
                    {
                        rollbackErrors.Add(new InvalidOperationException(
                            "Rollback commit failed.",
                            rollbackCommitError));
                    }
                }

                if (rollbackErrors.Count > 0)
                    throw new TeklaPlanRollbackException(transactionError, rollbackErrors);

                ExceptionDispatchInfo.Capture(transactionError).Throw();
                throw;
            }
        }
    }
}
