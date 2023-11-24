using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Remora.Rest.Core;

namespace RemoraPolyfillCache;

public static class ExpressionBuilder
{
    private static readonly MethodInfo _getTypeFromHandle = typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle), BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo _optionalHasValue = typeof(IOptional).GetMethod("get_" + nameof(IOptional.HasValue), BindingFlags.Public | BindingFlags.Instance)!;
    private static readonly MethodInfo _getUnitializedObject = typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.GetUninitializedObject), BindingFlags.Public | BindingFlags.Static)!;
    
    internal static Dictionary<Type, Delegate> _lookupCache = new();
    
    
    public static T Polyfill<T>(T old, T @new)
    {
        if (_lookupCache.ContainsKey(typeof(T)))
        {
            return Unsafe.As<Func<object?[], object?[], T>>(_lookupCache[typeof(T)])(GetProperties(old), GetProperties(@new));
        }
        
        // Cached delegate unavailable; build one.
        var typeInfo = typeof(T);

        var ctorInfo = typeInfo.GetConstructors()[0];
        var ctorParams = ctorInfo.GetParameters();
        var propertyInfo = typeInfo.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        if (ctorParams.Length != propertyInfo.Length)
        {
            throw new InvalidOperationException($"{typeInfo.FullName} cannot be poly-filled as there is a " +
                                                $"mismatch between properties and constructor arguments.");
        }

        var arguments = new Expression[ctorParams.Length];
        var oldArguments = Expression.Parameter(typeof(object?[]), "old");
        var newArguments = Expression.Parameter(typeof(object?[]), "new");
        
        for (int i = 0; i < arguments.Length; i++)
        {
            var argumentType = ctorParams[i].ParameterType;

            var left = Expression.Convert(Expression.ArrayIndex(oldArguments, Expression.Constant(i)), argumentType);
            var right = Expression.Convert(Expression.ArrayIndex(newArguments, Expression.Constant(i)), argumentType);

            Expression check;

            if (argumentType.IsGenericType && argumentType.GetGenericTypeDefinition() == typeof(Optional<>))
            {
                // Check that the provider of the polyfill has a value for us. If it does, always update.
                check = Expression.NotEqual(right, Expression.Default(argumentType));
            }
            else
            {
                check = Expression.NotEqual(left, right);
            }

            arguments[i] = Expression.Condition(check, right, left, argumentType);
        }

        var instance = Expression.New(ctorInfo, arguments);

        var lambda = Expression.Lambda<Func<object?[], object?[], T>>(instance, oldArguments, newArguments);

        var func = lambda.Compile();

        _lookupCache[typeInfo] = func;

        return func(GetProperties(old), GetProperties(@new));
    }
    
    public static T PolyfillFast<T>(T old, T @new)
    {
        if (_lookupCache.ContainsKey(typeof(T)))
        {
            return Unsafe.As<Func<T, T, T>>(_lookupCache[typeof(T)])(old, @new);
        }
        
        // Cached delegate unavailable; build one.
        var typeInfo = typeof(T);
        var func = GetPolyfillDelegate<T>();

        return ((Func<T, T, T>)(_lookupCache[typeInfo] = func))(old, @new);
    }

    public static Func<T, T, T> GetPolyfillDelegate<T>()
    {
        var typeInfo = typeof(T);
        var method = new DynamicMethod($"polyfill_{typeInfo.Name}", typeInfo, new[] { typeInfo, typeInfo }, restrictedSkipVisibility: true);
        var il = method.GetILGenerator();

        il.Emit(OpCodes.Ldtoken, typeInfo);
        il.Emit(OpCodes.Call, _getTypeFromHandle);
        il.Emit(OpCodes.Call, _getUnitializedObject);
        il.Emit(OpCodes.Castclass, typeInfo);

        var result = il.DeclareLocal(typeInfo);
        il.Emit(OpCodes.Stloc, result);

        // TODO: Potentially do this loop in IL and load fields based on an index if possible?
        foreach (var field in typeInfo.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!field.FieldType.IsAssignableTo(typeof(IOptional)))
            {
                // The 'slow' polyfill method basically just checks lhs == rhs, and takes the left if true.
                il.Emit(OpCodes.Ldloc, result);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldfld, field);
                il.Emit(OpCodes.Stfld, field);
            }
            else
            {
                var nop = il.DefineLabel(); // basically a NOP between field settings
                var optionalHasValue = il.DefineLabel();

                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldflda, field);
                il.Emit(OpCodes.Constrained, field.FieldType);
                il.Emit(OpCodes.Callvirt, _optionalHasValue);
                il.Emit(OpCodes.Brtrue, optionalHasValue);

                il.Emit(OpCodes.Ldloc, result);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Br, nop);

                il.MarkLabel(optionalHasValue);
                il.Emit(OpCodes.Ldloc, result);
                il.Emit(OpCodes.Ldarg_1);

                il.MarkLabel(nop);

                il.Emit(OpCodes.Ldfld, field);
                il.Emit(OpCodes.Stfld, field);
            }
        }

        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ret);


        var func = method.CreateDelegate<Func<T, T, T>>();
        return func;
    }

    private static void SetField(FieldInfo info, object instance, object value)
    {
        var tr = __makeref(instance);
        info.SetValueDirect(tr, value);
    }

    private static object?[] GetProperties<T>(T obj)
    {
        var parameters = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var ret = new object?[parameters.Length];
        
        for (int i = 0; i < parameters.Length; i++)
        {
            ret[i] = parameters[i].GetValue(obj);
        }

        return ret;
    }
}