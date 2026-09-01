using System;
using System.Linq;
using System.Reflection;

namespace TeronClaudeCodeVS.Tests.Infrastructure
{
    /// <summary>
    /// Calls a genuinely private static method on the shipping assembly.
    /// <para>
    /// <c>InternalsVisibleTo</c> covers the internal seams, and those are called as ordinary code.
    /// A handful of the oldest ones are <c>private</c> - <c>VsDiffTab.ApplyForward</c> and its
    /// neighbours - and widening them to internal purely so a test can reach them would change the
    /// shipping code to suit the test, which is the wrong way round. This reaches them the way the
    /// PowerShell harnesses did, with one improvement: a missing method reports what the type
    /// actually offers, so a rename surfaces as a readable failure rather than a null reference.
    /// </para>
    /// </summary>
    internal static class Reflect
    {
        private const BindingFlags AnyStatic =
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

        public static T? StaticCall<T>(Type type, string methodName, params object?[] arguments)
            => (T?)StaticCall(type, methodName, arguments);

        public static object? StaticCall(Type type, string methodName, params object?[] arguments)
        {
            MethodInfo method = Method(type, methodName, arguments.Length);

            try
            {
                return method.Invoke(null, arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                // Surface the real exception, not the reflection wrapper around it.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;   // unreachable
            }
        }

        private static MethodInfo Method(Type type, string methodName, int parameterCount)
        {
            MethodInfo[] candidates = type.GetMethods(AnyStatic)
                .Where(m => m.Name == methodName && m.GetParameters().Length == parameterCount)
                .ToArray();

            if (candidates.Length == 1)
                return candidates[0];

            string available = string.Join(", ", type.GetMethods(AnyStatic).Select(m => m.Name).Distinct().OrderBy(n => n));

            throw new MissingMethodException(
                candidates.Length == 0
                    ? $"{type.Name}.{methodName} taking {parameterCount} argument(s) was not found. Static methods on {type.Name}: {available}"
                    : $"{type.Name}.{methodName} taking {parameterCount} argument(s) is ambiguous ({candidates.Length} overloads).");
        }
    }
}
