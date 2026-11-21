using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ample.Speech.Flite.Core;

// Port of Flite (CMU, https://github.com/festvox/flite, commit 6c9f20dc915b17f5619340069889db0aa007fcdc), src/utils/cst_val.c,
// cst_val_const.c and the val accessors. Translated to C# and modified: the C union is a class hierarchy, errors that call
// cst_error() in C are thrown as FliteException. See LICENSES/flite.txt and THIRD-PARTY-NOTICES.md for the licence.

/// <summary>An error that the C program reports with <c>cst_error()</c> (it exits there); the port throws it instead.</summary>
internal sealed class FliteException : Exception
{
    public FliteException(string message) : base(message)
    {
    }
}

/// <summary>
/// The Flite value (<c>cst_val</c>): an int, a float, a string, a cons cell or a user object. <c>null</c> stands for the C NULL (the empty list).
/// Type codes are those of <c>cst_val.h</c>: cons 0, int 1, float 3, string 5, everything else (users) 7 and up.
/// </summary>
internal abstract class Value
{
    /// <summary>The <c>CST_VAL_TYPE</c> of this value.</summary>
    public abstract int TypeCode { get; }
}

/// <summary>An int value; the C code reads it back as <c>int</c>.</summary>
internal sealed class IntValue(int number) : Value
{
    public int Number { get; } = number;

    public override int TypeCode => Values.TypeInt;
}

/// <summary>A float value; stored as <c>float</c>, as the C accessor <c>val_float</c> returns it.</summary>
internal sealed class FloatValue(float number) : Value
{
    public float Number { get; } = number;

    public override int TypeCode => Values.TypeFloat;
}

/// <summary>A string value; the C string is bytes, here the .NET string whose characters are the (Latin-1 or UTF-16) text the caller decoded.</summary>
internal sealed class StringValue(string text) : Value
{
    public string Text { get; } = text;

    public override int TypeCode => Values.TypeString;
}

/// <summary>A cons cell; <c>Cdr == null</c> ends the list.</summary>
internal sealed class ConsValue(Value? car, Value? cdr) : Value
{
    public Value? Car { get; set; } = car;

    public Value? Cdr { get; set; } = cdr;

    public override int TypeCode => Values.TypeCons;
}

/// <summary>A user value that points at an object (an item, a relation, a phoneset, a function, ...): two are equal when the objects are the same.</summary>
internal class ObjectValue(int typeCode, object target) : Value
{
    public object Target { get; } = target;

    public override int TypeCode { get; } = typeCode;
}

/// <summary>A user value that points at an <see cref="Item"/> (<c>item_val</c>).</summary>
internal sealed class ItemValue(Item item) : ObjectValue(Values.TypeItem, item)
{
    public Item Item { get; } = item;
}

/// <summary>A user value that points at a <see cref="Relation"/> (<c>relation_val</c>).</summary>
internal sealed class RelationValue(Relation relation) : ObjectValue(Values.TypeRelation, relation)
{
    public Relation Relation { get; } = relation;
}

/// <summary>A user value that points at a feature function (<c>ffunc_val</c>).</summary>
internal sealed class FunctionValue(Func<Item, Value?> function) : ObjectValue(Values.TypeFunction, function)
{
    public Func<Item, Value?> Function { get; } = function;
}

/// <summary>A user value that points at a phoneset (<c>phoneset_val</c>).</summary>
internal sealed class PhonesetValue(Phoneset phoneset) : ObjectValue(Values.TypePhoneset, phoneset)
{
    public Phoneset Phoneset { get; } = phoneset;
}

/// <summary>The functions of <c>cst_val.c</c> and <c>cst_val_const.c</c> (accessors with their type coercions, equality, comparison, lists, shared constants).</summary>
internal static class Values
{
    public const int TypeCons = 0;
    public const int TypeInt = 1;
    public const int TypeFloat = 3;
    public const int TypeString = 5;

    // the user types of cst_val_user.c get a code of their own (only the identity matters)
    public const int TypeItem = 7;
    public const int TypeRelation = 9;
    public const int TypeFunction = 11;
    public const int TypePhoneset = 13;
    public const int TypeCart = 15;

    /// <summary><c>val_int_const_max</c>: the first constants are shared; larger numbers all give the last one.</summary>
    public const int IntConstMax = 25;

    /// <summary><c>CST_CONST_INT_MAX</c>: the bound of the counting loops of the feature functions.</summary>
    public const int CountLimit = 19;

    private static readonly IntValue[] IntConstants = Enumerable.Range(0, IntConstMax).Select(i => new IntValue(i)).ToArray();
    private static readonly StringValue[] StringConstants =
        Enumerable.Range(0, IntConstMax).Select(i => new StringValue(i.ToString(CultureInfo.InvariantCulture))).ToArray();

    /// <summary><c>val_string_0</c> .. : the shared string "0".</summary>
    public static StringValue String0 => StringConstants[0];

    /// <summary>The shared string "1".</summary>
    public static StringValue String1 => StringConstants[1];

    /// <summary>The shared string "3".</summary>
    public static StringValue String3 => StringConstants[3];

    /// <summary>The shared string "4".</summary>
    public static StringValue String4 => StringConstants[4];

    /// <summary><c>val_int_n</c>: a shared int value; 24 for every number from 24 up (the C table is 25 long).</summary>
    public static IntValue IntN(int n) => n < IntConstMax ? IntConstants[n] : IntConstants[IntConstMax - 1];

    /// <summary><c>val_string_n</c>: the shared string of the number; negative gives "0", 24 and up give "24".</summary>
    public static StringValue StringN(int n) => n < 0 ? StringConstants[0] : n < IntConstMax ? StringConstants[n] : StringConstants[IntConstMax - 1];

