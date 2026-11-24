namespace AngleSharp.Browser.Tests;

using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

/// <summary>
/// The WebAssembly JavaScript API as pages use it: promises, imports of functions of scripts,
/// exports called as functions, and memory shared with scripts.
/// </summary>
[TestFixture]
public class WebAssemblyTests
{
    private const string Address = "https://example.com/";

    private const string Program = """
        (module
          (import "env" "log" (func $log (param i32) (result i32)))
          (import "env" "offset" (global $offset i32))
          (memory (export "memory") 1 10)
          (global $counter (export "counter") (mut i32) (i32.const 7))
          (table (export "table") 2 funcref)
          (elem (i32.const 0) $double)
          (func $double (export "double") (param i32) (result i32) local.get 0 i32.const 2 i32.mul)
          (func (export "callLog") (param i32) (result i32) local.get 0 global.get $offset i32.add call $log)
          (func (export "store") (param i32 i32) local.get 0 local.get 1 i32.store8)
          (func (export "load") (param i32) (result i32) local.get 0 i32.load8_u)
          (func (export "grow") (result i32) i32.const 1 memory.grow)
          (func (export "add64") (param i64 i64) (result i64) local.get 0 local.get 1 i64.add)
          (func (export "half") (param f64) (result f64) local.get 0 f64.const 2 f64.div)
          (func (export "pair") (result i32 i32) i32.const 1 i32.const 2)
          (func (export "fail") unreachable))
        """;

