#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit;
}

namespace System.Collections.Concurrent
{
    internal static class ConcurrentDictionaryCompatibilityExtensions
    {
        internal static TValue GetOrAdd<TKey, TValue, TArg>(
            this ConcurrentDictionary<TKey, TValue> dictionary,
            TKey key,
            Func<TKey, TArg, TValue> valueFactory,
            TArg factoryArgument)
        {
            return dictionary.GetOrAdd(key, current => valueFactory(current, factoryArgument));
        }
    }
}
#endif
