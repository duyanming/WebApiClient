﻿using Castle.DynamicProxy;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading.Tasks;
using WebApiClient.Attributes;
using WebApiClient.Contexts;

namespace WebApiClient
{
    /// <summary>
    /// 提供Descriptor缓存
    /// </summary>
    static class DescriptorCache
    {
        /// <summary>
        /// 缓存字典
        /// </summary>
        private static readonly ConcurrentDictionary<IInvocation, ApiActionDescriptor> cache;

        /// <summary>
        /// Castle相关上下文
        /// </summary>
        static DescriptorCache()
        {
            cache = new ConcurrentDictionary<IInvocation, ApiActionDescriptor>(new IInvocationComparer());
        }

        /// <summary>
        /// 从缓存获得ApiActionDescriptor
        /// </summary>
        /// <param name="invocation">拦截内容</param>
        /// <returns></returns>
        public static ApiActionDescriptor GetApiActionDescriptor(IInvocation invocation)
        {
            return cache.GetOrAdd(invocation, GetActionDescriptor);
        }

        /// <summary>
        /// 从拦截内容获得ApiActionDescriptor
        /// </summary>
        /// <param name="invocation">拦截内容</param>
        /// <returns></returns>
        private static ApiActionDescriptor GetActionDescriptor(IInvocation invocation)
        {
            var method = invocation.Method;
            if (method.ReturnType.IsGenericType == false || method.ReturnType.GetGenericTypeDefinition() != typeof(Task<>))
            {
                var message = string.Format("接口{0}返回类型应该是Task<{1}>", method.Name, method.ReturnType.Name);
                throw new NotSupportedException(message);
            }

            var filterAttributes = method
                .FindDeclaringAttributes<IApiActionFilterAttribute>(true)
                .Distinct(new AttributeComparer<IApiActionFilterAttribute>())
                .OrderBy(item => item.OrderIndex)
                .ToArray();

            var presetActionAttributes = GetPresetActionAttributes(invocation);
            var declaringtActionAttributes = method.FindDeclaringAttributes<IApiActionAttribute>(true);

            var actionAttributes = presetActionAttributes
                .Concat(declaringtActionAttributes)
                .Distinct(new AttributeComparer<IApiActionAttribute>())
                .OrderBy(item => item.OrderIndex)
                .ToArray();

            return new ApiActionDescriptor
            {
                Name = method.Name,
                Filters = filterAttributes,
                Return = GetReturnDescriptor(method),
                Attributes = actionAttributes,
                Parameters = method.GetParameters().Select((p, i) => GetParameterDescriptor(p, i)).ToArray()
            };
        }

        /// <summary>
        /// 从拦截上下文获取预设的特性
        /// </summary>
        /// <param name="invocation"></param>
        /// <returns></returns>
        private static IEnumerable<ApiActionAttribute> GetPresetActionAttributes(IInvocation invocation)
        {
            var hostAttribute = invocation.Proxy.GetType().GetCustomAttribute<HttpHostAttribute>();
            if (hostAttribute == null)
            {
                hostAttribute = invocation.Method.FindDeclaringAttribute<HttpHostAttribute>(false);
            }

            if (hostAttribute == null)
            {
                throw new HttpRequestException("未指定任何HttpHostAttribute");
            }

            yield return hostAttribute;
        }

        /// <summary>
        /// 生成ApiParameterDescriptor
        /// </summary>
        /// <param name="parameter">参数信息</param>
        /// <param name="index">参数索引</param>
        /// <returns></returns>
        private static ApiParameterDescriptor GetParameterDescriptor(ParameterInfo parameter, int index)
        {
            if (parameter.ParameterType.IsByRef == true)
            {
                var message = string.Format("接口参数不支持ref/out修饰：{0}", parameter);
                throw new NotSupportedException(message);
            }

            var parameterDescriptor = new ApiParameterDescriptor
            {
                Value = null,
                Name = parameter.Name,
                Index = index,
                ParameterType = parameter.ParameterType,
                IsSimpleType = parameter.ParameterType.IsSimple(),
                IsEnumerable = parameter.ParameterType.IsInheritFrom<IEnumerable>(),
                IsDictionaryOfObject = parameter.ParameterType.IsInheritFrom<IDictionary<string, object>>(),
                IsDictionaryOfString = parameter.ParameterType.IsInheritFrom<IDictionary<string, string>>(),
                Attributes = parameter.GetAttributes<IApiParameterAttribute>(true).ToArray()
            };

            if (parameterDescriptor.Attributes.Length == 0)
            {
                if (parameter.ParameterType.IsInheritFrom<IApiParameterable>() || parameter.ParameterType.IsInheritFrom<IEnumerable<IApiParameterable>>())
                {
                    parameterDescriptor.Attributes = new[] { new ParameterableAttribute() };
                }
                else if (parameter.ParameterType.IsInheritFrom<HttpContent>())
                {
                    parameterDescriptor.Attributes = new[] { new HttpContentAttribute() };
                }
                else if (parameterDescriptor.Attributes.Length == 0)
                {
                    parameterDescriptor.Attributes = new[] { new PathQueryAttribute() };
                }
            }
            return parameterDescriptor;
        }

        /// <summary>
        /// 生成ApiReturnDescriptor
        /// </summary>
        /// <param name="method">方法信息</param>
        /// <returns></returns>
        private static ApiReturnDescriptor GetReturnDescriptor(MethodInfo method)
        {
            var returnAttribute = method.FindDeclaringAttribute<IApiReturnAttribute>(true);
            if (returnAttribute == null)
            {
                returnAttribute = new AutoReturnAttribute();
            }

            return new ApiReturnDescriptor
            {
                Attribute = returnAttribute,
                TaskType = method.ReturnType,
                DataType = method.ReturnType.GetGenericArguments().FirstOrDefault(),
            };
        }

        /// <summary>
        /// 特性比较器
        /// </summary>
        private class AttributeComparer<T> : IEqualityComparer<T> where T : IAttributeMultiplable
        {
            /// <summary>
            /// 是否相等
            /// </summary>
            /// <param name="x"></param>
            /// <param name="y"></param>
            /// <returns></returns>
            public bool Equals(T x, T y)
            {
                // 如果其中一个不允许重复，返回true将y过滤
                return x.AllowMultiple == false || y.AllowMultiple == false;
            }

            /// <summary>
            /// 获取哈希码
            /// </summary>
            /// <param name="obj"></param>
            /// <returns></returns> 
            public int GetHashCode(T obj)
            {
                return obj.GetType().GetHashCode();
            }
        }


        /// <summary>
        /// IInvocation对象的比较器
        /// </summary>
        private class IInvocationComparer : IEqualityComparer<IInvocation>
        {
            /// <summary>
            /// 是否相等
            /// </summary>
            /// <param name="x"></param>
            /// <param name="y"></param>
            /// <returns></returns>
            public bool Equals(IInvocation x, IInvocation y)
            {
                return x.Method.Equals(y.Method);
            }

            /// <summary>
            /// 获取哈希码
            /// </summary>
            /// <param name="obj"></param>
            /// <returns></returns>
            public int GetHashCode(IInvocation obj)
            {
                return obj.Method.GetHashCode();
            }
        }
    }
}