    /// <summary><c>val_int</c>: an int as is, a float truncated, a string through <c>atoi</c>; anything else is an error.</summary>
    public static int Int(Value? v) => v switch
    {
        IntValue i => i.Number,
        FloatValue f => unchecked((int)f.Number),
        StringValue s => Atoi(s.Text),
        _ => throw new FliteException($"VAL: tried to access int in {TypeOf(v)} typed val"),
    };

    /// <summary><c>val_float</c>: an int converted, a float as is, a string through <c>atof</c> (as a float); anything else is an error.</summary>
    public static float Float(Value? v) => v switch
    {
        IntValue i => i.Number,
        FloatValue f => f.Number,
        StringValue s => (float)Atof(s.Text),
        _ => throw new FliteException($"VAL: tried to access float in {TypeOf(v)} typed val"),
    };

    /// <summary><c>val_string</c>: only a string value is accepted.</summary>
    public static string String(Value? v) => v is StringValue s ? s.Text : throw new FliteException($"VAL: tried to access string in {TypeOf(v)} typed val");

    /// <summary><c>val_car</c>: an error on anything but a cons.</summary>
    public static Value? Car(Value? v) => v is ConsValue c ? c.Car : throw new FliteException($"VAL: tried to access car in {TypeOf(v)} typed val");

    /// <summary><c>val_cdr</c>: an error on anything but a cons.</summary>
    public static Value? Cdr(Value? v) => v is ConsValue c ? c.Cdr : throw new FliteException($"VAL: tried to access cdr in {TypeOf(v)} typed val");

    /// <summary><c>val_stringp</c>: the value is a string (a cons is not).</summary>
    public static bool IsString(Value v) => v.TypeCode == TypeString;

    /// <summary><c>val_equal</c>: same type and same content; user values by identity of what they point at. A different type is never equal (an int 1 is not the string "1").</summary>
    public static bool Equal(Value? v1, Value? v2)
    {
        if (ReferenceEquals(v1, v2))
        {
            return true;
        }

        if (v1 is null || v2 is null)
        {
            return false;
        }

        if (v1.TypeCode != v2.TypeCode)
        {
            return false;
        }

        return v1 switch
        {
            ConsValue c1 => Equal(c1.Car, ((ConsValue)v2).Car) && Equal(c1.Cdr, ((ConsValue)v2).Cdr),
            IntValue i1 => i1.Number == ((IntValue)v2).Number,
            FloatValue f1 => f1.Number == ((FloatValue)v2).Number,
            StringValue s1 => string.Equals(s1.Text, ((StringValue)v2).Text, StringComparison.Ordinal),
            ObjectValue o1 => ReferenceEquals(o1.Target, ((ObjectValue)v2).Target),
            _ => false,
        };
    }

    /// <summary><c>val_less</c>: compared as floats, whatever the types.</summary>
    public static bool Less(Value? v1, Value? v2) => Float(v1) < Float(v2);

    /// <summary><c>val_greater</c>: compared as floats, whatever the types.</summary>
    public static bool Greater(Value? v1, Value? v2) => Float(v1) > Float(v2);

    /// <summary><c>val_member</c>: v1 equals an element of the list.</summary>
    public static bool Member(Value? v1, Value? list)
    {
        for (Value? i = list; i is not null; i = Cdr(i))
        {
            if (Equal(Car(i), v1))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>val_length</c>: the number of cells.</summary>
    public static int Length(Value? list)
    {
        int n = 0;
        for (Value? i = list; i is not null; i = Cdr(i))
        {
            n++;
        }

        return n;
    }

    /// <summary>
    /// <c>atoi</c> of the C library (glibc: <c>(int) strtol(s, NULL, 10)</c>): blanks, a sign, digits, then anything. A number that does not fit a long is
    /// <c>LONG_MAX</c> or <c>LONG_MIN</c> (<c>strtol</c> saturates), and the cast to int keeps the low 32 bits, so a number of twenty digits is -1 or 0, never a wrapped value.
    /// </summary>
    public static int Atoi(string s)
    {
        int i = 0;
        while (i < s.Length && IsCSpace(s[i]))
        {
            i++;
        }

        bool negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            negative = s[i] == '-';
            i++;
        }

        ulong limit = negative ? 9223372036854775808UL : 9223372036854775807UL;
        ulong magnitude = 0;
        bool saturated = false;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
        {
            ulong digit = (ulong)(s[i] - '0');
            if (!saturated && magnitude > (limit - digit) / 10)
            {
                saturated = true;
            }
            else if (!saturated)
            {
                magnitude = (magnitude * 10) + digit;
            }

            i++;
        }

        if (saturated)
        {
            magnitude = limit;
        }

        long value = unchecked(negative ? -(long)magnitude : (long)magnitude);
        return unchecked((int)value);
    }

    private static readonly Regex DecimalPrefix = new(@"\A[ \t\n\v\f\r]*[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>atof</c> of the C library for decimal numbers: blanks, a sign, digits with an optional point and exponent, then anything.
    /// No match gives 0. Hexadecimal, "inf" and "nan" forms are not read (a C library would): no feature of the voice holds them.
    /// </summary>
    public static double Atof(string s)
    {
        Match m = DecimalPrefix.Match(s);
        return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0.0;
    }

    /// <summary>ASCII lower case of <c>cst_downcase</c> (the C locale: only A to Z change).</summary>
    public static string Downcase(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is >= 'A' and <= 'Z')
            {
                sb ??= new StringBuilder(s);
                sb[i] = (char)(c + 32);
            }
        }

        return sb?.ToString() ?? s;
    }

    private static bool IsCSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    private static int TypeOf(Value? v) => v is null ? -1 : v.TypeCode;
}
