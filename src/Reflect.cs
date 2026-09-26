using System.Reflection;

namespace FastSeedRoll;

/// <summary>
/// 反射工具：<b>按参数名</b>构造对象、按成员名赋值。
///
/// 为什么不用"按位置硬编码构造器"：
/// RolltheSpire2 的 record 构造函数参数很多（<c>NeowSearchFilter</c> 有 12 个），
/// 一旦作者调整参数顺序或增删参数，按位置的反射调用会<b>静默传错值</b> —— 那是最坏的情况。
/// 按名字匹配则会在参数缺失时<b>大声报错</b>，或者至少把默认值放在对的位置。
///
/// 这是"宁可失败也不要给错种子"原则在反射层的落实。
/// </summary>
internal static class Reflect
{
    /// <summary>
    /// 找一个构造函数：优先选「所有必需参数都能在我们的 values 字典里按名字找到」的那个，
    /// 并在候选数相同时选参数最多的（信息量最大）。
    /// </summary>
    public static ConstructorInfo PickCtor(Type t, IEnumerable<string> knownNames)
    {
        var known = new HashSet<string>(knownNames, StringComparer.OrdinalIgnoreCase);
        ConstructorInfo best = null;
        var bestScore = -1;

        foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var ps = c.GetParameters();
            var score = 0;
            var usable = true;
            foreach (var p in ps)
            {
                var isKnown = p.Name != null && known.Contains(p.Name);
                if (isKnown) score++;
                else if (!p.IsOptional && !p.HasDefaultValue) { usable = false; break; }
            }
            if (!usable) continue;
            if (score > bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    /// <summary>
    /// 按参数名构造。不在 <paramref name="values"/> 里的参数：能用默认值就用，否则用 <c>default</c>。
    /// 值会做必要的宽松转换（int→long、string→enum 等）。
    /// </summary>
    public static object Construct(Type t, IDictionary<string, object> values)
    {
        var ctor = PickCtor(t, values.Keys)
            ?? throw new InvalidOperationException(
                $"{t.FullName}: 找不到可用的构造函数（已知参数: {string.Join(",", values.Keys)}）");

        var ps = ctor.GetParameters();
        var args = new object[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p.Name != null && values.TryGetValue(p.Name, out var v))
                args[i] = Coerce(v, p.ParameterType);
            else if (p.HasDefaultValue)
                args[i] = p.DefaultValue;
            else
                args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        }

        return ctor.Invoke(args);
    }

    /// <summary>
    /// 按成员名赋值。优先写属性/字段；如果只有 init-only 属性，就直写它的 backing field
    /// （<c>&lt;Name&gt;k__BackingField</c>），这在运行时是合法的。
    /// 找不到就抛 —— 不静默忽略。
    /// </summary>
    public static void SetMember(object target, string memberName, object value)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        var t = target.GetType();

        // 1) 可写属性
        var prop = t.GetProperty(memberName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(target, Coerce(value, prop.PropertyType));
            return;
        }

        // 2) 字段
        var field = t.GetField(memberName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null)
        {
            field.SetValue(target, Coerce(value, field.FieldType));
            return;
        }

        // 3) init-only 属性的 backing field
        var backing = t.GetField($"<{memberName}>k__BackingField",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (backing != null)
        {
            backing.SetValue(target, Coerce(value, backing.FieldType));
            return;
        }

        throw new InvalidOperationException($"{t.FullName} 上没有成员 {memberName}（属性/字段/backing field 都找不到）");
    }

    /// <summary>按名字读成员（属性/字段/backing field），读不到返回 <paramref name="fallback"/>。</summary>
    public static object GetMember(object target, string memberName, object fallback = null)
    {
        if (target == null) return fallback;
        var t = target.GetType();
        var prop = t.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null) return prop.GetValue(target);
        var field = t.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null) return field.GetValue(target);
        var backing = t.GetField($"<{memberName}>k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return backing != null ? backing.GetValue(target) : fallback;
    }

    /// <summary>把值宽松转换成目标类型。</summary>
    public static object Coerce(object value, Type target)
    {
        if (target == typeof(object) || value == null)
            return value;

        var vt = value.GetType();
        if (target.IsAssignableFrom(vt)) return value;

        // 可空类型拆一层
        var nullable = Nullable.GetUnderlyingType(target);
        if (nullable != null) return Coerce(value, nullable);

        // 枚举：字符串或数字
        if (target.IsEnum)
        {
            if (value is string s)
            {
                try { return Enum.Parse(target, s, ignoreCase: true); }
                catch { return Activator.CreateInstance(target); }
            }
            return Enum.ToObject(target, Convert.ToInt64(value));
        }

        // 数组 / 列表：把 IEnumerable 转成目标元素类型的数组或 List<T>
        if (target.IsArray)
        {
            var elem = target.GetElementType();
            if (value is System.Collections.IEnumerable en && !(value is string))
            {
                var items = en.Cast<object>().Select(x => Coerce(x, elem)).ToArray();
                var arr = Array.CreateInstance(elem, items.Length);
                for (var i = 0; i < items.Length; i++) arr.SetValue(items[i], i);
                return arr;
            }
        }

        if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var elem = target.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elem);
            if (value is System.Collections.IEnumerable en2 && !(value is string))
            {
                var list = (System.Collections.IList)Activator.CreateInstance(listType);
                foreach (var x in en2) list.Add(Coerce(x, elem));
                return list;
            }
        }

        // 数值
        if (target == typeof(long) || target == typeof(ulong) || target == typeof(double) ||
            target == typeof(float) || target == typeof(decimal) || target == typeof(int))
            return Convert.ChangeType(value, target);

        return value;
    }

    /// <summary>
    /// 在程序集里按「全名包含这些片段」找类型，返回第一个命中的。
    /// 用于 RT2 的类型可能有命名空间变动的情况。
    /// </summary>
    public static Type FindType(Assembly asm, string fullName)
        => asm.GetType(fullName, false);

    /// <summary>把一个枚举的成员名列举出来（诊断用）。</summary>
    public static string DescribeEnum(Type e)
        => e == null ? "(null)" : string.Join(" | ", Enum.GetNames(e));

    /// <summary>把一个类型的可用构造函数签名列举出来（诊断用）。</summary>
    public static string DescribeCtors(Type t)
    {
        if (t == null) return "(null)";
        var lines = new List<string>();
        foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            lines.Add("  (" + string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")) + ")");
        return lines.Count == 0 ? "(无构造函数)" : string.Join(Environment.NewLine, lines);
    }
}
