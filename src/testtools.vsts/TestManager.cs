// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Protocols.TestTools.Messages;
using Microsoft.Protocols.TestTools.Messages.Runtime;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Microsoft.Protocols.TestTools
{
    /// <summary>
    /// A delegate type representing a generic event handler.
    /// </summary>
    /// <param name="eventInfo"></param>
    /// <param name="target"></param>
    /// <param name="parameters"></param>
    internal delegate void GenericEventHandler(EventInfo eventInfo, object target, object[] parameters);

    /// <summary>
    /// Test manager supervises test case running process, and provides
    /// logging and checking functionality.
    /// </summary>
    public class TestManager : ITestManager
    {
        private PtfTestClassBase site;
        private ObservationQueue<AvailableEvent> eventQueue;
        private ObservationQueue<AvailableReturn> returnQueue;
        private static Dictionary<EventInfo, Dictionary<Type, Delegate>> adapterEventHandlers = new Dictionary<EventInfo, Dictionary<Type, Delegate>>();
        private List<TransactionEvent> transaction;
        private bool inTransaction { get { return transaction != null; } }

        /// <summary>
        /// if true, throw predefined <see cref="TestFailureException"/> when assertion fails and dynamic traversal
        /// can catch it and decide how to proceed.
        /// Otherwise, leaves to test control manager to handle assertion failure.
        /// </summary>
        public bool ThrowTestFailureException { get; set; }

        /// <summary>
        /// Constructs a test manager base, with given test site, maximal sizes for event and return queue.
        /// </summary>
        /// <param name="site">Basic test site.</param>
        /// <param name="maxEventQueueSize">Maximal size for event queue. Not implemented yet.</param>
        /// <param name="maxReturnQueueSize">Maximal size for return queue. Not implemented yet.</param>
        public TestManager(PtfTestClassBase site, int maxEventQueueSize, int maxReturnQueueSize)
        {
            this.site = site;
            eventQueue = new ObservationQueue<AvailableEvent>(maxEventQueueSize);
            returnQueue = new ObservationQueue<AvailableReturn>(maxReturnQueueSize);
        }

        /// <summary>
        /// Adds an event to the event queue.
        /// </summary>
        /// <param name="eventInfo">The reflection information of the event.</param>
        /// <param name="target">
        /// The target object. Must be given for instance-based, non-adapter methods, 
        /// otherwise must be null.
        ///  </param>
        /// <param name="arguments">the arguments to the return method.</param>
        public void AddEvent(EventInfo eventInfo, object target, params object[] arguments)
        {
            eventQueue.Add(new AvailableEvent(eventInfo, target, arguments));
        }

        /// <summary>
        /// Adds a method return to the return queue.
        /// </summary>
        /// <param name="methodInfo">The reflection information of the method.</param>
        /// <param name="target">
        /// The target object. Must be given for instance-based, non-adapter methods, 
        /// must be null otherwise.
        ///  </param>
        /// <param name="arguments">the arguments to the return method.</param>
        public void AddReturn(MethodBase methodInfo, object target, params object[] arguments)
        {
            returnQueue.Add(new AvailableReturn(methodInfo, target, arguments));
        }

        /// <summary>
        /// Executes a test assertion.
        /// </summary>
        /// <param name="condition"></param>
        /// <param name="description"></param>
        public void Assert(bool condition, string description)
        {
            if (inTransaction)
            {
                transaction.Add(new TransactionEvent(TransactionEventKind.Assert, condition, description, null));

                bool failed = !site.IsTrue(condition, description);

                if (failed)
                    throw new TransactionFailedException();
            }
            else
                InternalAssert(condition, description);
        }

        /// <summary>
        /// Executes a test assumption.
        /// </summary>
        /// <param name="condition"></param>
        /// <param name="description"></param>
        public void Assume(bool condition, string description)
        {
            if (inTransaction)
            {
                transaction.Add(new TransactionEvent(TransactionEventKind.Assume, condition, description, null));
                if (!condition)
                    throw new TransactionFailedException();
            }
            else
                site.Assume(condition, description);
        }

        /// <summary>
        /// Upon observation timeout, checks current event observation queue status and decides 
        /// whether case should pass or fail.
        /// </summary>
        /// <param name="isAcceptingState"></param>
        /// <param name="expected"></param>
        public void CheckObservationTimeout(bool isAcceptingState, params ExpectedEvent[] expected)
        {
            bool queueEmpty = eventQueue.GetEnumerator().Count == 0;
            if (isAcceptingState && queueEmpty)
            {
                // empty queue at an accepting state, pass case
                StringBuilder diag = new StringBuilder();
                diag.AppendLine("Observation timeout while expecting events:");
                foreach (var e in expected)
                {
                    diag.AppendLine("\t" + e.ToString());
                }
                site.Comment(diag.ToString());
                return;
            }
            else
            {
                StringBuilder diag = new StringBuilder();
                diag.AppendLine("Expected event didn't come within configured timeout.");

                diag.AppendLine("Expected events:");
                foreach (var e in expected)
                {
                    diag.AppendLine("\t" + e.ToString());
                }

                diag.AppendLine("Observed events:");
                foreach (var o in eventQueue.GetEnumerator())
                {
                    diag.AppendLine("\t" + o.ToString());
                }

                InternalAssert(false, diag.ToString());
            }
        }

        /// <summary>
        /// Selects one satisfied pre-constraint from one or more given preconstraints described by patterns.
        /// </summary>   
        /// <param name="printDiagnosisIfFail">Behavior on failure.</param>
        /// <param name="expected">Expected pre constraints</param>
        /// <returns>
        ///  Returns index of the first preconstraint which satisfies.
        ///  if none of the pre-constraints satisfies, returns -1 if <c>failIfNone</c> is false, otherwise
        ///  produces test failure with corresponding diagnostics.
        /// </returns>
        public int SelectSatisfiedPreConstraint(bool printDiagnosisIfFail, params ExpectedPreConstraint[] expected)
        {
            List<List<TransactionEvent>> failedTransactions;
            if (printDiagnosisIfFail)
                failedTransactions = new List<List<TransactionEvent>>();
            else
                failedTransactions = null;
            int index = 0;
            foreach (ExpectedPreConstraint expectedPreConstraint in expected)
            {
                BeginTransaction();

                try
                {
                    try
                    {
                        expectedPreConstraint.Checker.DynamicInvoke(new object[0]);
                    }
                    catch (TargetInvocationException e)
                    {
                        throw e.InnerException;
                    }

                    EndTransaction(true);
                    return index;
                }
                catch (TransactionFailedException)
                {
                    if (printDiagnosisIfFail)
                    {
                        failedTransactions.Add(transaction);
                    }
                    EndTransaction(false);
                }
                index++;
            }
            if (!printDiagnosisIfFail)
                return -1;

            StringBuilder diagnosis = new StringBuilder();
            diagnosis.AppendLine("None of the expected pre-constraints are matched.");
            foreach (List<TransactionEvent> failedTransactionEvents in failedTransactions)
            {
                Describe(diagnosis, "    ", failedTransactionEvents);
            }

            site.Comment(diagnosis.ToString());
            return -1;
        }

        /// <summary>
        /// Begins executing a test case.
        /// </summary>
        /// <param name="name"></param>
        public void BeginTest(string name)
        {
            if (inTransaction)
            {
                transaction.Add(new TransactionEvent(TransactionEventKind.Checkpoint, true, "Begin Test: " + name, null));
            }
            else
                site.BeginTest(name);
        }

        /// <summary>
        /// Begins a transaction. Note that the execution of a Checker happens implicitly within a transaction.
        /// </summary>
        public void BeginTransaction()
        {
            if (inTransaction)
                throw new InvalidOperationException("nested test manager transactions not allowed");
            transaction = new List<TransactionEvent>();
        }

        /// <summary>
        /// See site Checkpoint
        /// </summary>
        /// <param name="description">Description message for a check point in log</param>
        public void Checkpoint(string description)
        {
            if (inTransaction)
            {
                transaction.Add(new TransactionEvent(TransactionEventKind.Checkpoint, true, description, null));
            }
            else
                site.Checkpoint(description);
        }

        /// <summary>
        /// See site Comment
        /// </summary>
        /// <param name="description">Description message for a check point in log</param>
        public void Comment(string description)
        {
            if (inTransaction)
            {
                transaction.Add(new TransactionEvent(TransactionEventKind.Comment, true, description, null));
            }
            else
                site.Comment(description);
        }

        /// <summary>
        /// Creates a new variable which can be transacted.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="name"></param>
        /// <returns>IVariable</returns>
        public IVariable<T> CreateVariable<T>(string name)
        {
            return new Variable<T>(name, this);
        }

        /// <summary>
        /// Ends executing a test case.
        /// </summary>
        public void EndTest()
        {
            if (inTransaction)
            {
                transaction.Add(new TransactionEvent(TransactionEventKind.Checkpoint, true, "End Test.", null));
            }
            else
                site.EndTest();
        }

        /// <summary>
        /// Ends transaction, either committing variables which have been bound, or rolling them back. 
        /// Note that the execution of a Checker happens implicitly within a transaction.
        /// </summary>
        /// <param name="commit"></param>
        public void EndTransaction(bool commit)
        {
            if (!inTransaction)
                throw new InvalidOperationException("no test manager transaction active which can be ended");
            if (commit)
            {
                foreach (TransactionEvent te in transaction)
                {
                    switch (te.Kind)
                    {
                        case TransactionEventKind.Assert:
                            site.Assert(te.condition, te.description);
                            break;
                        case TransactionEventKind.Assume:
                            site.Assume(te.condition, te.description);
                            break;
                        case TransactionEventKind.Checkpoint:
                            site.Checkpoint(te.description);
                            break;
                        case TransactionEventKind.Comment:
                            site.Comment(te.description);
                            break;
                        case TransactionEventKind.VariableBound:
                            site.Comment(String.Format("bound variable {0} to value: {1} ", te.variable.Name, te.variable.ObjectValue));
                            break;
                    }
                }
            }
            else
            {
                // unroll variable bindings
                foreach (TransactionEvent te in transaction)
                    if (te.Kind == TransactionEventKind.VariableBound)
                        te.variable.InternalUnbind();
            }
            transaction = null;
        }

        /// <summary>
        /// Expects one or more events described by patterns.
        /// </summary>
        /// <param name="timeOut">Time to wait for event.</param>
        /// <param name="failIfNone">Behavior on failure.</param>
        /// <param name="expected">Expected events.</param>
        /// <returns>
        ///  Returns index of expected which matched if event is available in time.
        ///  If event is not available, returns -1 if <c>failIfNone</c> is false, otherwise
        ///  produces test failure with corresponding diagnostics.
        /// </returns>
        public int ExpectEvent(TimeSpan timeOut, bool failIfNone, params ExpectedEvent[] expected)
        {
            return Expect<AvailableEvent, ExpectedEvent>(
                timeOut,
                failIfNone,
                (t, expectedEvents) =>
                {
                    StringBuilder diag = new StringBuilder();
                    diag.AppendLine(String.Format("Event must occur within {0}ms", t.TotalMilliseconds));
                    diag.AppendLine("Expecting events:");
                    foreach (var e in expectedEvents)
                    {
                        diag.AppendLine("\t" + e.ToString());
                    }
                    InternalAssert(false, diag.ToString());
                },
                (availableEvent, expectedEvent) =>
                {
                    return expectedEvent.Event == availableEvent.Event && expectedEvent.Target == availableEvent.Target;
                },
                (expectedEvent, availableEvent) =>
                {
                    if (expectedEvent.Checker != null)
                    {
                        CallChecker(expectedEvent.Checker, availableEvent.Target, availableEvent.Parameters);
                    }
                },
                expected
            );
        }

        /// <summary>
        /// Expects one or more returns described by patterns.
        /// </summary>
        /// <param name="timeOut">Time to wait for return.</param>
        /// <param name="failIfNone">Behavior on failure.</param>
        /// <param name="expected">Expected returns</param>
        /// <returns>
        ///  Returns index of expected which matched if return is available in time.
        ///  If return is not available, returns -1 if <c>failIfNone</c> is false, otherwise
        ///  produces test failure with corresponding diagnostics.
        /// </returns>
        public int ExpectReturn(TimeSpan timeOut, bool failIfNone, params ExpectedReturn[] expected)
        {
            return Expect<AvailableReturn, ExpectedReturn>(
                timeOut,
                failIfNone,
                (t, expectedReturns) =>
                {
                    InternalAssert(false, String.Format("expecting return within {0}ms", timeOut.TotalMilliseconds));
                },
                (availableReturn, expectedReturn) =>
                {
                    return (expectedReturn.Method == availableReturn.Method && expectedReturn.Target == availableReturn.Target);
                },
                (expectedReturn, availableReturn) =>
                {
                    if (expectedReturn.Checker != null)
                    {
                        CallChecker(expectedReturn.Checker, availableReturn.Target, availableReturn.Parameters);
                    }
                },
                expected
            );
        }

        /// <summary>
        /// Retrieves singleton instance of an adapter of the given type; throws exception on failure.
        /// </summary>
        /// <param name="adapterType"></param>
        /// <returns></returns>
        public object GetAdapter(Type adapterType)
        {
            return this.site.GetAdapter(adapterType);
        }

        /// <summary>
        /// Let test manager subscribe to the given event. Events raised
        /// on this <paramref name="eventInfo"/> will be propagated to the event queue.
        /// </summary>
        /// <param name="eventInfo">The event reflection information.</param>
        /// <param name="target">The target (instance to which the event belongs).</param>
        /// <param name="lateBoundMethod">Late Bound Method.</param>
        public void Subscribe(EventInfo eventInfo, object target, Delegate lateBoundMethod)
        {
            Delegate handler;
            bool created = false;
            Dictionary<Type, Delegate> handlers;
            Type type = target.GetType();
            if (!adapterEventHandlers.TryGetValue(eventInfo, out handlers))
            {
                handlers = new Dictionary<Type, Delegate>();
                adapterEventHandlers[eventInfo] = handlers;
            }

            if (!handlers.TryGetValue(type, out handler))
            {
                handler = lateBoundMethod;
                handlers[type] = handler;
                created = true;
            }

            if (!created)
            {
                UpdateEventHandlerProcessor(handler, AddEvent);
            }

            eventInfo.RemoveEventHandler(target, handler); // be sure we do not double-add event handler
            eventInfo.AddEventHandler(target, handler);
        }

        #region private methods

        /// <summary>
        /// Replace the internal generic processor of the given event handler
        /// </summary>
        /// <param name="eventHandler"></param>
        /// <param name="newProcessor"></param>
        private static void UpdateEventHandlerProcessor(Delegate eventHandler, GenericEventHandler newProcessor)
        {
            if (eventHandler == null || eventHandler.Method == null)
            {
                return;
            }

            Type declType = eventHandler.Method.DeclaringType;
            FieldInfo processorField = declType.BaseType.GetField("manager", BindingFlags.Instance | BindingFlags.NonPublic); 
            if (processorField == null)
            {
                return;
            }

            processorField.SetValue(eventHandler.Target, newProcessor.Target);
        }

        private void InternalAssert(bool condition, string description)
        {
            if (!condition && ThrowTestFailureException)
                throw new TestFailureException(description);
            else
                site.Assert(condition, description);
        }

        private void CallChecker(Delegate checker, object target, object[] parameters)
        {
            try
            {
                checker.DynamicInvoke(parameters);
            }
            catch (TargetInvocationException e)
            {
                throw e.InnerException;
            }
        }

        private int Expect<T, V>(TimeSpan timeout, bool failIfNone, Action<TimeSpan, V[]> FailAction, Func<T, V, bool> CompareAction, Action<V, T> ExpectCheckerAction, params V[] expected) where T : class
        {
            T availableObject = TryGetNext<T>(timeout, false);
            if (availableObject == null)
            {
                if (failIfNone)
                {
                    FailAction(timeout, expected);
                }
                return -1;
            }

            List<List<TransactionEvent>> failedTransactions;
            if (failIfNone)
                failedTransactions = new List<List<TransactionEvent>>();
            else
                failedTransactions = null;
            int index = 0;
            foreach (V expectedObject in expected)
            {
                if (CompareAction(availableObject, expectedObject))
                {
                    BeginTransaction();
                    try
                    {
                        ExpectCheckerAction(expectedObject, availableObject);

                        EndTransaction(true);

                        Type serviceInterface = typeof(T);
                        if (serviceInterface.Equals(typeof(AvailableEvent)))
                        {
                            AvailableEvent dummy;
                            eventQueue.TryGet(TimeSpan.FromSeconds(0), true, out dummy);
                        }

                        if (serviceInterface.Equals(typeof(AvailableReturn)))
                        {
                            AvailableReturn dummy;
                            returnQueue.TryGet(TimeSpan.FromSeconds(0), true, out dummy);
                        }

                        return index;
                    }
                    catch (TransactionFailedException)
                    {
                        if (failIfNone)
                        {
                            failedTransactions.Add(transaction);
                        }
                        EndTransaction(false);
                    }
                }

                index++;
            }

            if (!failIfNone)
                return -1;

            // build diagnosis
            StringBuilder diagnosis = new StringBuilder();
            index = 0;
            foreach (V expectedObject in expected)
            {
                if (CompareAction(availableObject, expectedObject))
                {
                    diagnosis.AppendLine(String.Format("  {0}. {1} is not matching",
                            index + 1, expectedObject.ToString()));
                }
                else
                {
                    MessageRuntimeHelper.Describe(diagnosis);
                }
                index++;
            }
            InternalAssert(false, String.Format("expected matching event, found '{0}'. Diagnosis:\r\n{1}", availableObject.ToString(), diagnosis.ToString()));
            return -1;
        }

        private T TryGetNext<T>(TimeSpan timeOut, bool consume) where T : class
        {
            Type serviceInterface = typeof(T);
            if (serviceInterface.Equals(typeof(AvailableEvent)))
            {
                AvailableEvent availableEvent = null;
                if (eventQueue.TryGet(timeOut, consume, out availableEvent))
                {
                    return availableEvent as T;
                }
                return null;
            }

            if (serviceInterface.Equals(typeof(AvailableReturn)))
            {
                AvailableReturn availableReturn = null;
                if (returnQueue.TryGet(timeOut, consume, out availableReturn))
                {
                    return availableReturn as T;
                }
                return null;
            }

            return null;
        }

        private static void Describe(StringBuilder sb, string prefix, List<TransactionEvent> transaction)
        {
            foreach (TransactionEvent te in transaction)
            {
                switch (te.Kind)
                {
                    case TransactionEventKind.Assert:
                    case TransactionEventKind.Assume:
                        sb.Append(prefix);
                        if (te.Kind == TransactionEventKind.Assert)
                            sb.Append("assert ");
                        else
                            sb.Append("assume ");
                        if (te.condition)
                            sb.Append(" succeeded: ");
                        else
                            sb.Append(" failed: ");
                        sb.AppendLine(te.description);
                        break;
                    case TransactionEventKind.Checkpoint:
                        sb.Append(prefix);
                        sb.Append("checkpoint: ");
                        sb.AppendLine(te.description);
                        break;
                    case TransactionEventKind.Comment:
                        sb.Append(prefix);
                        sb.Append("comment: ");
                        sb.AppendLine(te.description);
                        break;
                    case TransactionEventKind.VariableBound:
                        sb.Append(prefix);
                        sb.AppendLine(String.Format("bound variable {0} to value: {1} ",
                                                    te.variable.Name, te.variable.ObjectValue));
                        break;
                }
            }
        }
        #endregion
    }
}
