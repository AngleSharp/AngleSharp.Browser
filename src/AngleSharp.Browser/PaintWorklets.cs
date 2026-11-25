namespace AngleSharp.Browser;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Css.Painting;
using AngleSharp.Css.TypedOm;
using AngleSharp.Dom;
using AngleSharp.Io;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime.Interop;

/// <summary>
/// Loads the modules of paint worklets (CSS Painting API, `CSS.paintWorklet.addModule`): each
/// document's worklet runs its modules in a script realm of its own, where `registerPaint`
/// registers the classes painting `paint()` images with AngleSharp.Css's
/// <see cref="PaintWorklet"/>; once a module ran, the page is laid out again to paint them.
/// </summary>
sealed class PaintWorklets(Action changed) : IPaintWorkletModuleLoader
{
    private readonly ConditionalWeakTable<PaintWorkletGlobalScope, Realm> _realms = new();

    public async Task LoadAsync(PaintWorkletGlobalScope scope, Url moduleUrl, CancellationToken cancellationToken)
    {
        var source = await FetchAsync(scope.Document, moduleUrl, cancellationToken).ConfigureAwait(false);
        var realm = _realms.GetValue(scope, static current => new Realm(current));

        lock (realm)
        {
            realm.Run(source, moduleUrl.Href);
        }

        changed();
    }

    private static async Task<string> FetchAsync(IDocument document, Url url, CancellationToken cancellationToken)
    {
        var requester = document.Context?.GetServices<IRequester>().FirstOrDefault(m => m.SupportsProtocol(url.Scheme)) ??
            throw new DomException(DomError.Network);
        using var response = await requester.RequestAsync(new Request { Address = url, Method = HttpMethod.Get }, cancellationToken).ConfigureAwait(false);

        if (response is null || (int)response.StatusCode is < 200 or >= 300)
        {
            throw new DomException(DomError.Network);
        }

        using var reader = new StreamReader(response.Content);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The PaintWorkletGlobalScope of a document's worklet as script sees it.
    /// </summary>
    private sealed class Realm
    {
        private readonly Engine _engine;
        private readonly PaintWorkletGlobalScope _scope;

        public Realm(PaintWorkletGlobalScope scope)
        {
            _scope = scope;
            // the members of the DOM objects given to a paint (its context, size and properties)
            // are found by their names in script (fillRect for FillRect)
            _engine = new Engine(options => options
                .SetTypeResolver(new TypeResolver { MemberNameComparer = StringComparer.OrdinalIgnoreCase })
                .SetTypeConverter(engine => new DomTypeConverter(engine))
                .LimitRecursion(256)
                .TimeoutInterval(TimeSpan.FromSeconds(5)));
            _engine.SetValue("devicePixelRatio", scope.DevicePixelRatio);
            _engine.SetValue("registerPaint", new Action<string, JsValue>(Register));
        }

        public void Run(string source, string url) => _engine.Execute(source, url);

        private void Register(string name, JsValue paintClass)
        {
            if (!paintClass.IsConstructor())
            {
                throw new Jint.Runtime.JavaScriptException(_engine.Intrinsics.TypeError, "The paint definition has to be a class.");
            }

            var type = paintClass.AsObject();
            var options = type.Get("contextOptions");
            var definition = new Definition(this, type)
            {
                InputProperties = ReadStrings(type.Get("inputProperties")),
                InputArguments = ReadStrings(type.Get("inputArguments")),
                ContextOptions = new PaintRenderingContext2DSettings
                {
                    Alpha = options is not ObjectInstance optionsObject || optionsObject.Get("alpha") is not JsBoolean alpha || alpha.AsBoolean(),
                },
            };
            _scope.RegisterPaint(name, definition);
        }

        private static string[] ReadStrings(JsValue value) =>
            value is ObjectInstance list && !value.IsNull()
                ? Enumerable.Range(0, (int)Jint.Runtime.TypeConverter.ToLength(list.Get("length"))).Select(i => list.Get(i).ToString()).ToArray()
                : [];

        public void Paint(ObjectInstance type, ref ObjectInstance? instance, IPaintRenderingContext2D context, PaintSize size, IStylePropertyMapReadOnly properties, IReadOnlyList<CssStyleValue> arguments)
        {
            lock (this)
            {
                // one instance paints all images (a browser may create several)
                instance ??= _engine.Construct(type);
                var args = _engine.Intrinsics.Array.Construct(arguments.Select(argument => JsValue.FromObject(_engine, argument)).ToArray());
                _engine.Invoke(instance.Get("paint"), instance, [JsValue.FromObject(_engine, context), JsValue.FromObject(_engine, size), JsValue.FromObject(_engine, properties), args]);
            }
        }
    }

    /// <summary>
    /// Converts the strings of script to the enumerations of the DOM by the names they have
    /// there (e.g. "round" for CanvasLineCap.Round), as AngleSharp.Js does.
    /// </summary>
    private sealed class DomTypeConverter(Engine engine) : DefaultTypeConverter(engine)
    {
        public override object? Convert(object? value, Type type, IFormatProvider formatProvider) =>
            value is string text && ToEnum(text, type) is { } literal ? literal : base.Convert(value, type, formatProvider);

        public override bool TryConvert(object? value, Type type, IFormatProvider formatProvider, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? converted)
        {
            if (value is string text && ToEnum(text, type) is { } literal)
            {
                converted = literal;
                return true;
            }

            return base.TryConvert(value, type, formatProvider, out converted);
        }

        private static object? ToEnum(string text, Type type)
        {
            var enumType = Nullable.GetUnderlyingType(type) ?? type;

            if (!enumType.IsEnum)
            {
                return null;
            }

            foreach (var field in enumType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                var name = field.GetCustomAttributes(typeof(AngleSharp.Attributes.DomNameAttribute), false).OfType<AngleSharp.Attributes.DomNameAttribute>().FirstOrDefault()?.OfficialName ?? field.Name;

                if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                {
                    return field.GetValue(null);
                }
            }

            return null;
        }
    }

    private sealed class Definition(Realm realm, ObjectInstance type) : IPaintDefinition
    {
        private ObjectInstance? _instance;

        public IEnumerable<string> InputProperties { get; init; } = [];

        public IEnumerable<string> InputArguments { get; init; } = [];

        public PaintRenderingContext2DSettings ContextOptions { get; init; } = new();

        public void Paint(IPaintRenderingContext2D context, PaintSize size, IStylePropertyMapReadOnly properties, IReadOnlyList<CssStyleValue> arguments) =>
            realm.Paint(type, ref _instance, context, size, properties, arguments);
    }
}
