using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Engarde.Weaver;

/// <summary>
/// Injects calls to EngardeChecks for every [Engarde] (or derived) attribute in the module:
/// method/constructor and class guards at method entry, parameter guards at method entry,
/// property guards at setter entry and field guards before every write to the field in this module.
/// </summary>
internal sealed class ModuleWeaver(string assemblyPath, IReadOnlyCollection<string> referencePaths)
{
    private const string RuntimeAssemblyName = "Engarde";
    private const string AttributeFullName = "Engarde.EngardeAttribute";
    private const string ChecksFullName = "Engarde.EngardeChecks";
    private const string TestingAssemblyName = "Engarde.Testing";
    private const string TestingOverrideFullName = "Engarde.Testing.EngardeTestOverrideAttribute";
    private const string TestingRuntimeFullName = "Engarde.Testing.EngardeTesting";
    private const string CompilerGeneratedFullName = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";
    private const string MarkerNamespace = "Engarde.Woven";
    private const string MarkerName = "<EngardeWoven>";
    private const string BackingFieldPrefix = "<";
    private const string BackingFieldSuffix = ">k__BackingField";
    private const string TaskFullName = "System.Threading.Tasks.Task";
    private const string ValueTaskFullName = "System.Threading.Tasks.ValueTask";

    private readonly Dictionary<string, bool> _guardAttributeTypes = new();
    private readonly Dictionary<string, bool> _testingOverrideTypes = new();
    private readonly Dictionary<ModuleDefinition, bool> _modulesUsingGuards = new();
    private readonly Dictionary<FieldDefinition, (bool Guarded, PropertyDefinition? Property)> _fields = new();
    private readonly Dictionary<MethodDefinition, MethodDefinition> _asyncWrappers = new();

    private ModuleDefinition _module = null!;
    private RuntimeMethods? _runtime;
    private MethodReference? _testingEnter;

    public int Execute()
    {
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        var hasSymbols = File.Exists(pdbPath);

        using var resolver = new ReferenceAssemblyResolver(referencePaths, assemblyPath);
        using var assemblyStream = new MemoryStream(File.ReadAllBytes(assemblyPath));
        using var symbolStream = hasSymbols ? new MemoryStream(File.ReadAllBytes(pdbPath)) : null;
        using var module = ModuleDefinition.ReadModule(assemblyStream, new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadSymbols = hasSymbols,
            SymbolStream = symbolStream
        });

        _module = module;

        ValidateGuardedParameters();
        ValidateGuardedFieldReferences();

        // An up-to-date compile output may already be woven when only the weaver changed
        if (module.Types.Any(type => type.Namespace == MarkerNamespace && type.Name == MarkerName))
            return 0;

        var woven = WeaveGuardedMembers();
        RedirectAsyncMethodReferences();
        woven += WeaveFieldWrites();
        woven += WeaveTestingScopes();

        if (woven == 0)
            return 0;

        module.Types.Add(new TypeDefinition(MarkerNamespace, MarkerName,
            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object));

        module.Write(assemblyPath, new WriterParameters { WriteSymbols = hasSymbols });

