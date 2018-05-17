﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace JsonLogic.Net
{
    public class JsonLogicEvaluator : IProcessJsonLogic, IManageOperations
    {
        private Dictionary<string, Func<IProcessJsonLogic, JToken[], object, object>> registry;

        public JsonLogicEvaluator()
        {
            registry = new Dictionary<string, Func<IProcessJsonLogic, JToken[], object, object>>();
            AddDefaultOperations();
        }

        public void AddOperation(string name, Func<IProcessJsonLogic, JToken[], object, object> operation)
        {
            registry[name] = operation;
        }

        public object Apply(JToken rule, object data)
        {
            if (rule is null) return null;
            if (rule is JValue) return (rule as JValue).Value;
            if (rule is JArray) return (rule as JArray).Select(r => Apply(r, data));

            var ruleObj = (JObject) rule;
            var p = ruleObj.Properties().First();
            var opName = p.Name;
            var opArgs = (p.Value is JArray) ? (p.Value as JArray).ToArray() : new JToken[] { p.Value };
            var op = GetOperation(opName);
            return op(this, opArgs, data);
        }

        public void DeleteOperation(string name)
        {
            registry.Remove(name);
        }

        public Func<IProcessJsonLogic, JToken[], object, object> GetOperation(string name)
        {
            return registry[name];
        }

        private bool IsAny<T>(params object[] subjects) 
        {
            return subjects.Any(x => x != null && x is T);
        }

        private void AddDefaultOperations()
        {
            AddOperation("==", (p, args, data) => p.Apply(args[0], data).Equals(p.Apply(args[1], data)));
            
            AddOperation("===", (p, args, data) => p.Apply(args[0], data).Equals(p.Apply(args[1], data)));

            AddOperation("!==", (p, args, data) => !p.Apply(args[0], data).Equals(p.Apply(args[1], data)));

            AddOperation("!=", (p, args, data) => !p.Apply(args[0], data).Equals(p.Apply(args[1], data)));

            AddOperation("+", (p, args, data) => Min2From(args.Select(a => p.Apply(a, data))).Aggregate((prev, next) =>
            {
                if (IsAny<string>(next, prev))
                    return (prev ?? string.Empty).ToString() + next.ToString();

                return Convert.ToDouble(prev ?? 0d) + Convert.ToDouble(next);
            }));

            AddOperation("-", ReduceDoubleArgs((prev, next) => prev - next));

            AddOperation("/", ReduceDoubleArgs((prev, next) => prev / next));

            AddOperation("*", ReduceDoubleArgs((prev, next) => prev * next));

            AddOperation("%", ReduceDoubleArgs((prev, next) => prev % next));

            AddOperation("max", ReduceDoubleArgs((prev, next) => (prev > next) ? prev : next));

            AddOperation("min", ReduceDoubleArgs((prev, next) => (prev < next) ? prev : next));

            AddOperation("<", DoubleArgsSatisfy((prev, next) => prev < next));

            AddOperation("<=", DoubleArgsSatisfy((prev, next) => prev <= next));
            
            AddOperation(">", DoubleArgsSatisfy((prev, next) => prev > next));

            AddOperation(">=", DoubleArgsSatisfy((prev, next) => prev >= next));

            AddOperation("var", (p, args, data) => {
                var names = p.Apply(args.First(), data).ToString();
                try 
                {
                    return GetValueByName(data, names);
                }
                catch (Exception e) 
                {
                    object defaultValue = (args.Count() == 2) ? p.Apply(args.Last(), data) : null;
                    return defaultValue;
                }
            });

            AddOperation("and", (p, args, data) => {
                bool value = Convert.ToBoolean(p.Apply(args[0], data));
                for (var i = 1; i < args.Length && value; i++) 
                {
                    value = Convert.ToBoolean(p.Apply(args[i], data));
                }
                return value;
            });

            AddOperation("or", (p, args, data) => {
                bool value = Convert.ToBoolean(p.Apply(args[0], data));
                for (var i = 1; i < args.Length && !value; i++) 
                {
                    value = Convert.ToBoolean(p.Apply(args[i], data));
                }
                return value;
            });

            AddOperation("if", (p, args, data) => {
                for (var i = 0; i < args.Length - 1; i += 2) 
                {
                    if (Convert.ToBoolean(p.Apply(args[i], data))) return p.Apply(args[i+1], data);
                }
                return p.Apply(args[args.Length - 1], data);
            });

            AddOperation("missing", (p, args, data) => args.Select(a => p.Apply(a, data).ToString()).Where(n => {
                try 
                {
                    GetValueByName(data, n);
                    return false;
                }
                catch
                {
                    return true;
                }
            }));

            AddOperation("missing_some", (p, args, data) => {
                var minRequired = Convert.ToDouble(p.Apply(args[0], data));
                var keys = (args[1] as JArray).ToArray();
                var missingKeys = GetOperation("missing").Invoke(p, keys, data) as IEnumerable<object>;
                var validKeyCount = keys.Length - missingKeys.Count();
                return (validKeyCount >= minRequired) ? new object[0] : missingKeys;
            });

            AddOperation("map", (p, args, data) => {
                IEnumerable<object> arr = MakeEnumerable(p.Apply(args[0], data));
                return arr.Select(item => p.Apply(args[1], item)).ToArray();
            });

            AddOperation("filter", (p, args, data) => {
                IEnumerable<object> arr = MakeEnumerable(p.Apply(args[0], data));
                return arr.Where(item => Convert.ToBoolean(p.Apply(args[1], item))).ToArray();
            });

            AddOperation("reduce", (p, args, data) => {
                IEnumerable<object> arr = MakeEnumerable(p.Apply(args[0], data));
                var initialValue = p.Apply(args[2], data);
                return arr.Aggregate(initialValue, (acc, current) => {
                    object result = p.Apply(args[1], new{current = current, accumulator = acc});
                    return result;
                });
            });

            AddOperation("all", (p, args, data) => {
                IEnumerable<object> arr = MakeEnumerable(p.Apply(args[0], data));
                return arr.All(item => Convert.ToBoolean(p.Apply(args[1], item)));
            });

            AddOperation("none", (p, args, data) => {
                IEnumerable<object> arr = MakeEnumerable(p.Apply(args[0], data));
                return !arr.Any(item => Convert.ToBoolean(p.Apply(args[1], item)));
            });

            AddOperation("some", (p, args, data) => {
                IEnumerable<object> arr = MakeEnumerable(p.Apply(args[0], data));
                return arr.Any(item => Convert.ToBoolean(p.Apply(args[1], item)));
            });

            AddOperation("merge", (p, args, data) => args.Select(a => p.Apply(a, data)).Aggregate((IEnumerable<object>)new object[0], (acc, current) => {
                try {
                    return acc.Concat(MakeEnumerable(current));
                }
                catch {
                    return acc.Concat(new object[]{ current });
                }
            }));

            AddOperation("in", (p, args, data) => {
                object needle = p.Apply(args[0], data);
                object haystack = p.Apply(args[1], data);
                if (haystack is String) return (haystack as string).IndexOf(needle.ToString()) >= 0;

                return MakeEnumerable(haystack).Any(item => item.Equals(needle));
            });

            AddOperation("cat", (p, args, data) => args.Select(a => p.Apply(a, data)).Aggregate("", (acc, current) => acc + current.ToString()));

            AddOperation("substr", (p, args, data) => {
                string value = p.Apply(args[0], data).ToString();
                int start = Convert.ToInt32(p.Apply(args[1], data));
                if (start < 0) start += value.Length;
                int length = (args.Count() == 2) ? value.Length - start : Convert.ToInt32(p.Apply(args[2], data));
                return value.Substring(start, length);
            });

            AddOperation("log", (p, args, data) => {
                object value = p.Apply(args[0], data);
                Console.WriteLine(value);
                return value;
            });
        }

        private IEnumerable<object> MakeEnumerable(object value)
        {
            if (value is Array) return (value as Array).Cast<object>();

            if (value is IEnumerable<object>) return (value as IEnumerable<object>);

            throw new ArgumentException("Argument is not enumerable");
        }

        private object GetValueByName(object data, string namePath)
        {
            if (string.IsNullOrEmpty(namePath)) return data;

            string[] names = namePath.Split('.');
            object d = data;
            foreach (string name in names) 
            {
                if (d == null) return null;
                if (d.GetType().IsArray) 
                {
                    d = (d as Array).GetValue(int.Parse(name));
                }
                else if (typeof(IEnumerable<object>).IsAssignableFrom(d.GetType())) 
                {
                    d = (d as IEnumerable<object>).Skip(int.Parse(name)).First();
                }
                else if (typeof(IDictionary<string, object>).IsAssignableFrom(d.GetType()))
                {
                    var dict = (d as IDictionary<string, object>);
                    if (!dict.ContainsKey(name)) throw new Exception();
                    d = dict[name];
                }
                else 
                {
                    var property = d.GetType().GetProperty(name);
                    if (property == null) throw new Exception();
                    d = property.GetValue(d);
                }
            }
            return d;
        }

        private Func<IProcessJsonLogic, JToken[], object, object> DoubleArgsSatisfy(Func<double, double, bool> criteria)
        {
            return (p, args, data) => {
                var values = args.Select(a => a == null ? 0d : Convert.ToDouble(p.Apply(a, data))).ToArray();
                for (int i = 1; i < values.Length; i++) {
                    if (!criteria(values[i-1], values[i])) return false;
                }
                return true;
            };
        }

        private static bool IsEnumerable(object d)
        {
            return d.GetType().IsArray || (d as IEnumerable<object>) != null;
        }

        private static Func<IProcessJsonLogic, JToken[], object, object> ReduceDoubleArgs(Func<double, double, double> reducer)
        {
            return (p, args, data) => Min2From(args.Select(a => p.Apply(a, data))).Select(a => a == null ? 0d : Convert.ToDouble(a)).Aggregate(reducer);
        }

        private static IEnumerable<object> Min2From(IEnumerable<object> source) 
        {
            var count = source.Count();
            if (count >= 2) return source;

            return new object[]{ null, count == 0 ? null : source.First() };
        }
    }
}
