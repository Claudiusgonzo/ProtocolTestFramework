// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Protocols.TestTools.Messages;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Microsoft.Protocols.TestTools
{
    /// <summary>
    /// Helpers for dealing with test managers.
    /// </summary>
    public static class TestManagerHelpers
    {
        static Dictionary<Type, bool> adapterTypes = new Dictionary<Type, bool>();

        /// <summary>
        /// Checks whether a given type represents a test adapter.
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static bool IsAdapter(Type type)
        {
            bool isAdapter;

            lock (adapterTypes)
            {
                if (!adapterTypes.TryGetValue(type, out isAdapter))
                {
                    object[] attrs = type.GetCustomAttributes(typeof(TestAdapterAttribute), true);
                    isAdapter = (attrs != null && attrs.Length > 0);

                    if (!isAdapter)
                    {
                        foreach (Type intf in type.GetInterfaces())
                        {
                            if (IsAdapter(intf))
                            {
                                isAdapter = true;
                                break;
                            }
                        }

                        if (!isAdapter && type.BaseType != null)
                        {
                            isAdapter = IsAdapter(type.BaseType);
                        }
                    }

                    adapterTypes[type] = isAdapter;
                }
            }

            return isAdapter;
        }

        /// <summary>
        /// Gets the parameter types of a delegate.
        /// </summary>
        /// <param name="delegateType"></param>
        /// <returns></returns>
        internal static Type[] GetDelegateParameterTypes(Type delegateType)
        {
            if (!typeof(Delegate).IsAssignableFrom(delegateType))
                throw new InvalidOperationException("not a delegate type");
            return GetMethodParameterTypes(delegateType.GetMethod("Invoke"));
        }

        /// <summary>
        /// Determines the way how to call a checker for a return method event. Calculates whether the method and checker
        /// types are compatible, and whether to include the target in the call or not.
        /// </summary>
        /// <param name="methodInfo"></param>
        /// <param name="checkerType"></param>
        /// <returns></returns>
        internal static CheckerCallingStyle GetReturnCheckerCallingStyle(MethodBase methodInfo, Type checkerType)
        {

            Type[] checkerArguments = GetDelegateParameterTypes(checkerType);
            if (checkerArguments.Length == 1 && checkerArguments[0] == typeof(object))
                if (RequiresTarget(methodInfo))
                    return CheckerCallingStyle.TargetAndParametersArray;
                else
                    return CheckerCallingStyle.ParametersArray;
            int offset = 0;
            CheckerCallingStyle style = CheckerCallingStyle.ParametersDirect;
            List<Type> methodOutputs = new List<Type>();
            Type returnType;
            if (methodInfo is MethodInfo)
                returnType = ((MethodInfo)methodInfo).ReturnType;
            else
                returnType = null;

            foreach (ParameterInfo info in methodInfo.GetParameters())
            {
                if (info.ParameterType.IsByRef)
                    methodOutputs.Add(info.ParameterType.GetElementType());
            }

            //resultType should be reordered after method parameters
            if (returnType != null && returnType != typeof(void))
                methodOutputs.Add(returnType);

            if (checkerArguments.Length == methodOutputs.Count + 1)
            {
                if (!RequiresTarget(methodInfo) || checkerArguments[0] != methodInfo.DeclaringType)
                    return CheckerCallingStyle.Invalid;
                offset = 1;
                style = CheckerCallingStyle.TargetAndParametersDirect;
            }
            for (int i = 0; i < methodOutputs.Count; i++)
                if (methodOutputs[i] != checkerArguments[offset + i])
                    return CheckerCallingStyle.Invalid;
            return style;
        }

        /// <summary>
        /// Determines the way how to call a checker for an event. Calculates whether the event and checker
        /// types are compatible, and whether to include the target in the call or not.
        /// </summary>
        /// <param name="eventInfo"></param>
        /// <param name="checkerType"></param>
        /// <returns></returns>
        internal static CheckerCallingStyle GetEventCheckerCallingStyle(EventInfo eventInfo, Type checkerType)
        {
            Type handlerType = eventInfo.EventHandlerType;
            if (checkerType == handlerType)
                return CheckerCallingStyle.ParametersDirect;
            Type[] eventArguments = GetDelegateParameterTypes(handlerType);
            Type[] checkerArguments = GetDelegateParameterTypes(checkerType);
            if (checkerArguments.Length == 1 && checkerArguments[0] == typeof(object))
                if (RequiresTarget(eventInfo))
                    return CheckerCallingStyle.TargetAndParametersArray;
                else
                    return CheckerCallingStyle.ParametersArray;
            int offset = 0;
            CheckerCallingStyle style = CheckerCallingStyle.ParametersDirect;
            if (checkerArguments.Length == eventArguments.Length + 1)
            {
                if (!RequiresTarget(eventInfo) || checkerArguments[0] != eventInfo.DeclaringType)
                    return CheckerCallingStyle.Invalid;
                offset = 1;
                style = CheckerCallingStyle.TargetAndParametersDirect;
            }
            for (int i = 0; i < eventArguments.Length; i++)
                if (eventArguments[i] != checkerArguments[offset + i])
                    return CheckerCallingStyle.Invalid;
            return style;
        }

        /// <summary>
        /// Gets the parameter types of a method.
        /// </summary>
        /// <param name="methodInfo"></param>
        /// <returns></returns>
        internal static Type[] GetMethodParameterTypes(MethodBase methodInfo)
        {
            ParameterInfo[] paramInfos = methodInfo.GetParameters();
            Type[] result = new Type[paramInfos.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = paramInfos[i].ParameterType;
            return result;
        }

        /// <summary>
        /// Checks whether an event requires a target object, which is the case
        /// when it is instance based and when it is not originating from an adapter.
        /// </summary>
        /// <param name="memberInfo">reflection member info</param>
        /// <returns></returns>
        internal static bool RequiresTarget(MemberInfo memberInfo)
        {
            bool isStatic =
                memberInfo is MethodBase ? ((MethodBase)memberInfo).IsStatic
                : memberInfo is EventInfo ? ((EventInfo)memberInfo).GetAddMethod().IsStatic
                : false;
            return !isStatic && !TestManagerHelpers.IsAdapter(memberInfo.DeclaringType);
        }

        /// <summary>
        /// Equality helper class to compare whether two objects are equal.
        /// </summary>
        /// <param name="left"></param>
        /// <param name="right"></param>
        /// <returns></returns>
        public static bool Equality(object left, object right)
        {
            if (left == null && right == null)
                return true;
            else if (left == null || right == null)
                return false;
            Type leftType = left.GetType();
            Type rightType = right.GetType();
            if (leftType == rightType)
            {
                if ((leftType.IsClass && !typeof(CompoundValue).IsAssignableFrom(leftType)))
                    return Object.ReferenceEquals(left, right);
                else
                    return left.Equals(right);
            }
            else
            {
                throw new NotSupportedException(string.Format("Test Manager doesn't know how to compare left {0} and right {1} value", left, right));
            }
        }

        #region Reflection helpers

        /// <summary>
        /// Return method info for given meta-data information. Throws exception if method cannot be resolved.
        /// </summary>
        /// <param name="type"></param>
        /// <param name="name"></param>
        /// <param name="parameterTypes"></param>
        /// <returns></returns>
        public static MethodInfo GetMethodInfo(Type type, string name, params Type[] parameterTypes)
        {
            if (type == null)
            {
                throw new ArgumentNullException("type");
            }
            MethodInfo info = type.GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                null, parameterTypes, null);
            if (info == null)
                throw new InvalidOperationException(String.Format("Cannot resolve method '{0}' in type '{1}'",
                                                                    name, type));
            return info;
        }

        /// <summary>
        /// Return constructor for given meta-data information. Throws exception if constructor cannot be resolved.
        /// </summary>
        /// <param name="type"></param>
        /// <param name="parameterTypes"></param>
        /// <returns></returns>
        public static ConstructorInfo GetConstructorInfo(Type type, params Type[] parameterTypes)
        {
            if (type == null)
            {
                throw new ArgumentNullException("type");
            }
            ConstructorInfo info = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                                     parameterTypes, null);
            if (info == null)
                throw new InvalidOperationException(String.Format("Cannot resolve constructor for type '{0}'",
                                                                    type));
            return info;
        }


        /// <summary>
        /// Return event for given meta-data information. Throws exception if event cannot be resolved.
        /// </summary>
        /// <param name="type"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        public static EventInfo GetEventInfo(Type type, string name)
        {
            if (type == null)
            {
                throw new ArgumentNullException("type");
            }
            EventInfo info = type.GetEvent(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (info == null)
                throw new InvalidOperationException(String.Format("Cannot resolve event '{0}' for type '{1}'",
                                                                name, type));
            return info;
        }

        #endregion

        #region Assertion Helpers

        /// <summary>
        /// Asserts two values are equal.
        /// </summary>
        /// <typeparam name="T">Type of values.</typeparam>
        /// <param name="manager">The test manager.</param>
        /// <param name="expected">The expected value.</param>
        /// <param name="actual">The actual value.</param>
        /// <param name="context">The description of the context under which both values are compared.</param>
        public static void AssertAreEqual<T>(ITestManager manager, T expected, T actual, string context)
        {
            manager.Assert(Object.Equals(expected, actual),
                string.Format("expected \'{0}\', actual \'{1}\' ({2})",
                    MessageRuntimeHelper.Describe(expected), MessageRuntimeHelper.Describe(actual), context));
        }

        /// <summary>
        /// Asserts a variable's equality to a value or bind the variable to a value if it hasn't been bound yet.
        /// </summary>
        /// <typeparam name="T">Type of the variable and value.</typeparam>
        /// <param name="manager">The test manager.</param>
        /// <param name="var">The variable.</param>
        /// <param name="actual">The actual value.</param>
        /// <param name="context">The description of the context under which the comparison or binding happens.</param>
        public static void AssertBind<T>(ITestManager manager, IVariable<T> var, T actual, string context)
        {
            if (var.IsBound)
            {
                AssertAreEqual<T>(manager, var.Value, actual,
                    context + "; expected value originates from previous binding");
            }
            else
            {
                var.Value = actual;
            }
        }

        /// <summary>
        /// Asserts equality of two variables, or bind one variable to another if only one of them is bound. 
        /// If neither of the two variables are bound, this API does nothing.
        /// </summary>
        /// <typeparam name="T">Type of the variables.</typeparam>
        /// <param name="manager">The test manager.</param>
        /// <param name="v1">The first variable.</param>
        /// <param name="v2">The second variable.</param>
        /// <param name="context">The context under which the comparison or binding happens.</param>
        public static void AssertBind<T>(ITestManager manager, IVariable<T> v1, IVariable<T> v2, string context)
        {
            if ((v1.IsBound && v2.IsBound))
            {
                AssertAreEqual<T>(manager, v1.Value, v2.Value,
                    context + "; values originate from previous binding");
                return;
            }
            if (v1.IsBound)
            {
                v2.Value = v1.Value;
            }
            else
            {
                if (v2.IsBound)
                {
                    v1.Value = v2.Value;
                }
            }
        }

        /// <summary>
        /// Asserts a value is not null.
        /// </summary>
        /// <param name="manager">The test manager.</param>
        /// <param name="actual">The value under check.</param>
        /// <param name="context">The context under which the value is checked.</param>
        public static void AssertNotNull(ITestManager manager, object actual, string context)
        {
            manager.Assert(actual != null, string.Format("expected non-null value ({0})", context));
        }

        #endregion

    }

    /// <summary>
    /// A base class for value classes. Value classes and its extenders have a sealed structural equality and hashcode which is inherited by extenders.
    /// </summary>
    [Serializable]
    public abstract class CompoundValue
    {
        /// <summary>
        /// Fixes the equality of all extenders of a compound value. Two instances are
        /// equal if they have the same type, and the assignments to all fields are equal.
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public override bool Equals(object obj)
        {
            if (Object.ReferenceEquals(this, obj))
                return true;
            if (obj == null)
                return false;
            Type myType = GetType();
            if (obj.GetType() != myType)
                return false;
            FieldInfo[] fields = myType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                object thisValue = field.GetValue(this);
                object otherValue = field.GetValue(obj);
                if (thisValue == null)
                {
                    if (otherValue != null)
                        return false;
                }
                else if (otherValue == null || !thisValue.Equals(otherValue))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Operator equality to Equals.
        /// </summary>
        /// <param name="left"></param>
        /// <param name="right"></param>
        /// <returns></returns>
        public static bool operator ==(CompoundValue left, CompoundValue right)
        {
            if ((object)left == null)
                return (object)right == null;
            else
                return left.Equals(right);
        }

        /// <summary>
        /// Operator inequality mapping to Equals.
        /// </summary>
        /// <param name="left"></param>
        /// <param name="right"></param>
        /// <returns></returns>
        public static bool operator !=(CompoundValue left, CompoundValue right)
        {
            return !(left == right);
        }

        /// <summary>
        /// Fixes the hashcode of all extenders of a value class such that it is consistent
        /// with the fixed equality.
        /// </summary>
        /// <returns></returns>
        public override int GetHashCode()
        {
            Type myType = GetType();
            int hc = myType.GetHashCode();
            FieldInfo[] fields = myType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            for (int i = 0; i < fields.Length; i++)
            {
                object thisValue = fields[i].GetValue(this);
                if (thisValue != null)
                    hc = ((hc << 27) | (hc >> 5)) + thisValue.GetHashCode();
            }
            return hc;
        }

        /// <summary>
        /// Provides a default implementation for conversion to strings, displaying the structure
        /// of the instance.
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            Type myType = GetType();
            FieldInfo[] fields = myType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            StringBuilder b = new StringBuilder();
            b.Append(myType.Name);
            b.Append("(");
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                    b.Append(",");
                b.Append(fields[i].Name);
                b.Append("=");
                object thisValue = fields[i].GetValue(this);
                if (thisValue == null)
                    b.Append("null");
                else
                    b.Append(MessageRuntimeHelper.Describe<object>(thisValue));
            }
            b.Append(")");
            return b.ToString();
        }

    }

    /// <summary>
    /// An enumeration indicating how to invoke a checker.
    /// </summary>
    public enum CheckerCallingStyle
    {
        /// <summary>
        /// Invalid
        /// </summary>
        Invalid,
        /// <summary>
        /// ParametersDirect
        /// </summary>
        ParametersDirect,
        /// <summary>
        /// TargetAndParametersDirect
        /// </summary>
        TargetAndParametersDirect,
        /// <summary>
        /// ParametersArray
        /// </summary>
        ParametersArray,
        /// <summary>
        /// TargetAndParametersArray
        /// </summary>
        TargetAndParametersArray
    }
}
