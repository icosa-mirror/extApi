using System;
using System.Collections;
using System.Linq;

namespace extApi
{
    internal static class ApiUtils
    {
        public static string Combine(string uri1, string uri2) => $"{uri1.TrimEnd('/')}/{uri2.TrimStart('/')}";

        public static object CreateDefault(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

        public static bool IsSimpleType(Type type)
        {
            return type.IsPrimitive ||
                   type.IsEnum ||
                   type == typeof(string) ||
                   type == typeof(decimal) ||
                   type == typeof(DateTime) ||
                   type == typeof(DateTimeOffset) ||
                   type == typeof(Guid) ||
                   type == typeof(TimeSpan);
        }

        public static bool IsCollectionType(Type type)
        {
            return type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);
        }

        public static Type GetCollectionElementType(Type type)
        {
            if (type.IsArray)
                return type.GetElementType() ?? typeof(object);

            if (type.IsGenericType)
                return type.GetGenericArguments().FirstOrDefault() ?? typeof(object);

            return typeof(object);
        }

        public static string GetFriendlyTypeName(Type type)
        {
            if (type.IsArray)
                return $"{GetFriendlyTypeName(type.GetElementType())}[]";

            if (type.IsGenericType)
            {
                var genericTypeName = type.Name[..type.Name.IndexOf('`')];
                var genericArguments = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
                return $"{genericTypeName}<{genericArguments}>";
            }

            return type.Name;
        }
    }
}