    private BrowserEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _engine = new BrowserEngine(new BrowserOptions { ProfileDirectory = null, ViewportWidth = 800, ViewportHeight = 600 });
    }

    [TearDown]
    public void TearDown()
    {
        _engine.Dispose();
    }

    private static string ToBytes(string wat) => string.Join(",", Wasmtime.Module.ConvertText(wat).Select(b => b.ToString()));

    //  Runs the body of an async function with the bytes of the program in "bytes"; the result
    //  (or what was thrown) ends up in window.result.
    private async Task<string?> Run(string body, string wat = Program)
    {
        await _engine.LoadHtmlAsync("<title>Wasm</title>", Address);
        await _engine.EvaluateScriptAsync($$"""
            var bytes = new Uint8Array([{{ToBytes(wat)}}]);
            var logged = [];
            var imports = { env: { log: function (x) { logged.push(x); return x + 1; }, offset: 100 } };
            (async function () { {{body}} })().then(function (r) { window.result = 'ok:' + r; }, function (e) { window.result = 'error:' + (e && e.name) + ':' + (e && e.message); });
            """);

        for (var i = 0; i < 100; i++)
        {
            if (await _engine.EvaluateScriptAsync("window.result || ''") is string { Length: > 0 } result)
            {
                return result;
            }

            await Task.Delay(20);
        }

        return null;
    }

    [Test]
    public async Task WebAssemblyIsANamespaceWithItsInterfaces()
    {
        var result = await Run("""
            return [typeof WebAssembly, Object.prototype.toString.call(WebAssembly), typeof WebAssembly.Module, typeof WebAssembly.Instance,
                typeof WebAssembly.Memory, typeof WebAssembly.Table, typeof WebAssembly.Global, typeof WebAssembly.CompileError,
                typeof WebAssembly.RuntimeError, typeof window.Module, typeof window.Instance, 'validate' in window, WebAssembly.validate(bytes)].join();
            """);

        Assert.That(result, Is.EqualTo("ok:object,[object WebAssembly],function,function,function,function,function,function,function,undefined,undefined,false,true"));
    }

    [Test]
    public async Task InstantiateGivesAModuleAndAnInstanceWithFunctions()
    {
        var result = await Run("""
            var p = WebAssembly.instantiate(bytes, imports);
            var source = await p;
            var e = source.instance.exports;
            return [p instanceof Promise, source.module instanceof WebAssembly.Module, source.instance instanceof WebAssembly.Instance,
                typeof e.double, e.double(21), e.callLog(5), logged.join('+'), e.half(3), e.add64(2n ** 62n, 2n ** 62n), e.pair().join('|'),
                Object.keys(e).join('|')].join();
            """);

        Assert.That(result, Is.EqualTo("ok:true,true,true,function,42,106,105,1.5,-9223372036854775808,1|2,memory|counter|table|double|callLog|store|load|grow|add64|half|pair|fail"));
    }

    [Test]
    public async Task MemoryBufferIsTheMemory()
    {
        var result = await Run("""
            var e = (await WebAssembly.instantiate(bytes, imports)).instance.exports;
            var buffer = e.memory.buffer;
            var view = new Uint8Array(buffer);
            view[10] = 42;
            e.store(11, 7);
            var before = [buffer.byteLength, e.memory.buffer === buffer, e.load(10), view[11]];
            var old = e.grow();
            var grown = e.memory.buffer;
            return before.concat([old, grown !== buffer, grown.byteLength, new Uint8Array(grown)[10], e.memory.grow(1), e.memory.buffer.byteLength]).join();
            """);

        Assert.That(result, Is.EqualTo("ok:65536,true,42,7,1,true,131072,42,2,196608"));
    }

    [Test]
    public async Task ModuleAndInstanceCanBeCreatedDirectly()
    {
        var result = await Run("""
            var module = new WebAssembly.Module(bytes);
            var instance = new WebAssembly.Instance(module, imports);
            var other = await WebAssembly.instantiate(module, imports);
            return [WebAssembly.Module.exports(module).length, WebAssembly.Module.imports(module).map(function (i) { return i.module + '.' + i.name + ':' + i.kind; }).join('|'),
                instance.exports.double(4), other instanceof WebAssembly.Instance].join();
            """);

        Assert.That(result, Is.EqualTo("ok:12,env.log:function|env.offset:global,8,true"));
    }

    [Test]
    public async Task ObjectsOfScriptsAreImported()
    {
        const string wat = """
            (module
              (import "js" "mem" (memory 1))
              (import "js" "g" (global $g (mut i32)))
              (import "js" "tbl" (table 1 funcref))
              (type $t (func (param i32) (result i32)))
              (func (export "read") (result i32) i32.const 0 i32.load8_u)
              (func (export "bump") global.get $g i32.const 1 i32.add global.set $g)
              (func (export "callAt") (param i32 i32) (result i32) local.get 1 local.get 0 call_indirect (type $t)))
            """;
        var result = await Run($$"""
            var mem = new WebAssembly.Memory({ initial: 1, maximum: 2 });
            var g = new WebAssembly.Global({ value: 'i32', mutable: true }, 41);
            var tbl = new WebAssembly.Table({ element: 'anyfunc', initial: 1 });
            var other = (await WebAssembly.instantiate(new Uint8Array([{{ToBytes(Program)}}]), imports)).instance.exports;
            tbl.set(0, other.double);
            new Uint8Array(mem.buffer)[0] = 9;
            var e = (await WebAssembly.instantiate(bytes, { js: { mem: mem, g: g, tbl: tbl } })).instance.exports;
            e.bump();
            return [e.read(), g.value, e.callAt(0, 5), tbl.get(0) === other.double, tbl.length].join();
            """, wat);

        Assert.That(result, Is.EqualTo("ok:9,42,10,true,1"));
    }

    [Test]
    public async Task TrapIsARuntimeError()
    {
        var result = await Run("""
            var e = (await WebAssembly.instantiate(bytes, imports)).instance.exports;
            try { e.fail(); } catch (x) { return [x instanceof WebAssembly.RuntimeError, x instanceof Error, x.name].join(); }
            """);

        Assert.That(result, Is.EqualTo("ok:true,true,RuntimeError"));
    }

    [Test]
    public async Task ErrorOfAnImportReachesTheCaller()
    {
        var result = await Run("""
            var thrown = { reason: 'exit' };
            var e = (await WebAssembly.instantiate(bytes, { env: { log: function () { throw thrown; }, offset: 0 } })).instance.exports;
            try { e.callLog(1); } catch (x) { return x === thrown; }
            """);

        Assert.That(result, Is.EqualTo("ok:true"));
    }

    [Test]
    public async Task InvalidBytesRejectWithACompileError()
    {
        var result = await Run("""
            try { await WebAssembly.compile(new Uint8Array([0, 97, 115, 109, 9])); } catch (x) { return [x instanceof WebAssembly.CompileError, x instanceof Error, WebAssembly.validate(new Uint8Array([1, 2]))].join(); }
            """);

        Assert.That(result, Is.EqualTo("ok:true,true,false"));
    }

    [Test]
    public async Task MissingImportRejectsWithALinkError()
    {
        var result = await Run("""
            try { await WebAssembly.instantiate(bytes, { env: { log: 5, offset: 0 } }); } catch (x) { return [x instanceof WebAssembly.LinkError, x.name].join(); }
            """);

        Assert.That(result, Is.EqualTo("ok:true,LinkError"));
    }

    [Test]
    public async Task InstantiateStreamingTakesAFetch()
    {
        var wasm = Wasmtime.Module.ConvertText(Program);
        using var server = new TestServer(new()
        {
            ["/"] = ("text/html", "<title>Wasm</title>"u8.ToArray()),
            ["/program.wasm"] = ("application/wasm", wasm),
            ["/wrong.wasm"] = ("text/plain", wasm),
        });

        await _engine.NavigateAsync(server.Address);
        await _engine.EvaluateScriptAsync("""
            var imports = { env: { log: function (x) { return x; }, offset: 0 } };
            (async function () {
                var source = await WebAssembly.instantiateStreaming(fetch('/program.wasm'), imports);
                var module = await WebAssembly.compileStreaming(fetch('/program.wasm'));
                var wrong = await WebAssembly.instantiateStreaming(fetch('/wrong.wasm'), imports).then(function () { return 'loaded'; }, function () { return 'rejected'; });
                return [source.instance.exports.double(8), module instanceof WebAssembly.Module, wrong].join();
            })().then(function (r) { window.result = 'ok:' + r; }, function (e) { window.result = 'error:' + e; });
            """);
        string? result = null;

        for (var i = 0; i < 100 && result is null; i++)
        {
            await Task.Delay(20);
            result = await _engine.EvaluateScriptAsync("window.result || ''") is string { Length: > 0 } text ? text : null;
        }

        Assert.That(result, Is.EqualTo("ok:16,true,rejected"));
    }
}
