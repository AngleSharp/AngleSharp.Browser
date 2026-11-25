namespace AngleSharp.Browser;

using System;
using System.Diagnostics.CodeAnalysis;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Scripting;
using AngleSharp.Wasm.Dom;

/// <summary>
/// Completes the generated DOM registrations of the libraries where they miss what the
/// reflection path exposes. Registered last, see <see cref="BrowserEngine"/>; remove the
/// parts once fixed upstream.
/// </summary>
internal static class UpstreamDom
{
    private const string MediaQueryListType = "AngleSharp.Css.Dom.CssMediaQueryList";

    public static void Register(DomRegistry registry)
    {
        RegisterMediaQueryList(registry);
        RegisterWasmDescriptors(registry);
    }

    //  AngleSharp.Js gives an object the prototype of the most derived registered type, walking
    //  them in the order of registration: Core's EventTarget class comes before Css's
    //  IMediaQueryList and is never replaced by it, so the object matchMedia returns (a
    //  CssMediaQueryList, which derives from EventTarget) got EventTarget's prototype, without
    //  media and matches. Registering the internal type itself, chained to IMediaQueryList,
    //  gives it the right one (and the shared MediaQueryList interface object).
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Instantiated over a reference type, whose shared code always exists.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The type is kept, as matchMedia creates it.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "The generic method only declares the type.")]
    private static void RegisterMediaQueryList(DomRegistry registry)
    {
        var type = typeof(IMediaQueryList).Assembly.GetType(MediaQueryListType);

        if (type is not null && typeof(IMediaQueryList).IsAssignableFrom(type) && type.IsClass)
        {
            typeof(UpstreamDom)
                .GetMethod(nameof(DeclareMediaQueryList), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(type)
                .Invoke(null, [registry]);
        }
    }

    private static void DeclareMediaQueryList<T>(DomRegistry registry)
        where T : class, IMediaQueryList
    {
        registry.Type<T>("MediaQueryList").Inherits<IMediaQueryList>();
    }

    //  AngleSharp.Wasm's generated registration leaves out the descriptors WebAssembly.Module's
    //  imports and exports give (their members have DOM names, the types none), so they came
    //  out as empty objects.
    private static void RegisterWasmDescriptors(DomRegistry registry)
    {
        registry.Type<WasmModuleImportDescriptor>("ModuleImportDescriptor")
            .WithoutInterfaceObject()
            .Property<string>("module", t => t.Module)
            .Property<string>("name", t => t.Name)
            .Property<string>("kind", t => t.Kind);

        registry.Type<WasmModuleExportDescriptor>("ModuleExportDescriptor")
            .WithoutInterfaceObject()
            .Property<string>("name", t => t.Name)
            .Property<string>("kind", t => t.Kind);
    }
}
