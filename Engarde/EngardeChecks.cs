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
    private static Func<Type, string, bool?>? _guardOverride;

    /// <summary>Installs the optional guard override supplied by Engarde.Testing.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void SetGuardOverride(Func<Type, string, bool?>? guardOverride) =>
        Volatile.Write(ref _guardOverride, guardOverride);

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

    private static async Task CheckTaskMethodCore(
        object? target, object?[] arguments, RuntimeMethodHandle implementation,
        RuntimeMethodHandle method, RuntimeTypeHandle type, Type[]? genericArguments)
    {
        await ValidateMethodAsync(method, type, arguments).ConfigureAwait(false);
        await ((Task)Invoke(implementation, type, genericArguments, target, arguments)!).ConfigureAwait(false);
    }

    private static async Task<TResult> CheckTaskMethodCore<TResult>(
        object? target, object?[] arguments, RuntimeMethodHandle implementation,
        RuntimeMethodHandle method, RuntimeTypeHandle type, Type[]? genericArguments)
    {
        await ValidateMethodAsync(method, type, arguments).ConfigureAwait(false);
        return await ((Task<TResult>)Invoke(implementation, type, genericArguments, target, arguments)!).ConfigureAwait(false);
    }

    private static async ValueTask CheckValueTaskMethodCore(
        object? target, object?[] arguments, RuntimeMethodHandle implementation,
        RuntimeMethodHandle method, RuntimeTypeHandle type, Type[]? genericArguments)
    {
        await ValidateMethodAsync(method, type, arguments).ConfigureAwait(false);
        await ((ValueTask)Invoke(implementation, type, genericArguments, target, arguments)!).ConfigureAwait(false);
    }

    private static async ValueTask<TResult> CheckValueTaskMethodCore<TResult>(
        object? target, object?[] arguments, RuntimeMethodHandle implementation,
        RuntimeMethodHandle method, RuntimeTypeHandle type, Type[]? genericArguments)
    {
        await ValidateMethodAsync(method, type, arguments).ConfigureAwait(false);
        return await ((ValueTask<TResult>)Invoke(implementation, type, genericArguments, target, arguments)!).ConfigureAwait(false);
    }

    private static object? Invoke(
        RuntimeMethodHandle implementation, RuntimeTypeHandle type, Type[]? genericArguments,
        object? target, object?[] arguments)
    {
        var method = (MethodInfo)MethodBase.GetMethodFromHandle(implementation, type)!;
        if (method.IsGenericMethodDefinition)
            method = method.MakeGenericMethod(genericArguments!);

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
        if (!HasEnabledGuards(guards))
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

    private static async ValueTask ValidateMethodAsync(
        RuntimeMethodHandle method, RuntimeTypeHandle type, object?[] arguments)
    {
        var methodGuards = GetMethodGuards(method, type);
        var parameterGuards = new Guards[arguments.Length];
        var hasEnabledGuards = HasEnabledGuards(methodGuards);
        for (var index = 0; index < arguments.Length; index++)
        {
            parameterGuards[index] = GetParameterGuards(method, type, index);
            hasEnabledGuards |= !IsEmptyObject(arguments[index]) && HasEnabledGuards(parameterGuards[index]);
        }
        if (!hasEnabledGuards)
            return;

        await using var evaluation = EngardeEvaluation.Begin();
        await ValidateAsync(evaluation, methodGuards, null).ConfigureAwait(false);

        for (var index = 0; index < arguments.Length; index++)
        {
            var value = arguments[index];
            if (!IsEmptyObject(value))
                await ValidateAsync(evaluation, parameterGuards[index], value).ConfigureAwait(false);
        }
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
                if (!IsGuardEnabled(guard, featureName))
                    continue;
                if (!await guard.EvaluateAsync(evaluation, featureName, guards.Member, value, null).ConfigureAwait(false))
                {
                    enabled = false;
                    context ??= evaluation.CreateContext(guards.Member, value);
                    guard.OnFeatureMissing(featureName, context);
                }
            }
        }
        return enabled;
    }

    private static bool HasEnabledGuards(Guards guards) =>
        guards.Attributes.Any(guard => guard.FeatureNames.Any(featureName => IsGuardEnabled(guard, featureName)));

    private static bool IsGuardEnabled(EngardeAttribute guard, string featureName) =>
        Volatile.Read(ref _guardOverride)?.Invoke(guard.GetType(), featureName) != false;

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
        if (type.IsInterface)
            yield break;

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
        }
        foreach (var guard in sources.SelectMany(GetGuards))
            yield return guard;
    }

    private static bool SameMethod(MethodBase left, MethodBase right) =>
        left.MetadataToken == right.MetadataToken && left.Module == right.Module && left.DeclaringType == right.DeclaringType;

    private sealed record Guards(MemberInfo Member, EngardeAttribute[] Attributes);
}
