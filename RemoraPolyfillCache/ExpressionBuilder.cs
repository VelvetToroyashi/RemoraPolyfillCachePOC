using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Remora.Rest.Core;

namespace RemoraPolyfillCache;

public static class ExpressionBuilder
{
    private static readonly MethodInfo _getUnitializedObject = typeof(RuntimeHelpers).GetMethod
        (nameof(RuntimeHelpers.GetUninitializedObject), BindingFlags.Public | BindingFlags.Static)!;
    
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
        var properties = typeInfo.GetFields(BindingFlags.Public | BindingFlags.Instance);

        var setters = new Expression[properties.Length + 1];

        var oldParam = Expression.Parameter(typeInfo, "old");
        var newParam = Expression.Parameter(typeInfo, "new");

        var instance = Expression.Convert(Expression.Call(_getUnitializedObject, Expression.Constant(typeInfo)), typeInfo);
        
        for (int i = 0; i < setters.Length - 1; i++)
        {
            var argumentType = properties[i].FieldType;

            var left = Expression.Field(oldParam, properties[i]);
            var right = Expression.Field(newParam, properties[i]);

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

            setters[i] = Expression.Assign(instance, Expression.Condition(check, right, left, argumentType));
        }

        setters[^1] = instance;

        var block = Expression.Block(setters);
        
        var lambda = Expression.Lambda<Func<T, T, T>>(block, oldParam, newParam);

        var func = lambda.Compile();

        _lookupCache[typeInfo] = func;

        return func(old, @new);
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