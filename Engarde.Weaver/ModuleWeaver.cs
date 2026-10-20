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
    private const string FallbackConstructorFullName = "Engarde.EngardeFallbackConstructorAttribute";
    private const string ValueConstructorFullName = "Engarde.EngardeValueConstructorAttribute";
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
    private readonly Dictionary<MethodDefinition, MethodDefinition> _methodWrappers = new();

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
        ValidateFallbacks();

        // An up-to-date compile output may already be woven when only the weaver changed
        if (module.Types.Any(type => type.Namespace == MarkerNamespace && type.Name == MarkerName))
            return 0;

        var woven = AddInheritedImplementations();
        woven += WeaveGuardedMembers();
        RedirectMethodReferences();
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

    private void ValidateFallbacks()
    {
        foreach (var type in _module.GetTypes())
        {
            foreach (var attribute in type.CustomAttributes)
            {
                if (IsValueConstructor(attribute))
                    throw new NotSupportedException(
                        $"A value-selective guard cannot be applied to type '{type.FullName}'; use it on a field, property, or parameter.");
                if (GetFallbackKind(attribute) is not { } kind) continue;
                if (kind != FallbackKind.Type || !type.IsClass && !type.IsInterface)
                    throw new NotSupportedException(
                        $"Fallback constructor on '{type.FullName}' is for a class/interface replacement; method fallbacks can only be applied to methods.");
                var fallback = (TypeReference)attribute.ConstructorArguments[0].Value;
                if (fallback.ContainsGenericParameter || TryResolve(fallback) is not { IsClass: true, IsAbstract: false } definition ||
                    !IsAssignableTo(definition, type))
                    throw new NotSupportedException(
                        $"Fallback type '{fallback.FullName}' must be a concrete class that implements or inherits guarded type '{type.FullName}'.");
            }

            foreach (var method in type.Methods)
            foreach (var attribute in method.CustomAttributes)
            {
                if (IsValueConstructor(attribute))
                    throw new NotSupportedException(
                        $"A value-selective guard cannot be applied to method or constructor '{method.FullName}'; use it on a field, property, or parameter.");
                if (GetFallbackKind(attribute) is not { } kind) continue;
                if (kind != FallbackKind.Method || method.IsConstructor)
                    throw new NotSupportedException(
                        $"Fallback constructor on '{method.FullName}' is for a method replacement; type fallbacks can only be applied to classes or interfaces.");
                var name = (string)attribute.ConstructorArguments[0].Value;
                var fallback = (TypeReference)attribute.ConstructorArguments[1].Value;
                if (fallback.ContainsGenericParameter || TryResolve(fallback) is not { } definition)
                    throw new NotSupportedException($"Cannot resolve fallback type '{fallback.FullName}' for '{method.FullName}'.");
                var candidate = definition.Methods.FirstOrDefault(value => value.Name == name &&
                    (!method.IsStatic || value.IsStatic) && SameSignatureAndReturn(value, method));
                if (candidate is null)
                    throw new NotSupportedException(
                        $"Fallback method '{fallback.FullName}.{name}' must have a compatible static/instance form and the same generic arity, parameters and return type as '{method.FullName}'.");
                if (candidate == method)
                    throw new NotSupportedException($"Fallback method for '{method.FullName}' cannot be the guarded method itself.");
            }

            foreach (var provider in type.Methods.Cast<ICustomAttributeProvider>()
                         .Concat(type.Properties).Concat(type.Fields)
                         .Concat(type.Methods.SelectMany(method => method.Parameters)))
            foreach (var attribute in provider.CustomAttributes)
            {
                if (provider is MethodDefinition || GetFallbackKind(attribute) is null) continue;
                throw new NotSupportedException("Engarde fallbacks can only be applied to methods, classes, and interfaces.");
            }
        }
    }

    private static bool SameSignatureAndReturn(MethodDefinition left, MethodDefinition right) =>
        left.GenericParameters.Count == right.GenericParameters.Count &&
        Signature(left, null) == Signature(right, null) &&
        SignatureName(left.ReturnType) == SignatureName(right.ReturnType);

    private static bool IsAssignableTo(TypeDefinition fallback, TypeDefinition guarded)
    {
        if (fallback.FullName == guarded.FullName) return true;
        if (guarded.IsInterface)
            return Interfaces(fallback, null).Any(value => value.Type.FullName == guarded.FullName);
        return BaseTypes(fallback).Any(value => value.Type.FullName == guarded.FullName);
    }

    private static FallbackKind? GetFallbackKind(CustomAttribute attribute)
    {
        var constructor = TryResolve(attribute.Constructor);
        var marker = constructor?.CustomAttributes.FirstOrDefault(value =>
            value.AttributeType.FullName == FallbackConstructorFullName);
        return marker is null ? null : (FallbackKind)(int)marker.ConstructorArguments[0].Value;
    }

    private static bool IsValueConstructor(CustomAttribute attribute) =>
        TryResolve(attribute.Constructor)?.CustomAttributes.Any(value =>
            value.AttributeType.FullName == ValueConstructorFullName) == true;

    private enum FallbackKind { Type, Method }

    /// <summary>
    /// A class re-declaring an interface may implement its members with methods inherited from a base class that
    /// doesn't implement the interface. Each such guarded member gets a compiler-generated explicit implementation
    /// forwarding to the inherited method, so calls through the interface check the member's guards on top of the
    /// inherited method's own.
    /// </summary>
    private int AddInheritedImplementations()
    {
        var added = 0;
        foreach (var type in _module.GetTypes().Where(type => type.IsClass && !type.IsInterface && !type.IsValueType).ToList())
        {
            var bases = BaseTypes(type).ToList();
            var seen = new HashSet<string>();
            foreach (var (@interface, context) in Interfaces(type, null).ToList())
            {
                var interfaceReference = (TypeReference?)context ?? @interface;
                if (!seen.Add(interfaceReference.FullName))
                    continue;
                foreach (var member in @interface.Methods.Where(member => !member.IsStatic && member.IsVirtual &&
                             HasMemberGuard(@interface, member) && !HasByReference(member)).ToList())
                {
                    var signature = Signature(member, context);
                    if (type.Methods.Any(method =>
                            method.Overrides.Any(@override => @override.Name == member.Name &&
                                @override.DeclaringType.FullName == interfaceReference.FullName) ||
                            (method.IsPublic && !method.IsStatic && method.Name == member.Name && Signature(method, null) == signature)))
                        continue;

                    var inherited = bases
                        .Select(@base => (@base.Type, @base.Context,
                            Method: @base.Type.Methods.FirstOrDefault(method => method.IsPublic && !method.IsStatic &&
                                method.Name == member.Name && Signature(method, @base.Context) == signature)))
                        .FirstOrDefault(candidate => candidate.Method is not null);
                    // A base class implementing the interface itself already checks the member's guards
                    if (inherited.Method is null ||
                        BaseTypes(inherited.Type).Prepend((inherited.Type, inherited.Context)).Any(@base =>
                            Interfaces(@base.Type, null).Any(implemented => implemented.Type == @interface)))
                        continue;

                    AddForwarder(type, interfaceReference, context, member,
                        inherited.Method, (TypeReference?)inherited.Context ?? inherited.Type);
                    added++;
                }
            }
        }
        return added;
    }

    private bool HasMemberGuard(TypeDefinition @interface, MethodDefinition member) =>
        HasGuard(member) || member.Parameters.Any(HasGuard) ||
        @interface.Properties.FirstOrDefault(property => property.SetMethod == member) is { } property && HasGuard(property);

    private static bool HasByReference(MethodDefinition method) =>
        method.ReturnType is ByReferenceType or IModifierType ||
        method.Parameters.Any(parameter => parameter.ParameterType is ByReferenceType or IModifierType or PointerType);

    private void AddForwarder(TypeDefinition type, TypeReference interfaceReference, GenericInstanceType? interfaceContext,
        MethodDefinition member, MethodDefinition inherited, TypeReference inheritedDeclaringType)
    {
        var forwarder = new MethodDefinition(interfaceReference.FullName + "." + member.Name,
            MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.HideBySig |
            MethodAttributes.NewSlot | (member.IsSpecialName ? MethodAttributes.SpecialName : 0),
            _module.TypeSystem.Void) { HasThis = true };
        type.Methods.Add(forwarder);
        forwarder.CustomAttributes.Add(new CustomAttribute(_module.ImportReference(
            typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes))));

        var generics = new Dictionary<GenericParameter, GenericParameter>();
        foreach (var parameter in member.GenericParameters)
        {
            var copy = new GenericParameter(parameter.Name, forwarder) { Attributes = parameter.Attributes };
            forwarder.GenericParameters.Add(copy);
            generics.Add(parameter, copy);
        }
        TypeReference Map(TypeReference type) =>
            _module.ImportReference(ReplaceMethodGenerics(Substitute(type, interfaceContext), generics), forwarder);
        foreach (var parameter in member.GenericParameters)
            foreach (var constraint in parameter.Constraints)
                generics[parameter].Constraints.Add(new GenericParameterConstraint(Map(constraint.ConstraintType)));
        forwarder.ReturnType = Map(member.ReturnType);
        foreach (var parameter in member.Parameters)
            forwarder.Parameters.Add(new ParameterDefinition(parameter.Name, ParameterAttributes.None, Map(parameter.ParameterType)));
        forwarder.Overrides.Add(_module.ImportReference(MemberOn(member, interfaceReference)));

        var il = forwarder.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        foreach (var parameter in forwarder.Parameters)
            il.Emit(OpCodes.Ldarg, parameter);
        var target = _module.ImportReference(MemberOn(inherited, inheritedDeclaringType));
        if (target.HasGenericParameters)
        {
            var instance = new GenericInstanceMethod(target);
            foreach (var parameter in forwarder.GenericParameters)
                instance.GenericArguments.Add(parameter);
            target = instance;
        }
        // A virtual inherited method may be overridden further down
        il.Emit(inherited.IsVirtual && !inherited.IsFinal ? OpCodes.Callvirt : OpCodes.Call, target);
        il.Emit(OpCodes.Ret);

        if (!member.IsSetter ||
            member.DeclaringType.Properties.FirstOrDefault(property => property.SetMethod == member) is not { } guarded)
            return;
        forwarder.SemanticsAttributes = MethodSemanticsAttributes.Setter;
        type.Properties.Add(new PropertyDefinition(interfaceReference.FullName + "." + guarded.Name, PropertyAttributes.None,
            forwarder.Parameters[^1].ParameterType) { HasThis = true, SetMethod = forwarder });
    }

    // A method as declared on a possibly generic instance of its type
    private static MethodReference MemberOn(MethodDefinition method, TypeReference declaringType)
    {
        var reference = new MethodReference(method.Name, method.ReturnType, declaringType)
        {
            HasThis = method.HasThis,
            ExplicitThis = method.ExplicitThis,
            CallingConvention = method.CallingConvention
        };
        foreach (var parameter in method.GenericParameters)
            reference.GenericParameters.Add(new GenericParameter(parameter.Name, reference));
        foreach (var parameter in method.Parameters)
            reference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        return reference;
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

            var bases = BaseTypes(type).ToList();
            var interfaces = Interfaces(type, null).ToList();
            // Base classes' and interfaces' guards apply to methods; instance constructors only check the interfaces
            // their type introduces, since base constructors check the rest.
            var methodTypeGuarded = typeGuarded || bases.Any(@base => HasGuard(@base.Type)) ||
                interfaces.Concat(bases.SelectMany(@base => Interfaces(@base.Type, @base.Context))).Any(@interface => HasGuard(@interface.Type));
            var constructorTypeGuarded = typeGuarded || interfaces.Any(@interface => HasGuard(@interface.Type));

            var guardedSetters = type.Properties
                .Where(property => property.SetMethod is not null && HasPropertyGuard(property, bases, interfaces))
                .Select(property => property.SetMethod)
                .ToHashSet();

            foreach (var method in type.Methods.Where(method => method.HasBody).ToList())
            {
                var prologue = new List<Instruction>();
                var related = RelatedMethods(method, bases, interfaces).ToList();
                var typeGuardsApply = method.IsConstructor
                    ? method.IsStatic ? typeGuarded : constructorTypeGuarded
                    : methodTypeGuarded;
                var guardsMethod = HasGuard(method) || related.Any(HasGuard) || (typeGuardsApply && IsGuardedByType(method));
                var hasFallback = !method.IsConstructor && IsGuardedByType(method) &&
                    MethodGuardAttributes(method, related, bases, interfaces).Any(attribute => GetFallbackKind(attribute) is not null);
                var guardedParameters = method.Parameters
                    .Where(parameter => IsValueType(parameter.ParameterType) && (HasGuard(parameter) ||
                        related.Any(inherited => HasGuard(inherited.Parameters[parameter.Index]))))
                    .ToList();

                if ((guardsMethod || guardedParameters.Count != 0) && GetAsyncReturn(method.ReturnType) is { } asyncReturn)
                {
                    WrapAsyncMethod(method, asyncReturn);
                    woven++;
                    continue;
                }

                if (hasFallback)
                {
                    if (method.Parameters.Any(parameter => parameter.ParameterType is ByReferenceType or PointerType) ||
                        method.ReturnType is ByReferenceType or PointerType)
                        throw new NotSupportedException(
                            $"Cannot use a fallback on '{method.FullName}': by-reference and pointer signatures are unsupported.");
                    WrapSyncMethod(method);
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
                     .Where(method => method.HasBody && !(method.IsConstructor && method.IsStatic) && HasTestingOverride(method))
                     .ToList())
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
        _methodWrappers.Add(implementation, wrapper);
        BuildAsyncWrapperBody(wrapper, implementation, asyncReturn);
    }

    private void WrapSyncMethod(MethodDefinition implementation)
    {
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
        foreach (var constraint in parameter.Constraints)
            genericMap[parameter].Constraints.Add(new GenericParameterConstraint(
                ReplaceMethodGenerics(constraint.ConstraintType, genericMap)));

        wrapper.ReturnType = ReplaceMethodGenerics(implementation.ReturnType, genericMap);
        foreach (var parameter in implementation.Parameters)
        {
            var copy = new ParameterDefinition(parameter.Name, parameter.Attributes,
                ReplaceMethodGenerics(parameter.ParameterType, genericMap));
            if (parameter.HasConstant) copy.Constant = parameter.Constant;
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
        _methodWrappers.Add(implementation, wrapper);
        BuildSyncWrapperBody(wrapper, implementation);
    }

    private void BuildSyncWrapperBody(MethodDefinition wrapper, MethodDefinition implementation)
    {
        var body = new MethodBody(wrapper) { InitLocals = false, MaxStackSize = 8 };
        wrapper.Body = body;
        var il = body.GetILProcessor();
        AppendInvocationArguments(il, wrapper, implementation);
        var runtimeMethod = wrapper.ReturnType.MetadataType == MetadataType.Void
            ? Runtime.InvokeMethod
            : MakeGeneric(Runtime.InvokeMethodOf, wrapper.ReturnType);
        il.Append(il.Create(OpCodes.Call, runtimeMethod));
        il.Append(il.Create(OpCodes.Ret));
    }

    private void AppendInvocationArguments(ILProcessor il, MethodDefinition wrapper, MethodDefinition implementation)
    {
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

    private void RedirectMethodReferences()
    {
        if (_methodWrappers.Count == 0)
            return;

        foreach (var method in _module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is not MethodReference reference)
                    continue;
                var definition = TryResolve(reference);
                if (definition is null || !_methodWrappers.TryGetValue(definition, out var wrapper))
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
            info = (HasGuard(definition) || (property is not null &&
                HasPropertyGuard(property, BaseTypes(property.DeclaringType).ToList(), Interfaces(property.DeclaringType, null).ToList())),
                property);
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

    private bool HasPropertyGuard(PropertyDefinition property, List<(TypeDefinition Type, GenericInstanceType? Context)> bases,
        List<(TypeDefinition Type, GenericInstanceType? Context)> interfaces) =>
        HasGuard(property) || (property.SetMethod is { } setter && RelatedMethods(setter, bases, interfaces)
            .Any(inherited => inherited.DeclaringType.Properties.FirstOrDefault(candidate => candidate.SetMethod == inherited) is { } guarded &&
                HasGuard(guarded)));

    /// <summary>
    /// The base declarations a method overrides and the interface members it implements, whose guards it inherits.
    /// Mirrors the runtime's resolution; a stray match only adds a check that finds no guards.
    /// </summary>
    private static IEnumerable<MethodDefinition> RelatedMethods(MethodDefinition method,
        List<(TypeDefinition Type, GenericInstanceType? Context)> bases, List<(TypeDefinition Type, GenericInstanceType? Context)> interfaces)
    {
        foreach (var @override in method.Overrides)
            if (TryResolve(@override) is { } explicitlyImplemented)
                yield return explicitlyImplemented;
        if (!method.IsVirtual || method.IsStatic)
            yield break;

        var signature = Signature(method, null);
        var candidates = (method.IsNewSlot ? [] : bases).Concat(method.DeclaringType.IsInterface ? [] : interfaces);
        foreach (var (type, context) in candidates)
            foreach (var candidate in type.Methods.Where(candidate => candidate.IsVirtual && candidate.Name == method.Name &&
                         Signature(candidate, context) == signature))
                yield return candidate;
    }

    private static IEnumerable<CustomAttribute> MethodGuardAttributes(MethodDefinition method,
        List<MethodDefinition> related, List<(TypeDefinition Type, GenericInstanceType? Context)> bases,
        List<(TypeDefinition Type, GenericInstanceType? Context)> interfaces)
    {
        foreach (var attribute in method.CustomAttributes)
            yield return attribute;
        foreach (var inherited in related)
        foreach (var attribute in inherited.CustomAttributes)
            yield return attribute;

        foreach (var source in new[] { method.DeclaringType }
                     .Concat(bases.Select(value => value.Type))
                     .Concat(interfaces.Select(value => value.Type)))
        foreach (var attribute in source.CustomAttributes)
            yield return attribute;
    }

    private static IEnumerable<(TypeDefinition Type, GenericInstanceType? Context)> BaseTypes(TypeDefinition type)
    {
        var reference = type.BaseType;
        while (TryResolve(reference) is { } definition)
        {
            var context = reference as GenericInstanceType;
            yield return (definition, context);
            reference = definition.BaseType is null ? null : Substitute(definition.BaseType, context);
        }
    }

    // A type's interfaces with their inherited interfaces, generic arguments expressed in the type's own terms
    private static IEnumerable<(TypeDefinition Type, GenericInstanceType? Context)> Interfaces(TypeDefinition type, GenericInstanceType? context)
    {
        foreach (var implementation in type.Interfaces)
        {
            var reference = Substitute(implementation.InterfaceType, context);
            if (TryResolve(reference) is not { } definition)
                continue;
            yield return (definition, reference as GenericInstanceType);
            foreach (var inherited in Interfaces(definition, reference as GenericInstanceType))
                yield return inherited;
        }
    }

    private static string Signature(MethodDefinition method, GenericInstanceType? context) =>
        method.GenericParameters.Count + "(" + string.Join(",", method.Parameters
            .Select(parameter => SignatureName(Substitute(parameter.ParameterType, context)))) + ")";

    // Method generic parameters are compared by position, since an implementation may rename them
    private static string SignatureName(TypeReference type) => type switch
    {
        GenericParameter { Type: GenericParameterType.Method } parameter => "!!" + parameter.Position,
        GenericInstanceType instance => SignatureName(instance.ElementType) + "<" +
            string.Join(",", instance.GenericArguments.Select(SignatureName)) + ">",
        ArrayType array => SignatureName(array.ElementType) + "[" + array.Rank + "]",
        ByReferenceType reference => SignatureName(reference.ElementType) + "&",
        _ => type.FullName
    };

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

            case ByReferenceType reference:
                return new ByReferenceType(Substitute(reference.ElementType, context));

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
            ImportOverload("CheckValueTaskMethod", false), ImportOverload("CheckValueTaskMethod", true),
            ImportOverload("InvokeMethod", false), ImportOverload("InvokeMethod", true));
    }

    private sealed record RuntimeMethods(
        MethodReference CheckMethod,
        MethodReference CheckParameter,
        MethodReference CheckProperty,
        MethodReference CheckField,
        MethodReference CheckTask,
        MethodReference CheckTaskOf,
        MethodReference CheckValueTask,
        MethodReference CheckValueTaskOf,
        MethodReference InvokeMethod,
        MethodReference InvokeMethodOf);

    private sealed record AsyncReturn(bool IsValueTask, TypeReference? ResultType);
}
