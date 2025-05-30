// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an expression that might be made up of multiple resource properties. For example,
/// a connection string might be made up of a host, port, and password from different endpoints.
/// </summary>
public class ReferenceExpression : IManifestExpressionProvider, IValueProvider, IValueWithReferences
{
    private readonly string[] _manifestExpressions;

    private ReferenceExpression(string format, IValueProvider[] valueProviders, string[] manifestExpressions)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(valueProviders);
        ArgumentNullException.ThrowIfNull(manifestExpressions);

        Format = format;
        ValueProviders = valueProviders;
        _manifestExpressions = manifestExpressions;
    }

    /// <summary>
    /// The format string for this expression.
    /// </summary>
    public string Format { get; }

    /// <summary>
    /// The manifest expressions for the parameters for the format string.
    /// </summary>
    public IReadOnlyList<string> ManifestExpressions => _manifestExpressions;

    /// <summary>
    /// The list of <see cref="IValueProvider"/> that will be used to resolve parameters for the format string.
    /// </summary>
    public IReadOnlyList<IValueProvider> ValueProviders { get; }

    IEnumerable<object> IValueWithReferences.References => ValueProviders;

    /// <summary>
    /// The value expression for the format string.
    /// </summary>
    public string ValueExpression =>
        string.Format(CultureInfo.InvariantCulture, Format, _manifestExpressions);

    /// <summary>
    /// Gets the value of the expression. The final string value after evaluating the format string and its parameters.
    /// </summary>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/>.</param>
    /// <returns></returns>
    public async ValueTask<string?> GetValueAsync(CancellationToken cancellationToken)
    {
        // NOTE: any logical changes to this method should also be made to ExpressionResolver.EvalExpressionAsync
        if (Format.Length == 0)
        {
            return null;
        }

        var args = new object?[ValueProviders.Count];
        for (var i = 0; i < ValueProviders.Count; i++)
        {
            args[i] = await ValueProviders[i].GetValueAsync(cancellationToken).ConfigureAwait(false);
        }

        return string.Format(CultureInfo.InvariantCulture, Format, args);
    }

    internal static ReferenceExpression Create(string format, IValueProvider[] valueProviders, string[] manifestExpressions)
    {
        return new(format, valueProviders, manifestExpressions);
    }

    /// <summary>
    /// Creates a new instance of <see cref="ReferenceExpression"/> with the specified format and value providers.
    /// </summary>
    /// <param name="handler">The handler that contains the format and value providers.</param>
    /// <returns>A new instance of <see cref="ReferenceExpression"/> with the specified format and value providers.</returns>
    public static ReferenceExpression Create(in ExpressionInterpolatedStringHandler handler)
    {
        return handler.GetExpression();
    }

    /// <summary>
    /// Represents a handler for interpolated strings that contain expressions. Those expressions will either be literal strings or
    /// instances of types that implement both <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
    /// </summary>
    /// <param name="literalLength">The length of the literal part of the interpolated string.</param>
    /// <param name="formattedCount">The number of formatted parts in the interpolated string.</param>
    [InterpolatedStringHandler]
    public ref struct ExpressionInterpolatedStringHandler(int literalLength, int formattedCount)
    {
        private static readonly char[] s_braces = ['{', '}'];
        private readonly StringBuilder _builder = new(literalLength * 2);
        private readonly List<IValueProvider> _valueProviders = new(formattedCount);
        private readonly List<string> _manifestExpressions = new(formattedCount);

        /// <summary>
        /// Appends a literal value to the expression.
        /// </summary>
        /// <param name="value">The literal string value to be appended to the interpolated string.</param>
        public readonly void AppendLiteral(string value)
        {
            // Only escape single braces, leave already escaped braces untouched
            _builder.Append(EscapeUnescapedBraces(value));
        }

        /// <summary>
        /// Appends a formatted value to the expression.
        /// </summary>
        /// <param name="value">The formatted string to be appended to the interpolated string.</param>
        public readonly void AppendFormatted(string? value)
        {
            // The value that comes in is a literal string that is not meant to be interpreted.
            // But the _builder later gets treated as a format string, so we just need to escape the braces.
            if (value is not null)
            {
                _builder.Append(EscapeUnescapedBraces(value));
            }
        }

        /// <summary>
        /// Appends a formatted value to the expression. The value must implement <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
        /// </summary>
        /// <param name="valueProvider">An instance of an object which implements <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</param>
        /// <exception cref="InvalidOperationException"></exception>
        public void AppendFormatted<T>(T valueProvider) where T : IValueProvider, IManifestExpressionProvider
        {
            var index = _valueProviders.Count;
            _builder.Append(CultureInfo.InvariantCulture, $"{{{index}}}");

            _valueProviders.Add(valueProvider);
            _manifestExpressions.Add(valueProvider.ValueExpression);
        }

        /// <summary>
        /// Appends a formatted value to the expression. The value must implement <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
        /// </summary>
        /// <param name="valueProvider">An instance of an object which implements <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</param>
        /// <exception cref="InvalidOperationException"></exception>
        public void AppendFormatted<T>(IResourceBuilder<T> valueProvider)
            where T : IResource, IValueProvider, IManifestExpressionProvider
        {
            var index = _valueProviders.Count;
            _builder.Append(CultureInfo.InvariantCulture, $"{{{index}}}");

            _valueProviders.Add(valueProvider.Resource);
            _manifestExpressions.Add(valueProvider.Resource.ValueExpression);
        }

        internal readonly ReferenceExpression GetExpression() =>
            new(_builder.ToString(), [.. _valueProviders], [.. _manifestExpressions]);

        private static string EscapeUnescapedBraces(string input)
        {
            // Fast path: nothing to escape
            if (input.IndexOfAny(s_braces) == -1)
            {
                return input;
            }

            // Allocate a bit of extra space in case we need to escape a few braces.
            var sb = new StringBuilder(input.Length + 4);

            for (var i = 0; i < input.Length; i++)
            {
                var c = input[i];
                if (IsBrace(c))
                {
                    if (IsNextCharSame(input, i))
                    {
                        // Already escaped, copy both and skip next
                        sb.Append(c).Append(c);
                        i++;
                    }
                    else
                    {
                        // Escape single brace
                        sb.Append(c).Append(c);
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();

            static bool IsBrace(char ch) => ch == '{' || ch == '}';
            static bool IsNextCharSame(string s, int idx) =>
                idx + 1 < s.Length && s[idx + 1] == s[idx];
        }
    }
}

/// <summary>
/// A builder for creating <see cref="ReferenceExpression"/> instances.
/// </summary>
public class ReferenceExpressionBuilder
{
    private readonly StringBuilder _builder = new();
    private readonly List<IValueProvider> _valueProviders = new();
    private readonly List<string> _manifestExpressions = new();

    /// <summary>
    /// Indicates whether the expression is empty.
    /// </summary>
    public bool IsEmpty => _builder.Length == 0;

    /// <summary>
    /// Appends an interpolated string to the expression.
    /// </summary>
    /// <param name="handler"></param>
    public void Append([InterpolatedStringHandlerArgument("")] in ReferenceExpressionBuilderInterpolatedStringHandler handler)
    {
    }

    /// <summary>
    /// Appends a literal value to the expression.
    /// </summary>
    /// <param name="value">The literal string value to be appended to the interpolated string.</param>
    public void AppendLiteral(string value)
    {
        _builder.Append(value);
    }

    /// <summary>
    /// Appends a formatted value to the expression.
    /// </summary>
    /// <param name="value">The formatted string to be appended to the interpolated string.</param>
    public void AppendFormatted(string? value)
    {
        _builder.Append(value);
    }

    /// <summary>
    /// Appends a formatted value to the expression. The value must implement <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
    /// </summary>
    /// <param name="valueProvider">An instance of an object which implements <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</param>
    /// <exception cref="InvalidOperationException"></exception>
    public void AppendFormatted<T>(T valueProvider) where T : IValueProvider, IManifestExpressionProvider
    {
        var index = _valueProviders.Count;
        _builder.Append(CultureInfo.InvariantCulture, $"{{{index}}}");

        _valueProviders.Add(valueProvider);
        _manifestExpressions.Add(valueProvider.ValueExpression);
    }

    /// <summary>
    /// Builds the <see cref="ReferenceExpression"/>.
    /// </summary>
    public ReferenceExpression Build() =>
        ReferenceExpression.Create(_builder.ToString(), [.. _valueProviders], [.. _manifestExpressions]);

    /// <summary>
    /// Represents a handler for interpolated strings that contain expressions. Those expressions will either be literal strings or
    /// instances of types that implement both <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
    /// </summary>
    /// <param name="literalLength">The length of the literal part of the interpolated string.</param>
    /// <param name="formattedCount">The number of formatted parts in the interpolated string.</param>
    /// <param name="builder">The builder that will be used to create the <see cref="ReferenceExpression"/>.</param>
    [InterpolatedStringHandler]
#pragma warning disable CS9113 // Parameter is unread.
    public ref struct ReferenceExpressionBuilderInterpolatedStringHandler(int literalLength, int formattedCount, ReferenceExpressionBuilder builder)
#pragma warning restore CS9113 // Parameter is unread.
    {
        /// <summary>
        /// Appends a literal value to the expression.
        /// </summary>
        /// <param name="value">The literal string value to be appended to the interpolated string.</param>
        public readonly void AppendLiteral(string value)
        {
            builder.AppendLiteral(value);
        }

        /// <summary>
        /// Appends a formatted value to the expression.
        /// </summary>
        /// <param name="value">The formatted string to be appended to the interpolated string.</param>
        public readonly void AppendFormatted(string? value)
        {
            builder.AppendFormatted(value);
        }

        /// <summary>
        /// Appends a formatted value to the expression. The value must implement <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
        /// </summary>
        /// <param name="valueProvider">An instance of an object which implements <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</param>
        /// <exception cref="InvalidOperationException"></exception>
        public void AppendFormatted<T>(T valueProvider) where T : IValueProvider, IManifestExpressionProvider
        {
            builder.AppendFormatted(valueProvider);
        }

        /// <summary>
        /// Appends a formatted value to the expression. The value must implement <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.
        /// </summary>
        /// <param name="valueProvider">An instance of an object which implements <see cref="IValueProvider"/> and <see cref="IManifestExpressionProvider"/>.</param>
        /// <exception cref="InvalidOperationException"></exception>
        public void AppendFormatted<T>(IResourceBuilder<T> valueProvider)
            where T : IResource, IValueProvider, IManifestExpressionProvider
        {
            builder.AppendFormatted(valueProvider.Resource);
        }
    }
}
