using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Engarde;

/// <summary>
/// Entry points called by woven code; not intended to be called directly
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class EngardeChecks
{
    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly ConcurrentDictionary<(nint, nint), Guards> MethodGuardsCache = new();
    private static readonly ConcurrentDictionary<(nint, nint, int), Guards> ParameterGuardsCache = new();
    private static readonly ConcurrentDictionary<(nint, nint), Guards> PropertyGuardsCache = new();
    private static readonly ConcurrentDictionary<(nint, nint), Guards> FieldGuardsCache = new();
    private static readonly ConcurrentDictionary<Type, bool> SkipFallbackFailuresCache = new();
    private static readonly AsyncLocal<SuppressedGuard?> Suppressed = new();
    private static Func<Type, string, bool?>? _guardOverride;

    /// <summary>Installs the optional forced guard result supplied by Engarde.Testing.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void SetGuardOverride(Func<Type, string, bool?>? guardOverride) =>
        Volatile.Write(ref _guardOverride, guardOverride);

    internal static object ResolveGuardedService(
        IServiceProvider services, Type guardedType, EngardeAttribute guard, Func<object> createPrimary)
    {
        var enabled = Wait((services, guardedType, guard), static state =>
            EvaluateTypeGuardAsync(state.services, state.guardedType, state.guard));
        using var suppression = Suppress(new FallbackInvocation(null, null, guard.GetType(), guard.FeatureNames.ToArray()));
        if (enabled)
            return createPrimary();
        return services.GetService(guard.FallbackType!)
            ?? throw new InvalidOperationException(
                $"Fallback type '{guard.FallbackType}' is not registered in the active DI scope.");
    }

    private static async ValueTask<bool> EvaluateTypeGuardAsync(
        IServiceProvider services, Type guardedType, EngardeAttribute guard)
    {
        await using var evaluation = EngardeEvaluation.Begin(services, false);
        foreach (var featureName in guard.FeatureNames)
            if (!await EvaluateGuardAsync(evaluation, guard, featureName, guardedType, null).ConfigureAwait(false))
            {
                if (!evaluation.SkipFallbackFailures && !SkipsFallbackFailures(guard.GetType()))
                    guard.OnFeatureMissing(featureName, evaluation.CreateContext(guardedType, null));
                return false;
            }
        return true;
    }

    public static void CheckMethod(RuntimeMethodHandle method, RuntimeTypeHandle type)
    {
        Validate(GetMethodGuards(method, type), null);
    }

    public static void CheckParameter<T>(T value, RuntimeMethodHandle method, RuntimeTypeHandle type, int index)
    {
        if (IsEmpty(value))
            return;

        Validate(GetParameterGuards(method, type, index), value);
    }

    public static bool CheckProperty<T>(T value, RuntimeMethodHandle setter, RuntimeTypeHandle type)
    {
        if (IsEmpty(value))
            return true;

        var guards = PropertyGuardsCache.GetOrAdd((setter.Value, type.Value), static (_, handles) =>
        {
            var property = FindSetterProperty(MethodBase.GetMethodFromHandle(handles.setter, handles.type)!)!;

            return new Guards(property, CollectPropertyGuards(property));
        }, (setter, type));

        return Validate(guards, value);
    }

    public static bool CheckField<T>(T value, RuntimeFieldHandle field, RuntimeTypeHandle type)
    {
        if (IsEmpty(value))
            return true;

        var guards = FieldGuardsCache.GetOrAdd((field.Value, type.Value), static (_, handles) =>
        {
            var fieldInfo = FieldInfo.GetFieldFromHandle(handles.field, handles.type);

            // Writes to the backing field of a guarded auto-property (e.g. from a constructor) are checked as the property
            var property = GetBackingFieldProperty(fieldInfo);

            return property is null
                ? new Guards(fieldInfo, GetGuards(fieldInfo))
                : new Guards(property, [..GetGuards(fieldInfo), ..CollectPropertyGuards(property)]);
        }, (field, type));

        return Validate(guards, value);
    }

    public static Task CheckTaskMethod(
        object? target, object?[] arguments, RuntimeMethodHandle implementation,
        RuntimeMethodHandle method, RuntimeTypeHandle type, Type[]? genericArguments) =>
        CheckTaskMethodCore(target, arguments, implementation, method, type, genericArguments);

    public static Task<TResult> CheckTaskMethod<TResult>(
        object? target, object?[] arguments, RuntimeMethodHandle implementation,
        RuntimeMethodHandle method, RuntimeTypeHandle type, Type[]? genericArguments) =>
        CheckTaskMethodCore<TResult>(target, arguments, implementation, method, type, genericArguments);

    public static ValueTask CheckValueTaskMethod(
        object? target, object?[] arguments, RuntimeMethodHandle implementation,
        RuntimeMethodHandle method, RuntimeTypeHandle type, Type[]? genericArguments) =>
        CheckValueTaskMethodCore(target, arguments, implementation, method, type, genericArguments);

    public static ValueTask<TResult> CheckValueTaskMethod<TResult>(
        object? target, object?[] arguments, RuntimeMethodHandle implementation,
        RuntimeMethodHandle method, RuntimeTypeHandle type, Type[]? genericArguments) =>
        CheckValueTaskMethodCore<TResult>(target, arguments, implementation, method, type, genericArguments);

    public static void InvokeMethod(
        object? target, object?[] arguments, RuntimeMethodHandle implementation,
        RuntimeMethodHandle method, RuntimeTypeHandle type, Type[]? genericArguments) =>
        Wait((target, arguments, implementation, method, type, genericArguments), static state =>
            InvokeMethodCore<object?>(state.target, state.arguments, state.implementation, state.method, state.type,
                state.genericArguments, false));

    public static TResult InvokeMethod<TResult>(
        object? target, object?[] arguments, RuntimeMethodHandle implementation,
        RuntimeMethodHandle method, RuntimeTypeHandle type, Type[]? genericArguments) =>
        Wait((target, arguments, implementation, method, type, genericArguments), static state =>
            InvokeMethodCore<TResult>(state.target, state.arguments, state.implementation, state.method, state.type,
                state.genericArguments, true));

    private static async Task CheckTaskMethodCore(
        object? target, object?[] arguments, RuntimeMethodHandle implementation,
        RuntimeMethodHandle method, RuntimeTypeHandle type, Type[]? genericArguments)
    {
        await using var evaluation = EngardeEvaluation.Begin();
        var fallback = await ValidateMethodAsync(evaluation, method, type, arguments, target, genericArguments).ConfigureAwait(false);
        using var suppression = Suppress(fallback);
        await ((Task)InvokeSelected(fallback, implementation, type, genericArguments, target, arguments)!).ConfigureAwait(false);
    }

    private static async Task<TResult> CheckTaskMethodCore<TResult>(
        object? target, object?[] arguments, RuntimeMethodHandle implementation,
        RuntimeMethodHandle method, RuntimeTypeHandle type, Type[]? genericArguments)
    {
        await using var evaluation = EngardeEvaluation.Begin();
        var fallback = await ValidateMethodAsync(evaluation, method, type, arguments, target, genericArguments).ConfigureAwait(false);
        using var suppression = Suppress(fallback);
        return await ((Task<TResult>)InvokeSelected(fallback, implementation, type, genericArguments, target, arguments)!).ConfigureAwait(false);
    }

    private static async ValueTask CheckValueTaskMethodCore(
        object? target, object?[] arguments, RuntimeMethodHandle implementation,
        RuntimeMethodHandle method, RuntimeTypeHandle type, Type[]? genericArguments)
    {
        await using var evaluation = EngardeEvaluation.Begin();
        var fallback = await ValidateMethodAsync(evaluation, method, type, arguments, target, genericArguments).ConfigureAwait(false);
        using var suppression = Suppress(fallback);
        await ((ValueTask)InvokeSelected(fallback, implementation, type, genericArguments, target, arguments)!).ConfigureAwait(false);
    }

    private static async ValueTask<TResult> CheckValueTaskMethodCore<TResult>(
        object? target, object?[] arguments, RuntimeMethodHandle implementation,
        RuntimeMethodHandle method, RuntimeTypeHandle type, Type[]? genericArguments)
    {
        await using var evaluation = EngardeEvaluation.Begin();
        var fallback = await ValidateMethodAsync(evaluation, method, type, arguments, target, genericArguments).ConfigureAwait(false);
        using var suppression = Suppress(fallback);
        return await ((ValueTask<TResult>)InvokeSelected(fallback, implementation, type, genericArguments, target, arguments)!).ConfigureAwait(false);
    }

    private static async ValueTask<TResult> InvokeMethodCore<TResult>(
        object? target, object?[] arguments, RuntimeMethodHandle implementation,
        RuntimeMethodHandle method, RuntimeTypeHandle type, Type[]? genericArguments, bool hasResult)
    {
        await using var evaluation = EngardeEvaluation.Begin();
        var fallback = await ValidateMethodAsync(evaluation, method, type, arguments, target, genericArguments).ConfigureAwait(false);
        using var suppression = Suppress(fallback);
        var result = InvokeSelected(fallback, implementation, type, genericArguments, target, arguments);
        return hasResult ? (TResult)result! : default!;
    }

    private static object? InvokeSelected(FallbackInvocation? fallback, RuntimeMethodHandle implementation,
        RuntimeTypeHandle type, Type[]? genericArguments, object? target, object?[] arguments) =>
        fallback is null
            ? Invoke(implementation, type, genericArguments, target, arguments)
            : Invoke(fallback.Method!, fallback.Target, arguments);

    private static object? Invoke(
        RuntimeMethodHandle implementation, RuntimeTypeHandle type, Type[]? genericArguments,
        object? target, object?[] arguments)
    {
        var method = (MethodInfo)MethodBase.GetMethodFromHandle(implementation, type)!;
        if (method.IsGenericMethodDefinition)
            method = method.MakeGenericMethod(genericArguments!);

        return Invoke(method, target, arguments);
    }

    private static object? Invoke(MethodInfo method, object? target, object?[] arguments)
    {
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static PropertyInfo? GetBackingFieldProperty(FieldInfo field)
    {
        const string prefix = "<";
        const string suffix = ">k__BackingField";

        if (!field.Name.StartsWith(prefix, StringComparison.Ordinal) || !field.Name.EndsWith(suffix, StringComparison.Ordinal))
            return null;

        var propertyName = field.Name.Substring(prefix.Length, field.Name.Length - prefix.Length - suffix.Length);

        return field.DeclaringType!.GetProperties(DeclaredMembers).FirstOrDefault(property => property.Name == propertyName);
    }

    private static bool Validate(Guards guards, object? value)
    {
        if (!HasEnabledGuards(guards, value))
            return true;

        return Wait((guards, value), static state => ValidateAsync(state.guards, state.value));
    }

    private static async ValueTask<bool> ValidateAsync(Guards guards, object? value)
    {
        await using var evaluation = EngardeEvaluation.Begin();
        return await ValidateAsync(evaluation, guards, value).ConfigureAwait(false);
    }

    private static T Wait<TState, T>(TState state, Func<TState, ValueTask<T>> operation)
    {
        // Attribute checks must also work on threads with a single-threaded synchronization context.
        if (SynchronizationContext.Current is not null)
            return Task.Run(async () => await operation(state).ConfigureAwait(false)).GetAwaiter().GetResult();
        var task = operation(state);
        return task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
    }

    private static async ValueTask<FallbackInvocation?> ValidateMethodAsync(
        EngardeEvaluation evaluation, RuntimeMethodHandle method, RuntimeTypeHandle type, object?[] arguments,
        object? target, Type[]? genericArguments)
    {
        var methodGuards = GetMethodGuards(method, type);
        var parameterGuards = new Guards[arguments.Length];
        var hasEnabledGuards = HasEnabledGuards(methodGuards, null);
        for (var index = 0; index < arguments.Length; index++)
        {
            parameterGuards[index] = GetParameterGuards(method, type, index);
            hasEnabledGuards |= !IsEmptyObject(arguments[index]) && HasEnabledGuards(parameterGuards[index], arguments[index]);
        }
        if (!hasEnabledGuards)
            return null;

        var fallback = await ValidateMethodGuardsAsync(evaluation, methodGuards, target, genericArguments).ConfigureAwait(false);

        for (var index = 0; index < arguments.Length; index++)
        {
            var value = arguments[index];
            if (!IsEmptyObject(value))
                await ValidateAsync(evaluation, parameterGuards[index], value).ConfigureAwait(false);
        }
        return fallback;
    }

    private static async ValueTask<FallbackInvocation?> ValidateMethodGuardsAsync(
        EngardeEvaluation evaluation, Guards guards, object? target, Type[]? genericArguments)
    {
        EngardeContext? context = null;
        FallbackInvocation? fallback = null;
        foreach (var guard in guards.Attributes)
        {
            for (var index = 0; index < guard.FeatureNames.Count; index++)
            {
                var featureName = guard.FeatureNames[index];
                if (await EvaluateGuardAsync(evaluation, guard, featureName, guards.Member, null).ConfigureAwait(false))
                    continue;

                var resolved = ResolveFallback(evaluation, (MethodInfo)guards.Member, guard, featureName, target, genericArguments);
                fallback ??= resolved;
                if (resolved is not null)
                {
                    if (!evaluation.SkipFallbackFailures && !SkipsFallbackFailures(guard.GetType()))
                    {
                        context ??= evaluation.CreateContext(guards.Member, null);
                        guard.OnFeatureMissing(featureName, context);
                    }
                    continue;
                }
                context ??= evaluation.CreateContext(guards.Member, null);
                guard.OnFeatureMissing(featureName, context);
            }
        }
        return fallback;
    }

    private static FallbackInvocation? ResolveFallback(EngardeEvaluation evaluation, MethodInfo guarded,
        EngardeAttribute guard, string featureName, object? target, Type[]? genericArguments)
    {
        var fallbackType = guard.FallbackType;
        if (fallbackType is null)
            return null;

        var fallbackName = guard.FallbackMethod ?? guarded.Name;
        var source = guarded.IsGenericMethodDefinition && genericArguments is not null
            ? guarded.MakeGenericMethod(genericArguments)
            : guarded;
        MethodInfo? fallback = null;
        foreach (var candidate in fallbackType.GetMethods(DeclaredMembers).Where(method => method.Name == fallbackName))
        {
            MethodInfo current = candidate;
            if (candidate.IsGenericMethodDefinition)
            {
                if (genericArguments is null || candidate.GetGenericArguments().Length != genericArguments.Length)
                    continue;
                current = candidate.MakeGenericMethod(genericArguments);
            }
            if (current.ReturnType != source.ReturnType ||
                !current.GetParameters().Select(parameter => parameter.ParameterType)
                    .SequenceEqual(source.GetParameters().Select(parameter => parameter.ParameterType)))
                continue;
            fallback = current;
            break;
        }
        if (fallback is null)
            return null;

        object? fallbackTarget;
        if (fallback.IsStatic)
            fallbackTarget = null;
        else if (guard.FallbackMethod is not null && fallbackType.IsInstanceOfType(target))
            fallbackTarget = target;
        else
            fallbackTarget = evaluation.Services?.GetService(fallbackType);
        return fallback.IsStatic || fallbackTarget is not null
            ? new FallbackInvocation(fallback, fallbackTarget, guard.GetType(), guard.FeatureNames.ToArray())
            : null;
    }

    private static async ValueTask<bool> ValidateAsync(EngardeEvaluation evaluation, Guards guards, object? value)
    {
        var enabled = true;
        EngardeContext? context = null;
        foreach (var guard in guards.Attributes)
        {
            for (var index = 0; index < guard.FeatureNames.Count; index++)
            {
                var featureName = guard.FeatureNames[index];
                if (!MatchesGuardedValue(guard, value))
                    continue;
                if (!await EvaluateGuardAsync(evaluation, guard, featureName, guards.Member, value).ConfigureAwait(false))
                {
                    enabled = false;
                    context ??= evaluation.CreateContext(guards.Member, value);
                    guard.OnFeatureMissing(featureName, context);
                }
            }
        }
        return enabled;
    }

    private static bool HasEnabledGuards(Guards guards, object? value)
    {
        foreach (var guard in guards.Attributes)
        {
            if (!MatchesGuardedValue(guard, value))
                continue;
            for (var index = 0; index < guard.FeatureNames.Count; index++)
                if (GetGuardResultOverride(guard.GetType(), guard.FeatureNames[index]) != true)
                    return true;
        }
        return false;
    }

    private static bool MatchesGuardedValue(EngardeAttribute guard, object? value) =>
        !guard.HasGuardedValue || Equals(guard.GuardedValue, value);

    private static ValueTask<bool> EvaluateGuardAsync(EngardeEvaluation evaluation, EngardeAttribute guard,
        string featureName, MemberInfo member, object? value)
    {
        var forced = GetGuardResultOverride(guard.GetType(), featureName);
        return forced.HasValue
            ? new ValueTask<bool>(forced.Value)
            : guard.EvaluateAsync(evaluation, featureName, member, value, null);
    }

    private static bool? GetGuardResultOverride(Type guardType, string featureName) =>
        IsSuppressed(guardType, featureName)
            ? true
            : Volatile.Read(ref _guardOverride)?.Invoke(guardType, featureName);

    private static bool IsSuppressed(Type guardType, string featureName)
    {
        for (var current = Suppressed.Value; current is not null; current = current.Previous)
            if (current.GuardType == guardType && current.FeatureNames.Contains(featureName))
                return true;
        return false;
    }

    private static IDisposable Suppress(FallbackInvocation? fallback)
    {
        if (fallback is null) return EmptyDisposable.Instance;
        var previous = Suppressed.Value;
        Suppressed.Value = new(fallback.GuardType, fallback.FeatureNames, previous);
        return new Scope(() => Suppressed.Value = previous);
    }

    private static bool SkipsFallbackFailures(Type guardType) => SkipFallbackFailuresCache.GetOrAdd(guardType,
        static type => type.IsDefined(typeof(SkipFallbackFailuresAttribute), inherit: true));

    private static Guards GetMethodGuards(RuntimeMethodHandle method, RuntimeTypeHandle type) =>
        MethodGuardsCache.GetOrAdd((method.Value, type.Value), static (_, handles) =>
        {
            var methodBase = MethodBase.GetMethodFromHandle(handles.method, handles.type)!;
            return new Guards(methodBase, CollectMethodGuards(methodBase));
        }, (method, type));

    private static Guards GetParameterGuards(RuntimeMethodHandle method, RuntimeTypeHandle type, int index) =>
        ParameterGuardsCache.GetOrAdd((method.Value, type.Value, index), static (_, handles) =>
        {
            var methodBase = MethodBase.GetMethodFromHandle(handles.method, handles.type)!;
            return new Guards(methodBase, CollectParameterGuards(methodBase, handles.index));
        }, (method, type, index));

    private static bool IsEmpty<T>(T value)
    {
        return value switch
        {
            null => true,
            string text => text.Length == 0,
            ICollection collection => collection.Count == 0,
            _ => EqualityComparer<T>.Default.Equals(value, default!)
        };
    }

    private static bool IsEmptyObject(object? value)
    {
        return value switch
        {
            null => true,
            string text => text.Length == 0,
            ICollection collection => collection.Count == 0,
            ValueType => value.Equals(Activator.CreateInstance(value.GetType())),
            _ => false
        };
    }

    private static EngardeAttribute[] GetGuards(ICustomAttributeProvider provider)
    {
        return provider.GetCustomAttributes(typeof(EngardeAttribute), false).Cast<EngardeAttribute>().ToArray();
    }

    private static EngardeAttribute[] CollectMethodGuards(MethodBase method)
    {
        IEnumerable<EngardeAttribute> guards = [..GetGuards(method), ..InheritedMethods(method).SelectMany(GetGuards)];
        return ((method is ConstructorInfo || !method.IsSpecialName) && !method.IsDefined(typeof(CompilerGeneratedAttribute), false)
            ? guards.Concat(TypeGuards(method))
            : guards).ToArray();
    }

    private static EngardeAttribute[] CollectParameterGuards(MethodBase method, int index) =>
        [..GetGuards(method.GetParameters()[index]), ..InheritedMethods(method).SelectMany(inherited => GetGuards(inherited.GetParameters()[index]))];

    private static EngardeAttribute[] CollectPropertyGuards(PropertyInfo property) =>
        [..GetGuards(property), ..(property.SetMethod is { } setter
            ? InheritedMethods(setter).SelectMany(inherited => FindSetterProperty(inherited) is { } guarded ? GetGuards(guarded) : [])
            : [])];

    private static PropertyInfo? FindSetterProperty(MethodBase setter) => setter.DeclaringType!.GetProperties(DeclaredMembers)
        .FirstOrDefault(property => property.SetMethod is { } candidate && SameMethod(candidate, setter));

    private static IEnumerable<MethodBase> InheritedMethods(MethodBase method)
    {
        if (method is not MethodInfo { IsVirtual: true } info)
            yield break;

        var declaringType = info.DeclaringType!;
        var root = info.GetBaseDefinition();
        if (!SameMethod(root, info))
        {
            for (var type = declaringType.BaseType; type is not null; type = type.BaseType)
            {
                var overridden = type.GetMethods(DeclaredMembers)
                    .FirstOrDefault(candidate => candidate.IsVirtual && SameMethod(candidate.GetBaseDefinition(), root));
                if (overridden is not null)
                    yield return overridden;
            }
        }

        if (declaringType.IsInterface)
            yield break;
        foreach (var @interface in declaringType.GetInterfaces().OrderBy(@interface => @interface.FullName, StringComparer.Ordinal))
        {
            var map = declaringType.GetInterfaceMap(@interface);
            for (var index = 0; index < map.TargetMethods.Length; index++)
                if (SameMethod(map.TargetMethods[index], info))
                    yield return map.InterfaceMethods[index];
        }
    }

    private static IEnumerable<EngardeAttribute> TypeGuards(MethodBase method)
    {
        var type = method.DeclaringType!;
        IEnumerable<Type> sources = [type];
        if (method is ConstructorInfo { IsStatic: false })
        {
            var inherited = type.BaseType?.GetInterfaces() ?? [];
            sources = sources.Concat(type.GetInterfaces().Except(inherited).OrderBy(@interface => @interface.FullName, StringComparer.Ordinal));
        }
        else if (method is not ConstructorInfo)
        {
            for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
                sources = sources.Append(baseType);
            sources = sources.Concat(type.GetInterfaces().OrderBy(@interface => @interface.FullName, StringComparer.Ordinal));
        }
        foreach (var guard in sources.SelectMany(GetGuards))
            yield return guard;
    }

    private static bool SameMethod(MethodBase left, MethodBase right) =>
        left.MetadataToken == right.MetadataToken && left.Module == right.Module && left.DeclaringType == right.DeclaringType;

    private sealed record Guards(MemberInfo Member, EngardeAttribute[] Attributes);
    private sealed record FallbackInvocation(MethodInfo? Method, object? Target, Type GuardType, string[] FeatureNames);
    private sealed record SuppressedGuard(Type GuardType, string[] FeatureNames, SuppressedGuard? Previous);
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
