// Copyright © Serilog Contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Diagnostics;
using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;
using Serilog.Parsing;
using Serilog.Sinks.Seq.Conventions;
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable PossibleMultipleEnumeration

namespace Serilog.Sinks.Seq;

/// <summary>
/// An <see cref="ITextFormatter"/> that writes events in a compact JSON format.
/// </summary>
/// <remarks>Modified from <c>Serilog.Formatting.Compact.CompactJsonFormatter</c> to add
/// implicit SerilogTracing span support.</remarks>
public sealed class SeqCompactJsonFormatter: ITextFormatter
{
    readonly JsonValueFormatter _valueFormatter;
    readonly IFormatProvider _formatProvider;
    readonly IDottedPropertyNameConvention _dottedPropertyNameConvention;
    
    /// <summary>
    /// Construct a <see cref="SeqCompactJsonFormatter"/>.
    /// </summary>
    /// <param name="valueFormatter">A value formatter for <see cref="LogEventPropertyValue"/>s on the event.</param>
    /// <param name="formatProvider">An <see cref="IFormatProvider"/> that will be used to render log event tokens.</param>
    /// <param name="preserveDottedPropertyNames">If <c langword="true"/>, log event property names that
    /// contain <c>.</c> will be sent to Seq as-is. Otherwise, properties with dotted names will be converted
    /// into nested objects.</param>
    public SeqCompactJsonFormatter(IFormatProvider? formatProvider = null, JsonValueFormatter? valueFormatter = null, bool preserveDottedPropertyNames = false)
    {
        _formatProvider = formatProvider ?? CultureInfo.InvariantCulture;
        _valueFormatter = valueFormatter ?? new("$type");
        _dottedPropertyNameConvention = preserveDottedPropertyNames
            ? new PreserveDottedPropertyNames()
            : new UnflattenDottedPropertyNames();
    }

    /// <summary>
    /// Format the log event into the output. Successive events will be newline-delimited.
    /// </summary>
    /// <param name="logEvent">The event to format.</param>
    /// <param name="output">The output.</param>
    public void Format(LogEvent logEvent, TextWriter output)
    {
        FormatEvent(logEvent, output);
        output.WriteLine();
    }

    /// <summary>
    /// Format the log event into the output, without newline delimiters.
    /// </summary>
    /// <param name="logEvent">The event to format.</param>
    /// <param name="output">The output.</param>
    public void FormatEvent(LogEvent logEvent, TextWriter output)
    {
        if (logEvent == null) throw new ArgumentNullException(nameof(logEvent));
        if (output == null) throw new ArgumentNullException(nameof(output));

        output.Write("{\"@t\":\"");
        output.Write(logEvent.Timestamp.UtcDateTime.ToString("O"));
        
        output.Write("\",\"@mt\":");
        JsonValueFormatter.WriteQuotedJsonString(logEvent.MessageTemplate.Text, output);

        var tokensWithFormat = logEvent.MessageTemplate.Tokens
            .OfType<PropertyToken>()
            .Where(pt => pt.Format != null);

        // Better not to allocate an array in the 99.9% of cases where this is false
        // ReSharper disable once PossibleMultipleEnumeration
        if (tokensWithFormat.Any())
        {
            output.Write(",\"@r\":[");
            var delim = "";
            foreach (var r in tokensWithFormat)
            {
                output.Write(delim);
                delim = ",";
                var space = new StringWriter();
                r.Render(logEvent.Properties, space, _formatProvider);
                JsonValueFormatter.WriteQuotedJsonString(space.ToString(), output);
            }

            output.Write(']');
        }

        if (logEvent.Level != LogEventLevel.Information)
        {
            output.Write(",\"@l\":\"");
            output.Write(logEvent.Level);
            output.Write('\"');
        }

        if (logEvent.Exception != null)
        {
            output.Write(",\"@x\":");
            JsonValueFormatter.WriteQuotedJsonString(logEvent.Exception.ToString(), output);
        }

        if (logEvent.TraceId != null)
        {
            output.Write(",\"@tr\":\"");
            output.Write(logEvent.TraceId.Value.ToHexString());
            output.Write('\"');
        }

        if (logEvent.SpanId != null)
        {
            output.Write(",\"@sp\":\"");
            output.Write(logEvent.SpanId.Value.ToHexString());
            output.Write('\"');
        }

        var skipSpanProperties = false;
        if (logEvent is {TraceId: not null, SpanId: not null} &&
            logEvent.Properties.TryGetValue("SpanStartTimestamp", out var st) &&
            st is ScalarValue { Value: DateTime spanStartTimestamp })
        {
            skipSpanProperties = true;
                
            output.Write(",\"@st\":\"");
            output.Write(spanStartTimestamp.ToString("o"));
            output.Write('\"');

            if (logEvent.Properties.TryGetValue("ParentSpanId", out var ps) &&
                ps is ScalarValue { Value: ActivitySpanId parentSpanId })
            {
                output.Write(",\"@ps\":\"");
                output.Write(parentSpanId.ToHexString());
                output.Write('\"');
            }

            if (logEvent.Properties.TryGetValue("SpanKind", out var sk) &&
                sk is ScalarValue { Value: ActivityKind spanKind } &&
                spanKind != ActivityKind.Internal)
            {
                output.Write(",\"@sk\":\"");
                output.Write(spanKind);
                output.Write('\"');
            }
        }

        var properties = _dottedPropertyNameConvention.ProcessDottedPropertyNames(logEvent.Properties);
        foreach (var property in properties)
        {
            var name = property.Key;
                
            if (skipSpanProperties && name is "SpanStartTimestamp" or "ParentSpanId" or "SpanKind")
                continue;
                
            if (name.Length > 0 && name[0] == '@')
            {
                // Escape first '@' by doubling
                name = '@' + name;
            }

            output.Write(',');
            JsonValueFormatter.WriteQuotedJsonString(name, output);
            output.Write(':');
            _valueFormatter.Format(property.Value, output);
        }

        output.Write('}');
    }
}