        return woven;
    }

    private void ValidateGuardedParameters()
    {
        foreach (var method in _module.GetTypes().SelectMany(type => type.Methods))
        {
            foreach (var parameter in method.Parameters.Where(parameter => parameter.ParameterType.IsByReference && HasGuard(parameter)))
                throw new NotSupportedException(
                    $"Cannot apply a guard to parameter '{parameter.Name}' of '{method.FullName}': ref, in and out parameters are unsupported; pass it by value or guard the method instead.");
        }
    }

    private void ValidateGuardedFieldReferences()
    {
        foreach (var method in _module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody))
        {
            foreach (var instruction in method.Body.Instructions.Where(instruction =>
                         instruction.OpCode == OpCodes.Ldflda || instruction.OpCode == OpCodes.Ldsflda))
            {
                var field = (FieldReference)instruction.Operand;
                if (!IsGuardedWrite(field, method))
                    continue;

                throw new NotSupportedException(
                    $"Cannot take a managed reference to guarded field '{field.FullName}' in '{method.FullName}': " +
                    "writes through ref, in, out, fixed, or reference-based APIs cannot be guarded; " +
                    "assign the field directly or guard the containing method instead.");
            }
        }
    }

    private int WeaveGuardedMembers()
    {
        var woven = 0;

        foreach (var type in _module.GetTypes().ToList())
        {
            var typeGuarded = HasGuard(type);
            // A guarded compiler-emitted .cctor must run at first use, not eagerly before guards are active.
            if (typeGuarded && type.Methods.Any(method => method.IsConstructor && method.IsStatic && method.HasBody))
                type.IsBeforeFieldInit = false;

            var guardedSetters = type.Properties
                .Where(property => property.SetMethod is not null && HasGuard(property))
                .Select(property => property.SetMethod)
                .ToHashSet();

            foreach (var method in type.Methods.Where(method => method.HasBody).ToList())
            {
                var prologue = new List<Instruction>();
                var guardsMethod = HasGuard(method) || (typeGuarded && IsGuardedByType(method));
                var guardedParameters = method.Parameters
                    .Where(parameter => IsValueType(parameter.ParameterType) && HasGuard(parameter)).ToList();

                if ((guardsMethod || guardedParameters.Count != 0) && GetAsyncReturn(method.ReturnType) is { } asyncReturn)
                {
                    WrapAsyncMethod(method, asyncReturn);
                    woven++;
                    continue;
                }

                if (guardsMethod)
                    prologue.AddRange(CheckMethod(method));

                foreach (var parameter in guardedParameters)
                    prologue.AddRange(CheckParameter(method, parameter));

                if (guardedSetters.Contains(method))
                    prologue.AddRange(CheckProperty(method));

                if (prologue.Count == 0)
                    continue;

                Prepend(method, prologue);
                woven++;
            }
        }

        return woven;
    }

    private int WeaveFieldWrites()
    {
        var woven = 0;

        foreach (var method in _module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody).ToList())
        {
            var writes = method.Body.Instructions
                .Where(instruction => instruction.OpCode == OpCodes.Stfld || instruction.OpCode == OpCodes.Stsfld)
                .Where(instruction => IsGuardedWrite((FieldReference)instruction.Operand, method))
                .ToList();

            if (writes.Count == 0)
                continue;

            var il = method.Body.GetILProcessor();
            method.Body.SimplifyMacros();

            foreach (var write in writes)
            {
                var field = (FieldReference)write.Operand;
                var isStatic = write.OpCode == OpCodes.Stsfld;
                var valueType = _module.ImportReference(Substitute(field.FieldType, field.DeclaringType as GenericInstanceType));

                // The value is duplicated in place of the original instruction (or its volatile./unaligned. prefix),
                // so branches targeting the write still run the check
                var anchor = write.Previous is { OpCode.OpCodeType: OpCodeType.Prefix } prefix ? prefix : write;
                var original = Clone(anchor);
                anchor.OpCode = OpCodes.Dup;
                anchor.Operand = null;

                var skipWrite = write.Next;
                if (skipWrite is null)
                {
                    skipWrite = il.Create(OpCodes.Nop);
                    il.Append(skipWrite);
                }

                var check = new[]
                {
                    il.Create(OpCodes.Ldtoken, field),
                    il.Create(OpCodes.Ldtoken, field.DeclaringType),
                    il.Create(OpCodes.Call, MakeGeneric(Runtime.CheckField, valueType)),
                    il.Create(OpCodes.Brtrue, original),
                    il.Create(OpCodes.Pop),
                    isStatic ? null : il.Create(OpCodes.Pop),
                    il.Create(OpCodes.Br, skipWrite),
                    original
                };

                var previous = anchor;
                foreach (var instruction in check.Where(instruction => instruction is not null))
                {
                    il.InsertAfter(previous, instruction!);
                    previous = instruction!;
                }

                woven++;
            }

            method.Body.OptimizeMacros();
        }

        return woven;
    }

    private int WeaveTestingScopes()
    {
        var woven = 0;
        foreach (var method in _module.GetTypes().SelectMany(type => type.Methods)
                     .Where(method => method.HasBody && !method.IsConstructor && HasTestingOverride(method)).ToList())
        {
            WrapTestingScope(method);
            woven++;
        }
        return woven;
    }

    private void WrapTestingScope(MethodDefinition method)
    {
        var body = method.Body;
        body.SimplifyMacros();
        body.InitLocals = true;
        var il = body.GetILProcessor();
        var originalFirst = body.Instructions[0];
        var originalReturns = body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ret).ToList();
        var scope = new VariableDefinition(_module.ImportReference(typeof(IDisposable)));
        body.Variables.Add(scope);
        VariableDefinition? result = null;
        if (method.ReturnType.MetadataType != MetadataType.Void)
        {
            result = new VariableDefinition(method.ReturnType);
            body.Variables.Add(result);
        }

        var (methodReference, typeReference) = SelfReference(method);
        foreach (var instruction in new[]
                 {
                     il.Create(OpCodes.Ldtoken, methodReference),
                     il.Create(OpCodes.Ldtoken, typeReference),
                     il.Create(OpCodes.Call, TestingEnter),
                     il.Create(OpCodes.Stloc, scope)
                 })
            il.InsertBefore(originalFirst, instruction);

        var finallyStart = il.Create(OpCodes.Ldloc, scope);
        var end = il.Create(OpCodes.Nop);
        foreach (var ret in originalReturns)
        {
            if (result is null)
            {
                ret.OpCode = OpCodes.Leave;
                ret.Operand = end;
            }
            else
            {
                ret.OpCode = OpCodes.Stloc;
                ret.Operand = result;
                il.InsertAfter(ret, il.Create(OpCodes.Leave, end));
            }
        }

        il.Append(finallyStart);
        il.Append(il.Create(OpCodes.Callvirt,
            _module.ImportReference(typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!)));
        il.Append(il.Create(OpCodes.Endfinally));
        il.Append(end);
        if (result is not null)
            il.Append(il.Create(OpCodes.Ldloc, result));
        il.Append(il.Create(OpCodes.Ret));
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = originalFirst,
            TryEnd = finallyStart,
            HandlerStart = finallyStart,
            HandlerEnd = end
        });
        body.OptimizeMacros();
    }

    private void WrapAsyncMethod(MethodDefinition implementation, AsyncReturn asyncReturn)
    {
        if (implementation.Parameters.Any(parameter => parameter.ParameterType is ByReferenceType or PointerType))
            throw new NotSupportedException(
                $"Cannot asynchronously guard '{implementation.FullName}': by-reference and pointer parameters are unsupported.");

        var originalName = implementation.Name;
        var originalAttributes = implementation.Attributes;
        var wrapper = new MethodDefinition(originalName, originalAttributes, _module.TypeSystem.Void)
        {
            ImplAttributes = implementation.ImplAttributes,
            CallingConvention = implementation.CallingConvention,
            ExplicitThis = implementation.ExplicitThis,
            HasThis = implementation.HasThis
        };

        var genericMap = new Dictionary<GenericParameter, GenericParameter>();
        foreach (var parameter in implementation.GenericParameters)
        {
            var copy = new GenericParameter(parameter.Name, wrapper) { Attributes = parameter.Attributes };
            wrapper.GenericParameters.Add(copy);
            genericMap.Add(parameter, copy);
        }
        foreach (var parameter in implementation.GenericParameters)
        {
            var copy = genericMap[parameter];
            foreach (var constraint in parameter.Constraints)
                copy.Constraints.Add(new GenericParameterConstraint(ReplaceMethodGenerics(constraint.ConstraintType, genericMap)));
        }

        wrapper.ReturnType = ReplaceMethodGenerics(implementation.ReturnType, genericMap);
        foreach (var parameter in implementation.Parameters)
        {
            var copy = new ParameterDefinition(parameter.Name, parameter.Attributes,
                ReplaceMethodGenerics(parameter.ParameterType, genericMap));
            if (parameter.HasConstant)
                copy.Constant = parameter.Constant;
            MoveCustomAttributes(parameter, copy);
            wrapper.Parameters.Add(copy);
        }
        MoveCustomAttributes(implementation.MethodReturnType, wrapper.MethodReturnType);
        MoveCustomAttributes(implementation, wrapper);
        foreach (var @override in implementation.Overrides.ToList())
        {
            implementation.Overrides.Remove(@override);
            wrapper.Overrides.Add(@override);
        }

        implementation.Name = $"<{originalName}>g__EngardeImplementation";
        implementation.Attributes = (implementation.Attributes & MethodAttributes.Static)
            | MethodAttributes.Private | MethodAttributes.HideBySig;
        implementation.DeclaringType.Methods.Add(wrapper);
        _asyncWrappers.Add(implementation, wrapper);
        BuildAsyncWrapperBody(wrapper, implementation, asyncReturn);
    }

    private void BuildAsyncWrapperBody(MethodDefinition wrapper, MethodDefinition implementation, AsyncReturn asyncReturn)
    {
        var body = new MethodBody(wrapper) { InitLocals = false, MaxStackSize = 8 };
        wrapper.Body = body;
        var il = body.GetILProcessor();
        var (wrapperReference, typeReference) = SelfReference(wrapper);

        if (wrapper.HasThis)
        {
            il.Append(il.Create(OpCodes.Ldarg_0));
            if (wrapper.DeclaringType.IsValueType || wrapper.DeclaringType.HasGenericParameters)
                il.Append(il.Create(OpCodes.Box, typeReference));
        }
        else
            il.Append(il.Create(OpCodes.Ldnull));

        il.Append(il.Create(OpCodes.Ldc_I4, wrapper.Parameters.Count));
        il.Append(il.Create(OpCodes.Newarr, _module.TypeSystem.Object));
        for (var index = 0; index < wrapper.Parameters.Count; index++)
        {
            var parameter = wrapper.Parameters[index];
            il.Append(il.Create(OpCodes.Dup));
            il.Append(il.Create(OpCodes.Ldc_I4, index));
            il.Append(il.Create(OpCodes.Ldarg, parameter));
            if (parameter.ParameterType.IsValueType || parameter.ParameterType is GenericParameter)
                il.Append(il.Create(OpCodes.Box, _module.ImportReference(parameter.ParameterType)));
            il.Append(il.Create(OpCodes.Stelem_Ref));
        }

        MethodReference implementationReference = SelfReference(implementation).Method;
        if (implementation.HasGenericParameters)
        {
            var instance = new GenericInstanceMethod(implementationReference);
            foreach (var parameter in wrapper.GenericParameters)
                instance.GenericArguments.Add(parameter);
            implementationReference = instance;
        }

        il.Append(il.Create(OpCodes.Ldtoken, implementationReference));
        il.Append(il.Create(OpCodes.Ldtoken, wrapperReference));
        il.Append(il.Create(OpCodes.Ldtoken, typeReference));

        if (wrapper.HasGenericParameters)
        {
            il.Append(il.Create(OpCodes.Ldc_I4, wrapper.GenericParameters.Count));
            il.Append(il.Create(OpCodes.Newarr, _module.ImportReference(typeof(Type))));
            for (var index = 0; index < wrapper.GenericParameters.Count; index++)
            {
                il.Append(il.Create(OpCodes.Dup));
                il.Append(il.Create(OpCodes.Ldc_I4, index));
                il.Append(il.Create(OpCodes.Ldtoken, wrapper.GenericParameters[index]));
                il.Append(il.Create(OpCodes.Call, _module.ImportReference(typeof(Type).GetMethod(
                    nameof(Type.GetTypeFromHandle), [typeof(RuntimeTypeHandle)])!)));
                il.Append(il.Create(OpCodes.Stelem_Ref));
            }
        }
        else
            il.Append(il.Create(OpCodes.Ldnull));

        var runtimeMethod = asyncReturn.IsValueTask
            ? asyncReturn.ResultType is null ? Runtime.CheckValueTask : Runtime.CheckValueTaskOf
            : asyncReturn.ResultType is null ? Runtime.CheckTask : Runtime.CheckTaskOf;
        if (asyncReturn.ResultType is not null)
            runtimeMethod = MakeGeneric(runtimeMethod, ReplaceMethodGenerics(asyncReturn.ResultType, wrapper.GenericParameters
                .Select((parameter, index) => new { parameter, original = implementation.GenericParameters[index] })
                .ToDictionary(pair => pair.original, pair => pair.parameter)));
        il.Append(il.Create(OpCodes.Call, runtimeMethod));
        il.Append(il.Create(OpCodes.Ret));
    }

    private void RedirectAsyncMethodReferences()
    {
        if (_asyncWrappers.Count == 0)
            return;

        foreach (var method in _module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is not MethodReference reference)
                    continue;
                var definition = TryResolve(reference);
                if (definition is null || !_asyncWrappers.TryGetValue(definition, out var wrapper))
                    continue;
                // The generated wrapper intentionally holds the implementation method token for deferred invocation.
                if (method == wrapper && instruction.OpCode == OpCodes.Ldtoken)
                    continue;
                instruction.Operand = Retarget(reference, wrapper);
            }
        }
    }

    private static MethodReference Retarget(MethodReference reference, MethodDefinition wrapper)
    {
        MethodReference element = reference is GenericInstanceMethod generic ? generic.ElementMethod : reference;
        var replacement = new MethodReference(wrapper.Name, element.ReturnType, element.DeclaringType)
        {
            HasThis = element.HasThis,
            ExplicitThis = element.ExplicitThis,
            CallingConvention = element.CallingConvention
        };
        foreach (var parameter in element.Parameters)
            replacement.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        foreach (var parameter in element.GenericParameters)
            replacement.GenericParameters.Add(new GenericParameter(parameter.Name, replacement));

        if (reference is not GenericInstanceMethod instance)
            return replacement;
        var result = new GenericInstanceMethod(replacement);
        foreach (var argument in instance.GenericArguments)
            result.GenericArguments.Add(argument);
        return result;
    }

    private static void MoveCustomAttributes(ICustomAttributeProvider source, ICustomAttributeProvider destination)
    {
        foreach (var attribute in source.CustomAttributes.ToList())
        {
            source.CustomAttributes.Remove(attribute);
            destination.CustomAttributes.Add(attribute);
        }
    }

    private static TypeReference ReplaceMethodGenerics(
        TypeReference type, IReadOnlyDictionary<GenericParameter, GenericParameter> replacements)
    {
        if (type is GenericParameter parameter && replacements.TryGetValue(parameter, out var replacement))
            return replacement;
        if (type is GenericInstanceType generic)
        {
            var result = new GenericInstanceType(generic.ElementType);
            foreach (var argument in generic.GenericArguments)
                result.GenericArguments.Add(ReplaceMethodGenerics(argument, replacements));
            return result;
        }
        if (type is ArrayType array)
            return new ArrayType(ReplaceMethodGenerics(array.ElementType, replacements), array.Rank);
        return type;
    }

    private static AsyncReturn? GetAsyncReturn(TypeReference type)
    {
        if (type.FullName == TaskFullName)
            return new(false, null);
        if (type.FullName == ValueTaskFullName)
            return new(true, null);
        if (type is GenericInstanceType generic && generic.GenericArguments.Count == 1)
        {
            if (generic.ElementType.FullName == $"{TaskFullName}`1")
                return new(false, generic.GenericArguments[0]);
            if (generic.ElementType.FullName == $"{ValueTaskFullName}`1")
                return new(true, generic.GenericArguments[0]);
        }
        return null;
    }

    private IEnumerable<Instruction> CheckMethod(MethodDefinition method)
    {
        var (methodReference, typeReference) = SelfReference(method);

        yield return Instruction.Create(OpCodes.Ldtoken, methodReference);
        yield return Instruction.Create(OpCodes.Ldtoken, typeReference);
        yield return Instruction.Create(OpCodes.Call, Runtime.CheckMethod);
    }

    private IEnumerable<Instruction> CheckParameter(MethodDefinition method, ParameterDefinition parameter)
    {
        var (methodReference, typeReference) = SelfReference(method);

        yield return Instruction.Create(OpCodes.Ldarg, parameter);
        yield return Instruction.Create(OpCodes.Ldtoken, methodReference);
        yield return Instruction.Create(OpCodes.Ldtoken, typeReference);
        yield return Instruction.Create(OpCodes.Ldc_I4, parameter.Index);
        yield return Instruction.Create(OpCodes.Call, MakeGeneric(Runtime.CheckParameter, Substitute(parameter.ParameterType, null)));
    }

    private IEnumerable<Instruction> CheckProperty(MethodDefinition setter)
    {
        var (methodReference, typeReference) = SelfReference(setter);
        var value = setter.Parameters[^1];

        yield return Instruction.Create(OpCodes.Ldarg, value);
        yield return Instruction.Create(OpCodes.Ldtoken, methodReference);
        yield return Instruction.Create(OpCodes.Ldtoken, typeReference);
        yield return Instruction.Create(OpCodes.Call, MakeGeneric(Runtime.CheckProperty, Substitute(value.ParameterType, null)));
        yield return Instruction.Create(OpCodes.Brtrue, setter.Body.Instructions[0]);
        yield return Instruction.Create(OpCodes.Ret);
    }

    private static void Prepend(MethodDefinition method, IEnumerable<Instruction> prologue)
    {
        var body = method.Body;
        body.SimplifyMacros();

        var il = body.GetILProcessor();
        var first = body.Instructions[0];

        foreach (var instruction in prologue)
            il.InsertBefore(first, instruction);

        body.OptimizeMacros();
    }

    private bool IsGuardedWrite(FieldReference field, MethodDefinition method)
    {
        var definition = TryResolve(field);

        if (definition is null || !UsesGuards(definition.Module))
            return false;

        if (!_fields.TryGetValue(definition, out var info))
        {
            var property = GetBackingFieldProperty(definition);
            info = (HasGuard(definition) || (property is not null && HasGuard(property)), property);
            _fields[definition] = info;
        }

        // A guarded property's own setter is already checked on entry
        return info.Guarded && (info.Property?.SetMethod is null || info.Property.SetMethod != method);
    }

    private static PropertyDefinition? GetBackingFieldProperty(FieldDefinition field)
    {
        if (!field.Name.StartsWith(BackingFieldPrefix, StringComparison.Ordinal)
            || !field.Name.EndsWith(BackingFieldSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var propertyName = field.Name[BackingFieldPrefix.Length..^BackingFieldSuffix.Length];

        return field.DeclaringType.Properties.FirstOrDefault(property => property.Name == propertyName);
    }

    private bool UsesGuards(ModuleDefinition module)
    {
        if (module == _module)
            return true;

        if (!_modulesUsingGuards.TryGetValue(module, out var usesGuards))
        {
            usesGuards = module.AssemblyReferences.Any(reference => reference.Name == RuntimeAssemblyName);
            _modulesUsingGuards[module] = usesGuards;
        }

        return usesGuards;
    }

    private bool HasGuard(ICustomAttributeProvider provider)
    {
        return provider.HasCustomAttributes && provider.CustomAttributes.Any(attribute => IsGuardType(attribute.AttributeType));
    }

    private bool HasTestingOverride(MethodDefinition method)
    {
        if (HasTestingOverride((ICustomAttributeProvider)method))
            return true;
        for (var type = method.DeclaringType; type is not null; type = TryResolve(type.BaseType))
            if (HasTestingOverride((ICustomAttributeProvider)type))
                return true;
        return false;
    }

    private bool HasTestingOverride(ICustomAttributeProvider provider) =>
        provider.HasCustomAttributes && provider.CustomAttributes.Any(attribute => IsTestingOverrideType(attribute.AttributeType));

    private bool IsTestingOverrideType(TypeReference type)
    {
        var key = $"{type.Scope.Name}|{type.FullName}";
        if (_testingOverrideTypes.TryGetValue(key, out var result))
            return result;
        for (var current = TryResolve(type); current is not null; current = TryResolve(current.BaseType))
        {
            if (current.FullName != TestingOverrideFullName) continue;
            _testingOverrideTypes[key] = true;
            return true;
        }
        _testingOverrideTypes[key] = false;
        return false;
    }

    private bool IsGuardType(TypeReference type)
    {
        var key = $"{type.Scope.Name}|{type.FullName}";

        if (_guardAttributeTypes.TryGetValue(key, out var isGuard))
            return isGuard;

        var definition = TryResolve(type);

        while (definition is not null && definition.FullName != AttributeFullName)
            definition = definition.BaseType is null ? null : TryResolve(definition.BaseType);

        isGuard = definition is not null;
        _guardAttributeTypes[key] = isGuard;

        return isGuard;
    }

    private static bool IsGuardedByType(MethodDefinition method)
    {
        if (method.IsConstructor)
            return true;

        return !method.IsSpecialName && !method.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == CompilerGeneratedFullName);
    }

    private static bool IsValueType(TypeReference type)
    {
        return type is not ByReferenceType and not PointerType;
    }

    /// <summary>
    /// Reference to the method as seen from inside its own (possibly generic) type, so tokens resolve to the running instantiation
    /// </summary>
    private static (MethodReference Method, TypeReference Type) SelfReference(MethodDefinition method)
    {
        var type = method.DeclaringType;

        if (!type.HasGenericParameters)
            return (method, type);

        var instance = new GenericInstanceType(type);
        foreach (var parameter in type.GenericParameters)
            instance.GenericArguments.Add(parameter);

        var reference = new MethodReference(method.Name, method.ReturnType, instance)
        {
            HasThis = method.HasThis,
            ExplicitThis = method.ExplicitThis,
            CallingConvention = method.CallingConvention
        };

        foreach (var parameter in method.GenericParameters)
            reference.GenericParameters.Add(new GenericParameter(parameter.Name, reference));

        foreach (var parameter in method.Parameters)
            reference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));

        return (reference, instance);
    }

    /// <summary>
    /// Replaces the declaring type's generic parameters with the arguments used at the write site and strips modifiers (e.g. volatile)
    /// </summary>
    private static TypeReference Substitute(TypeReference type, GenericInstanceType? context)
    {
        switch (type)
        {
            case IModifierType modifier:
                return Substitute(modifier.ElementType, context);

            case GenericParameter { Type: GenericParameterType.Type } parameter when context is not null:
                return context.GenericArguments[parameter.Position];

            case GenericInstanceType instance:
                var result = new GenericInstanceType(instance.ElementType);
                foreach (var argument in instance.GenericArguments)
                    result.GenericArguments.Add(Substitute(argument, context));
                return result;

            case ArrayType array:
                return new ArrayType(Substitute(array.ElementType, context), array.Rank);

            default:
                return type;
        }
    }

    private MethodReference MakeGeneric(MethodReference method, TypeReference argument)
    {
        var instance = new GenericInstanceMethod(method);
        instance.GenericArguments.Add(_module.ImportReference(argument));

        return instance;
    }

    private static Instruction Clone(Instruction instruction)
    {
        return instruction.Operand switch
        {
            null => Instruction.Create(instruction.OpCode),
            FieldReference field => Instruction.Create(instruction.OpCode, field),
            byte alignment => Instruction.Create(instruction.OpCode, alignment),
            _ => throw new NotSupportedException($"Unexpected operand for {instruction.OpCode}")
        };
    }

    private static TypeDefinition? TryResolve(TypeReference? type)
    {
        if (type is null) return null;
        try
        {
            return type.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    private static FieldDefinition? TryResolve(FieldReference field)
    {
        try
        {
            return field.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    private static MethodDefinition? TryResolve(MethodReference method)
    {
        try
        {
            return method.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    private RuntimeMethods Runtime => _runtime ??= LoadRuntime();

    private MethodReference TestingEnter => _testingEnter ??= LoadTestingEnter();

    private MethodReference LoadTestingEnter()
    {
        AssemblyDefinition testing;
        try
        {
            testing = _module.AssemblyResolver.Resolve(new AssemblyNameReference(TestingAssemblyName, null));
        }
        catch (AssemblyResolutionException e)
        {
            throw new InvalidOperationException(
                $"{_module.Name} uses Engarde testing overrides but doesn't reference {TestingAssemblyName}", e);
        }
        var runtime = testing.MainModule.GetType(TestingRuntimeFullName);
        return _module.ImportReference(runtime.Methods.Single(method => method.Name == "Enter"));
    }

    private RuntimeMethods LoadRuntime()
    {
        AssemblyDefinition runtime;

        try
        {
            runtime = _module.AssemblyResolver.Resolve(new AssemblyNameReference(RuntimeAssemblyName, null));
        }
        catch (AssemblyResolutionException e)
        {
            throw new InvalidOperationException(
                $"{_module.Name} uses [Engarde] but doesn't reference {RuntimeAssemblyName}", e);
        }

        var checks = runtime.MainModule.GetType(ChecksFullName);
        MethodReference Import(string name) => _module.ImportReference(checks.Methods.Single(method => method.Name == name));
        MethodReference ImportOverload(string name, bool generic) => _module.ImportReference(checks.Methods.Single(
            method => method.Name == name && method.HasGenericParameters == generic));

        return new RuntimeMethods(
            Import("CheckMethod"), Import("CheckParameter"), Import("CheckProperty"), Import("CheckField"),
            ImportOverload("CheckTaskMethod", false), ImportOverload("CheckTaskMethod", true),
            ImportOverload("CheckValueTaskMethod", false), ImportOverload("CheckValueTaskMethod", true));
    }

    private sealed record RuntimeMethods(
        MethodReference CheckMethod,
        MethodReference CheckParameter,
        MethodReference CheckProperty,
        MethodReference CheckField,
        MethodReference CheckTask,
        MethodReference CheckTaskOf,
        MethodReference CheckValueTask,
        MethodReference CheckValueTaskOf);

    private sealed record AsyncReturn(bool IsValueTask, TypeReference? ResultType);
}
