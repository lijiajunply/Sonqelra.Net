using System.Numerics;
using System.Globalization;

namespace Sonqelra.Core;

/// <summary>An exact, normalized rational number.</summary>
public readonly struct Rational : IComparable<Rational>, IEquatable<Rational>
{
    public BigInteger Numerator { get; }
    public BigInteger Denominator => field.IsZero ? BigInteger.One : field;

    public Rational(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero) throw new DivideByZeroException();
        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        Numerator = numerator / gcd;
        this.Denominator = denominator / gcd;
    }

    public Rational(BigInteger numerator) : this(numerator, BigInteger.One)
    {
    }

    public static Rational Parse(string value)
    {
        var parts = value.Split('/');
        if (parts.Length is < 1 or > 2) throw new FormatException("Invalid rational number");
        return new(BigInteger.Parse(parts[0], CultureInfo.InvariantCulture),
            parts.Length == 1 ? BigInteger.One : BigInteger.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    public static implicit operator Rational(int value) => new(value);
    public static implicit operator Rational(long value) => new(value);
    public static implicit operator Rational(BigInteger value) => new(value);
    public static explicit operator double(Rational x) => (double)x.Numerator / (double)x.Denominator;
    public static explicit operator int(Rational x) => (int)(x.Numerator / x.Denominator);

    public static Rational operator +(Rational a, Rational b) =>
        new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);

    public static Rational operator -(Rational a, Rational b) =>
        new(a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator);

    public static Rational operator -(Rational a) => new(-a.Numerator, a.Denominator);

    public static Rational operator *(Rational a, Rational b) =>
        new(a.Numerator * b.Numerator, a.Denominator * b.Denominator);

    public static Rational operator /(Rational a, Rational b) =>
        new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);

    public int CompareTo(Rational other) => (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);
    public bool Equals(Rational other) => Numerator == other.Numerator && Denominator == other.Denominator;
    public override bool Equals(object? obj) => obj is Rational r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);
    public static bool operator ==(Rational a, Rational b) => a.Equals(b);
    public static bool operator !=(Rational a, Rational b) => !a.Equals(b);
    public static bool operator <(Rational a, Rational b) => a.CompareTo(b) < 0;
    public static bool operator >(Rational a, Rational b) => a.CompareTo(b) > 0;
    public static bool operator <=(Rational a, Rational b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Rational a, Rational b) => a.CompareTo(b) >= 0;

    public override string ToString() => Denominator == BigInteger.One
        ? Numerator.ToString(CultureInfo.InvariantCulture)
        : $"{Numerator}/{Denominator}";
}